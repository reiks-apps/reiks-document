using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReiksDocument;

/// <summary>A stretch of a paragraph in one resolved format.</summary>
sealed class Span
{
    public int Start, Length;
    public CharFormat Fmt;
    public ResolvedFont Font = null!;
    public int SizeEmu, Asc, Desc;
    /// <summary>Size at which the characters are measured and drawn, and how far they are raised (negative: lowered).
    /// Both differ from SizeEmu and 0 only for raised and lowered text.</summary>
    public int DrawSize, Shift;
    public int End => Start + Length;
}

/// <summary>One line. Start/Length/X/Height/Baseline come from line breaking; Page/Top from page breaking.</summary>
sealed class LineBox
{
    public int Start, Length;
    /// <summary>Left edge of the first item, from the left edge of the text area.</summary>
    public int X;
    public int Height;
    /// <summary>Baseline, from the top of the line.</summary>
    public int Baseline;
    /// <summary>Advance of every item of the line, as placed.</summary>
    public int[] Adv = Array.Empty<int>();
    public int Page, Top;
    /// <summary>The bullet or number of a list paragraph on its first line, and its left edge from the left edge of the text area.</summary>
    public string? Mark;
    public int MarkX;
    /// <summary>Set by line breaking when the line is wider than the room it was given.</summary>
    internal bool Over;
    /// <summary>For a table paragraph, whose "lines" are its rows: the number of the row. Otherwise -1.</summary>
    public int Row = -1;
    /// <summary>A row of a table that a joined cell ties to the row below it: the two stay on one page.</summary>
    public bool Tied;
    /// <summary>The line ends inside a word: a hyphen is drawn behind its last item, whose advance holds the room for it.</summary>
    public bool Hyphen;

    public int End => Start + Length;

    public int XOffset(int items)
    {
        int x = X;
        for (int i = 0; i < items && i < Adv.Length; i++) x += Adv[i];
        return x;
    }
}

/// <summary>A picture that floats: where it stands on its page, and the item it belongs to.</summary>
sealed class FloatBox
{
    public int Para, Off;
    public Picture Pic = null!;
    public int Page, X, Y;
}

sealed class PageBox
{
    public List<(int Para, LineBox Line)> Lines = new();
    /// <summary>The footnotes of this page in order, with their numbers.</summary>
    public List<(Note Note, int Number)> Notes = new();
    /// <summary>Top of the footnote area; meaningful when Notes is not empty.</summary>
    public int NoteTop;
    internal long NoteHeight;
    /// <summary>The floating pictures of this page in the order they were placed.</summary>
    public List<FloatBox> Floats = new();
    /// <summary>The texts of the header and the footer of this page.</summary>
    public List<BandText> Bands = new();
    /// <summary>The pictures of the header and the footer of this page.</summary>
    public List<BandPic> BandPics = new();
    /// <summary>The table cells of this page.</summary>
    public List<CellBox> Cells = new();
}

/// <summary>A table cell as it stands on a page.</summary>
sealed class CellBox
{
    public int Para, Row, Col;
    public Cell Cell = null!;
    public int X, Y, W, H;
    /// <summary>How many columns and rows the cell fills; more than one for a joined cell.</summary>
    public int Cols = 1, Rows = 1;
}

/// <summary>One text of a header or footer as it stands on a page.</summary>
sealed class BandText
{
    public bool Footer;
    public int X, Baseline;
    public string Text = "";
}

/// <summary>The picture of a header or footer as it stands on a page.</summary>
sealed class BandPic
{
    public bool Footer;
    public Picture Pic = null!;
    public int X, Y;
}

/// <summary>What line breaking needs to know about a paragraph, measured once.</summary>
sealed class ParaData
{
    public Span[] Spans = Array.Empty<Span>();
    public int[] Nat = Array.Empty<int>();
    public Inline?[]? Ob;
    public ObjBox?[]? Boxes;
    public Span? Mark;
}

/// <summary>The layout rules of FORMAT.md. All arithmetic is in integers (EMU).</summary>
sealed class LayoutEngine
{
    public const int RulesVersion = 26;
    /// <summary>Tables: room between the edge of a cell and its text, at the sides and above and below; thickness of the lines.</summary>
    public const int CellPadX = 72000, CellPadY = 36000, CellBorder = 9525;
    /// <summary>Header: its text begins this far below the top of the page. Footer: its text ends this far above the bottom.</summary>
    public const int BandDistance = 457200;
    /// <summary>Between the picture of a header or footer and the text of the same place.</summary>
    public const int BandGap = 114300;
    /// <summary>A picture of a header or footer that is given its size by the program keeps this far from the edge of the page.</summary>
    public const int BandEdge = 180000;
    public const int TabStop = 457200;
    /// <summary>Footnote area: space above the rule, space between the rule and the first note, thickness of the rule.</summary>
    public const int NoteGapAbove = 101600, NoteGapBelow = 50800, NoteRule = 9525;
    /// <summary>Room kept free around a floating picture, and the narrowest stretch beside one that still takes text.</summary>
    public const int FloatGap = 127000, MinWrapWidth = 914400;
    /// <summary>Lists: where the bullet or number starts and where the text starts, from the paragraph's left indent.</summary>
    public const int ListMarkAt = 228600, ListTextAt = 457200;

    public readonly Document Doc;
    public List<PageBox> Pages = new();
    /// <summary>Footnote id to number, and the notes in the order of their numbers.</summary>
    public readonly Dictionary<int, int> NoteNumbers = new();
    public readonly List<Note> NoteOrder = new();
    readonly Dictionary<(int, int), FloatBox> floatAt = new();
    int[] buf = new int[256];

    public LayoutEngine(Document doc) { Doc = doc; }

    /// <summary>The paragraphs of a story: 0 the body, above 0 the footnote with that id, below 0 the table cell with the id -story.</summary>
    public List<Paragraph> Paras(int story) => story == 0 ? Doc.Body : story > 0 ? Doc.Notes[story].Paras : Doc.Cells[-story].Paras;

    static void Drop(Paragraph p)
    {
        p.Lines = null;
        p.FullLines = null;
        p.Data = null;
    }

    public void InvalidateAll()
    {
        foreach (var p in Doc.Body) { Drop(p); p.Spans = null; }
        foreach (var n in Doc.Notes.Values)
            foreach (var p in n.Paras) { Drop(p); p.Spans = null; }
        foreach (var c in Doc.Cells.Values)
            foreach (var p in c.Paras) { Drop(p); p.Spans = null; }
    }

    /// <summary>Breaks every paragraph that has no lines, then places all lines on pages.</summary>
    public void Update()
    {
        Hyphenator.Fill(Doc);
        SyncNumbers(true);
        foreach (var p in Doc.Body)
        {
            if (p.Table != null) BreakTable(p);
            else p.FullLines ??= BreakFull(p);
        }
        foreach (var n in NoteOrder)
            foreach (var p in n.Paras) p.Lines = p.FullLines ??= BreakFull(p);
        Paginate();
    }

    public void LoadStored(List<PageBox> pages)
    {
        Pages = pages;
        floatAt.Clear();
        foreach (var pg in pages)
            foreach (var f in pg.Floats) floatAt[(f.Para, f.Off)] = f;
    }

    // ---- footnote numbers and list numbers ----

    static bool Same(int[]? a, int[]? b) => a == null || b == null ? a == b : a.AsSpan().SequenceEqual(b);

    /// <summary>Numbers the footnotes in the order of their references in the body, and the paragraphs of numbered lists.</summary>
    public void SyncNumbers(bool invalidate)
    {
        NoteNumbers.Clear();
        NoteOrder.Clear();
        CiteNumbers.Clear();
        var count = new int[MaxListLevel + 1];
        var counted = new string?[MaxListLevel + 1];
        foreach (var p in Doc.Body)
        {
            int[]? nums = null;
            var objs = p.Objects;
            if (objs.Length > 0)
            {
                List<int>? list = null;
                foreach (var (_, o) in objs)
                    if (o is NoteRef nr && !NoteNumbers.ContainsKey(nr.Id) && Doc.Notes.TryGetValue(nr.Id, out var note))
                    {
                        NoteOrder.Add(note);
                        NoteNumbers[nr.Id] = NoteOrder.Count;
                        (list ??= new()).Add(NoteOrder.Count);
                    }
                nums = list?.ToArray();
            }
            // Citations: a source gets its number where the body cites it first.
            string? cites = null;
            foreach (var (_, o) in objs)
                if (o is Cite ci)
                {
                    if (!CiteNumbers.ContainsKey(ci.Source) && Doc.Sources.Exists(x => x.Id == ci.Source)) CiteNumbers[ci.Source] = CiteNumbers.Count + 1;
                    cites += CiteText(ci) + "\n";
                }
            // The bullet or number of a list paragraph. Every level counts by itself; a paragraph that is not in a list
            // starts all levels again, a paragraph of a level starts the deeper ones again, and a bullet or a number
            // of another kind starts its own level again.
            string? mark = null;
            if (p.List == null || p.Table != null)
            {
                Array.Clear(count);
                Array.Clear(counted);
            }
            else
            {
                int lv = Math.Clamp(p.ListLevel, 0, MaxListLevel);
                for (int k = lv + 1; k < count.Length; k++) { count[k] = 0; counted[k] = null; }
                if (Bullet(p.List) is { } b)
                {
                    count[lv] = 0;
                    counted[lv] = null;
                    mark = b;
                }
                else
                {
                    if (counted[lv] != null && counted[lv] != p.List) count[lv] = 0;
                    count[lv]++;
                    counted[lv] = p.List;
                    mark = NumberMark(p.List, lv, count);
                }
            }
            if (!Same(nums, p.NoteNums) || p.MarkNum != 0 || p.ListMark != mark || p.CiteSig != cites)
            {
                p.NoteNums = nums;
                p.MarkNum = 0;
                p.ListMark = mark;
                p.CiteSig = cites;
                p.ListNum = mark == null ? 0 : 1;
                if (invalidate) Drop(p);
            }
        }
        for (int i = 0; i < NoteOrder.Count; i++)
        {
            var paras = NoteOrder[i].Paras;
            for (int k = 0; k < paras.Count; k++)
            {
                int want = k == 0 ? i + 1 : 0;
                if (paras[k].MarkNum == want && paras[k].ListNum == 0) continue;
                paras[k].MarkNum = want;
                paras[k].ListNum = 0;
                if (invalidate) Drop(paras[k]);
            }
        }
    }

