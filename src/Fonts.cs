using System.Globalization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ReiksDocument;

/// <summary>Layout units: 1 unit = 1 EMU = 1/914400 inch = 1/12700 point = 1/36000 mm.</summary>
static class U
{
    public const int PerPoint = 12700, PerHalfPoint = 6350, PerInch = 914400, PerMm = 36000;
    public const double PerDip = 9525.0;

    /// <summary>a / b rounded to nearest, halves up. a >= 0, b > 0.</summary>
    public static long RoundDiv(long a, long b) => (2 * a + b) / (2 * b);

    public static double Dip(long emu) => emu / PerDip;
}

static class BE
{
    public static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    public static int S16(byte[] d, int o) => (short)((d[o] << 8) | d[o + 1]);
    public static uint U32(byte[] d, int o) => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
}

sealed class FaceInfo
{
    public string Path = "";
    public int Index;
    public string Family = "";
    public bool Bold, Italic;
}

/// <summary>One face of a TrueType/OpenType file: the tables the layout rules use.</summary>
sealed class FontFace
{
    public readonly FaceInfo Info;
    readonly byte[] d;
    public readonly int UnitsPerEm, Ascender, Descender, LineGap, UnderlinePos, UnderlineThick, StrikePos, StrikeSize;
    readonly int hmtx, numHM, cmap, cmapFormat, numGlyphs, locFormat;
    readonly int glyf = -1, loca = -1;
    readonly bool symbol;
    readonly Dictionary<int, (ushort Glyph, int Adv)> cache = new();
    string? sha;

    public string Sha256 => sha ??= Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();

    public FontFace(FaceInfo info, byte[] data)
    {
        Info = info;
        d = data;
        int face = 0;
        if (BE.U32(d, 0) == 0x74746366) face = (int)BE.U32(d, 12 + 4 * info.Index);
        var t = new Dictionary<uint, int>();
        int nt = BE.U16(d, face + 4);
        for (int i = 0; i < nt; i++)
        {
            int r = face + 12 + 16 * i;
            t[BE.U32(d, r)] = (int)BE.U32(d, r + 8);
            tables[BE.U32(d, r)] = ((int)BE.U32(d, r + 8), (int)BE.U32(d, r + 12));
        }
        int head = t[Tag("head")], hhea = t[Tag("hhea")];
        hmtx = t[Tag("hmtx")];
        UnitsPerEm = Math.Max(1, BE.U16(d, head + 18));
        numGlyphs = t.TryGetValue(Tag("maxp"), out int maxp) ? BE.U16(d, maxp + 4) : 0;
        if (t.TryGetValue(Tag("glyf"), out int gl) && t.TryGetValue(Tag("loca"), out int lc))
        {
            glyf = gl;
            loca = lc;
            locFormat = BE.S16(d, head + 50);
        }
        Ascender = Math.Max(0, BE.S16(d, hhea + 4));
        Descender = Math.Max(0, -BE.S16(d, hhea + 6));
        LineGap = Math.Max(0, BE.S16(d, hhea + 8));
        numHM = Math.Max(1, BE.U16(d, hhea + 34));
        if (t.TryGetValue(Tag("post"), out int post))
        {
            UnderlinePos = BE.S16(d, post + 8);
            UnderlineThick = BE.S16(d, post + 10);
        }
        if (UnderlineThick <= 0) UnderlineThick = Math.Max(1, UnitsPerEm / 20);
        if (UnderlinePos >= 0) UnderlinePos = -UnitsPerEm / 10;
        if (t.TryGetValue(Tag("OS/2"), out int os2) && os2 + 30 <= d.Length)
        {
            StrikeSize = BE.S16(d, os2 + 26);
            StrikePos = BE.S16(d, os2 + 28);
        }
        if (StrikeSize <= 0) StrikeSize = UnderlineThick;
        XMin = BE.S16(d, head + 36);
        YMin = BE.S16(d, head + 38);
        XMax = BE.S16(d, head + 40);
        YMax = BE.S16(d, head + 42);
        CapHeight = Ascender;
        if (tables.TryGetValue(Tag("OS/2"), out var o2))
        {
            // Embedding is not allowed with a restricted licence, nor when only bitmaps may be embedded.
            int fsType = BE.U16(d, o2.Off + 8);
            MayEmbed = (fsType & 0x0002) == 0 && (fsType & 0x0200) == 0;
            if (BE.U16(d, o2.Off) >= 2 && o2.Len >= 90 && BE.S16(d, o2.Off + 88) > 0) CapHeight = BE.S16(d, o2.Off + 88);
        }
        if (t.TryGetValue(Tag("post"), out int post2)) ItalicAngle = BE.S16(d, post2 + 4);
        string? ps = tables.TryGetValue(Tag("name"), out var nm) ? ReadName(d.AsSpan(nm.Off, nm.Len).ToArray(), 6) : null;
        var clean = new StringBuilder();
        foreach (char c in ps ?? info.Family)
            if (c < 127 && (char.IsLetterOrDigit(c) || c == '-')) clean.Append(c);
        PostScriptName = clean.Length > 0 ? clean.ToString() : "Font";
        if (StrikePos <= 0) StrikePos = UnitsPerEm / 4;

        // cmap subtable: (3,10) then (3,1) then (0,x) then (3,0); formats 4 and 12 only.
        int best = -1, bestRank = 99;
        if (t.TryGetValue(Tag("cmap"), out int cm))
        {
            int n = BE.U16(d, cm + 2);
            for (int i = 0; i < n; i++)
            {
                int pid = BE.U16(d, cm + 4 + 8 * i), eid = BE.U16(d, cm + 6 + 8 * i);
                int off = cm + (int)BE.U32(d, cm + 8 + 8 * i);
                int fmt = BE.U16(d, off);
                if (fmt != 4 && fmt != 12) continue;
                int rank = pid == 3 && eid == 10 ? 0 : pid == 3 && eid == 1 ? 1 : pid == 0 ? (fmt == 12 ? 2 : 3) : pid == 3 && eid == 0 ? 4 : 99;
                if (rank < bestRank) { bestRank = rank; best = off; }
            }
        }
        cmap = best;
        cmapFormat = best >= 0 ? BE.U16(d, best) : 0;
        symbol = bestRank == 4;
    }

