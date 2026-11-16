#!/usr/bin/env python3
"""Recompute the layout of .docu documents from the rules of FORMAT.md and
compare it with the layout stored in the file.

Usage: python check_format.py [--first N] file.docu [more.docu ...]
Exit code 0 only if nothing differs in any file.
"""
import hashlib
import json
import os
import struct
import sys
import zipfile

EXTS = ('.ttf', '.otf', '.ttc', '.otc')
TAB_STOP = 457200
PAD = 76200  # P of section 9


# ---------------------------------------------------------------- section 1
def rnd(a, b):
    """round(a, b) = floor((2a + b) / (2b)); section 1."""
    return (2 * a + b) // (2 * b)


# ---------------------------------------------------------------- section 4
def u16(d, o):
    return struct.unpack_from('>H', d, o)[0]


def i16(d, o):
    return struct.unpack_from('>h', d, o)[0]


def u32(d, o):
    return struct.unpack_from('>I', d, o)[0]


def read_directory(f, off):
    """Table directory of the face whose offset table starts at off."""
    f.seek(off)
    h = f.read(12)
    n = u16(h, 4)
    recs = f.read(16 * n)
    tabs = {}
    for k in range(n):
        tag = recs[16 * k:16 * k + 4].decode('latin-1')
        tabs[tag] = (u32(recs, 16 * k + 8), u32(recs, 16 * k + 12))
    return tabs


def read_table(f, tabs, tag, limit=None):
    if tag not in tabs:
        return None
    o, ln = tabs[tag]
    f.seek(o)
    return f.read(ln if limit is None else min(ln, limit))


def decode_name(rec_plat, raw):
    if rec_plat in (0, 3):
        return raw.decode('utf-16-be', 'replace')
    return raw.decode('mac_roman', 'replace')


def family_from_name(d):
    """Name record 1: Windows/US English first, else any Windows,
    else Unicode platform, else Macintosh (section 4.1)."""
    if d is None or len(d) < 6:
        return None
    count, so = u16(d, 2), u16(d, 4)
    recs = []
    for k in range(count):
        o = 6 + 12 * k
        if o + 12 > len(d):
            break
        plat, enc, lang, nid, ln, off = struct.unpack_from('>HHHHHH', d, o)
        if nid == 1:
            recs.append((plat, enc, lang, d[so + off:so + off + ln]))
    for test in (lambda r: r[0] == 3 and r[2] == 0x409, lambda r: r[0] == 3,
                 lambda r: r[0] == 0, lambda r: r[0] == 1):
        for r in recs:
            if test(r):
                return decode_name(r[0], r[3])
    return None


class FaceInfo:
    def __init__(self, path, index, off, family, bold, italic):
        self.path, self.index, self.off = path, index, off
        self.family, self.bold, self.italic = family, bold, italic


def scan_faces(path):
    out = []
    with open(path, 'rb') as f:
        head = f.read(12)
        if head[:4] == b'ttcf':
            n = u32(head, 8)
            f.seek(12)
            offs = struct.unpack('>%dI' % n, f.read(4 * n))
        else:
            offs = [0]
        for idx, off in enumerate(offs):
            try:
                tabs = read_directory(f, off)
                fam = family_from_name(read_table(f, tabs, 'name'))
                os2 = read_table(f, tabs, 'OS/2', 80)
                if os2 is not None and tabs['OS/2'][1] >= 64:
                    fs = u16(os2, 62)
                    bold, italic = bool(fs & 32), bool(fs & 1)
                else:
                    hd = read_table(f, tabs, 'head', 54)
                    mac = u16(hd, 44)
                    bold, italic = bool(mac & 1), bool(mac & 2)
                if fam:
                    out.append(FaceInfo(path, idx, off, fam, bold, italic))
            except Exception:
                pass
    return out


_FACES = None


def font_places():
    """Section 4.1: (folder, recursive) in the order they are read."""
    win = os.environ.get('WINDIR', r'C:\Windows')
    loc = os.environ.get('LOCALAPPDATA', '')
    return [(os.path.join(win, 'Fonts'), False),
            (os.path.join(loc, 'Microsoft', 'Windows', 'Fonts'), False),
            (os.path.join(loc, 'Microsoft', 'FontCache', '4', 'CloudFonts'), True)]


def place_files(d, recursive):
    out = []
    if not os.path.isdir(d):
        return out
    if recursive:
        for root, _dirs, names in os.walk(d):
            out.extend(os.path.join(root, n) for n in names)
    else:
        out.extend(os.path.join(d, n) for n in os.listdir(d))
    out = [f for f in out if f.lower().endswith(EXTS) and os.path.isfile(f)]
    return sorted(out, key=lambda f: f.upper())


def all_faces():
    global _FACES
    if _FACES is None:
        _FACES = []
        for d, rec in font_places():
            for path in place_files(d, rec):
                try:
                    _FACES.extend(scan_faces(path))
                except Exception:
                    pass
    return _FACES


def pick_face(family, bold, italic):
    """Section 4.1: first face per combination, with the fallback order."""
    faces = all_faces()
    fam = family.lower()
    if not any(f.family.lower() == fam for f in faces):
        fam = 'segoe ui'

    def first(b, i):
        for f in faces:
            if f.family.lower() == fam and f.bold == b and f.italic == i:
                return f
        return None
    for b, i in ((bold, italic), (bold, False), (False, italic), (False, False)):
        f = first(b, i)
        if f:
            return f
    for f in faces:
        if f.family.lower() == fam:
            return f
    return None


_FILEDATA = {}


class Font:
    """Metrics of one face (section 4.2)."""

    def __init__(self, info):
        self.info = info
        if info.path not in _FILEDATA:
            with open(info.path, 'rb') as f:
                _FILEDATA[info.path] = f.read()
        d = self.d = _FILEDATA[info.path]
        n = u16(d, info.off + 4)
        self.tabs = {}
        for k in range(n):
            o = info.off + 12 + 16 * k
            self.tabs[d[o:o + 4].decode('latin-1')] = (u32(d, o + 8), u32(d, o + 12))
        t = self.tabs
        ho = t['head'][0]
        self.upem = u16(d, ho + 18)
        self.loc_fmt = i16(d, ho + 50)
        hh = t['hhea'][0]
        self.ascender = max(0, i16(d, hh + 4))
        self.descender = max(0, -i16(d, hh + 6))
        self.lineGap = max(0, i16(d, hh + 8))
        self.nhm = u16(d, hh + 34)
        self.hmtx = t['hmtx'][0]
        self.cmap = self._pick_cmap()
        self._adv = {}
        self._cp = {}
        self._box = {}
        if 'post' in t:
            po = t['post'][0]
            self.ulpos, self.ulthick = i16(d, po + 8), i16(d, po + 10)
        else:
            self.ulpos = self.ulthick = 0

    # -- cmap: formats 4 and 12 only
    def _pick_cmap(self):
        d = self.d
        co = self.tabs['cmap'][0]
        subs = []
        for k in range(u16(d, co + 2)):
            plat, enc, off = struct.unpack_from('>HHI', d, co + 4 + 8 * k)
            fmt = u16(d, co + off)
            if fmt in (4, 12):
                subs.append((plat, enc, fmt, co + off))

        def find(plat, enc=None, fmt=None):
            for s in subs:
                if s[0] == plat and (enc is None or s[1] == enc) and (fmt is None or s[2] == fmt):
                    return s
            return None
        sel = (find(3, 10) or find(3, 1) or find(0, None, 12) or find(0, None, 4) or find(3, 0))
        self.symbol = bool(sel and sel[0] == 3 and sel[1] == 0)
        return sel

    def _lookup(self, cp):
        if self.cmap is None:
            return 0
        d = self.d
        plat, enc, fmt, o = self.cmap
        if fmt == 12:
            ng = u32(d, o + 12)
            lo, hi = 0, ng - 1
            while lo <= hi:
                mid = (lo + hi) // 2
                sc, ec, sg = struct.unpack_from('>III', d, o + 16 + 12 * mid)
                if cp < sc:
                    hi = mid - 1
                elif cp > ec:
                    lo = mid + 1
                else:
                    return sg + cp - sc
            return 0
        if cp > 0xFFFF:
            return 0
        segx2 = u16(d, o + 6)
        seg = segx2 // 2
        ends = o + 14
        starts = ends + segx2 + 2
        deltas = starts + segx2
        ranges = deltas + segx2
        for k in range(seg):
            if cp <= u16(d, ends + 2 * k):
                st = u16(d, starts + 2 * k)
                if cp < st:
                    return 0
                ro = u16(d, ranges + 2 * k)
                dl = u16(d, deltas + 2 * k)
                if ro == 0:
                    return (cp + dl) & 0xFFFF
                g = u16(d, ranges + 2 * k + ro + 2 * (cp - st))
                return 0 if g == 0 else (g + dl) & 0xFFFF
        return 0

    def glyph(self, cp):
        if cp not in self._cp:
            g = self._lookup(cp)
            if g == 0 and self.symbol and cp < 0x100:
                g = self._lookup(0xF000 + cp)
            self._cp[cp] = g
        return self._cp[cp]

    def adv_units(self, cp):
        if cp not in self._adv:
            g = min(self.glyph(cp), self.nhm - 1)
            self._adv[cp] = u16(self.d, self.hmtx + 4 * g)
        return self._adv[cp]

    def adv(self, cp, S):
        return rnd(self.adv_units(cp) * S, self.upem)

    def asc(self, S):
        return rnd((self.ascender + self.lineGap) * S, self.upem)

    def desc(self, S):
        return rnd(self.descender * S, self.upem)

    def text_width(self, text, S):
        return sum(self.adv(ord(c), S) for c in text)

    # -- glyph bounding box (section 8.2)
    def bbox(self, cp):
        g = self.glyph(cp)
        if 'glyf' not in self.tabs:
            return (-self.descender, self.ascender)
        d = self.d
        lo = self.tabs['loca'][0]
        if self.loc_fmt == 0:
            a, b = u16(d, lo + 2 * g) * 2, u16(d, lo + 2 * g + 2) * 2
        else:
            a, b = u32(d, lo + 4 * g), u32(d, lo + 4 * g + 4)
        if a == b:
            return (0, 0)
        go = self.tabs['glyf'][0] + a
        return (i16(d, go + 4), i16(d, go + 8))

    def gbox(self, cp, s):
        """Glyph box of a character at size s: W, H, D."""
        w = self.adv(cp, s)
        ymin, ymax = self.bbox(cp)
        h = rnd(ymax * s, self.upem) if ymax > 0 else 0
        dd = rnd(-ymin * s, self.upem) if ymin < 0 else 0
        return w, h, dd


_FONTS = {}


def get_font(family, bold, italic, used=None):
    f = pick_face(family, bold, italic)
    key = (f.path, f.index)
    if key not in _FONTS:
        _FONTS[key] = Font(f)
    if used is not None:
        used.add((family, bold, italic))
    return _FONTS[key]


# ---------------------------------------------------------------- content
class Fmt:
    """Resolved character format."""
    __slots__ = ('font', 'size', 'bold', 'italic', 'underline', 'color',
                 'strike', 'highlight', 'vertical')

    def __init__(self, font, size, bold, italic, underline, color, strike, highlight, vertical):
        self.font, self.size, self.bold, self.italic = font, size, bold, italic
        self.underline, self.color = underline, color
        self.strike, self.highlight, self.vertical = strike, highlight, vertical

    def draw_size(self):
        """Drawing size Sd in EMU (section 4.3)."""
        S = self.size * 6350
        return 2 * S // 3 if self.vertical in ('super', 'sub') else S