    /// <summary>Size of the raised digits of a footnote number, for a format of size S.</summary>
    public static int RefSize(int sizeEmu) => sizeEmu * 2 / 3;
    /// <summary>How far the digits of a footnote number are raised above the baseline.</summary>
    public static int RefRaise(int sizeEmu) => sizeEmu / 3;

    public static int DigitsWidth(FontFace face, int sizeEmu, int number)
    {
        if (number <= 0) return 0;
        int w = 0, s = RefSize(sizeEmu);
        foreach (char c in number.ToString(CultureInfo.InvariantCulture)) w += face.Advance(c, s);
        return w;
    }

    /// <summary>Width kept free before the first line of a footnote: its number and one space.</summary>
    public int MarkWidth(ParaStyle st, int number)
    {
        var sp = StyleSpan(st);
        return DigitsWidth(sp.Font.Face, sp.SizeEmu, number) + sp.Font.Face.Advance(' ', sp.SizeEmu);
    }

    /// <summary>The bullet or the number with its full stop.</summary>
    /// <summary>The number of each cited source: the order in which the body cites them first.</summary>
    public readonly Dictionary<string, int> CiteNumbers = new();

    /// <summary>What a citation reads (FORMAT.md §4.5).</summary>
    public string CiteText(Cite c) => Citations.Text(Doc, c, CiteNumbers);

    public const int MaxListLevel = 8;
    public static readonly string[] BulletKinds = { "bullet", "circle", "square", "dash" };
    public static readonly string[] NumberKinds = { "number", "number-paren", "roman-upper", "alpha-upper", "alpha-paren", "alpha", "roman", "decimal", "outline", "outline-paren" };

    public static bool IsListKind(string? kind) => kind != null && (Array.IndexOf(BulletKinds, kind) >= 0 || Array.IndexOf(NumberKinds, kind) >= 0);

    /// <summary>The sign of a list kind that does not count, or null for one that does.</summary>
    public static string? Bullet(string kind) => kind switch
    {
        "bullet" => ((char)0x2022).ToString(),
        "circle" => ((char)0x25E6).ToString(),
        "square" => ((char)0x25AA).ToString(),
        "dash" => ((char)0x2013).ToString(),
        _ => null,
    };

    /// <summary>The number of a list paragraph of a counting kind at a level, from the counts of all levels (FORMAT.md §5.5).</summary>
    public static string NumberMark(string kind, int level, int[] count)
    {
        static string D(int v) => v.ToString(CultureInfo.InvariantCulture);
        int n = count[level];
        switch (kind)
        {
            case "number-paren": return D(n) + ")";
            case "roman-upper": return Roman(n) + ".";
            case "roman": return Roman(n).ToLowerInvariant() + ".";
            case "alpha-upper": return Alpha(n).ToUpperInvariant() + ".";
            case "alpha": return Alpha(n) + ".";
            case "alpha-paren": return Alpha(n) + ")";
            case "decimal":
                string all = "";
                for (int k = 0; k <= level; k++) all += D(Math.Max(1, count[k])) + ".";
                return all;
            case "outline":
            case "outline-paren":
                string body = level % 3 == 0 ? D(n) : level % 3 == 1 ? Alpha(n) : Roman(n).ToLowerInvariant();
                return body + (kind == "outline" ? "." : ")");
            default: return D(n) + ".";
        }
    }

    /// <summary>a, b, … z, aa, ab, …</summary>
    public static string Alpha(int n)
    {
        string s = "";
        while (n > 0)
        {
            n--;
            s = (char)('a' + n % 26) + s;
            n /= 26;
        }
        return s;
    }

    /// <summary>I, II, III, IV, … up to 3999; beyond that the number in digits.</summary>
    public static string Roman(int n)
    {
        if (n < 1 || n > 3999) return n.ToString(CultureInfo.InvariantCulture);
        int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        string[] signs = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
        string s = "";
        for (int i = 0; i < values.Length; i++)
            while (n >= values[i]) { s += signs[i]; n -= values[i]; }
        return s;
    }

    /// <summary>The format of a list paragraph's bullet or number: that of its first item, or of its mark.</summary>
    public Span ListSpan(Paragraph p)
    {
        var d = DataOf(p);
        return d.Spans.Length > 0 ? d.Spans[0] : d.Mark ??= MarkSpan(p);
    }

    // ---- formats and fonts ----

    Span MakeSpan(int start, int len, CharFormat f)
    {
        var font = FontCatalog.Resolve(f.Font, f.Bold, f.Italic);
        var face = font.Face;
        int size = f.Size * U.PerHalfPoint;
        return new Span
        {
            Start = start, Length = len, Fmt = f, Font = font, SizeEmu = size,
            DrawSize = f.Vert == 0 ? size : RefSize(size),
            Shift = f.Vert == 1 ? RefRaise(size) : f.Vert == 2 ? -(size / 6) : 0,
            Asc = (int)U.RoundDiv((long)(face.Ascender + face.LineGap) * size, face.UnitsPerEm),
            Desc = (int)U.RoundDiv((long)face.Descender * size, face.UnitsPerEm),
        };
    }

    public Span[] SpansOf(Paragraph p)
    {
        if (p.Spans != null) return p.Spans;
        var st = Doc.StyleOf(p);
        var list = new List<Span>();
        int pos = 0;
        foreach (var r in p.Runs)
        {
            var f = Doc.Resolve(st, r);
            if (list.Count > 0 && list[^1].Fmt == f) list[^1].Length += r.Text.Length;
            else list.Add(MakeSpan(pos, r.Text.Length, f));
            pos += r.Text.Length;
        }
        return p.Spans = list.ToArray();
    }

    public Span MarkSpan(Paragraph p) => MakeSpan(0, 0, Doc.Resolve(Doc.StyleOf(p), p.Mark));
    public Span StyleSpan(ParaStyle st) => MakeSpan(0, 0, Doc.Resolve(st, new Run("")));

    public List<ResolvedFont> UsedFonts()
    {
        var set = new HashSet<ResolvedFont>();
        void Add(Paragraph p)
        {
            foreach (var s in SpansOf(p)) set.Add(s.Font);
            if (p.Runs.Count == 0 || p.Text.EndsWith(Document.LineBreak)) set.Add(MarkSpan(p).Font);
        }
        foreach (var p in Doc.Body)
        {
            if (p.Table != null) continue;
            Add(p);
            foreach (var (_, o) in p.Objects)
                if (o is Equation) set.Add(MathLayout.Font);
        }
        if (Doc.HasBand(false)) set.Add(BandSpan(false).Font);
        if (Doc.HasBand(true)) set.Add(BandSpan(true).Font);
        foreach (var c in Doc.CellsInOrder())
            foreach (var p in c.Paras)
            {
                Add(p);
                foreach (var (_, o) in p.Objects)
                    if (o is Equation) set.Add(MathLayout.Font);
            }
        foreach (var n in Doc.NotesInOrder())
        {
            set.Add(StyleSpan(Doc.StyleOf(n.Paras[0])).Font);
            foreach (var p in n.Paras) Add(p);
        }
        return set.OrderBy(f => f.Family, StringComparer.Ordinal).ThenBy(f => f.Bold).ThenBy(f => f.Italic).ToList();
    }

    // ---- line breaking ----

    ParaData DataOf(Paragraph p)
    {
        if (p.Data != null) return p.Data;
        string text = p.Text;
        int n = text.Length;
        var d = new ParaData { Spans = SpansOf(p), Nat = new int[n] };
        if (p.Objects.Length > 0)
        {
            d.Ob = new Inline?[n];
            foreach (var (off, o) in p.Objects) d.Ob[off] = o;
        }
        foreach (var sp in d.Spans)
            for (int i = sp.Start; i < sp.End; i++)
            {
                char c = text[i];
                if (c == '\t' || c == Document.LineBreak) continue;
                if (d.Ob?[i] is { } o)
                {
                    if (o is NoteRef nr) d.Nat[i] = DigitsWidth(sp.Font.Face, sp.SizeEmu, NoteNumbers.GetValueOrDefault(nr.Id));
                    else if (o is Cite ci) d.Nat[i] = ChartLayout.TextWidth(sp.Font.Face, CiteText(ci), sp.DrawSize);
                    else if (o is not Picture { Floating: true })
                    {
                        var measured = Objects.Measure(o, sp);
                        (d.Boxes ??= new ObjBox?[n])[i] = measured;
                        d.Nat[i] = measured.W;
                    }
                    continue;
                }
                if (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(text[i - 1])) continue;
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(c, text[i + 1]);
                d.Nat[i] = sp.Font.Face.Advance(cp, sp.DrawSize);
            }
        return p.Data = d;
    }