    static uint Tag(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    /// <summary>Lowest and highest y of a glyph's outline, in font units, from the glyf table.
    /// (0, 0) for a glyph without outline; descender and ascender if the font has no glyf table.</summary>
    public (int YMin, int YMax) GlyphY(int g)
    {
        if (glyf < 0) return (-Descender, Ascender);
        if (g < 0 || g >= numGlyphs) return (0, 0);
        long a, b;
        if (locFormat == 0) { a = 2L * BE.U16(d, loca + 2 * g); b = 2L * BE.U16(d, loca + 2 * g + 2); }
        else { a = BE.U32(d, loca + 4 * g); b = BE.U32(d, loca + 4 * g + 4); }
        if (b <= a) return (0, 0);
        int o = glyf + (int)a;
        return (BE.S16(d, o + 4), BE.S16(d, o + 8));
    }

    readonly Dictionary<uint, (int Off, int Len)> tables = new();

    /// <summary>For embedding in a PDF: the bounding box of all glyphs, the height of capitals, the slant in degrees,
    /// the name, whether the outlines are TrueType outlines, and whether the licence allows embedding.</summary>
    public readonly int XMin, YMin, XMax, YMax, CapHeight, ItalicAngle;
    public readonly string PostScriptName;
    public readonly bool MayEmbed = true;
    public bool HasOutlines => glyf >= 0;

    (long Start, long End) GlyphRange(int g)
    {
        if (locFormat == 0) return (2L * BE.U16(d, loca + 2 * g), 2L * BE.U16(d, loca + 2 * g + 2));
        return (BE.U32(d, loca + 4 * g), BE.U32(d, loca + 4 * g + 4));
    }

    static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
            sum += ((uint)data[i] << 24) | ((uint)(i + 1 < data.Length ? data[i + 1] : 0) << 16)
                 | ((uint)(i + 2 < data.Length ? data[i + 2] : 0) << 8) | (i + 3 < data.Length ? data[i + 3] : 0u);
        return sum;
    }