class Item:
    __slots__ = ('kind', 'u', 'fmt', 'adv', 'obj', 'box', 'block', 'note', 'cp', 'dims', 'pieces')

    def __init__(self, kind, u, fmt):
        self.kind, self.u, self.fmt = kind, u, fmt
        self.adv = 0
        self.obj = None
        self.box = kind in ('pic', 'eq', 'chart')
        self.block = False
        self.note = None
        self.cp = None
        self.dims = None  # (width, height above, depth below) of a box


class Doc:
    def __init__(self, content):
        self.c = content
        self.page = content['page']
        self.W = self.page['width'] - self.page['left'] - self.page['right']
        self.pst = {s['id']: s for s in content['paragraphStyles']}
        self.cst = {s['id']: s for s in content.get('characterStyles', [])}
        self.first_style = content['paragraphStyles'][0]
        self.used_fonts = set()
        # sources (section 2.2): a source whose id was used before is left out
        self.sources = {}
        for src in content.get('sources') or []:
            if not isinstance(src, dict):
                continue
            sid = src.get('id')
            if isinstance(sid, str) and sid != '' and sid not in self.sources:
                self.sources[sid] = src
        # `citations` is a value; `numbered` makes them read as numbers (section 2.2)
        self.system = content.get('citations')
        self.numbered = self.system in ('numbered', 'ieee', 'vancouver')
        self.cite_numbers = {}

    def cite_text(self, sid):
        """The text of a citation item (section 4.5)."""
        src = self.sources.get(sid) if isinstance(sid, str) else None
        if src is None:
            return '[?]'
        if self.numbered:
            return '[%d]' % self.cite_numbers[sid]

        def txt(v):
            return v.strip(' ') if isinstance(v, str) else ''
        fams = []
        for a in src.get('authors') or []:
            a = txt(a)
            if a:
                fams.append(a.split(',')[0].strip(' '))
        year = txt(src.get('year')) or 'n.d.'
        sysm = self.system
        n = len(fams)
        if n == 0:
            who = txt(src.get('title'))
        elif n == 1:
            who = fams[0]
        elif n == 2:
            who = fams[0] + (' and ' if sysm in ('harvard', 'chicago', 'mla') else ' & ') + fams[1]
        elif n == 3 and sysm == 'chicago':
            who = fams[0] + ', ' + fams[1] + ', and ' + fams[2]
        else:
            who = fams[0] + ' et al.'
        if sysm == 'mla':
            return '(%s)' % who
        if sysm in ('harvard', 'chicago'):
            return '(%s %s)' % (who, year)
        return '(%s, %s)' % (who, year)

    def pstyle(self, p):
        return self.pst.get(p.get('style'), self.first_style)

    def pval(self, p, key):
        """before, after, indentRight, indentFirst: the paragraph's own value
        if it has one (section 2.2), else the style's. Below 0 counts as 0
        for before, after and indentRight."""
        v = p[key] if key in p else self.pstyle(p)[key]
        if key != 'indentFirst' and v < 0:
            v = 0
        return v

    def fmt(self, st, cstyle_id, props):
        f = {k: st[k] for k in ('font', 'size', 'bold', 'italic', 'underline', 'color')}
        f['strike'] = st.get('strike', False)
        f['highlight'] = st.get('highlight', 'none')
        f['vertical'] = st.get('vertical', 'normal')
        if cstyle_id is not None and cstyle_id in self.cst:
            for k in f:
                if k in self.cst[cstyle_id]:
                    f[k] = self.cst[cstyle_id][k]
        for k in f:
            if props is not None and k in props:
                f[k] = props[k]
        f['size'] = min(3276, max(2, f['size']))
        return Fmt(f['font'], f['size'], f['bold'], f['italic'], f['underline'], f['color'],
                   f['strike'], f['highlight'], f['vertical'])

    def font_of(self, fmt):
        return get_font(fmt.font, fmt.bold, fmt.italic, self.used_fonts)

    def math_font(self):
        return get_font('Cambria Math', False, False, self.used_fonts)


def number_width(doc, fmt, num):
    """Width of a footnote number in a format (section 4.4)."""
    S = fmt.size * 6350
    Sr = 2 * S // 3
    f = doc.font_of(fmt)
    return sum(f.adv(ord(ch), Sr) for ch in str(num))