    /// <summary>The advance of a hyphen in the format of an item.</summary>
    static int HyphenAdvance(ParaData d, int item)
    {
        foreach (var sp in d.Spans)
            if (item >= sp.Start && item < sp.End) return sp.Font.Face.Advance('-', sp.DrawSize);
        return 0;
    }

    /// <summary>True if the block that begins with paragraph i should start on the next page: it does not fit into the room
    /// that is left, and less than half of it would. The block ends before the next paragraph that begins a block or a page;
    /// empty paragraphs at its end are room between blocks, not part of it.</summary>
    bool BlockMoves(List<Paragraph> body, int i, long left)
    {
        int end = i + 1;
        while (end < body.Count && !body[end].Together && !body[end].PageBreakBefore) end++;
        while (end > i + 1 && body[end - 1].Table == null && body[end - 1].Length == 0) end--;
        long need = 0;
        for (int q = i; q < end; q++)
        {
            need += Doc.BeforeOf(body[q]);
            foreach (var l in body[q].FullLines!) need += l.Height;
            if (q < end - 1) need += Doc.AfterOf(body[q]);
        }
        return need > left && 2 * left < need;
    }

    /// <summary>How many lines from the first on stay on one page, so that the first is not left alone at the bottom
    /// (and, in a paragraph of three lines, the last not alone at the top).</summary>
    static int KeepAtStart(int lines) => lines == 3 ? 3 : lines >= 2 ? 2 : 1;

    /// <summary>One line starting at item s, in a stretch that begins x0 from the left edge of the text area and is width wide.</summary>
    LineBox BreakLine(Paragraph p, ParaStyle st, int s, int x0, int width)
    {
        var d = DataOf(p);
        string text = p.Text;
        int n = text.Length;
        var nat = d.Nat;
        var ob = d.Ob;
        var boxes = d.Boxes;
        if (buf.Length < n) buf = new int[n];
        var tmp = buf;
        var align = p.Align ?? st.Align;
        bool first = s == 0;
        int leader = p.Toc ? text.LastIndexOf('\t') : -1;
        // The places where the line may end inside a word; hp is the next of them at or behind the item looked at.
        var hyphens = p.ValidHyphens;
        int hp = 0, hbest = -1, hbestAdv = 0;
        while (hp < hyphens.Length && hyphens[hp] <= s) hp++;

        // The left indent of a list can be below 0, as far as its number then stands at the edge of the text; elsewhere below 0 counts as 0.
        int ownLeft = p.IndentLeft ?? st.IndentLeft;
        if (ownLeft < 0) ownLeft = p.ListNum != 0 ? Math.Max(ownLeft, -ListMarkAt) : 0;
        int baseLeft = ownLeft + (p.ListNum != 0 ? Math.Clamp(p.ListLevel, 0, MaxListLevel) * ListTextAt : 0), indent = baseLeft;
        string? marker = null;
        if (p.ListNum != 0)
        {
            indent += ListTextAt;
            if (first)
            {
                var ms = d.Spans.Length > 0 ? d.Spans[0] : d.Mark ??= MarkSpan(p);
                marker = p.ListMark ?? "";
                int mw = ms.Font.Face.Advance(' ', ms.SizeEmu);
                foreach (char c in marker) mw += ms.Font.Face.Advance(c, ms.SizeEmu);
                indent += Math.Max(0, ListMarkAt + mw - ListTextAt);
            }
        }
        else if (first) indent += (p.IndentFirst ?? st.IndentFirst) + (p.MarkNum > 0 ? MarkWidth(st, p.MarkNum) : 0);
        int avail = Math.Max(0, width - indent - (p.IndentRight ?? st.IndentRight));

        // hard: the line was ended by a line break or by an object on its own line.
        bool hard = false, hyphenated = false;
        int w = 0, best = -1, e = -1;
        for (int i = s; i < n; i++)
        {
            char c = text[i];
            if (c == Document.LineBreak) { tmp[i] = 0; e = i + 1; hard = true; break; }
            var box = boxes?[i];
            if (box != null && ob![i]!.Block)
            {
                if (i > s) e = i;
                else { tmp[i] = box.W; e = i + 1; }
                hard = true;
                break;
            }
            if (box != null && i > s) best = i;
            int a = c == '\t' ? TabStop - w % TabStop : nat[i];
            if (i == leader)
            {
                // In the table of contents the last tab reaches so far that the page number ends at the right edge.
                long rest = 0;
                for (int j = i + 1; j < n; j++) rest += nat[j];
                if (avail - w - rest > a) a = (int)(avail - w - rest);
            }
            while (hp < hyphens.Length && hyphens[hp] < i) hp++;
            if (hp < hyphens.Length && hyphens[hp] == i)
            {
                // The line can end before this item if the hyphen still has room behind the item before it.
                int ha = HyphenAdvance(d, i - 1);
                if ((long)w + ha <= avail) { hbest = i; hbestAdv = ha; }
            }
            if (c != ' ' && a > 0 && i > s && (long)w + a > avail)
            {
                if (hbest > s && hbest > best)
                {
                    e = hbest;
                    tmp[e - 1] += hbestAdv;
                    hyphenated = true;
                }
                else e = best > s ? best : i;
                break;
            }
            tmp[i] = a;
            w += a;
            if ((c == ' ' || c == '-' || c == '\t' || box != null) && i + 1 < n && text[i + 1] != ' ') best = i + 1;
        }
        if (e < 0) e = n;

        var line = new LineBox { Start = s, Length = e - s, Adv = new int[e - s], Hyphen = hyphenated };
        Array.Copy(tmp, s, line.Adv, 0, e - s);

        // Measured width: without a line break at the end and without the spaces before the end.
        int t = e > s && text[e - 1] == Document.LineBreak ? e - 1 : e;
        while (t > s && text[t - 1] == ' ') t--;
        int m = 0;
        for (int i = s; i < t; i++) m += tmp[i];

        int shift = 0;
        if (align == Align.Center) shift = Math.Max(0, (avail - m) / 2);
        else if (align == Align.Right) shift = Math.Max(0, avail - m);
        else if (align == Align.Justify && !hard && e < n && avail > m)
        {
            int from = s;
            for (int i = s; i < t; i++) if (text[i] == '\t') from = i + 1;
            int k = 0;
            for (int i = from; i < t; i++) if (text[i] == ' ') k++;
            if (k > 0)
            {
                long extra = avail - m;
                int j = 0;
                for (int i = from; i < t; i++)
                    if (text[i] == ' ')
                    {
                        line.Adv[i - s] += (int)(extra * (j + 1) / k - extra * j / k);
                        j++;
                    }
            }
        }
        line.X = x0 + indent + shift;
        line.Over = m > avail;
        if (marker != null)
        {
            line.Mark = marker;
            line.MarkX = x0 + baseLeft + ListMarkAt;
        }

        int asc = 0, desc = 0, objAsc = 0, objDesc = 0;
        if (e > s)
        {
            foreach (var sp in d.Spans)
                if (sp.Start < e && sp.End > s) { asc = Math.Max(asc, sp.Asc); desc = Math.Max(desc, sp.Desc); }
            if (boxes != null)
                for (int i = s; i < e; i++)
                    if (boxes[i] is { } bx) { objAsc = Math.Max(objAsc, bx.H); objDesc = Math.Max(objDesc, bx.D); }
        }
        else
        {
            d.Mark ??= MarkSpan(p);
            asc = d.Mark.Asc;
            desc = d.Mark.Desc;
        }
        int deep = Math.Max(desc, objDesc);
        line.Height = Math.Max((int)U.RoundDiv((long)(asc + desc) * (p.Line ?? st.Line), 1000) + deep - desc, objAsc + deep);
        line.Baseline = line.Height - deep;
        // A line above the paragraph takes its room above the first line of text, a line below it under the last.
        var edges = p.Borders ?? st.Borders;
        if (first && edges.RoomAbove > 0)
        {
            line.Height += edges.RoomAbove;
            line.Baseline += edges.RoomAbove;
        }
        if (e >= n) line.Height += edges.RoomBelow;
        return line;
    }

    /// <summary>Between which places the lines at the edges of a paragraph stand, for a text area that begins at areaLeft and is
    /// areaWidth wide: the edges of its text, moved out by the room and the thickness of a line at that side.</summary>
    public (int Left, int Right) BorderEdges(Paragraph p, int areaLeft, int areaWidth)
    {
        var st = Doc.StyleOf(p);
        var b = p.Borders ?? st.Borders;
        int own = Math.Max(0, p.IndentLeft ?? st.IndentLeft);
        int textLeft = p.ListNum != 0
            ? own + Math.Clamp(p.ListLevel, 0, MaxListLevel) * ListTextAt
            : Math.Max(0, own + Math.Min(0, p.IndentFirst ?? st.IndentFirst));
        int textRight = areaWidth - Math.Max(0, p.IndentRight ?? st.IndentRight);
        return (areaLeft + textLeft - (b.Left > 0 ? b.SideGap + b.Left : 0), areaLeft + textRight + (b.Right > 0 ? b.SideGap + b.Right : 0));
    }