    static void Put32(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    /// <summary>A TrueType font file of its own that holds the outlines of the given glyphs only (and of the glyphs they
    /// are built from). Glyph numbers stay the same; all other glyphs are empty. Needs TrueType outlines.</summary>
    public byte[] Subset(IEnumerable<int> glyphs)
    {
        var keep = new HashSet<int>();
        var todo = new Stack<int>(glyphs);
        todo.Push(0);
        while (todo.Count > 0)
        {
            int g = todo.Pop();
            if (g < 0 || g >= numGlyphs || !keep.Add(g)) continue;
            var (a, b) = GlyphRange(g);
            if (b - a < 10 || BE.S16(d, glyf + (int)a) >= 0) continue;
            // A glyph built from other glyphs: those are needed too.
            int p = glyf + (int)a + 10;
            while (p + 4 <= glyf + b)
            {
                int flags = BE.U16(d, p);
                todo.Push(BE.U16(d, p + 2));
                p += 4 + ((flags & 1) != 0 ? 4 : 2);
                if ((flags & 0x08) != 0) p += 2;
                else if ((flags & 0x40) != 0) p += 4;
                else if ((flags & 0x80) != 0) p += 8;
                if ((flags & 0x20) == 0) break;
            }
        }

        var outlines = new MemoryStream();
        var offsets = new byte[4 * (numGlyphs + 1)];
        for (int g = 0; g < numGlyphs; g++)
        {
            Put32(offsets, 4 * g, (uint)outlines.Length);
            if (!keep.Contains(g)) continue;
            var (a, b) = GlyphRange(g);
            if (b <= a) continue;
            outlines.Write(d, glyf + (int)a, (int)(b - a));
            while (outlines.Length % 4 != 0) outlines.WriteByte(0);
        }
        Put32(offsets, 4 * numGlyphs, (uint)outlines.Length);

        var parts = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string tag in new[] { "head", "hhea", "maxp", "hmtx", "cvt ", "fpgm", "prep" })
            if (tables.TryGetValue(Tag(tag), out var e) && e.Off + e.Len <= d.Length) parts[tag] = d.AsSpan(e.Off, e.Len).ToArray();
        parts["glyf"] = outlines.ToArray();
        parts["loca"] = offsets;
        var headCopy = parts["head"];
        Put32(headCopy, 8, 0);
        headCopy[50] = 0;
        headCopy[51] = 1;

        int n = parts.Count, log = 0;
        while (2 << log <= n) log++;
        int size = 12 + 16 * n;
        foreach (var part in parts.Values) size += (part.Length + 3) & ~3;
        var file = new byte[size];
        Put32(file, 0, 0x00010000);
        file[4] = (byte)(n >> 8);
        file[5] = (byte)n;
        int range = (1 << log) * 16;
        file[6] = (byte)(range >> 8);
        file[7] = (byte)range;
        file[8] = (byte)(log >> 8);
        file[9] = (byte)log;
        file[10] = (byte)((n * 16 - range) >> 8);
        file[11] = (byte)(n * 16 - range);
        int record = 12, at = 12 + 16 * n, headAt = 0;
        foreach (var (tag, part) in parts)
        {
            Put32(file, record, Tag(tag));
            Put32(file, record + 4, Checksum(part));
            Put32(file, record + 8, (uint)at);
            Put32(file, record + 12, (uint)part.Length);
            Array.Copy(part, 0, file, at, part.Length);
            if (tag == "head") headAt = at;
            record += 16;
            at += (part.Length + 3) & ~3;
        }
        Put32(file, headAt + 8, 0xB1B0AFBA - Checksum(file));
        return file;
    }

    public int AdvanceOfGlyph(int g) => BE.U16(d, hmtx + 4 * Math.Min(g, numHM - 1));

    /// <summary>Glyph and advance (font units) for a Unicode scalar value; glyph 0 if the font has none.</summary>
    public (ushort Glyph, int Adv) Lookup(int cp)
    {
        if (cache.TryGetValue(cp, out var v)) return v;
        int g = Map(cp);
        if (g == 0 && symbol && cp < 0x100) g = Map(0xF000 | cp);
        v = ((ushort)g, AdvanceOfGlyph(g));
        cache[cp] = v;
        return v;
    }

    /// <summary>Advance in EMU at the given size (EMU), rounded to nearest.</summary>
    public int Advance(int cp, int sizeEmu) => (int)U.RoundDiv((long)Lookup(cp).Adv * sizeEmu, UnitsPerEm);