def build_items(doc, para, numbers, seen_refs=None, body=False, cell=False):
    """Items of a paragraph (section 2.2, 5.1)."""
    st = doc.pstyle(para)
    items = []
    for run in para.get('runs', []):
        fmt = doc.fmt(st, run.get('style'), run)
        if 'text' in run:
            t = run['text']
            u16s = t.encode('utf-16-le')
            units = struct.unpack('<%dH' % (len(u16s) // 2), u16s)
            for k, u in enumerate(units):
                kind = 'ch'
                if u == 9:
                    kind = 'tab'
                elif u == 0x2028:
                    kind = 'br'
                it = Item(kind, u, fmt)
                if 0xD800 <= u < 0xDC00 and k + 1 < len(units) and 0xDC00 <= units[k + 1] < 0xE000:
                    it.cp = 0x10000 + ((u - 0xD800) << 10) + (units[k + 1] - 0xDC00)
                    it.kind = 'hi'
                elif 0xDC00 <= u < 0xE000 and k > 0 and 0xD800 <= units[k - 1] < 0xDC00:
                    it.kind = 'lo'
                else:
                    it.cp = u
                items.append(it)
        elif 'picture' in run and run['picture'].get('wrap') and not cell:
            it = Item('fpic', 0xFFFC, fmt)
            it.obj = run['picture']
            items.append(it)
        elif 'picture' in run:
            it = Item('pic', 0xFFFC, fmt)
            it.obj = run['picture']
            items.append(it)
        elif 'equation' in run:
            it = Item('eq', 0xFFFC, fmt)
            it.obj = run['equation']
            items.append(it)
        elif 'chart' in run:
            it = Item('chart', 0xFFFC, fmt)
            it.obj = run['chart']
            items.append(it)
        elif 'cite' in run:
            if not body:
                continue
            it = Item('cite', 0xFFFC, fmt)
            it.note = doc.cite_text(run['cite'])
            items.append(it)
        elif 'note' in run:
            if cell:
                continue
            if seen_refs is not None:
                if run['note'] in seen_refs:
                    continue
                seen_refs.add(run['note'])
            it = Item('note', 0xFFFC, fmt)
            it.note = run['note']
            items.append(it)
        if items and items[-1].box:
            items[-1].block = bool(items[-1].obj.get('block'))
    return items


def natural_advance(doc, it, numbers):
    if it.kind in ('br', 'tab', 'lo', 'fpic'):
        return 0
    if it.kind == 'note':
        return number_width(doc, it.fmt, numbers.get(it.note, 0))
    if it.kind == 'cite':
        f = doc.font_of(it.fmt)
        Sd = it.fmt.draw_size()
        return sum(f.adv(ord(ch), Sd) for ch in it.note)
    if it.kind == 'pic':
        return it.obj['width']
    if it.kind == 'chart':
        return it.obj['width']
    if it.kind == 'eq':
        return it.dims[0]
    f = doc.font_of(it.fmt)
    return f.adv(it.cp, it.fmt.draw_size())


# ---------------------------------------------------------------- section 5
class Line:
    pass


BULLETS = {'bullet': '•', 'circle': '◦', 'square': '▪', 'dash': '–'}
NUMBERS = ('number', 'number-paren', 'roman-upper', 'alpha-upper', 'alpha-paren', 'alpha',
           'roman', 'decimal', 'outline', 'outline-paren')


def list_kind(p):
    """The list kind of a body paragraph, or None (a table is never a list
    paragraph; an unknown kind is ignored; sections 2.2, 5.5)."""
    k = p.get('list')
    if 'table' in p or not isinstance(k, str):
        return None
    return k if (k in BULLETS or k in NUMBERS) else None


def list_level(p):
    lv = p.get('level', 0)
    return lv if isinstance(lv, int) and not isinstance(lv, bool) and 0 <= lv <= 8 else 0


def to_roman(n):
    if not 1 <= n <= 3999:
        return str(n)
    out = ''
    for v, s in ((1000, 'M'), (900, 'CM'), (500, 'D'), (400, 'CD'), (100, 'C'), (90, 'XC'),
                 (50, 'L'), (40, 'XL'), (10, 'X'), (9, 'IX'), (5, 'V'), (4, 'IV'), (1, 'I')):
        while n >= v:
            out += s
            n -= v
    return out


def to_letters(n):
    out = ''
    while n > 0:
        n, r = divmod(n - 1, 26)
        out = chr(97 + r) + out
    return out


def list_numbers(body):
    """Marker text of each list paragraph (section 5.5); None for the others."""
    out = []
    c = [0] * 9
    mem = [None] * 9
    for p in body:
        kind = list_kind(p)
        if kind is None:
            c = [0] * 9
            mem = [None] * 9
            out.append(None)
            continue
        L = list_level(p)
        for k in range(L + 1, 9):
            c[k], mem[k] = 0, None
        if kind in BULLETS:
            c[L], mem[L] = 0, None
            out.append(BULLETS[kind])
            continue
        if mem[L] is not None and mem[L] != kind:
            c[L], mem[L] = 0, None
        c[L] += 1
        mem[L] = kind
        n = c[L]
        if kind == 'number':
            t = '%d.' % n
        elif kind == 'number-paren':
            t = '%d)' % n
        elif kind == 'roman-upper':
            t = to_roman(n) + '.'
        elif kind == 'roman':
            t = to_roman(n).lower() + '.'
        elif kind == 'alpha-upper':
            t = to_letters(n).upper() + '.'
        elif kind == 'alpha':
            t = to_letters(n) + '.'
        elif kind == 'alpha-paren':
            t = to_letters(n) + ')'
        elif kind == 'decimal':
            t = ''.join('%d.' % max(1, c[k]) for k in range(L + 1))
        else:  # outline, outline-paren
            m = L % 3
            t = str(n) if m == 0 else (to_letters(n) if m == 1 else to_roman(n).lower())
            t += '.' if kind == 'outline' else ')'
        out.append(t)
    return out


def list_marker(doc, para, items, text):
    """(text, mw) of the marker of a list paragraph (section 5.5)."""
    if items:
        fmt = items[0].fmt
    else:
        fmt = doc.fmt(doc.pstyle(para), para.get('mark', {}).get('style'), para.get('mark'))
    f = doc.font_of(fmt)
    S = fmt.size * 6350
    mw = sum(f.adv(ord(ch), S) for ch in text) + f.adv(0x20, S)
    return text, mw


def valid_hyphens(para, n):
    """The paragraph's `hyphens` that count (section 2.2)."""
    h = para.get('hyphens')
    out = set()
    if not isinstance(h, list):
        return out
    prev = 0
    for v in h:
        if isinstance(v, bool) or not isinstance(v, int):
            continue
        if v > prev and v < n:
            out.add(v)
            prev = v
    return out


def border_room(doc, para):
    """top, bottom, gap of the paragraph's borders (2.2): its own if it has
    `borders` that is an object, else its style's (none if that is not an
    object); a value that is not a whole number counts as 0."""
    if 'table' in para:
        return 0, 0, 0
    b = para.get('borders')
    if not isinstance(b, dict):
        b = doc.pstyle(para).get('borders')
    if not isinstance(b, dict):
        return 0, 0, 0
    def num(k):
        v = b.get(k, 0)
        if isinstance(v, bool) or not isinstance(v, (int, float)):
            return 0
        if isinstance(v, float):
            if not v.is_integer():
                return 0
            v = int(v)
        return v
    def thick(k):
        v = num(k)
        return 0 if v <= 0 else min(max(v, 3175), 76200)
    return thick('top'), thick('bottom'), min(max(num('gap'), 0), 393700)


def make_line(doc, para, items, s, x0, width, mark, lm, toc=False):
    """The line that starts at item s, broken for the stretch x0, width
    (sections 5.2, 5.3). Its x is relative to the text area's left edge."""
    st = doc.pstyle(para)
    left, right, first = para.get('indentLeft', st['indentLeft']), doc.pval(para, 'indentRight'), doc.pval(para, 'indentFirst')
    left = max(left, -228600 if lm is not None else 0)
    spacing = para.get('line', st['line'])
    align = para.get('align', st['align'])
    n = len(items)
    if lm is not None:
        left += list_level(para) * 457200
        indent = left + 457200
        if s == 0:
            indent += max(0, 228600 + lm[1] - 457200)
    else:
        indent = left + (first + mark if s == 0 else 0)
    avail = max(0, width - indent - right)
    w = 0
    best = None
    advs = []
    e = None
    hard = False
    last_tab = -1
    if toc and para.get('toc') and 'table' not in para:
        for k in range(n):
            if items[k].kind == 'tab':
                last_tab = k
    hyset = valid_hyphens(para, n)
    hbest = hd = None
    hy = False
    i = s
    while i < n:
        c = items[i]
        if c.kind == 'br':
            advs.append(0)
            e, hard = i + 1, True
            break
        if c.box and c.block:
            if i > s:
                e, hard = i, True
            else:
                advs.append(c.adv)
                e, hard = i + 1, True
            break
        if c.box and i > s:
            best = i
        if c.kind == 'tab':
            a = TAB_STOP - (w % TAB_STOP)
        else:
            a = c.adv
        if i == last_tab:
            rest = sum(items[k].adv for k in range(i + 1, n))
            if avail - w - rest > a:
                a = avail - w - rest
        space = (c.kind == 'ch' and c.u == 0x20)
        if i in hyset and i > s:
            pf = items[i - 1].fmt
            d = doc.font_of(pf).adv(0x2D, pf.draw_size())
            if w + d <= avail:
                hbest, hd = i, d
        if (not space) and a > 0 and i > s and w + a > avail:
            if hbest is not None and (best is None or hbest > best):
                e = hbest
                hy = True
                break
            e = best if best is not None else i
            break
        advs.append(a)
        w += a
        if (space or (c.kind == 'ch' and c.u == 0x2D) or c.kind == 'tab' or c.box) \
                and i + 1 < n and not (items[i + 1].kind == 'ch' and items[i + 1].u == 0x20):
            best = i + 1
        i += 1
    if e is None:
        e = n
    advs = advs[:e - s]
    if hy:
        advs[-1] += hd
    L = Line()
    L.hy = hy
    L.s, L.n, L.e, L.hard, L.a, L.avail = s, e - s, e, hard, advs, avail
    last = (e == n)
    # measured width and alignment (5.3)
    t = e
    if t > s and items[t - 1].kind == 'br':
        t -= 1
    while t > s and items[t - 1].kind == 'ch' and items[t - 1].u == 0x20:
        t -= 1
    m = sum(advs[:t - s])
    L.m = m
    shift = 0
    if align == 'center':
        shift = (avail - m) // 2 if avail > m else 0
    elif align == 'right':
        shift = avail - m if avail > m else 0
    elif align == 'justify':
        if not hard and not last and avail > m:
            frm = s
            for k in range(s, t):
                if items[k].kind == 'tab':
                    frm = k + 1
            spaces = [k for k in range(frm, t) if items[k].kind == 'ch' and items[k].u == 0x20]
            k_ = len(spaces)
            extra = avail - m
            for j, k in enumerate(spaces):
                advs[k - s] += extra * (j + 1) // k_ - extra * j // k_
    L.x = x0 + indent + shift
    L.mark = None
    if lm is not None and s == 0:
        L.mark = (x0 + left + 228600, lm[0])
    # height (5.3)
    if e > s:
        asc = desc = 0
        tall = deep = 0
        for k in range(s, e):
            it = items[k]
            f = doc.font_of(it.fmt)
            S = it.fmt.size * 6350
            asc = max(asc, f.asc(S))
            desc = max(desc, f.desc(S))
            if it.box:
                tall = max(tall, it.dims[1])
                deep = max(deep, it.dims[2])
    else:
        mk = doc.fmt(st, para.get('mark', {}).get('style'), para.get('mark'))
        f = doc.font_of(mk)
        S = mk.size * 6350
        asc, desc = f.asc(S), f.desc(S)
        tall = deep = 0
    d = max(desc, deep)
    L.h = max(rnd((asc + desc) * spacing, 1000) + d - desc, tall + d)
    L.base = L.h - d
    bt, bb, bg = border_room(doc, para)
    if s == 0 and bt > 0:
        L.h += bt + bg
        L.base += bt + bg
    if e == n and bb > 0:
        L.h += bb + bg
    return L


def more_lines(items, L):
    """Does another line follow L? (section 5.2)"""
    return not (L.e >= len(items) and not (L.hard and L.e > L.s and items[L.e - 1].kind == 'br'))


def layout_paragraph(doc, para, items, mark, lm, x0=0, width=None, toc=False):
    """All lines of a paragraph broken for a stretch (the whole text area by default)."""
    lines = []
    s = 0
    if width is None:
        width = doc.W
    while True:
        L = make_line(doc, para, items, s, x0, width, mark, lm, toc)
        lines.append(L)
        if not more_lines(items, L):
            return lines
        s = L.e


# ---------------------------------------------------------------- section 8
LEVEL_SIZE = (lambda S: S, lambda S: 7 * S // 10, lambda S: S // 2)
ABOVE_BELOW = ('\u2211', '\u220F', '\u22C3', '\u22C2')
ABOVE_NAMES = ('lim', 'max', 'min')


class Eq:
    def __init__(self, doc, S):
        self.f = doc.math_font()
        self.S = S
        self.draw = []
        f = self.f
        mlo, mhi = f.bbox(0x2212) if f.glyph(0x2212) else (0, 0)
        self.ok = f.glyph(0x2212) != 0 and mhi > mlo
        self.mlo, self.mhi = mlo, mhi

    def axis(self, s):
        if not self.ok:
            return s // 4
        return rnd((self.mlo + self.mhi) * s, 2 * self.f.upem)

    def thick(self, s):
        if not self.ok:
            return max(1, s // 16)
        return max(1, rnd((self.mhi - self.mlo) * s, self.f.upem))

    # a "box" here is (W, H, D, pieces); pieces are in local (x, up) coordinates
    def size(self, level):
        return LEVEL_SIZE[level](self.S)

    def klass(self, node):
        if isinstance(node, list):
            return self.klass(node[0]) if len(node) == 1 else 'ord'
        if 'c' in node:
            return node.get('k', 'ord')
        if 'fn' in node:
            return 'op'
        if 'base' in node:
            return self.klass(node['base'])
        return 'ord'

    def row(self, nodes, level, display):
        s = self.size(level)
        e = s // 18
        kl = [self.klass(nd) for nd in nodes]
        for i in range(len(nodes)):
            if kl[i] == 'bin':
                if i == 0 or kl[i - 1] in ('bin', 'rel', 'punct', 'op') or i == len(nodes) - 1 \
                        or kl[i + 1] in ('rel', 'punct'):
                    kl[i] = 'ord'
        x = 0
        H = D = 0
        pieces = []
        for i, nd in enumerate(nodes):
            if i > 0 and level == 0:
                x += self.spacing(kl[i - 1], kl[i]) * e
            w, h, d, pc = self.node(nd, level, display, kl[i])
            pieces += [shift_piece(p, x, 0) for p in pc]
            x += w
            H, D = max(H, h), max(D, d)
        return x, H, D, pieces

    @staticmethod
    def spacing(l, r):
        if l == 'bin' or r == 'bin':
            return 4
        if l == 'rel' and r == 'rel':
            return 0
        if l == 'rel' or r == 'rel':
            if l == 'punct':
                return 3
            if r == 'punct':
                return 0
            return 5
        if l == 'punct':
            return 3
        if l == 'op' and r in ('ord', 'op'):
            return 3
        if l == 'ord' and r == 'op':
            return 3
        return 0

    def glyph_piece(self, ch, s, up=0):
        return ('glyph', 0, up, s, ch)

    def node(self, nd, level, display, klass='ord'):
        f = self.f
        s = self.size(level)
        a = self.axis(s)
        t = self.thick(s)
        e = s // 18
        if isinstance(nd, list):
            return self.row(nd, level, display)
        if 'c' in nd:
            ch = nd['c']
            cp = ord(ch)
            if klass == 'op':
                s2 = 3 * s // 2 if display else s
                w, h, d = f.gbox(cp, s2)
                sh = a - (h - d) // 2
                return w, max(0, h + sh), max(0, d - sh), [self.glyph_piece(ch, s2, sh)]
            w, h, d = f.gbox(cp, s)
            return w, h, d, [self.glyph_piece(ch, s)]
        if 'fn' in nd:
            x = 0
            H = D = 0
            pcs = []
            for ch in nd['fn']:
                w, h, d = f.gbox(ord(ch), s)
                pcs.append(shift_piece(self.glyph_piece(ch, s), x, 0))
                x += w
                H, D = max(H, h), max(D, d)
            return x, H, D, pcs
        if 'frac' in nd:
            nl = level if display else min(level + 1, 2)
            Wn, Hn, Dn, pn = self.row(nd['frac'][0], nl, False)
            Wd, Hd, Dd, pd = self.row(nd['frac'][1], nl, False)
            g = 3 * t if display else t + t // 2
            inner = max(Wn, Wd) + 2 * e
            W = inner + 2 * e
            bar = a - t // 2
            pcs = [('rect', e, bar + t, inner, t)]
            nx, nup = e + (inner - Wn) // 2, bar + t + g + Dn
            dx, dup = e + (inner - Wd) // 2, bar - g - Hd
            pcs += [shift_piece(p, nx, nup) for p in pn]
            pcs += [shift_piece(p, dx, dup) for p in pd]
            return W, nup + Hn, max(0, Dd - dup), pcs
        if 'sqrt' in nd:
            Wb, Hb, Db, pb = self.row(nd['sqrt'], level, display)
            g = 2 * t if display else t + t // 2
            W = 11 * e + Wb
            yb = -Db
            yc = Hb + g + t // 2
            hs = yc - yb
            pts = [(e, yb + 2 * hs // 5), (3 * e, yb + hs // 2), (6 * e, yb), (9 * e, yc), (W, yc)]
            pcs = [('line', t, pts)] + [shift_piece(p, 10 * e, 0) for p in pb]
            return W, Hb + g + t, Db, pcs
        if 'base' in nd:
            return self.script(nd, level, display)
        if 'open' in nd:
            return self.brackets(nd, level, display)
        raise ValueError('unknown node %r' % (nd,))

    def script(self, nd, level, display):
        s = self.size(level)
        t = self.thick(s)
        e = s // 18
        base = nd['base']
        Wb, Hb, Db, pb = self.node(base, level, display, self.klass(base)) if not isinstance(base, list) \
            else self.row(base, level, display)
        nl = min(level + 1, 2)
        sup, sub = nd.get('sup'), nd.get('sub')
        Wp = Hp = Dp = Wq = Hq = Dq = 0
        pp = pq = []
        if sup is not None:
            Wp, Hp, Dp, pp = self.row(sup, nl, False)
        if sub is not None:
            Wq, Hq, Dq, pq = self.row(sub, nl, False)
        bnode = base[0] if isinstance(base, list) and len(base) == 1 else base
        above = False
        if display and isinstance(bnode, dict):
            if ('c' in bnode and bnode['c'] in ABOVE_BELOW) or \
                    ('fn' in bnode and bnode['fn'] in ABOVE_NAMES):
                above = True
        if above:
            W = max(Wb, Wp, Wq)
            pcs = [shift_piece(p, (W - Wb) // 2, 0) for p in pb]
            H, D = Hb, Db
            if sup is not None:
                up = Hb + 3 * e + Dp
                H = up + Hp
                pcs += [shift_piece(p, (W - Wp) // 2, up) for p in pp]
            if sub is not None:
                dn = Db + 3 * e + Hq
                D = dn + Dq
                pcs += [shift_piece(p, (W - Wq) // 2, -dn) for p in pq]
            return W, H, D, pcs
        u = max(2 * s // 5, Hb - 3 * s // 10)
        v = max(s // 5, Db + s // 10)
        if sup is not None and sub is not None:
            gap = (u - Dp) - (Hq - v)
            if gap < 3 * t:
                v += 3 * t - gap
        pcs = list(pb)
        H, D = Hb, Db
        if sup is not None:
            pcs += [shift_piece(p, Wb, u) for p in pp]
            H = max(Hb, u + Hp)
        if sub is not None:
            pcs += [shift_piece(p, Wb, -v) for p in pq]
            D = max(Db, v + Dq)
        return Wb + max(Wp, Wq) + e, H, D, pcs

    def brackets(self, nd, level, display):
        f = self.f
        s = self.size(level)
        a = self.axis(s)
        Wb, Hb, Db, pb = self.row(nd['body'], level, display)

        def bracket(ch):
            cp = ord(ch)
            w, h, d = f.gbox(cp, s)
            if Hb <= h and Db <= d:
                return w, h, d, self.glyph_piece(ch, s)
            need = 2 * max(Hb - a, Db + a)
            den = max(1, h + d)
            s2 = -(-s * need // den)
            s2 = min(6 * s, max(s, s2))
            w, h, d = f.gbox(cp, s2)
            sh = a - (h - d) // 2
            return w, max(0, h + sh), max(0, d - sh), self.glyph_piece(ch, s2, sh)
        pcs = []
        x = 0
        H, D = Hb, Db
        o = nd.get('open')
        if o:
            w, h, d, p = bracket(o)
            pcs.append(shift_piece(p, x, 0))
            x += w
            H, D = max(H, h), max(D, d)
        pcs += [shift_piece(p, x, 0) for p in pb]
        x += Wb
        c = nd.get('close')
        if c:
            w, h, d, p = bracket(c)
            pcs.append(shift_piece(p, x, 0))
            x += w
            H, D = max(H, h), max(D, d)
        return x, H, D, pcs


def shift_piece(p, dx, dup):
    """Move a piece given in local (x, up) coordinates."""
    k = p[0]
    if k == 'glyph':
        return ('glyph', p[1] + dx, p[2] + dup, p[3], p[4])
    if k == 'rect':
        return ('rect', p[1] + dx, p[2] + dup, p[3], p[4])
    if k == 'line':
        return ('line', p[1], [(x + dx, u + dup) for x, u in p[2]])
    raise ValueError(k)


def equation_box(doc, eq, fmt, block):
    S = fmt.size * 6350
    E = Eq(doc, S)
    w, h, d, pcs = E.row(eq['tree'], 0, bool(block))
    return (w, h, d), pcs


def equation_draw(pcs, x, base, color):
    out = []
    for p in pcs:
        if p[0] == 'glyph':
            out.append(['glyph', x + p[1], base - p[2], p[3], p[4], color])
        elif p[0] == 'rect':
            out.append(['rect', x + p[1], base - p[2], p[3], p[4], color])
        elif p[0] == 'line':
            flat = []
            for px, pu in p[2]:
                flat += [x + px, base - pu]
            out.append(['line', p[1]] + flat + [color])
    return out


# ---------------------------------------------------------------- section 9
CHART_COLORS = ('0072B2', 'E69F00', '009E73', 'D55E00', 'CC79A7', '56B4E9', 'F0E442', '999999')


def thousandths(v):
    """Value as whole thousandths (section 2.2); None stays None."""
    if v is None:
        return None
    x = v * 1000
    return int(x + 0.5) if x >= 0 else -int(-x + 0.5)


def axis_ticks(lo, hi):
    """step, aLo, aHi of the axis rule of section 9."""
    step = 1
    while True:
        for m in (1, 2, 5):
            st = step * m
            aLo = (lo // st) * st
            aHi = -((-hi) // st) * st
            if (aHi - aLo) // st <= 6:
                return st, aLo, aHi
        step *= 10


def value_text(v):
    a = abs(v)
    s = str(a // 1000)
    fr = '%03d' % (a % 1000)
    if fr != '000':
        s += '.' + fr.rstrip('0')
    return ('\u2212' if v < 0 else '') + s


def chart_draw(doc, ch, fmt, x0, y0):
    """The draw list of a chart whose top left corner is (x0, y0)."""
    W, H = ch['width'], ch['height']
    f = doc.font_of(fmt)
    S = fmt.size * 6350
    Sc = 4 * S // 5
    ac, dc = f.asc(Sc), f.desc(Sc)
    lc = ac + dc
    P = PAD
    col = fmt.color
    out = []

    def rect(x, y, w, h, c):
        out.append(['rect', x0 + x, y0 + y, w, h, c])

    def text(x, y, size, s, c=col):
        out.append(['text', x0 + x, y0 + y, size, s, c])
    def textup(x, y, size, s, c=col):
        out.append(['textup', x0 + x, y0 + y, size, s, c])
    # frame
    rect(0, 0, W, 9525, 'D0D0D0')
    rect(0, H - 9525, W, 9525, 'D0D0D0')
    rect(0, 0, 9525, H, 'D0D0D0')
    rect(W - 9525, 0, 9525, H, 'D0D0D0')
    # title
    title = ch.get('title') or ''
    if title:
        tw = f.text_width(title, S)
        text(max(0, (W - tw) // 2), P + f.asc(S), S, title)
        y0c = P + f.asc(S) + f.desc(S) + 38100
    else:
        y0c = P
    cats = ch.get('categories', [])
    series = ch.get('series', [])
    n = len(cats)
    kind = ch['type']
    if kind == 'pie':
        entries = [(c, CHART_COLORS[i % 8]) for i, c in enumerate(cats)]
    elif len(series) >= 2:
        entries = [(sr['name'], CHART_COLORS[i % 8]) for i, sr in enumerate(series)]
    else:
        entries = []
    if entries:
        top = H - P - lc
        y1 = top - 38100
        q = 7 * Sc // 10
        gap = Sc // 3
        wid = [q + gap + f.text_width(t, Sc) for t, _ in entries]
        total = sum(wid) + Sc * (len(entries) - 1)
        x = max(P, (W - total) // 2)
        for (t, c), w in zip(entries, wid):
            rect(x, top + ac - q, q, q, c)
            text(x + q + gap, top + ac, Sc, t)
            x += w + Sc
    else:
        y1 = H - P
    xtitle, xb, ytitle, yc = '', 0, '', 0
    ends = ch.get('axisNames') == 'ends'
    if kind != 'pie':
        ytitle = ch.get('yTitle') or ''
        xtitle = ch.get('xTitle') or ''
        if xtitle:
            xb = y1 - dc
            y1 -= lc + Sc // 4
        if ytitle and ends:
            text(P, y0c + ac, Sc, ytitle)
            y0c += lc + Sc // 4
        elif ytitle:
            yc = lc + Sc // 4
    names = (xtitle, xb, ytitle, yc, ends, textup)
    if kind == 'plot':
        plot_draw(ch, f, S, Sc, y0c, y1, out, rect, text, x0, y0, names)
        return out
    if n == 0 or len(series) == 0:
        return out

    def vals(sr):
        v = [thousandths(z) for z in sr.get('values', [])][:n]
        return v + [0] * (n - len(v))
    allv = [vals(sr) for sr in series]
    if kind == 'pie':
        v = [max(0, z) for z in allv[0]]
        total = sum(v)
        r = min(W - 2 * P, y1 - y0c) // 2
        if total > 0 and r > 0:
            acc = 0
            for i, z in enumerate(v):
                if z > 0:
                    out.append(['slice', x0 + W // 2, y0 + (y0c + y1) // 2, r, acc, acc + z, total,
                                CHART_COLORS[i % 8]])
                acc += z
        return out
    flat = [z for vv in allv for z in vv]
    lo, hi = min([0] + flat), max([0] + flat)
    if lo == hi:
        hi = lo + 1000
    st, aLo, aHi = axis_ticks(lo, hi)
    ticks = list(range(aLo, aHi + 1, st))
    left = P + yc + max(f.text_width(value_text(v), Sc) for v in ticks) + Sc // 3
    right = W - P
    top = y0c + lc // 2
    bottom = y1 - lc - Sc // 4
    pw, ph = right - left, bottom - top
    if pw <= 0 or ph <= 0:
        return out
    axis_names(f, Sc, ac, P, left, top, pw, ph, names, text)

    def Y(v):
        return bottom - (v - aLo) * ph // (aHi - aLo)
    for v in ticks:
        rect(left, Y(v) - 4762, pw, 9525, '808080' if v == 0 else 'D9D9D9')
        tx = value_text(v)
        text(left - Sc // 3 - f.text_width(tx, Sc), Y(v) + (ac - dc) // 2, Sc, tx)
    bw = pw // n
    for i, c in enumerate(cats):
        text(left + i * bw + (bw - f.text_width(c, Sc)) // 2, bottom + Sc // 4 + ac, Sc, c)
    m = len(series)
    if kind == 'bar':
        mg = bw // 8
        sw = (bw - 2 * mg) // m
        for j in range(m):
            for i in range(n):
                v = allv[j][i]
                ya, yb = Y(0), Y(v)
                if ya != yb:
                    rect(left + i * bw + mg + j * sw, min(ya, yb), sw, abs(ya - yb), CHART_COLORS[j % 8])
    elif kind == 'line':
        for j in range(m):
            pts = [(left + i * bw + bw // 2, Y(allv[j][i])) for i in range(n)]
            c = CHART_COLORS[j % 8]
            if n > 1:
                flatp = []
                for px, py in pts:
                    flatp += [x0 + px, y0 + py]
                out.append(['line', 19050] + flatp + [c])
            for px, py in pts:
                out.append(['disc', x0 + px, y0 + py, 28575, c])
    return out


def axis_names(f, Sc, ac, P, left, top, pw, ph, names, text):
    xtitle, xb, ytitle, yc, ends, textup = names
    if xtitle:
        w = f.text_width(xtitle, Sc)
        text(left + pw - w if ends else left + (pw - w) // 2, xb, Sc, xtitle)
    if ytitle and not ends:
        textup(P + ac, top + ph - (ph - f.text_width(ytitle, Sc)) // 2, Sc, ytitle)


def plot_draw(ch, f, S, Sc, y0c, y1, out, rect, text, x0, y0, names):
    """Graph of formulas (end of section 9); appends to out."""
    W = ch['width']
    ac, dc = f.asc(Sc), f.desc(Sc)
    lc = ac + dc
    P = PAD
    series = [[thousandths(z) for z in sr.get('values', [])] for sr in ch.get('series', [])]
    allv = [z for sv in series for z in sv if z is not None]
    frm, to = thousandths(ch['from']), thousandths(ch['to'])
    if not allv or to <= frm:
        return
    lo, hi = min(allv), max(allv)
    if lo == hi:
        hi = lo + 1000
    ys, yLo, yHi = axis_ticks(lo, hi)
    xs = 1
    while True:
        done = False
        for m in (1, 2, 5):
            if (to - frm) // (xs * m) <= 8:
                xs *= m
                done = True
                break
        if done:
            break
        xs *= 10
    yticks = list(range(yLo, yHi + 1, ys))
    left = P + names[3] + max(f.text_width(value_text(v), Sc) for v in yticks) + Sc // 3
    top = y0c + lc // 2
    bottom = y1 - lc - Sc // 4
    pw, ph = W - P - left, bottom - top
    if pw <= 0 or ph <= 0:
        return
    axis_names(f, Sc, ac, P, left, top, pw, ph, names, text)

    def Y(v):
        return bottom - (v - yLo) * ph // (yHi - yLo)

    def X(v):
        return left + (v - frm) * pw // (to - frm)
    for v in yticks:
        rect(left, Y(v) - 4762, pw, 9525, '808080' if v == 0 else 'D9D9D9')
        tx = value_text(v)
        text(left - Sc // 3 - f.text_width(tx, Sc), Y(v) + (ac - dc) // 2, Sc, tx)
    v = -((-frm) // xs) * xs
    while v <= to:
        rect(X(v) - 4762, top, 9525, ph, '808080' if v == 0 else 'D9D9D9')
        tx = value_text(v)
        text(X(v) - f.text_width(tx, Sc) // 2, bottom + Sc // 4 + ac, Sc, tx)
        v += xs
    for j, sv in enumerate(series):
        N = len(sv) - 1
        c = CHART_COLORS[j % 8]
        run = []
        for k in range(N + 2):
            z = sv[k] if k <= N else None
            if z is not None:
                run.append((left + k * pw // N, Y(z)))
            else:
                if len(run) >= 2:
                    flat = []
                    for px, py in run:
                        flat += [x0 + px, y0 + py]
                    out.append(['line', 19050] + flat + [c])
                run = []


# ---------------------------------------------------------------- layout
def prepare(doc, paras, numbers, seen=None, body=False, cell=False):
    """Items of all paragraphs, with box sizes and natural advances."""
    res = []
    for p in paras:
        items = build_items(doc, p, numbers, seen, body, cell)
        for it in items:
            if it.kind == 'pic':
                it.dims = (it.obj['width'], it.obj['height'], 0)
            elif it.kind == 'chart':
                it.dims = (it.obj['width'], it.obj['height'], 0)
            elif it.kind == 'eq':
                it.dims, it.pieces = equation_box(doc, it.obj, it.fmt, it.block)
            it.adv = natural_advance(doc, it, numbers)
        res.append(items)
    return res


class TableRow:
    """A row of a table: height, and per cell that starts in it its column,
    width, lines and number of rows (section 6, Tables). Lines carry their
    offset below the top of the row."""

    def __init__(self, r, h, cells):
        self.r, self.h, self.cells = r, h, cells
        self.y = 0
        self.tbl = None      # all rows of the table
        self.tx = 0          # left edge of the table
        self.tied = False    # tied to the row below


def _whole(v):
    return v if isinstance(v, int) and not isinstance(v, bool) else 1


def table_grid(tb):
    """Cells of a table as (row, column, paragraphs, cols, rows), in the order
    of the rows and columns they start at (section 2.2)."""
    nc, nr = len(tb['columns']), len(tb['rows'])
    covered = set()
    out = []
    for r in range(nr):
        row = tb['rows'][r]
        for c in range(nc):
            if (r, c) in covered:
                continue
            e = row[c] if isinstance(row, list) and c < len(row) else None
            cs = rs = 1
            if isinstance(e, dict):
                paras = e.get('paras')
                cs, rs = _whole(e.get('cols', 1)), _whole(e.get('rows', 1))
            else:
                paras = e
            if not isinstance(paras, list) or not paras:
                paras = [{}]
            cs = max(1, min(cs, nc - c))
            while cs > 1 and any((r, c + k) in covered for k in range(cs)):
                cs -= 1
            rs = max(1, min(rs, nr - r))
            for i in range(rs):
                for k in range(cs):
                    covered.add((r + i, c + k))
            out.append((r, c, paras, cs, rs))
    return out


def layout_table(doc, para):
    """Rows of a table paragraph with the lines of every cell."""
    st = doc.pstyle(para)
    tb = para['table']
    cols = tb['columns']
    tw = sum(cols)
    room_ = doc.W - tw
    align = para.get('align', st['align'])
    if room_ <= 0 or align in ('left', 'justify'):
        shift = 0
    elif align == 'center':
        shift = room_ // 2
    else:
        shift = room_
    tleft = doc.page['left'] + shift
    nr = len(tb['rows'])
    rows = [TableRow(r, 72000, []) for r in range(nr)]
    spans = []
    for r, c, cell, cs, rs in table_grid(tb):
        cx = tleft + sum(cols[:c])
        cw = sum(cols[c:c + cs])
        items = prepare(doc, cell, {}, None, False, True)
        placed = []
        yy = 36000
        for k, (cp, its) in enumerate(zip(cell, items)):
            cst = doc.pstyle(cp)
            if k > 0:
                yy += doc.pval(cp, 'before')
            for L in layout_paragraph(doc, cp, its, 0, None, cx - doc.page['left'] + 72000,
                                      max(0, cw - 144000)):
                placed.append((k, L, yy, its))
                yy += L.h
            if k < len(cell) - 1:
                yy += doc.pval(cp, 'after')
        content = min(yy - 36000, 1073741823)
        rows[r].cells.append((c, cx, cw, placed, rs))
        if rs == 1:
            rows[r].h = max(rows[r].h, content + 72000)
        else:
            spans.append((r, rs, content + 72000))
        if rs > 1:
            for i in range(rs - 1):
                rows[r + i].tied = True
    for r, rs, need in spans:
        have = sum(rows[r + i].h for i in range(rs))
        if need > have:
            rows[r + rs - 1].h = min(rows[r + rs - 1].h + need - have, 1073741823)
    for row in rows:
        row.tbl = rows
        row.tx = tleft
    return rows


def table_blocks(rows):
    """Rows grouped in blocks: a row is joined to the one above if that is tied to it."""
    blocks = []
    for row in rows:
        if blocks and blocks[-1][-1].tied:
            blocks[-1].append(row)
        else:
            blocks.append([row])
    return blocks


class Floating:
    """A floating picture once it is placed on a page (section 6)."""

    def __init__(self, pi, k, obj, py, left):
        self.pi, self.k = pi, k
        self.wrap = obj['wrap']
        self.dx = obj.get('dx', 0)
        self.w, self.h = obj['width'], obj['height']
        self.y = py
        self.x = left + self.dx


def room(floats, W, t, h):
    """Section 6: (fl, fr, free, beside) for a line with top t and height h."""
    fl, fr, down, beside = 0, W, t, None
    for f in floats:
        if f.wrap not in ('topbottom', 'square'):
            continue
        b0, b1 = f.y - 127000, f.y + f.h + 127000
        if t >= b1 or t + h <= b0:
            continue
        if f.wrap == 'topbottom':
            down = max(down, b1)
            continue
        pl, pr = f.dx - 127000, f.dx + f.w + 127000
        if pl >= fr or pr <= fl:
            continue
        beside = b1 if beside is None else min(beside, b1)
        if min(fr, pl) - fl >= fr - max(fl, pr):
            fr = min(fr, pl)
        else:
            fl = max(fl, pr)
    if down > t:
        free = down
    elif (fl > 0 or fr < W) and fr - fl < 914400:
        free = beside
    else:
        free = t
    return fl, fr, free, beside


# ------------------------------------------------- section 6, Giving room
def has_edges(doc, para):
    """The paragraph has a line at one of its edges (2.2 borders)."""
    if 'table' in para:
        return False
    b = para.get('borders')
    if not isinstance(b, dict):
        b = doc.pstyle(para).get('borders')
    if not isinstance(b, dict):
        return False
    for k in ('top', 'bottom', 'left', 'right'):
        v = b.get(k, 0)
        if isinstance(v, bool) or not isinstance(v, (int, float)):
            continue
        if isinstance(v, float):
            if not v.is_integer():
                continue
            v = int(v)
        if v > 0:
            return True
    return False


def make_gaps(U, u, low, virt=None, pics=()):
    """U: list of (paragraph, top, height, blank). Gaps among the first u units."""
    gaps = []
    prev = None
    for idx in range(u):
        if U[idx][3]:
            continue
        if prev is not None and U[prev][0] != U[idx][0]:
            a, b = prev, idx
            size = max(0, U[b][1] - (U[a][1] + U[a][2]))
            gaps.append({'a': a, 'b': b, 'size': size, 'atop': U[a][1], 'btop': U[b][1]})
        prev = idx
    if virt is not None:
        last = None
        for idx in range(u):
            if not U[idx][3]:
                last = idx
        if last is not None:
            gaps.append({'a': last, 'b': u, 'size': max(0, virt - (U[last][1] + U[last][2])),
                         'atop': U[last][1], 'btop': virt})
    pos = sorted(g['size'] for g in gaps if g['size'] > 0)
    usual = pos[len(pos) // 2] if pos else 0
    for g in gaps:
        size = g['size']
        gg = 400 if size > usual else 200
        g['least'] = size - size * gg // 1000
        if size > 2 * usual:
            g['most'] = size
        else:
            m = 250 if 2 * size < usual else 400
            g['most'] = size + size * m // 1000
    for E, pp, py_, hasu in pics:
        for g in gaps:
            if g['a'] >= pp and g['atop'] < E:
                if g['btop'] < E:
                    g['least'] = g['most'] = g['size']
                else:
                    g['least'] = max(g['least'], g['size'] - (g['btop'] - E))
        if hasu:
            lastg = None
            for g in gaps:
                if g['b'] <= pp:
                    lastg = g
            if lastg is not None:
                g = lastg
                g['least'] = max(g['least'], g['size'] - max(0, py_ - (g['atop'] + U[g['a']][2])))
    return gaps


def shrink_by(gaps, n):
    if n <= 0 or sum(g['size'] - g['least'] for g in gaps) < n:
        return None
    def f(L):
        return sum(g['size'] - max(g['least'], min(g['size'], L)) for g in gaps)
    lo, hi = 0, max(g['size'] for g in gaps)
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if f(mid) >= n:
            lo = mid
        else:
            hi = mid
    return [max(g['least'], min(g['size'], lo)) for g in gaps]


def stretch_by(gaps, n):
    c = sum(1 for g in gaps if g['most'] > g['size'])
    n2 = n - c + 1
    if n2 <= 0:
        return [g['size'] for g in gaps]
    if sum(g['most'] - g['size'] for g in gaps) < n2:
        return None
    def f(L):
        return sum(min(g['most'], max(g['size'], L)) - g['size'] for g in gaps)
    lo, hi = -1, max(g['most'] for g in gaps)
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if f(mid) >= n2:
            hi = mid
        else:
            lo = mid
    return [min(g['most'], max(g['size'], hi)) for g in gaps]


def moved_tops(U, u, gaps, news):
    """New tops of the first u units when the gaps get their new sizes."""
    byb = {g['b']: (g, nw) for g, nw in zip(gaps, news)}
    inside = {}
    for g, nw in zip(gaps, news):
        for q in range(g['a'] + 1, g['b']):
            inside[q] = (g, nw)
    tops = []
    d = 0
    for idx in range(u):
        top = U[idx][1]
        if idx in byb:
            g, nw = byb[idx]
            d += nw - g['size']
            tops.append(top + d)
        elif idx in inside:
            g, nw = inside[idx]
            a = g['a']
            r = top - (U[a][1] + U[a][2])
            term = 0 if g['size'] == 0 else (r * nw) // g['size']
            tops.append(tops[a] + U[a][2] + term)
        else:
            tops.append(top + d)
    return tops


def compute_layout(doc):
    c = doc.c
    body = c['body']
    W = doc.W
    # numbering of footnotes: order of references in the body
    numbers = {}
    for p in body:
        for run in p.get('runs', []):
            if 'note' in run and run['note'] not in numbers:
                numbers[run['note']] = len(numbers) + 1
    seen = set()
    # numbers of cited sources: order of first citation in the body (section 4.5)
    for p in body:
        for run in p.get('runs', []):
            sid = run.get('cite') if 'cite' in run else None
            if isinstance(sid, str) and sid in doc.sources and sid not in doc.cite_numbers:
                doc.cite_numbers[sid] = len(doc.cite_numbers) + 1
    bitems = prepare(doc, body, numbers, seen, True)
    lnum = list_numbers(body)
    lmarks = [list_marker(doc, p, it, n) if n is not None else None
              for p, it, n in zip(body, bitems, lnum)]
    blines = [layout_table(doc, p) if 'table' in p else layout_paragraph(doc, p, it, 0, lm, toc=True)
              for p, it, lm in zip(body, bitems, lmarks)]
    # footnotes
    notes = {}
    for nt in c.get('notes', []):
        if nt['id'] in numbers and nt['id'] not in notes:
            notes[nt['id']] = nt
    nlines = {}   # id -> list of (paragraph, items, lines, mark)
    nheight = {}
    for nid, nt in notes.items():
        paras = nt['paragraphs']
        its = prepare(doc, paras, numbers)
        res = []
        h = 0
        for k, (p, it) in enumerate(zip(paras, its)):
            st = doc.pstyle(p)
            mark = 0
            if k == 0:
                f = doc.fmt(st, None, None)
                mark = number_width(doc, f, numbers[nid]) + doc.font_of(f).adv(0x20, f.size * 6350)
            ls = layout_paragraph(doc, p, it, mark, None)
            res.append((p, it, ls, mark))
            h += sum(l.h for l in ls) + doc.pval(p, 'after')
        nlines[nid] = res
        nheight[nid] = h
    pg = doc.page
    top = pg['top']
    bottom = pg['height'] - pg['bottom']
    left = pg['left']

    pages = []

    class Pg:
        pass

    def new_page():
        p = Pg()
        p.lines = []     # (para index, Line or TableRow)
        p.cells = []     # (para index, TableRow)
        p.floats = []
        p.notes = []
        p.area = 0
        p.given = False
        p.meta = []      # per unit: (area before it, number of notes before it)
        pages.append(p)
        return p
    cur = new_page()
    y = top
    placed = set()
    widow = doc.c.get('widowControl') is True
    adapt = doc.c.get('adaptSpacing') is True

    def line_notes(pi, L):
        out = []
        for k in range(L.s, L.e):
            it = bitems[pi][k]
            if it.kind == 'note' and it.note in notes:
                out.append(it.note)
        return out

    # ---- Giving room (section 6)
    def begins_section(j):
        b = body[j]
        return (j == 0 or (b.get('together') is True and 'table' not in b)
                or bool(b.get('pageBreakBefore')) or bool(doc.pstyle(b)['keepNext']))

    def section_end(i):
        e = i
        while (e + 1 < len(body) and not body[e + 1].get('pageBreakBefore')
               and (not begins_section(e + 1) or doc.pstyle(body[e])['keepNext'])):
            e += 1
        return e

    def may_give():
        return adapt and not cur.given

    def low_():
        return max([f.y + f.h + 127000 for f in cur.floats if f.wrap in ('topbottom', 'square')] or [0])

    def unit_data():
        U = []
        for pi_, L_ in cur.lines:
            bl = ('table' not in body[pi_] and not bitems[pi_] and L_.mark is None
                  and not has_edges(doc, body[pi_]))
            U.append((pi_, L_.y, L_.h, bl))
        return U

    def hole_val():
        L_ = cur.lines[-1][1]
        return bottom - cur.area - (L_.y + L_.h)

    def hole_ok():
        return hole_val() >= cur.lines[-1][1].h

    def unit_of(f):
        for q, (pi_, L_) in enumerate(cur.lines):
            if pi_ == f.pi and 'table' not in body[pi_] and L_.s <= f.k < L_.e:
                return q
        return None

    def pics_():
        out = []
        for f in cur.floats:
            if f.wrap in ('topbottom', 'square'):
                q = unit_of(f)
                out.append((f.y + f.h + 127000, 0 if q is None else q, f.y, q is not None))
        return out

    def commit(tops):
        U = unit_data()
        for f in cur.floats:
            q = unit_of(f)
            if q is not None and q < len(tops):
                f.y += tops[q] - U[q][1]
        for (pi_, L_), ty_ in zip(cur.lines, tops):
            L_.y = ty_

    def keep_foot(u_):
        L_ = cur.lines[u_ - 1][1]
        v = L_.y + L_.h
        for f in cur.floats:
            if f.wrap in ('topbottom', 'square'):
                q = unit_of(f)
                if q is not None and q < u_:
                    v = max(v, f.y + f.h)
        return v

    def trailing(j):
        return (adapt and len(body) > 1 and j == len(body) - 1 and 'table' not in body[j]
                and not bitems[j] and list_kind(body[j]) is None and not has_edges(doc, body[j]))

    def pull_in(i, k, e):
        nonlocal y
        for j in range(i, e + 1):
            if 'table' in body[j] or any(it.kind == 'fpic' for it in bitems[j]):
                return False
        if y < low_():
            return False
        at = y
        newn = []
        for j in range(i, e + 1):
            if trailing(j):
                continue
            if j > i:
                at += doc.pval(body[j - 1], 'after')
            if j > i or k == 0:
                at += doc.pval(body[j], 'before')
            for L_ in (blines[j][k:] if j == i else blines[j]):
                at += L_.h
                for nt_ in line_notes(j, L_):
                    if nt_ not in cur.notes and nt_ not in newn:
                        newn.append(nt_)
        area_ = cur.area
        if newn:
            area_ = (cur.area if cur.notes else 152400) + sum(nheight[q] for q in newn)
        need = at - (bottom - area_)
        if need <= 0:
            return False
        U = unit_data()
        virt = y + doc.pval(body[i], 'before') if k == 0 else None
        gaps = make_gaps(U, len(U), low_(), virt, pics_())
        news = shrink_by(gaps, need)
        if news is None:
            return False
        tops = moved_tops(U, len(U), gaps, news)
        y += sum(nw - g['size'] for g, nw in zip(gaps, news))
        commit(tops)
        cur.given = True
        return True

    def first_unit(p_):
        for q, (pi_, L_) in enumerate(cur.lines):
            if pi_ == p_ and 'table' not in body[p_] and L_.s == 0:
                return q
        return None

    def push_over(i):
        a = i
        while (a - 1 >= 0 and 'table' not in body[a - 1] and doc.pstyle(body[a - 1])['keepNext']
               and first_unit(a - 1) is not None):
            a -= 1
        fa = first_unit(a)
        if fa is None or fa == 0 or cur.lines[fa][1].y < low_():
            return None
        for j in range(a, i + 1):
            if any(it.kind == 'fpic' for it in bitems[j]):
                return None
        tot = 0
        for j in range(a, i + 1):
            tot += sum(L_.h for L_ in blines[j])
            if j < i:
                tot += doc.pval(body[j], 'after')
            if j > a:
                tot += doc.pval(body[j], 'before')
        if tot > bottom - top:
            return None
        U = unit_data()
        u = fa
        n = bottom - cur.meta[fa][0] - keep_foot(u)
        gaps = make_gaps(U, u, low_(), None, pics_())
        news = stretch_by(gaps, n)
        if news is None:
            return None
        tops = moved_tops(U, u, gaps, news)
        commit(tops)
        cur.area = cur.meta[fa][0]
        del cur.notes[cur.meta[fa][1]:]
        del cur.lines[fa:]
        del cur.meta[fa:]
        cur.given = True
        return a

    def stretch_hole():
        U = unit_data()
        gaps = make_gaps(U, len(U), low_(), None, pics_())
        news = stretch_by(gaps, bottom - cur.area - keep_foot(len(cur.lines)))
        if news is None:
            return False
        commit(moved_tops(U, len(U), gaps, news))
        cur.given = True
        return True

    def only_objects(i):
        its = bitems[i]
        return any(it.u == 0xFFFC for it in its) and all(it.u in (0xFFFC, 9, 0x20, 0xA0, 0x2028) for it in its)

    def try_step1(i):
        if may_give() and hole_ok():
            return pull_in(i, 0, section_end(i))
        return False

    pi = -1
    while pi + 1 < len(body):
        pi += 1
        para = body[pi]
        st = doc.pstyle(para)
        items = bitems[pi]
        lm = lmarks[pi]
        ls = blines[pi]
        if cur.lines and not trailing(pi):
            if para.get('pageBreakBefore'):
                cur = new_page()
                y = top
            else:
                if para.get('together') is True and 'table' not in para:
                    e = pi + 1
                    while e < len(body) and not ((body[e].get('together') is True and 'table' not in body[e])
                                                 or body[e].get('pageBreakBefore')):
                        e += 1
                    while e - 1 > pi and not bitems[e - 1] and 'table' not in body[e - 1]:
                        e -= 1
                    tneed = 0
                    for k in range(pi, e):
                        tneed += doc.pval(body[k], 'before') + sum(l.h for l in blines[k])
                        if k < e - 1:
                            tneed += doc.pval(body[k], 'after')
                    troom = bottom - cur.area - y
                    if tneed > troom and 2 * troom < tneed and not try_step1(pi):
                        cur = new_page()
                        y = top
                if cur.lines and st['keepNext'] and pi + 1 < len(body):
                    need = doc.pval(para, 'before') + sum(l.h for l in ls) + doc.pval(para, 'after')
                    nxt = body[pi + 1]
                    if not nxt.get('pageBreakBefore'):
                        nl_ = blines[pi + 1]
                        if widow and 'table' not in nxt:
                            kn = len(nl_)
                            k0 = 3 if kn == 3 else (2 if kn >= 2 else 1)
                        else:
                            k0 = 1
                        need += doc.pval(nxt, 'before') + sum(l.h for l in nl_[:k0])
                    if y + need > bottom - cur.area and not try_step1(pi):
                        cur = new_page()
                        y = top
        if 'table' in para:
            for blk in table_blocks(ls):
                bh = min(sum(r_.h for r_ in blk), 1073741823)
                while True:
                    gap = doc.pval(para, 'before') if blk[0].r == 0 and cur.lines else 0
                    t = y + gap
                    while True:
                        fl, fr, free, beside = room(cur.floats, W, t, bh)
                        if free > t:
                            t = free
                        elif fl != 0 or fr != W:
                            t = beside
                        else:
                            break
                    if cur.lines and t + bh > bottom - cur.area:
                        cur = new_page()
                        y = top
                        continue
                    break
                for row in blk:
                    row.y = t
                    t += row.h
                    cur.lines.append((pi, row))
                    cur.meta.append((cur.area, len(cur.notes)))
                    cur.cells.append((pi, row))
                y = t
            y += doc.pval(para, 'after')
            continue
        s = 0
        restart = None
        while True:
            # a. the trial line; b. the top; c. floating pictures; d. room; e. footnotes
            while True:
                trial = make_line(doc, para, items, s, 0, W, 0, lm, True)
                gap = doc.pval(para, 'before') if s == 0 and cur.lines else 0
                t = y + gap
                newpage = False
                for k in range(trial.s, trial.e):
                    it = items[k]
                    if it.kind != 'fpic' or (pi, k) in placed:
                        continue
                    dy = it.obj.get('dy', 0)
                    if it.obj['wrap'] in ('topbottom', 'square'):
                        py = t + max(0, dy)
                        if cur.lines and py + it.obj['height'] > bottom:
                            cur = new_page()
                            y = top
                            newpage = True
                            break
                    else:
                        py = t + dy
                    cur.floats.append(Floating(pi, k, it.obj, py, left))
                    placed.add((pi, k))
                if newpage:
                    continue
                while True:
                    fl, fr, free, beside = room(cur.floats, W, t, trial.h)
                    if free > t:
                        t = free
                        continue
                    if fl == 0 and fr == W:
                        line = trial
                        break
                    cand = make_line(doc, para, items, s, fl, fr - fl, 0, lm, True)
                    ok = cand.m <= cand.avail
                    if ok and cand.h > trial.h:
                        r = room(cur.floats, W, t, cand.h)
                        ok = r[2] == t and r[0] == fl and r[1] == fr
                    if ok:
                        line = cand
                        break
                    t = beside
                ln = line_notes(pi, line)
                area2 = cur.area
                if ln:
                    area2 = (cur.area if cur.notes else 152400) + sum(nheight[k] for k in ln)
                th, ta = line.h, area2
                if widow and cur.lines and line is trial:
                    F = ls
                    kk = None
                    for q_, fl_ in enumerate(F):
                        if fl_.s == trial.s:
                            kk = q_
                            break
                    if kk is not None:
                        nn = len(F)
                        if kk == 0 and nn == 3:
                            keep = 3
                        elif (kk == 0 and nn >= 2) or kk == nn - 2:
                            keep = 2
                        else:
                            keep = 1
                        extra = F[kk + 1:kk + keep]
                        th += sum(l.h for l in extra)
                        en = []
                        for l in extra:
                            for nt_ in line_notes(pi, l):
                                if nt_ not in cur.notes and nt_ not in ln and nt_ not in en:
                                    en.append(nt_)
                        if en:
                            ta = (cur.area if cur.notes else 152400) + sum(nheight[k] for k in ln + en)
                if cur.lines and t + th > bottom - ta:
                    if trailing(pi):
                        t = max(top, bottom - cur.area - line.h)
                        break
                    if may_give():
                        kF = None
                        if line is trial:
                            for q_, fl_ in enumerate(ls):
                                if fl_.s == trial.s:
                                    kF = q_
                                    break
                        whole = kF is not None
                        opens = s == 0 and begins_section(pi)
                        byhand = s > 0 and items[s - 1].kind == 'br'
                        hok = hole_ok()
                        done = False
                        if whole and (not opens or hok):
                            e_ = section_end(pi)
                            done = pull_in(pi, kF, e_)
                            lk = list_kind(para)
                            tried_i = False
                            if (not done and lk is not None
                                    and (s > 0 or (cur.lines[-1][0] == pi - 1
                                                   and list_kind(body[pi - 1]) == lk))):
                                l_ = pi
                                while l_ < e_ and list_kind(body[l_ + 1]) == lk:
                                    l_ += 1
                                if l_ < e_:
                                    tried_i = l_ == pi
                                    done = pull_in(pi, kF, l_)
                            if (not done and not tried_i and e_ > pi and ((s > 0 and not byhand)
                                                         or (s == 0 and only_objects(pi)))):
                                done = pull_in(pi, kF, pi)
                        if done:
                            continue
                        if whole and s > 0 and not byhand:
                            a_ = push_over(pi)
                            if a_ is not None:
                                restart = a_
                                cur = new_page()
                                y = top
                                break
                        if not opens and hok:
                            stretch_hole()
                    cur = new_page()
                    y = top
                    continue
                break
            if restart is not None:
                break
            line.y = t
            y = t + line.h
            cur.lines.append((pi, line))
            cur.meta.append((cur.area, len(cur.notes)))
            if ln:
                cur.notes += ln
                cur.area = area2
            if not more_lines(items, line):
                break
            s = line.e
        if restart is not None:
            pi = restart - 1
            continue
        y += doc.pval(para, 'after')
    # build the output structure
    out_pages = []
    for p in pages:
        lines = []
        objects = []
        for f in p.floats:
            objects.append({'kind': 'picture', 'wrap': f.wrap, 'p': f.pi, 'i': f.k, 'x': f.x, 'y': f.y,
                            'w': f.w, 'h': f.h, 'b': f.y + f.h})
        for pi, L in p.lines:
            if isinstance(L, TableRow):
                lines.append({'p': pi, 's': 0, 'n': 0, 'x': L.tx, 'y': L.y, 'h': L.h,
                              'b': L.y + L.h, 'row': L.r, 'a': []})
                continue
            x = left + L.x
            d = {'p': pi, 's': L.s, 'n': L.n, 'x': x, 'y': L.y, 'h': L.h,
                 'b': L.y + L.base, 'a': list(L.a), 'hy': L.hy}
            if L.mark is not None:
                d['mark'] = {'x': left + L.mark[0], 'text': L.mark[1]}
            lines.append(d)
            xx = x
            for k in range(L.s, L.e):
                it = bitems[pi][k]
                if it.box:
                    b = L.y + L.base
                    w, h, dd = it.dims
                    o = {'kind': {'pic': 'picture', 'eq': 'equation', 'chart': 'chart'}[it.kind],
                         'p': pi, 'i': k, 'x': xx, 'y': b - h, 'w': w, 'h': h + dd, 'b': b}
                    if it.kind == 'eq':
                        o['draw'] = equation_draw(it.pieces, xx, b, it.fmt.color)
                    elif it.kind == 'chart':
                        o['draw'] = chart_draw(doc, it.obj, it.fmt, xx, b - h)
                    objects.append(o)
                xx += L.a[k - L.s]
        page = {'lines': lines, 'objects': objects}
        if p.cells:
            cl = []
            for pi, row in p.cells:
                for c, cx, w, placed, rs in row.cells:
                    cl.append({'p': pi, 'r': row.r, 'c': c, 'x': cx, 'y': row.y, 'w': w,
                               'h': min(sum(row.tbl[row.r + i].h for i in range(rs)), 1073741823),
                               'lines': [{'p': k, 's': L.s, 'n': L.n, 'x': left + L.x, 'y': row.y + off,
                                          'h': L.h, 'b': row.y + off + L.base, 'a': list(L.a), 'hy': L.hy}
                                         for k, L, off, its in placed]})
                    objs = []
                    for k, L, off, its in placed:
                        xx = left + L.x
                        b = row.y + off + L.base
                        for q in range(L.s, L.e):
                            it = its[q]
                            if it.box:
                                w, h, dd = it.dims
                                o = {'kind': {'pic': 'picture', 'eq': 'equation', 'chart': 'chart'}[it.kind],
                                     'p': k, 'i': q, 'x': xx, 'y': b - h, 'w': w, 'h': h + dd, 'b': b}
                                if it.kind == 'eq':
                                    o['draw'] = equation_draw(it.pieces, xx, b, it.fmt.color)
                                elif it.kind == 'chart':
                                    o['draw'] = chart_draw(doc, it.obj, it.fmt, xx, b - h)
                                objs.append(o)
                            xx += L.a[q - L.s]
                    if objs:
                        cl[-1]['objects'] = objs
            page['cells'] = cl
        if p.notes:
            area_top = bottom - p.area
            ny = area_top + 101600
            ty = area_top + 152400
            nitems = []
            for nid in p.notes:
                nl = []
                for k, (para, its, ls, mark) in enumerate(nlines[nid]):
                    st = doc.pstyle(para)
                    for L in ls:
                        nl.append({'p': k, 's': L.s, 'n': L.n, 'x': left + L.x, 'y': ty, 'h': L.h,
                                   'b': ty + L.base, 'a': list(L.a), 'hy': L.hy})
                        ty += L.h
                    ty += doc.pval(para, 'after')
                nitems.append({'id': nid, 'number': numbers[nid], 'lines': nl})
            page['notes'] = {'y': ny, 'items': nitems}
        out_pages.append(page)
    add_bands(doc, out_pages)
    return {'pages': out_pages}


def band_texts(hf, band):
    """The three texts (left, centre, right) of the header or footer."""
    b = hf.get(band) or {}
    return [(pos, b.get(pos) or '') for pos in ('left', 'centre', 'right')]


def band_picture(hf, band):
    """The picture of the header or footer, or None (a picture without a media name is not there)."""
    p = (hf.get(band) or {}).get('picture')
    if not isinstance(p, dict) or not p.get('media'):
        return None
    place = p.get('place') if p.get('place') in ('left', 'centre', 'right') else 'left'
    return p['media'], p['width'], p['height'], place


def band_sources(hf):
    """The header/footer dicts that count: the usual one, and that of the first page with skipFirstPage."""
    if hf.get('skipFirstPage'):
        return [hf, hf.get('first') or {}]
    return [hf]


def band_used(src, band):
    return any(t for _, t in band_texts(src, band)) or band_picture(src, band) is not None


def add_bands(doc, out_pages):
    """Header and footer texts and pictures of every page (end of section 6)."""
    hf = doc.c.get('headerFooter')
    if not hf:
        return
    pg = doc.page
    W = doc.W
    count = len(out_pages)
    for band, style_id in (('header', 'Header'), ('footer', 'Footer')):
        if not any(band_used(src, band) for src in band_sources(hf)):
            continue
        st = doc.pst.get(style_id, doc.first_style)
        fmt = doc.fmt(st, None, None)
        font = doc.font_of(fmt)
        S = fmt.size * 6350
        asc, desc = font.asc(S), font.desc(S)
        P = asc + desc
        for k, page in enumerate(out_pages):
            src = hf.get('first') or {} if k == 0 and hf.get('skipFirstPage') else hf
            texts = band_texts(src, band)
            pic = band_picture(src, band)
            lines = {}
            for pos, raw in texts:
                raw = raw.replace('{pages}', str(count)).replace('{page}', str(k + 1)) if raw else ''
                lines[pos] = raw.split(chr(10))[:8] if raw else []
            n = max(1, max(len(v) for v in lines.values()))
            top = 457200 if band == 'header' else pg['height'] - 457200 - n * P
            for pos, _ in texts:
                ls = lines[pos]
                ws = [font.text_width(t, S) if t else 0 for t in ls]
                w = max(ws, default=0)
                show = any(ls)
                s0 = None
                if pic is not None and pos == pic[3]:
                    media, pw, ph, _ = pic
                    g = pw + 114300 + w if show else pw
                    if pos == 'left':
                        s0 = pg['left']
                    elif pos == 'centre':
                        s0 = pg['left'] + (W - g) // 2
                    else:
                        s0 = pg['left'] + W - g
                    page.setdefault('bandPictures', []).append({
                        'band': band, 'media': media, 'x': s0 + g - pw if pos == 'right' else s0,
                        'y': top + (n * P - ph) // 2, 'w': pw, 'h': ph})
                for j, t in enumerate(ls):
                    if not t:
                        continue
                    if s0 is not None:
                        x = s0 + w - ws[j] if pos == 'right' else s0 + pw + 114300
                    elif pos == 'left':
                        x = pg['left']
                    elif pos == 'centre':
                        x = pg['left'] + (W - ws[j]) // 2
                    else:
                        x = pg['left'] + W - ws[j]
                    page.setdefault('bands', []).append({'band': band, 'x': x, 'b': top + j * P + asc, 'text': t})


# ---------------------------------------------------------------- compare
class Report:
    def __init__(self, first):
        self.first = first
        self.diffs = []
        self.lines = self.lines_bad = 0
        self.objs = self.objs_bad = 0
        self.other = 0
        self.bands = 0
        self.band_pics = 0
        self.cells = self.cells_bad = 0
        self.cell_objs = 0

    def add(self, msg):
        self.diffs.append(msg)


def cmp_line(rep, where, exp, got):
    rep.lines += 1
    bad = []
    for k in ('p', 's', 'n', 'x', 'y', 'h', 'b', 'row', 'mark'):
        if exp.get(k) != got.get(k):
            bad.append('%s expected %r computed %r' % (k, exp.get(k), got.get(k)))
    if bool(exp.get('hy', False)) != bool(got.get('hy', False)):
        bad.append('hy expected %r computed %r' % (bool(exp.get('hy', False)), bool(got.get('hy', False))))
    ea, ga = exp.get('a', []), got['a']
    if ea != ga:
        if len(ea) != len(ga):
            bad.append('a length expected %d computed %d' % (len(ea), len(ga)))
        else:
            ks = [k for k in range(len(ea)) if ea[k] != ga[k]]
            bad.append('a[%d..] (%d differ) expected %r computed %r' % (ks[0], len(ks), ea[ks[0]], ga[ks[0]]))
    if bad:
        rep.lines_bad += 1
        rep.add('%s: %s' % (where, '; '.join(bad)))


def cmp_cell(rep, where, exp, got):
    rep.cells += 1
    bad = ['%s expected %r computed %r' % (k, exp.get(k), got.get(k))
           for k in ('p', 'r', 'c', 'x', 'y', 'w', 'h') if exp.get(k) != got.get(k)]
    la, lb = exp.get('lines', []), got['lines']
    if len(la) != len(lb):
        bad.append('line count expected %d computed %d' % (len(la), len(lb)))
    before = len(rep.diffs)
    for k in range(min(len(la), len(lb))):
        cmp_line(rep, '%s line %d' % (where, k), la[k], lb[k])
    if bad:
        rep.add('%s: %s' % (where, '; '.join(bad)))
    oa, ob = exp.get('objects', []), got.get('objects', [])
    if len(oa) != len(ob):
        bad.append('object count expected %d computed %d' % (len(oa), len(ob)))
    for k in range(min(len(oa), len(ob))):
        rep.cell_objs += 1
        cmp_obj(rep, '%s object %d (%s p=%s i=%s)' % (where, k, oa[k].get('kind'), oa[k].get('p'), oa[k].get('i')),
                oa[k], ob[k])
    if bad or len(rep.diffs) > before:
        rep.cells_bad += 1


def cmp_obj(rep, where, exp, got):
    rep.objs += 1
    bad = []
    for k in ('kind', 'wrap', 'p', 'i', 'x', 'y', 'w', 'h', 'b'):
        if exp.get(k) != got.get(k):
            bad.append('%s expected %r computed %r' % (k, exp.get(k), got.get(k)))
    ed, gd = exp.get('draw'), got.get('draw')
    if ed != gd:
        if ed is None or gd is None:
            bad.append('draw expected %s computed %s' % ('present' if ed is not None else 'absent',
                                                          'present' if gd is not None else 'absent'))
        else:
            if len(ed) != len(gd):
                bad.append('draw length expected %d computed %d' % (len(ed), len(gd)))
            for k in range(min(len(ed), len(gd))):
                if ed[k] != gd[k]:
                    nd = sum(1 for z in range(min(len(ed), len(gd))) if ed[z] != gd[z])
                    bad.append('draw[%d] (%d entries differ) expected %r computed %r' % (k, nd, ed[k], gd[k]))
                    break
    if bad:
        rep.objs_bad += 1
        rep.add('%s: %s' % (where, '; '.join(bad)))


def compare(rep, stored, comp):
    sp, cp = stored['pages'], comp['pages']
    if len(sp) != len(cp):
        rep.other += 1
        rep.add('page count expected %d computed %d' % (len(sp), len(cp)))
    for pn in range(min(len(sp), len(cp))):
        a, b = sp[pn], cp[pn]
        la, lb = a.get('lines', []), b['lines']
        if len(la) != len(lb):
            rep.other += 1
            rep.add('page %d: line count expected %d computed %d' % (pn, len(la), len(lb)))
        for k in range(min(len(la), len(lb))):
            cmp_line(rep, 'page %d line %d (p=%s)' % (pn, k, la[k].get('p')), la[k], lb[k])
        oa, ob = a.get('objects', []), b['objects']
        if len(oa) != len(ob):
            rep.other += 1
            rep.add('page %d: object count expected %d computed %d' % (pn, len(oa), len(ob)))
        for k in range(min(len(oa), len(ob))):
            cmp_obj(rep, 'page %d object %d (%s p=%s i=%s)' % (pn, k, oa[k].get('kind'), oa[k].get('p'), oa[k].get('i')),
                    oa[k], ob[k])
        ca, cb = a.get('cells', []), b.get('cells', [])
        if len(ca) != len(cb):
            rep.other += 1
            rep.add('page %d: cell count expected %d computed %d' % (pn, len(ca), len(cb)))
        for k in range(min(len(ca), len(cb))):
            cmp_cell(rep, 'page %d cell %d (p=%s r=%s c=%s)' % (pn, k, ca[k].get('p'), ca[k].get('r'), ca[k].get('c')),
                     ca[k], cb[k])
        ba, bb = a.get('bands', []), b.get('bands', [])
        if len(ba) != len(bb):
            rep.other += 1
            rep.add('page %d: band count expected %d computed %d' % (pn, len(ba), len(bb)))
        for k in range(min(len(ba), len(bb))):
            rep.bands += 1
            bad = ['%s expected %r computed %r' % (f, ba[k].get(f), bb[k].get(f))
                   for f in ('band', 'x', 'b', 'text') if ba[k].get(f) != bb[k].get(f)]
            if bad:
                rep.other += 1
                rep.add('page %d band %d: %s' % (pn, k, '; '.join(bad)))
        pa, pb = a.get('bandPictures', []), b.get('bandPictures', [])
        if len(pa) != len(pb):
            rep.other += 1
            rep.add('page %d: header/footer picture count expected %d computed %d' % (pn, len(pa), len(pb)))
        for k in range(min(len(pa), len(pb))):
            rep.band_pics += 1
            bad = ['%s expected %r computed %r' % (f, pa[k].get(f), pb[k].get(f))
                   for f in ('band', 'media', 'x', 'y', 'w', 'h') if pa[k].get(f) != pb[k].get(f)]
            if bad:
                rep.other += 1
                rep.add('page %d header/footer picture %d: %s' % (pn, k, '; '.join(bad)))
        na, nb = a.get('notes'), b.get('notes')
        if (na is None) != (nb is None):
            rep.other += 1
            rep.add('page %d: notes expected %s computed %s' % (pn, na is not None, nb is not None))
        elif na is not None:
            if na['y'] != nb['y']:
                rep.other += 1
                rep.add('page %d: notes.y expected %r computed %r' % (pn, na['y'], nb['y']))
            ia, ib = na['items'], nb['items']
            if [(i['id'], i['number']) for i in ia] != [(i['id'], i['number']) for i in ib]:
                rep.other += 1
                rep.add('page %d: note ids/numbers expected %r computed %r' % (
                    pn, [(i['id'], i['number']) for i in ia], [(i['id'], i['number']) for i in ib]))
            for q in range(min(len(ia), len(ib))):
                fa, fb = ia[q]['lines'], ib[q]['lines']
                if len(fa) != len(fb):
                    rep.other += 1
                    rep.add('page %d note %s: line count expected %d computed %d' % (pn, ia[q]['id'], len(fa), len(fb)))
                for k in range(min(len(fa), len(fb))):
                    cmp_line(rep, 'page %d note %s line %d' % (pn, ia[q]['id'], k), fa[k], fb[k])


# ---------------------------------------------------------------- manifest
def sha(b):
    return hashlib.sha256(b).hexdigest()


def check_manifest(rep, z, man, doc):
    """Section 3 conditions 1-4 (the fingerprints and the fonts)."""
    for key, want in (('formatVersion', 26), ('layoutRules', 26)):
        if man.get(key) != want:
            rep.other += 1
            rep.add('manifest %s: expected %r found %r' % (key, want, man.get(key)))
    for key, name in (('content', 'content.json'), ('layout', 'layout.json')):
        h = sha(z.read(name))
        if h != man.get(key):
            rep.other += 1
            rep.add('manifest %s: expected %s computed %s' % (key, man.get(key), h))
    entries = {(e['family'], e['bold'], e['italic']): e for e in man.get('fonts', [])}
    for key in sorted(doc.used_fonts):
        e = entries.get(key)
        if e is None:
            rep.other += 1
            rep.add('manifest has no font entry for %r' % (key,))
            continue
        info = pick_face(*key)
        with open(info.path, 'rb') as f:
            h = sha(f.read())
        if h != e['sha256'] or info.index != e['face'] or info.family != e['usedFamily']:
            rep.other += 1
            rep.add('font %r: manifest (%s face %s %s) found (%s face %s %s)' % (
                key, e['usedFamily'], e['face'], e['sha256'][:12], info.family, info.index, h[:12]))
    for key in entries:
        if key not in doc.used_fonts:
            rep.other += 1
            rep.add('manifest lists unused font %r' % (key,))


def note_band_fonts(doc):
    """Fonts of the styles Header and Footer, when that header or footer has a text (section 2.1)."""
    hf = doc.c.get('headerFooter')
    if not hf:
        return
    for band, style_id in (('header', 'Header'), ('footer', 'Footer')):
        if any(band_used(src, band) for src in band_sources(hf)):
            doc.font_of(doc.fmt(doc.pst.get(style_id, doc.first_style), None, None))


def check_file(path, first):
    rep = Report(first)
    z = zipfile.ZipFile(path)
    man = json.loads(z.read('manifest.json'))
    content = json.loads(z.read('content.json'))
    stored = json.loads(z.read('layout.json'))
    doc = Doc(content)
    comp = compute_layout(doc)
    note_band_fonts(doc)
    check_manifest(rep, z, man, doc)
    compare(rep, stored, comp)
    return rep, len(stored['pages']), len(comp['pages'])


def main(argv):
    first = 5
    files = []
    k = 0
    while k < len(argv):
        if argv[k] == '--first':
            first = int(argv[k + 1])
            k += 2
        else:
            files.append(argv[k])
            k += 1
    if not files:
        print(__doc__)
        return 2
    code = 0
    sys.stdout.reconfigure(errors='backslashreplace')
    for f in files:
        try:
            rep, np_, nc = check_file(f, first)
        except Exception as ex:
            import traceback
            traceback.print_exc()
            print('%s: FAILED (%s)' % (f, ex))
            code = 1
            continue
        total = len(rep.diffs)
        print('%s: pages %d (computed %d), lines compared %d, different %d, objects compared %d (of which in cells %d), different %d, cells compared %d, different %d, header/footer texts compared %d, pictures compared %d, other differences %d'
              % (f, np_, nc, rep.lines, rep.lines_bad, rep.objs, rep.cell_objs, rep.objs_bad, rep.cells, rep.cells_bad, rep.bands, rep.band_pics, rep.other))
        for d in rep.diffs[:first]:
            print('   ' + d)
        if total:
            code = 1
    return code


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