    /// <summary>True when after this line, which ends the text with a line break, one more empty line follows.</summary>
    static bool EndsOpen(string text, LineBox line) =>
        line.Length > 0 && line.End == text.Length && text[line.End - 1] == Document.LineBreak;

    /// <summary>The lines of a paragraph across the whole width of the text area.</summary>
    List<LineBox> BreakFull(Paragraph p) => BreakStretch(p, 0, Doc.Page.ContentWidth);

    /// <summary>The lines of a paragraph for one stretch.</summary>
    List<LineBox> BreakStretch(Paragraph p, int x0, int width)
    {
        var st = Doc.StyleOf(p);
        string text = p.Text;
        int s = 0;
        var lines = new List<LineBox>();
        LineBox line;
        do
        {
            line = BreakLine(p, st, s, x0, width);
            lines.Add(line);
            s = line.End;
        } while (s < text.Length || EndsOpen(text, line));
        return lines;
    }

    // ---- tables ----

    /// <summary>The left edge of a table, from the left edge of the text area: by the alignment of its paragraph.</summary>
    public int TableLeft(Paragraph p)
    {
        int room = Doc.Page.ContentWidth - p.Table!.Width;
        var align = p.Align ?? Doc.StyleOf(p).Align;
        return room <= 0 ? 0 : align == Align.Center ? room / 2 : align == Align.Right ? room : 0;
    }

    /// <summary>Breaks the paragraphs of every cell for the width of its column and makes one "line" per row.</summary>
    void BreakTable(Paragraph p)
    {
        var t = p.Table!;
        int left = TableLeft(p), n = t.Rows.Count;
        var heights = new int[n];
        // A cell joined over several rows: where it starts, how many rows, and the height it needs.
        var tall = new List<(int Row, int Rows, int Need)>();
        for (int r = 0; r < n; r++)
        {
            int x = left;
            heights[r] = 2 * CellPadY;
            for (int c = 0; c < t.Columns; c++)
            {
                int cellLeft = x;
                x += t.Widths[c];
                if (!t.Starts(r, c) || !Doc.Cells.TryGetValue(t.Rows[r][c], out var cell)) continue;
                var (cols, rows) = t.Span(r, c);
                int x0 = cellLeft + CellPadX, width = Math.Max(0, t.SpanWidth(c, cols) - 2 * CellPadX);
                long h = 0;
                for (int k = 0; k < cell.Paras.Count; k++)
                {
                    var cp = cell.Paras[k];
                    if (cp.FullLines == null || cp.CellX != x0 || cp.CellW != width || cp.MarkNum != 0 || cp.ListNum != 0)
                    {
                        cp.MarkNum = cp.ListNum = 0;
                        cp.CellX = x0;
                        cp.CellW = width;
                        cp.Data = null;
                        cp.FullLines = BreakStretch(cp, x0, width);
                    }
                    cp.Lines = cp.FullLines;
                    var st = Doc.StyleOf(cp);
                    if (k > 0) h += cp.Before ?? st.Before;
                    foreach (var l in cp.Lines) h += l.Height;
                    if (k < cell.Paras.Count - 1) h += cp.After ?? st.After;
                }
                int need = (int)Math.Min(h, int.MaxValue / 2) + 2 * CellPadY;
                if (rows == 1) heights[r] = Math.Max(heights[r], need);
                else tall.Add((r, rows, need));
            }
        }
        // What a cell over several rows needs beyond the height of its rows goes to the last of them.
        foreach (var (row, count, need) in tall)
        {
            long have = 0;
            for (int k = 0; k < count; k++) have += heights[row + k];
            if (need > have) heights[row + count - 1] = (int)Math.Min(heights[row + count - 1] + (need - have), int.MaxValue / 2);
        }
        var lines = new List<LineBox>();
        for (int r = 0; r < n; r++)
        {
            bool tied = false;
            for (int c = 0; r + 1 < n && c < t.Columns && !tied; c++) tied = t.Rows[r][c] == t.Rows[r + 1][c];
            lines.Add(new LineBox { Row = r, X = left, Height = heights[r], Baseline = heights[r], Tied = tied });
        }
        p.FullLines = lines;
    }

    /// <summary>Where a cell is: the paragraph of its table, its row and its column.</summary>
    public (int Para, int Row, int Col)? FindCell(int cellId)
    {
        for (int i = 0; i < Doc.Body.Count; i++)
        {
            var t = Doc.Body[i].Table;
            if (t == null) continue;
            for (int r = 0; r < t.Rows.Count; r++)
            {
                int c = Array.IndexOf(t.Rows[r], cellId);
                if (c >= 0) return (i, r, c);
            }
        }
        return null;
    }

    /// <summary>The column whose right edge is at x (within tol) at a point of a page, for dragging it.</summary>
    public (int Para, int Col)? ColumnEdgeAt(int page, int x, int y, int tol)
    {
        foreach (var cb in Pages[Math.Clamp(page, 0, Pages.Count - 1)].Cells)
            if (y >= cb.Y && y < cb.Y + cb.H && Math.Abs(x - (cb.X + cb.W)) <= tol) return (cb.Para, cb.Col + cb.Cols - 1);
        return null;
    }

    // ---- page breaking ----

    long NoteHeight(Note note)
    {
        long h = 0;
        foreach (var p in note.Paras)
        {
            foreach (var l in p.Lines!) h += l.Height;
            h += Doc.AfterOf(p);
        }
        return h;
    }

    static long Area(int notes, long height) => notes == 0 ? 0 : NoteGapAbove + NoteGapBelow + height;

    /// <summary>The stretch a line at top with height h can use beside the pictures of the page: fl to fr from the left edge of
    /// the text area. Returns top if the line can stand there, else the y to try next.</summary>
    static long Free(PageBox pg, long top, int h, int left, int width, out int fl, out int fr, out long narrow)
    {
        fl = 0;
        fr = width;
        long below = top;
        narrow = long.MaxValue;
        foreach (var f in pg.Floats)
        {
            bool around = f.Pic.Wrap == "square";
            if (!around && f.Pic.Wrap != "topbottom") continue;
            long bt = f.Y - FloatGap, bb = (long)f.Y + f.Pic.Height + FloatGap;
            if (top >= bb || top + h <= bt) continue;
            if (!around) { below = Math.Max(below, bb); continue; }
            int pl = f.X - left - FloatGap, pr = f.X - left + f.Pic.Width + FloatGap;
            if (pl >= fr || pr <= fl) continue;
            narrow = Math.Min(narrow, bb);
            // Text goes on the wider side of the picture, on the left when both are the same.
            if (Math.Min(fr, pl) - fl >= fr - Math.Max(fl, pr)) fr = Math.Min(fr, pl);
            else fl = Math.Max(fl, pr);
        }
        if (below > top) return below;
        if ((fl > 0 || fr < width) && fr - fl < MinWrapWidth) return narrow;
        return top;
    }