    int Map(int c)
    {
        if (cmap < 0) return 0;
        if (cmapFormat == 4)
        {
            if (c > 0xFFFF) return 0;
            int segs = BE.U16(d, cmap + 6) / 2;
            int ends = cmap + 14, starts = ends + 2 * segs + 2, deltas = starts + 2 * segs, ranges = deltas + 2 * segs;
            int lo = 0, hi = segs - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (BE.U16(d, ends + 2 * mid) < c) lo = mid + 1; else hi = mid;
            }
            if (lo >= segs) return 0;
            int start = BE.U16(d, starts + 2 * lo);
            if (BE.U16(d, ends + 2 * lo) < c || start > c) return 0;
            int delta = BE.U16(d, deltas + 2 * lo), ro = BE.U16(d, ranges + 2 * lo);
            if (ro == 0) return (c + delta) & 0xFFFF;
            int addr = ranges + 2 * lo + ro + 2 * (c - start);
            if (addr + 1 >= d.Length) return 0;
            int g = BE.U16(d, addr);
            return g == 0 ? 0 : (g + delta) & 0xFFFF;
        }
        else
        {
            long n = BE.U32(d, cmap + 12);
            long lo = 0, hi = n - 1;
            while (lo <= hi)
            {
                long mid = (lo + hi) / 2;
                int r = cmap + 16 + (int)(12 * mid);
                uint s = BE.U32(d, r), e = BE.U32(d, r + 4);
                if (c < s) hi = mid - 1;
                else if (c > e) lo = mid + 1;
                else return (int)(BE.U32(d, r + 8) + (uint)c - s) & 0xFFFF;
            }
            return 0;
        }
    }

    // ---- reading only names and style flags, without loading the whole file ----

    static byte[] ReadAt(FileStream fs, long off, int len)
    {
        if (len <= 0 || off < 0 || off + len > fs.Length) return Array.Empty<byte>();
        var b = new byte[len];
        fs.Position = off;
        fs.ReadExactly(b, 0, len);
        return b;
    }

    public static List<FaceInfo> Scan(string path)
    {
        var list = new List<FaceInfo>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var h = ReadAt(fs, 0, 12);
            if (h.Length < 12) return list;
            var offs = new List<long>();
            if (BE.U32(h, 0) == 0x74746366)
            {
                int nf = (int)BE.U32(h, 8);
                var o = ReadAt(fs, 12, 4 * nf);
                for (int i = 0; i * 4 < o.Length; i++) offs.Add(BE.U32(o, 4 * i));
            }
            else offs.Add(0);
            for (int fi = 0; fi < offs.Count; fi++)
            {
                var th = ReadAt(fs, offs[fi], 12);
                if (th.Length < 12) continue;
                uint ver = BE.U32(th, 0);
                if (ver != 0x00010000 && ver != 0x4F54544F && ver != 0x74727565) continue;
                int nt = BE.U16(th, 4);
                var dir = ReadAt(fs, offs[fi] + 12, 16 * nt);
                long name = -1, os2 = -1, head = -1;
                int nameLen = 0, os2Len = 0;
                bool hasCmap = false, hasHmtx = false, hasHhea = false;
                for (int i = 0; i * 16 < dir.Length; i++)
                {
                    uint tag = BE.U32(dir, 16 * i);
                    long off = BE.U32(dir, 16 * i + 8);
                    int len = (int)Math.Min(BE.U32(dir, 16 * i + 12), 1 << 22);
                    if (tag == Tag("name")) { name = off; nameLen = len; }
                    else if (tag == Tag("OS/2")) { os2 = off; os2Len = len; }
                    else if (tag == Tag("head")) head = off;
                    else if (tag == Tag("cmap")) hasCmap = true;
                    else if (tag == Tag("hmtx")) hasHmtx = true;
                    else if (tag == Tag("hhea")) hasHhea = true;
                }
                if (name < 0 || head < 0 || !hasCmap || !hasHmtx || !hasHhea) continue;
                string? family = ReadName(ReadAt(fs, name, nameLen), 1);
                if (string.IsNullOrWhiteSpace(family)) continue;
                bool bold, italic;
                var o2 = os2 >= 0 && os2Len >= 64 ? ReadAt(fs, os2, 64) : Array.Empty<byte>();
                if (o2.Length == 64)
                {
                    int sel = BE.U16(o2, 62);
                    bold = (sel & 0x20) != 0;
                    italic = (sel & 0x01) != 0;
                }
                else
                {
                    var hd = ReadAt(fs, head, 54);
                    int mac = hd.Length == 54 ? BE.U16(hd, 44) : 0;
                    bold = (mac & 1) != 0;
                    italic = (mac & 2) != 0;
                }
                list.Add(new FaceInfo { Path = path, Index = fi, Family = family.Trim(), Bold = bold, Italic = italic });
            }
        }
        catch (Exception) { }
        return list;
    }

    static string? ReadName(byte[] t, int nameId)
    {
        if (t.Length < 6) return null;
        int count = BE.U16(t, 2), strings = BE.U16(t, 4);
        string? best = null;
        int bestRank = 99;
        for (int i = 0; i < count; i++)
        {
            int r = 6 + 12 * i;
            if (r + 12 > t.Length) break;
            if (BE.U16(t, r + 6) != nameId) continue;
            int pid = BE.U16(t, r), lang = BE.U16(t, r + 4), len = BE.U16(t, r + 8), off = strings + BE.U16(t, r + 10);
            if (off + len > t.Length) continue;
            int rank = pid == 3 ? (lang == 0x409 ? 0 : 1) : pid == 0 ? 2 : pid == 1 ? 3 : 99;
            if (rank >= bestRank) continue;
            best = pid == 1 ? Encoding.Latin1.GetString(t, off, len) : Encoding.BigEndianUnicode.GetString(t, off, len);
            bestRank = rank;
        }
        return best;
    }
}