    void Paginate()
    {
        var pg = new PageBox();
        Pages = new List<PageBox> { pg };
        floatAt.Clear();
        int top = Doc.Page.Top, bottom = Doc.Page.Height - Doc.Page.Bottom, left = Doc.Page.Left, width = Doc.Page.ContentWidth;
        long y = top;
        var placed = new HashSet<int>();
        var fresh = new List<Note>();
        bool adapt = Doc.AdaptSpacing, gave = false;
        var lastPara = Doc.Body.Count > 0 ? Doc.Body[^1] : null;
        // The empty paragraph that ends the document never begins a page of its own (FORMAT.md §6, The last paragraph).
        bool tail = adapt && Doc.Body.Count > 1 && lastPara!.Table == null && lastPara.Length == 0 && lastPara.List == null && Doc.BordersOf(lastPara).None;
        // The paragraphs whose first line was placed on this page: the unit it became, and the footnotes the page had before it.
        var began = new Dictionary<int, (int Unit, int Notes, long NoteH)>();
        int pushFrom = -1;

        void Finish()
        {
            if (pg.Notes.Count == 0) return;
            pg.NoteTop = (int)(bottom - Area(pg.Notes.Count, pg.NoteHeight));
            long ny = pg.NoteTop + NoteGapAbove + NoteGapBelow;
            foreach (var (note, _) in pg.Notes)
                foreach (var p in note.Paras)
                {
                    foreach (var l in p.Lines!)
                    {
                        l.Page = Pages.Count - 1;
                        l.Top = (int)ny;
                        ny += l.Height;
                    }
                    ny += Doc.AfterOf(p);
                }
        }
        void NewPage() { Finish(); pg = new PageBox(); Pages.Add(pg); y = top; gave = false; began.Clear(); }

        var body = Doc.Body;

        // ---- giving room at the ends of pages (FORMAT.md §6, Giving room)
        bool Starts(int j) => j == 0 || body[j].Together || body[j].PageBreakBefore || Doc.StyleOf(body[j]).KeepNext;
        int SectionEnd(int j)
        {
            while (j + 1 < body.Count && !body[j + 1].PageBreakBefore && (!Starts(j + 1) || Doc.StyleOf(body[j]).KeepNext)) j++;
            return j;
        }
        long Hole()
        {
            var last = pg.Lines[^1].Line;
            return bottom - Area(pg.Notes.Count, pg.NoteHeight) - ((long)last.Top + last.Height);
        }
        bool MayGive() => adapt && !gave && pg.Lines.Count > 0;
        // Where the pictures of the page that the text keeps clear of end: only what lies below gives.
        long Low()
        {
            long low = 0;
            foreach (var f in pg.Floats)
                if (f.Pic.Wrap is "topbottom" or "square") low = Math.Max(low, (long)f.Y + f.Pic.Height + FloatGap);
            return low;
        }
        // Less room between the paragraphs of the page, so that the body from paragraph `from` (its line k across the whole
        // width) up to paragraph `upTo` is placed on it.
        bool PullIn(int from, int k, int upTo)
        {
            if (y < Low()) return false;
            // A piece that begins a paragraph brings the room above it onto the page: that gives as well.
            var gaps = GapsOf(pg, pg.Lines.Count, k == 0 ? y + Doc.BeforeOf(body[from]) : -1);
            long can = 0;
            foreach (var g in gaps) can += g.Size - g.Least;
            if (can <= 0) return false;
            long at = y, noteH = pg.NoteHeight;
            int notes = pg.Notes.Count;
            var seen = new HashSet<int>();
            for (int j = from; j <= upTo; j++)
            {
                var q = body[j];
                if (tail && j == body.Count - 1) break;
                if (q.Table != null) return false;
                var qo = q.Objects;
                foreach (var (_, o) in qo) if (o is Picture { Floating: true }) return false;
                if (j > from) at += Doc.AfterOf(body[j - 1]);
                if (j > from || k == 0) at += Doc.BeforeOf(q);
                var fl = q.FullLines!;
                for (int n = j == from ? k : 0; n < fl.Count; n++)
                {
                    at += fl[n].Height;
                    foreach (var (off, o) in qo)
                        if (off >= fl[n].Start && off < fl[n].End && o is NoteRef nr && NoteNumbers.ContainsKey(nr.Id) && !placed.Contains(nr.Id) && seen.Add(nr.Id))
                        {
                            notes++;
                            noteH += NoteHeight(Doc.Notes[nr.Id]);
                        }
                }
                // More than the page can give, whatever footnotes follow.
                if (at - bottom > can) return false;
            }
            long need = at - (bottom - Area(notes, noteH));
            if (need <= 0 || !Shrink(gaps, need)) return false;
            Shift(pg, gaps, pg.Lines.Count);
            foreach (var g in gaps) y += g.New - g.Size;
            gave = true;
            return true;
        }
        bool PullSection(int i0) => MayGive() && Hole() >= pg.Lines[^1].Line.Height && PullIn(i0, 0, SectionEnd(i0));
        // More room above paragraph i0 and the headings that keep with it, which then begin the next page whole. Returns the
        // first of them, or -1.
        int PushOver(int i0)
        {
            if (!began.ContainsKey(i0)) return -1;
            int a = i0;
            while (a > 0 && began.ContainsKey(a - 1) && body[a - 1].Table == null && Doc.StyleOf(body[a - 1]).KeepNext) a--;
            var (j0, notes, noteH) = began[a];
            long low = Low();
            if (j0 == 0 || pg.Lines[j0].Line.Top < low) return -1;
            long h = 0;
            for (int j = a; j <= i0; j++)
            {
                foreach (var (_, o) in body[j].Objects) if (o is Picture { Floating: true }) return -1;
                if (j > a) h += Doc.AfterOf(body[j - 1]) + Doc.BeforeOf(body[j]);
                foreach (var l in body[j].FullLines!) h += l.Height;
            }
            if (h > bottom - top) return -1;
            var gaps = GapsOf(pg, j0);
            if (!Stretch(gaps, bottom - Area(notes, noteH) - Lowest(j0))) return -1;
            Shift(pg, gaps, j0);
            pg.Lines.RemoveRange(j0, pg.Lines.Count - j0);
            for (int n = notes; n < pg.Notes.Count; n++) placed.Remove(pg.Notes[n].Note.Id);
            pg.Notes.RemoveRange(notes, pg.Notes.Count - notes);
            pg.NoteHeight = noteH;
            gave = true;
            return a;
        }
        // More room between all paragraphs of the page, so that it is filled down to its lower edge.
        // The foot of the first `units` units, or lower where a picture that moves with them reaches lower.
        long Lowest(int units)
        {
            var l = pg.Lines[units - 1].Line;
            long low = (long)l.Top + l.Height;
            foreach (var f in pg.Floats)
                if (Keeps(f) && AnchorOf(pg, f, units) >= 0) low = Math.Max(low, (long)f.Y + f.Pic.Height);
            return low;
        }
        void Fill()
        {
            var gaps = GapsOf(pg, pg.Lines.Count);
            if (!Stretch(gaps, bottom - Area(pg.Notes.Count, pg.NoteHeight) - Lowest(pg.Lines.Count))) return;
            Shift(pg, gaps, pg.Lines.Count);
            gave = true;
        }

        for (int i = 0; i < body.Count; i++)
        {
            var p = body[i];
            var st = Doc.StyleOf(p);
            var full = p.FullLines!;
            var objs = p.Objects;
            string text = p.Text;
            if (pg.Lines.Count > 0 && !(tail && i == body.Count - 1))
            {
                if (p.PageBreakBefore) NewPage();
                else if (p.Together && BlockMoves(body, i, bottom - Area(pg.Notes.Count, pg.NoteHeight) - y) && !PullSection(i)) NewPage();
                if (pg.Lines.Count > 0 && !p.PageBreakBefore && st.KeepNext && i + 1 < body.Count)
                {
                    long need = (long)Doc.BeforeOf(p) + Doc.AfterOf(p);
                    foreach (var l in full) need += l.Height;
                    var nx = body[i + 1];
                    if (!nx.PageBreakBefore)
                    {
                        // With the lines of the next paragraph that have to stay with its first.
                        need += Doc.BeforeOf(nx);
                        int stay = Doc.WidowControl && nx.Table == null ? KeepAtStart(nx.FullLines!.Count) : 1;
                        for (int q = 0; q < stay && q < nx.FullLines!.Count; q++) need += nx.FullLines[q].Height;
                    }
                    if (y + need > bottom - Area(pg.Notes.Count, pg.NoteHeight) && !PullSection(i)) NewPage();
                }
            }

            if (p.Table != null)
            {
                // A table: its rows one below the other; a row goes to the next page whole, and keeps clear of floating pictures.
                var t = p.Table;
                for (int r = 0; r < full.Count;)
                {
                    // The rows that joined cells tie together are placed as one block.
                    int last = r;
                    long blockHeight = full[r].Height;
                    while (last + 1 < full.Count && full[last].Tied) blockHeight += full[++last].Height;
                    int block = (int)Math.Min(blockHeight, int.MaxValue / 2);
                    while (true)
                    {
                        bool empty = pg.Lines.Count == 0;
                        long lt = y + (r == 0 && !empty ? Doc.BeforeOf(p) : 0);
                        while (true)
                        {
                            long next = Free(pg, lt, block, left, width, out int fl, out int fr, out long beside);
                            if (next > lt) { lt = next; continue; }
                            if (fl == 0 && fr == width) break;
                            lt = beside;
                        }
                        if (!empty && lt + block > bottom - Area(pg.Notes.Count, pg.NoteHeight))
                        {
                            NewPage();
                            continue;
                        }
                        for (int q = r; q <= last; q++)
                        {
                            var row = full[q];
                            row.Page = Pages.Count - 1;
                            row.Top = (int)lt;
                            pg.Lines.Add((i, row));
                            int cx = left + row.X;
                            for (int c = 0; c < t.Columns; c++)
                            {
                                if (t.Starts(q, c) && Doc.Cells.TryGetValue(t.Rows[q][c], out var cell))
                                {
                                    var (cols, rows) = t.Span(q, c);
                                    long ly = lt + CellPadY, ch = 0;
                                    for (int rk = 0; rk < rows && q + rk < full.Count; rk++) ch += full[q + rk].Height;
                                    for (int kp = 0; kp < cell.Paras.Count; kp++)
                                    {
                                        var cst = Doc.StyleOf(cell.Paras[kp]);
                                        if (kp > 0) ly += cell.Paras[kp].Before ?? cst.Before;
                                        foreach (var l in cell.Paras[kp].Lines!)
                                        {
                                            l.Page = Pages.Count - 1;
                                            l.Top = (int)ly;
                                            ly += l.Height;
                                        }
                                        ly += cell.Paras[kp].After ?? cst.After;
                                    }
                                    pg.Cells.Add(new CellBox
                                    {
                                        Para = i, Row = q, Col = c, Cell = cell, X = cx, Y = (int)lt, W = t.SpanWidth(c, cols),
                                        H = (int)Math.Min(ch, int.MaxValue / 2), Cols = cols, Rows = rows,
                                    });
                                }
                                cx += t.Widths[c];
                            }
                            lt += row.Height;
                        }
                        y = lt;
                        break;
                    }
                    r = last + 1;
                }
                y += Doc.AfterOf(p);
                p.Lines = full;
                continue;
            }

            // own: the lines as placed, once they differ from the lines across the whole width.
            List<LineBox>? own = null;
            int s = 0, k = 0, count = 0;
            LineBox line;
            var more = new List<Note>();
            do
            {
                while (k < full.Count && full[k].Start < s) k++;
                var trial = k < full.Count && full[k].Start == s ? full[k] : BreakLine(p, st, s, 0, width);
                while (true)
                {
                    bool empty = pg.Lines.Count == 0;
                    long lt = y + (s == 0 && !empty ? Doc.BeforeOf(p) : 0);

                    // The floating pictures whose item is in the line take their place first.
                    bool moved = false;
                    foreach (var (off, o) in objs)
                    {
                        if (off < trial.Start || off >= trial.End || o is not Picture { Floating: true } pic || floatAt.ContainsKey((i, off))) continue;
                        bool pushes = pic.Wrap is "topbottom" or "square";
                        long fy = lt + (pushes ? Math.Max(0, pic.Dy) : pic.Dy);
                        if (pushes && !empty && fy + pic.Height > bottom) { NewPage(); moved = true; break; }
                        var fb = new FloatBox { Para = i, Off = off, Pic = pic, Page = Pages.Count - 1, X = left + pic.Dx, Y = (int)fy };
                        pg.Floats.Add(fb);
                        floatAt[(i, off)] = fb;
                    }
                    if (moved) continue;

                    // Down past pictures that take the whole width; beside pictures that let text pass.
                    // A line broken again beside a picture holds no more than the trial line, so it is no taller.
                    // If it does not fit its stretch, it goes below the picture beside it that ends first.
                    while (true)
                    {
                        long next = Free(pg, lt, trial.Height, left, width, out int fl, out int fr, out long beside);
                        if (next > lt) { lt = next; continue; }
                        if (fl == 0 && fr == width) { line = trial; break; }
                        line = BreakLine(p, st, s, fl, fr - fl);
                        if (!line.Over) break;
                        lt = beside;
                    }

                    // The footnotes whose first reference is on this line.
                    fresh.Clear();
                    long add = 0;
                    foreach (var (off, o) in objs)
                        if (off >= line.Start && off < line.End && o is NoteRef nr && NoteNumbers.ContainsKey(nr.Id) && !placed.Contains(nr.Id))
                        {
                            var note = Doc.Notes[nr.Id];
                            if (fresh.Contains(note)) continue;
                            fresh.Add(note);
                            add += NoteHeight(note);
                        }
                    // No line alone at the bottom or the top of a page: the lines that have to stay with this one
                    // must fit behind it, with their footnotes. Only while the lines are those of the whole width.
                    long together = 0, addMore = 0;
                    int moreNotes = 0;
                    if (Doc.WidowControl && !empty && k < full.Count && ReferenceEquals(line, full[k]))
                    {
                        int stay = k == 0 ? KeepAtStart(full.Count) : k == full.Count - 2 ? 2 : 1;
                        more.Clear();
                        for (int q = k + 1; q < k + stay && q < full.Count; q++)
                        {
                            together += full[q].Height;
                            foreach (var (off, o) in objs)
                                if (off >= full[q].Start && off < full[q].End && o is NoteRef nr2 && NoteNumbers.ContainsKey(nr2.Id) && !placed.Contains(nr2.Id))
                                {
                                    var note = Doc.Notes[nr2.Id];
                                    if (fresh.Contains(note) || more.Contains(note)) continue;
                                    more.Add(note);
                                    addMore += NoteHeight(note);
                                }
                        }
                        moreNotes = more.Count;
                    }
                    bool tailLine = !empty && tail && i == body.Count - 1;
                    if (tailLine && lt + line.Height > bottom - Area(pg.Notes.Count, pg.NoteHeight))
                        lt = Math.Max(top, bottom - Area(pg.Notes.Count, pg.NoteHeight) - line.Height);
                    if (!tailLine && !empty && lt + line.Height + together > bottom - Area(pg.Notes.Count + fresh.Count + moreNotes, pg.NoteHeight + add + addMore))
                    {
                        if (MayGive())
                        {
                            bool whole = k < full.Count && full[k].Start == s && ReferenceEquals(line, full[k]);
                            bool opens = s == 0 && Starts(i);
                            bool byHand = s > 0 && text[s - 1] == Document.LineBreak;
                            long hole = Hole(), lastH = pg.Lines[^1].Line.Height;
                            if (whole && (!opens || hole >= lastH))
                            {
                                // The rest of the section; of the list; of the paragraph.
                                int upTo = SectionEnd(i), listEnd = -1;
                                bool done = PullIn(i, k, upTo);
                                if (!done && p.List != null && (s > 0 || pg.Lines[^1].Para == i - 1 && body[i - 1].List == p.List))
                                {
                                    listEnd = i;
                                    while (listEnd < upTo && body[listEnd + 1].List == p.List) listEnd++;
                                    if (listEnd < upTo) done = PullIn(i, k, listEnd);
                                    else listEnd = -1;
                                }
                                if (!done && upTo > i && listEnd != i && (s > 0 && !byHand || s == 0 && OnlyObjects(p))) done = PullIn(i, k, i);
                                if (done) continue;
                            }
                            if (whole && s > 0 && !byHand && PushOver(i) is var back && back >= 0)
                            {
                                pushFrom = back;
                                goto Pushed;
                            }
                            if (!opens && hole >= lastH) Fill();
                        }
                        NewPage();
                        continue;
                    }
                    if (s == 0) began[i] = (pg.Lines.Count, pg.Notes.Count, pg.NoteHeight);
                    line.Page = Pages.Count - 1;
                    line.Top = (int)lt;
                    y = lt + line.Height;
                    pg.Lines.Add((i, line));
                    foreach (var note in fresh)
                    {
                        placed.Add(note.Id);
                        pg.Notes.Add((note, NoteNumbers[note.Id]));
                    }
                    pg.NoteHeight += add;
                    break;
                }
                if (own == null && !(count < full.Count && ReferenceEquals(full[count], line))) own = full.GetRange(0, count);
                own?.Add(line);
                count++;
                s = line.End;
            } while (s < text.Length || EndsOpen(text, line));
            y += Doc.AfterOf(p);
            p.Lines = own ?? full;
            continue;
        Pushed:
            // The paragraph, with the headings that keep with it, begins the next page.
            NewPage();
            i = pushFrom - 1;
        }
        Finish();
        PlaceBands();
    }

    /// <summary>Giving room at the ends of pages (FORMAT.md §6): how much a gap between paragraphs may shrink, in thousandths
    /// of itself (an ordinary one; one larger than is usual on its page), and grow (an ordinary one; one less than half the usual).</summary>
    public const int GiveMost = 200, BigGiveMost = 400, GrowMost = 400, SmallGrowMost = 250;

    /// <summary>The room between two paragraphs on a page: the solid units above and below it (places in the page's list of
    /// lines), its size, the least and the most it may become, and the size it gets.</summary>
    sealed class RoomGap
    {
        public int Above, Below;
        /// <summary>Top: that of the unit below (or of the line that would begin there).</summary>
        public long Size, Least, Most, New, Top;
    }

    static bool Keeps(FloatBox f) => f.Pic.Wrap is "topbottom" or "square";

    /// <summary>The place, among the first `units` units of the page, of the line that holds the picture's item; -1 if none does.</summary>
    static int AnchorOf(PageBox pg, FloatBox f, int units)
    {
        for (int j = 0; j < units; j++)
        {
            var (para, l) = pg.Lines[j];
            if (para == f.Para && l.Row < 0 && f.Off >= l.Start && f.Off < l.End) return j;
        }
        return -1;
    }

    /// <summary>A line with nothing to show: of a paragraph without items, list marker or lines at its edges.</summary>
    bool Blank(int para, LineBox l)
    {
        var p = Doc.Body[para];
        return p.Table == null && p.Length == 0 && l.Mark == null && Doc.BordersOf(p).None;
    }

    /// <summary>The gaps between the paragraphs among the first `units` units of a page, below `low` (the pictures the
    /// text keeps clear of), each with the least and the most it may become. With `next` (0 or more): one more gap, between
    /// the last solid unit and a line that would begin a paragraph there with its top at `next`.</summary>
    List<RoomGap> GapsOf(PageBox pg, int units, long next = -1)
    {
        var gaps = new List<RoomGap>();
        int prev = -1;
        for (int j = 0; j < units; j++)
        {
            var (para, l) = pg.Lines[j];
            if (Blank(para, l)) continue;
            if (prev >= 0 && pg.Lines[prev].Para != para)
            {
                var up = pg.Lines[prev].Line;
                gaps.Add(new RoomGap { Above = prev, Below = j, Top = l.Top, Size = Math.Max(0, (long)l.Top - ((long)up.Top + up.Height)) });
            }
            prev = j;
        }
        if (next >= 0 && prev >= 0)
        {
            var up = pg.Lines[prev].Line;
            gaps.Add(new RoomGap { Above = prev, Below = units, Top = next, Size = Math.Max(0, next - ((long)up.Top + up.Height)) });
        }
        var sizes = gaps.Where(g => g.Size > 0).Select(g => g.Size).OrderBy(x => x).ToList();
        long usual = sizes.Count == 0 ? 0 : sizes[sizes.Count / 2];
        foreach (var g in gaps)
        {
            g.Least = g.Size - g.Size * (g.Size > usual ? BigGiveMost : GiveMost) / 1000;
            g.Most = g.Size > 2 * usual ? g.Size : g.Size + g.Size * (2 * g.Size < usual ? SmallGrowMost : GrowMost) / 1000;
            g.New = g.Size;
        }
        // A picture the text keeps clear of: what was placed beside or below it after its item moves with it, and nothing
        // moves up into its band.
        foreach (var f in pg.Floats)
        {
            if (!Keeps(f)) continue;
            int unit = AnchorOf(pg, f, pg.Lines.Count), at = Math.Max(0, unit);
            long end = (long)f.Y + f.Pic.Height + FloatGap;
            // The picture moves with its line: it never comes up past the foot of the paragraph above it.
            RoomGap? above = null;
            if (unit >= 0)
                foreach (var g in gaps)
                    if (g.Below <= unit) above = g;
            if (above != null)
            {
                var up = pg.Lines[above.Above].Line;
                above.Least = Math.Max(above.Least, above.Size - Math.Max(0, f.Y - ((long)up.Top + up.Height)));
            }
            foreach (var g in gaps)
            {
                if (g.Above < at) continue;
                if (g.Top < end) g.Least = g.Most = g.Size;
                else if (pg.Lines[g.Above].Line.Top < end) g.Least = Math.Max(g.Least, g.Size - (g.Top - end));
            }
        }
        return gaps;
    }