/// <summary>A requested family and style, and the face that stands for it on this PC.</summary>
sealed class ResolvedFont
{
    public string Family = "";
    public bool Bold, Italic;
    public FontFace Face = null!;
    public bool SimBold, SimItalic, Substituted;
}

/// <summary>The fonts installed on this PC, by family name and bold/italic.</summary>
static class FontCatalog
{
    public const string Fallback = "Segoe UI";
    static readonly Dictionary<string, FaceInfo?[]> families = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, FontFace> faces = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(string, bool, bool), ResolvedFont> resolved = new();
    static List<string> names = new();
    static bool scanned;

    public static IReadOnlyList<string> Families { get { EnsureScanned(); return names; } }

    static void EnsureScanned()
    {
        if (scanned) return;
        scanned = true;
        var paths = new List<string>();
        foreach (var dir in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
            // The fonts Office keeps on this PC for its own documents (Aptos and others), one folder for each family.
            // They come after the installed ones, so an installed font of the same name is the one used.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "FontCache", "4", "CloudFonts"),
        })
        {
            if (!Directory.Exists(dir)) continue;
            var found = Directory.GetFiles(dir, "*", dir.EndsWith("CloudFonts", StringComparison.Ordinal) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(f =>
            {
                var e = Path.GetExtension(f).ToLowerInvariant();
                return e is ".ttf" or ".otf" or ".ttc" or ".otc";
            }).ToList();
            found.Sort(StringComparer.OrdinalIgnoreCase);
            paths.AddRange(found);
        }
        var results = new List<FaceInfo>[paths.Count];
        Parallel.For(0, paths.Count, i => results[i] = FontFace.Scan(paths[i]));
        foreach (var list in results)
            foreach (var f in list)
            {
                if (!families.TryGetValue(f.Family, out var slots)) families[f.Family] = slots = new FaceInfo?[4];
                int k = (f.Bold ? 1 : 0) + (f.Italic ? 2 : 0);
                slots[k] ??= f;
            }
        names = families.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static FontFace Load(FaceInfo info)
    {
        string key = info.Path + "|" + info.Index;
        if (faces.TryGetValue(key, out var f)) return f;
        if (!files.TryGetValue(info.Path, out var data)) files[info.Path] = data = File.ReadAllBytes(info.Path);
        return faces[key] = new FontFace(info, data);
    }

    /// <summary>The families a character is taken from when the font of its text does not have it, in this order.
    /// In a text set in a Segoe font, the signs of Segoe come first.</summary>
    static readonly string[] Others =
    {
        MathLayout.Family, "Segoe UI Symbol", "Segoe UI", "Segoe UI Emoji", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Nirmala UI", "Segoe UI Historic",
    };
    static readonly Dictionary<(int, bool), string?> others = new();

    /// <summary>An installed family that has this character, for a text whose own font does not; null if none of them has it.</summary>
    public static string? Other(int cp, bool segoe = false)
    {
        EnsureScanned();
        if (others.TryGetValue((cp, segoe), out var known)) return known;
        var order = segoe ? Others.OrderBy(f => f.StartsWith("Segoe UI", StringComparison.Ordinal) ? 0 : 1) : Others.AsEnumerable();
        string? found = order.FirstOrDefault(f => families.ContainsKey(f) && Resolve(f, false, false).Face.Lookup(cp).Glyph != 0);
        return others[(cp, segoe)] = found;
    }

    static bool Drawn(int cp) => CharUnicodeInfo.GetUnicodeCategory(cp) is not (UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned);

    /// <summary>Gives each character of a paragraph that the font of its text does not have a font that has it, as text of its own
    /// with that font. A character no font of the list has stays as it is. Returns how many characters got another font.</summary>
    public static int Cover(Document d, Paragraph p)
    {
        List<Run>? res = null;
        ParaStyle? st = null;
        int n = 0;
        for (int k = 0; k < p.Runs.Count; k++)
        {
            var run = p.Runs[k];
            string t = run.Text;
            if (run.Obj != null || !t.Any(c => c > 0x7E)) { res?.Add(run); continue; }
            st ??= d.StyleOf(p);
            var f = d.Resolve(st, run);
            var face = Resolve(f.Font, f.Bold, f.Italic).Face;
            bool segoe = f.Font.StartsWith("Segoe", StringComparison.OrdinalIgnoreCase);
            var pieces = new List<Run>();
            int from = 0, made = 0;
            string? cur = null;
            for (int i = 0; i < t.Length;)
            {
                int len = char.IsSurrogatePair(t, i) ? 2 : 1;
                int cp = len == 2 ? char.ConvertToUtf32(t[i], t[i + 1]) : t[i];
                string? family = cp > 0x7E && cp != Document.LineBreak && Drawn(cp) && face.Lookup(cp).Glyph == 0 ? Other(cp, segoe) : null;
                if (family != null) made++;
                if (family != cur)
                {
                    if (i > from) pieces.Add(Piece(run, t[from..i], cur));
                    from = i;
                    cur = family;
                }
                i += len;
            }
            if (made == 0) { res?.Add(run); continue; }
            pieces.Add(Piece(run, t[from..], cur));
            res ??= new List<Run>(p.Runs.Take(k));
            res.AddRange(pieces);
            n += made;
        }
        if (res == null) return 0;
        p.Runs = res;
        p.Touch();
        return n;
    }

    static Run Piece(Run run, string text, string? family) =>
        family == null ? run with { Text = text } : run with { Text = text, Props = run.Props with { Font = family } };

    public static ResolvedFont Resolve(string family, bool bold, bool italic)
    {
        EnsureScanned();
        var key = (family.ToLowerInvariant(), bold, italic);
        if (resolved.TryGetValue(key, out var r)) return r;
        r = new ResolvedFont { Family = family, Bold = bold, Italic = italic };
        if (!families.TryGetValue(family, out var slots))
        {
            r.Substituted = true;
            if (!families.TryGetValue(Fallback, out slots)) slots = families[names[0]];
        }
        // Exact style, else drop italic, else drop bold, else regular, else any face of the family.
        int want = (bold ? 1 : 0) + (italic ? 2 : 0);
        FaceInfo? pick = null;
        foreach (int k in new[] { want, want & 1, want & 2, 0, 1, 2, 3 })
        {
            pick = slots[k];
            if (pick != null) break;
        }
        try { r.Face = Load(pick!); }
        catch (Exception)
        {
            if (family.Equals(Fallback, StringComparison.OrdinalIgnoreCase)) throw;
            var fb = Resolve(Fallback, bold, italic);
            r.Face = fb.Face;
            r.Substituted = true;
        }
        r.SimBold = bold && !r.Face.Info.Bold;
        r.SimItalic = italic && !r.Face.Info.Italic;
        return resolved[key] = r;
    }
}