    /// <summary>Takes n from the gaps, from the largest first, none below its least. False if they cannot give that much.</summary>
    static bool Shrink(List<RoomGap> gaps, long n)
    {
        long can = 0, hi = 0;
        foreach (var g in gaps) { can += g.Size - g.Least; hi = Math.Max(hi, g.Size); }
        if (n <= 0 || can < n) return false;
        long Taken(long level)
        {
            long t = 0;
            foreach (var g in gaps) t += g.Size - Math.Max(g.Least, Math.Min(g.Size, level));
            return t;
        }
        long lo = 0;
        while (hi - lo > 1)
        {
            long mid = lo + (hi - lo) / 2;
            if (Taken(mid) >= n) lo = mid;
            else hi = mid;
        }
        foreach (var g in gaps) g.New = Math.Max(g.Least, Math.Min(g.Size, lo));
        return true;
    }

    /// <summary>Adds nearly n to the gaps (no more, and less by fewer than the number of gaps that may grow), to the smallest
    /// first, none above its most. False if they cannot take that much.</summary>
    static bool Stretch(List<RoomGap> gaps, long n)
    {
        int c = 0;
        long can = 0, hi = 0;
        foreach (var g in gaps)
        {
            if (g.Most > g.Size) c++;
            can += g.Most - g.Size;
            hi = Math.Max(hi, g.Most);
        }
        n = n - c + 1;
        if (n <= 0) return true;
        if (can < n) return false;
        long Added(long level)
        {
            long a = 0;
            foreach (var g in gaps) a += Math.Min(g.Most, Math.Max(g.Size, level)) - g.Size;
            return a;
        }
        long lo = 0;
        while (hi - lo > 1)
        {
            long mid = lo + (hi - lo) / 2;
            if (Added(mid) >= n) hi = mid;
            else lo = mid;
        }
        foreach (var g in gaps) g.New = Math.Min(g.Most, Math.Max(g.Size, hi));
        return true;
    }

    /// <summary>Moves the first `units` units of a page so that every gap has its new size; empty lines in a gap keep their
    /// place in it in proportion, and their height. Returns how far the last of them moved.</summary>
    static long Shift(PageBox pg, List<RoomGap> gaps, int units)
    {
        if (units == 0) return 0;
        var tops = new long[units];
        long d = 0;
        int g = 0;
        for (int j = 0; j < units; j++)
        {
            var l = pg.Lines[j].Line;
            while (g < gaps.Count && gaps[g].Below < j) g++;
            if (g < gaps.Count && gaps[g].Below == j)
            {
                d += gaps[g].New - gaps[g].Size;
                tops[j] = l.Top + d;
            }
            else if (g < gaps.Count && j > gaps[g].Above)
            {
                // An empty line in the gap.
                var gap = gaps[g];
                var up = pg.Lines[gap.Above].Line;
                long foot = (long)up.Top + up.Height;
                tops[j] = foot + d + (gap.Size > 0 ? (l.Top - foot) * gap.New / gap.Size : 0);
            }
            else tops[j] = l.Top + d;
        }
        long last = tops[units - 1] - pg.Lines[units - 1].Line.Top;
        // A picture moves with the line that holds its item.
        foreach (var f in pg.Floats)
        {
            int at = AnchorOf(pg, f, units);
            if (at >= 0) f.Y += (int)(tops[at] - pg.Lines[at].Line.Top);
        }
        for (int j = 0; j < units; j++)
        {
            var (para, l) = pg.Lines[j];
            int delta = (int)(tops[j] - l.Top);
            if (delta == 0) continue;
            l.Top += delta;
            if (l.Row < 0) continue;
            foreach (var cell in pg.Cells)
                if (cell.Para == para && cell.Row == l.Row)
                {
                    cell.Y += delta;
                    foreach (var cp in cell.Cell.Paras)
                        foreach (var cl in cp.Lines!) cl.Top += delta;
                }
        }
        return last;
    }

    /// <summary>A paragraph that holds items and nothing else but tabs, spaces and line breaks.</summary>
    static bool OnlyObjects(Paragraph p) =>
        p.Objects.Length > 0 && p.Text.All(c => c is Run.ObjectChar or '\t' or ' ' or '\u00A0' or Document.LineBreak);

    static int FloorHalf(int v) => v >= 0 ? v / 2 : -((-v + 1) / 2);

    /// <summary>The format of the header or the footer: that of the paragraph style of that name.</summary>
    public Span BandSpan(bool footer) => StyleSpan(Doc.FindPara(footer ? "Footer" : "Header") ?? Doc.ParaStyles[0]);

    /// <summary>The size the program gives a picture of a header or footer: its own, but no taller than the margin lets it be
    /// (the picture stands with its middle on the middle of the line, keeps BandEdge from the edge of the page and half of BandGap
    /// from the text area) and no wider than the text area, or a third of it if texts stand in other places of the same line.</summary>
    public (int W, int H) FitBandPicture(bool footer, long w, long h, bool shares, int rows = 1)
    {
        var sp = BandSpan(footer);
        long line = sp.Asc + sp.Desc, mid = BandDistance + rows * line / 2;
        long margin = footer ? Doc.Page.Bottom : Doc.Page.Top;
        long maxH = Math.Max(line, 2 * Math.Min(mid - BandEdge, margin - mid - BandGap / 2));
        long maxW = shares ? Doc.Page.ContentWidth / 3 : Doc.Page.ContentWidth;
        w = Math.Clamp(w, 1, 100000000);
        h = Math.Clamp(h, 1, 100000000);
        if (h > maxH) { w = Math.Max(1, w * maxH / h); h = maxH; }
        if (w > maxW) { h = Math.Max(1, h * maxW / w); w = maxW; }
        return ((int)w, (int)h);
    }

    /// <summary>The margin a header (or footer) needs so that its lines do not reach into the text area; 0 if there is none.</summary>
    public int BandRoom(bool footer)
    {
        if (!Doc.HasBand(footer)) return 0;
        var sp = BandSpan(footer);
        int rows = (footer ? Doc.Footer : Doc.Header).Rows;
        if (Doc.BandsSkipFirst) rows = Math.Max(rows, (footer ? Doc.FirstFooter : Doc.FirstHeader).Rows);
        return BandDistance + rows * (sp.Asc + sp.Desc) + BandGap;
    }

    /// <summary>Header and footer of every page: up to three texts each, at the left, in the middle and at the right.</summary>
    void PlaceBands()
    {
        if (!Doc.HasBand(false) && !Doc.HasBand(true)) return;
        string total = Pages.Count.ToString(CultureInfo.InvariantCulture);
        int left = Doc.Page.Left, width = Doc.Page.ContentWidth;
        for (int k = 0; k < Pages.Count; k++)
        {
            string number = (k + 1).ToString(CultureInfo.InvariantCulture);
            foreach (bool footer in new[] { false, true })
            {
                var band = footer ? Doc.FooterOf(k) : Doc.HeaderOf(k);
                if (band.IsEmpty) continue;
                var sp = BandSpan(footer);
                // The header or footer is as many lines high as its longest text; its texts begin on its first line.
                int pitch = sp.Asc + sp.Desc, rows = band.Rows;
                int top = footer ? Doc.Page.Height - BandDistance - rows * pitch : BandDistance;
                string[] parts = { band.Left, band.Centre, band.Right };
                for (int i = 0; i < 3; i++)
                {
                    string[] lines = Band.LinesOf(parts[i].Replace("{pages}", total).Replace("{page}", number));
                    var widths = new int[lines.Length];
                    int w = 0;
                    bool any = false;
                    for (int j = 0; j < lines.Length; j++)
                    {
                        if (lines[j].Length == 0) continue;
                        any = true;
                        widths[j] = ChartLayout.TextWidth(sp.Font.Face, lines[j], sp.SizeEmu);
                        w = Math.Max(w, widths[j]);
                    }
                    var pic = band.Picture is { } own && own.Place == i ? own : null;
                    if (!any && pic == null) continue;
                    // A picture and the text of its place stand side by side and are placed as one.
                    int lead = pic == null ? 0 : pic.Width + (any ? BandGap : 0);
                    int group = lead + w;
                    int x = left + (i == 0 ? 0 : i == 1 ? FloorHalf(width - group) : width - group);
                    if (pic != null)
                        Pages[k].BandPics.Add(new BandPic
                        {
                            Footer = footer, Pic = new Picture(pic.Media, pic.Width, pic.Height, false),
                            X = i == 2 ? x + group - pic.Width : x, Y = top + FloorHalf(rows * pitch - pic.Height),
                        });
                    for (int j = 0; j < lines.Length; j++)
                    {
                        if (lines[j].Length == 0) continue;
                        // Without a picture each line is placed by itself; beside a picture the lines begin (at the right: end) together.
                        int lx = pic != null ? (i == 2 ? x + w - widths[j] : x + lead)
                            : left + (i == 0 ? 0 : i == 1 ? FloorHalf(width - widths[j]) : width - widths[j]);
                        Pages[k].Bands.Add(new BandText { Footer = footer, X = lx, Baseline = top + j * pitch + sp.Asc, Text = lines[j] });
                    }
                }
            }
        }
    }

    // ---- positions ----

    public LineBox LineAt(int story, Pos pos, bool atEnd, out int index)
    {
        var p = Paras(story)[pos.Para];
        var lines = p.Lines!;
        int i = lines.Count - 1;
        for (int k = 0; k < lines.Count; k++)
            if (pos.Off < lines[k].End) { i = k; break; }
        if (atEnd && i > 0 && pos.Off == lines[i].Start && p.Text[lines[i].Start - 1] != Document.LineBreak) i--;
        index = i;
        return lines[i];
    }

    /// <summary>Caret x on the page for a position.</summary>
    public int CaretX(int story, Pos pos, bool atEnd)
    {
        var line = LineAt(story, pos, atEnd, out _);
        return Doc.Page.Left + line.XOffset(pos.Off - line.Start);
    }

    /// <summary>The offset in the paragraph nearest to page x on the given line.</summary>
    public int OffsetAtX(Paragraph p, LineBox line, int x, out bool atEnd)
    {
        string t = p.Text;
        int xr = x - Doc.Page.Left - line.X;
        int max = line.Length;
        if (max > 0 && t[line.End - 1] == Document.LineBreak) max--;
        int cum = 0, k = 0;
        for (; k < max; k++)
        {
            int a = line.Adv[k];
            if (xr < cum + (a + 1) / 2) break;
            cum += a;
        }
        if (k > 0 && k < line.Length && char.IsLowSurrogate(t[line.Start + k]) && char.IsHighSurrogate(t[line.Start + k - 1])) k++;
        atEnd = k > 0 && k == line.Length && line.End < t.Length;
        return line.Start + k;
    }

    /// <summary>The position nearest to a point of a page. With only set, the answer stays in that story.</summary>
    public (int Story, Pos Pos) HitTest(int page, int x, int y, out bool atEnd, int? only = null)
    {
        page = Math.Clamp(page, 0, Pages.Count - 1);
        var pg = Pages[page];
        atEnd = false;
        if (only == null || only < 0)
        {
            // In a table cell: the line at y among the lines of that cell.
            foreach (var cb in pg.Cells)
            {
                if (only != null ? cb.Cell.Id != -only : x < cb.X || x >= cb.X + cb.W || y < cb.Y || y >= cb.Y + cb.H) continue;
                int para = cb.Cell.Paras.Count - 1;
                var at = cb.Cell.Paras[^1].Lines![^1];
                bool inCell = false;
                for (int k = 0; k < cb.Cell.Paras.Count && !inCell; k++)
                    foreach (var l in cb.Cell.Paras[k].Lines!)
                        if (y < l.Top + l.Height)
                        {
                            para = k;
                            at = l;
                            inCell = true;
                            break;
                        }
                return (-cb.Cell.Id, new Pos(para, OffsetAtX(cb.Cell.Paras[para], at, x, out atEnd)));
            }
            if (only != null)
            {
                // The wanted cell is on another page.
                var cp = Doc.Cells[-only.Value].Paras;
                bool below = cp[0].Lines != null && page > cp[0].Lines![0].Page;
                return (only.Value, below ? new Pos(cp.Count - 1, cp[^1].Length) : new Pos(0, 0));
            }
        }
        int story = only ?? (pg.Notes.Count > 0 && y >= pg.NoteTop + NoteGapAbove ? -1 : 0);
        if (story == 0)
        {
            var hit = pg.Lines[^1];
            foreach (var l in pg.Lines)
                if (y < l.Line.Top + l.Line.Height) { hit = l; break; }
            return (0, new Pos(hit.Para, OffsetAtX(Doc.Body[hit.Para], hit.Line, x, out atEnd)));
        }
        // In the footnotes: the line at y among the notes of this page (or of the one wanted).
        (Note Note, int Para, LineBox Line)? found = null, last = null;
        foreach (var (note, _) in pg.Notes)
        {
            if (story > 0 && note.Id != story) continue;
            for (int k = 0; k < note.Paras.Count && found == null; k++)
                foreach (var l in note.Paras[k].Lines!)
                {
                    last = (note, k, l);
                    if (y < l.Top + l.Height) { found = last; break; }
                }
            if (found != null) break;
        }
        found ??= last;
        if (found is { } f)
            return (f.Note.Id, new Pos(f.Para, OffsetAtX(f.Note.Paras[f.Para], f.Line, x, out atEnd)));
        // The wanted note is on another page.
        var paras = Doc.Notes[story].Paras;
        bool after = paras[0].Lines != null && page > paras[0].Lines![0].Page;
        return (story, after ? new Pos(paras.Count - 1, paras[^1].Length) : new Pos(0, 0));
    }

    public Span? SpanAt(Paragraph p, int off)
    {
        foreach (var sp in SpansOf(p))
            if (off >= sp.Start && off < sp.End) return sp;
        return null;
    }

    /// <summary>The box of the picture, equation or chart standing in a line at a position of the body.</summary>
    public ObjBox? BoxAt(Pos pos, int story = 0)
    {
        var p = Paras(story)[pos.Para];
        return p.ObjectAt(pos.Off) is { } o && o is not NoteRef && o is not Cite && o is not Picture { Floating: true } && SpanAt(p, pos.Off) is { } sp
            ? Objects.Measure(o, sp) : null;
    }

    public FloatBox? FloatAt(Pos pos) => floatAt.GetValueOrDefault((pos.Para, pos.Off));

    /// <summary>Where an object of the body stands: page and rectangle.</summary>
    public (int Page, int X, int Y, int W, int H)? ObjectRect(Pos pos, int story = 0)
    {
        if (story == 0 && FloatAt(pos) is { } f) return (f.Page, f.X, f.Y, f.Pic.Width, f.Pic.Height);
        if (BoxAt(pos, story) is not { } b || Paras(story)[pos.Para].Lines == null) return null;
        var line = LineAt(story, pos, false, out _);
        return (line.Page, Doc.Page.Left + line.XOffset(pos.Off - line.Start), line.Top + line.Baseline - b.H, b.W, b.H + b.D);
    }

    /// <summary>True when the point is on the text of a line (between its first and last item).</summary>
    bool OnText(PageBox pg, int x, int y)
    {
        foreach (var (_, line) in pg.Lines)
        {
            if (y < line.Top || y >= line.Top + line.Height) continue;
            int x0 = Doc.Page.Left + line.X;
            return x >= x0 && x < x0 + line.XOffset(line.Length) - line.X;
        }
        return false;
    }

    /// <summary>The same, also for what stands in a table cell: the story it is in and its place there.</summary>
    public (int Story, Pos Pos)? ObjectAtAny(int page, int x, int y)
    {
        if (ObjectAt(page, x, y) is { } inBody) return (0, inBody);
        foreach (var cb in Pages[Math.Clamp(page, 0, Pages.Count - 1)].Cells)
        {
            if (x < cb.X || x >= cb.X + cb.W || y < cb.Y || y >= cb.Y + cb.H) continue;
            for (int k = 0; k < cb.Cell.Paras.Count; k++)
                foreach (var (off, o) in cb.Cell.Paras[k].Objects)
                {
                    var pos = new Pos(k, off);
                    if (ObjectRect(pos, -cb.Cell.Id) is { } r && x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H) return (-cb.Cell.Id, pos);
                }
        }
        return null;
    }

    /// <summary>The picture, equation or chart under a point of a page, if any. A picture behind the text counts only
    /// where no text lies over it.</summary>
    public Pos? ObjectAt(int page, int x, int y)
    {
        var pg = Pages[Math.Clamp(page, 0, Pages.Count - 1)];
        for (int i = pg.Floats.Count - 1; i >= 0; i--)
        {
            var f = pg.Floats[i];
            if (x < f.X || x >= f.X + f.Pic.Width || y < f.Y || y >= f.Y + f.Pic.Height) continue;
            if (f.Pic.Wrap == "behind" && OnText(pg, x, y)) continue;
            return new Pos(f.Para, f.Off);
        }
        foreach (var (para, line) in pg.Lines)
        {
            if (y < line.Top || y >= line.Top + line.Height) continue;
            foreach (var (off, o) in Doc.Body[para].Objects)
            {
                if (off < line.Start || off >= line.End || o is NoteRef || o is Cite) continue;
                var pos = new Pos(para, off);
                if (BoxAt(pos) != null && ObjectRect(pos) is { } r && x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H) return pos;
            }
        }
        return null;
    }
}
