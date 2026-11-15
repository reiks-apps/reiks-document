using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReiksDocument;

/// <summary>What a page is made of, in EMU from the top left corner of the page. The screen, the printer and the PDF
/// each take these pieces in their own way.</summary>
abstract class PageSink
{
    /// <summary>Characters of one font and size, one after another from x on the baseline, each with its advance.</summary>
    public abstract void Text(ResolvedFont font, int size, int x, int baseline, List<int> chars, List<int> advances, int rgb);
    /// <summary>The same turned a quarter turn against the clock around x, baseline: the characters run upward from there.</summary>
    public abstract void TextUp(ResolvedFont font, int size, int x, int baseline, List<int> chars, List<int> advances, int rgb);
    public abstract void Fill(long x, long y, long w, long h, int rgb, byte alpha = 255);
    /// <summary>A line through the points x0, y0, x1, y1, …</summary>
    public abstract void Line(int[] points, int thickness, int rgb);
    /// <summary>A stretch of text that is a link, for what can make it one that works.</summary>
    public virtual void Link(long x, long y, long w, long h, string address) { }
    /// <summary>A line of the table of contents, for what can make it lead to its heading.</summary>
    public virtual void Entry(Paragraph p, long x, long y, long w, long h) { }
    public abstract void Disc(int cx, int cy, int r, int rgb);
    /// <summary>The part from..to of total of a full turn of a disc, clockwise from 12 o'clock.</summary>
    public abstract void Slice(int cx, int cy, int r, long from, long to, long total, int rgb);
    public abstract void Image(Document doc, Picture pic, int x, int y);
}

/// <summary>Turns a laid-out page into the pieces a PageSink takes (FORMAT.md §7).</summary>
static class PageDrawer
{
    const int MarkWidth = 60000, Selection = 0x3399FF;

    /// <summary>Set while the window draws its pages: the stretches of a paragraph the spelling check does not know.
    /// Empty everywhere else, so that print, PDF and pictures carry no marks.</summary>
    public static Func<Paragraph, IReadOnlyList<(int Start, int Length)>?>? Spelling;

    static void Add(List<int> chars, List<int> advs, FontFace face, string s, int size)
    {
        for (int i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) cp = char.ConvertToUtf32(s[i], s[++i]);
            chars.Add(cp);
            advs.Add(face.Advance(cp, size));
        }
    }

    /// <summary>A text with the natural advances of its characters.</summary>
    static void Plain(PageSink sink, ResolvedFont font, int size, int x, int baseline, string text, int rgb, bool up = false)
    {
        if (text.Length == 0) return;
        var chars = new List<int>();
        var advs = new List<int>();
        Add(chars, advs, font.Face, text, size);
        if (up) sink.TextUp(font, size, x, baseline, chars, advs, rgb);
        else sink.Text(font, size, x, baseline, chars, advs, rgb);
    }

    /// <summary>The raised digits of a footnote number, in the given format.</summary>
    static void Number(PageSink sink, Span sp, int number, int x, int baseline)
    {
        if (number > 0)
            Plain(sink, sp.Font, LayoutEngine.RefSize(sp.SizeEmu), x, baseline - LayoutEngine.RefRaise(sp.SizeEmu),
                number.ToString(CultureInfo.InvariantCulture), sp.Fmt.Color);
    }

    /// <summary>What an equation or chart is made of; x and baseY are where the object stands.</summary>
    public static void Prims(PageSink sink, ObjBox box, int x, int baseY, Span sp)
    {
        foreach (var p in box.Prims)
        {
            int rgb = p.Color < 0 ? sp.Fmt.Color : p.Color;
            switch (p.Kind)
            {
                case PrimKind.Glyph:
                    Plain(sink, MathLayout.Font, p.Size, x + p.X, baseY + p.Y, char.ConvertFromUtf32(p.Cp), rgb);
                    break;
                case PrimKind.Text:
                case PrimKind.TextUp:
                    Plain(sink, sp.Font, p.Size, x + p.X, baseY + p.Y, p.Text ?? "", rgb, p.Kind == PrimKind.TextUp);
                    break;
                case PrimKind.Rect:
                    sink.Fill(x + p.X, baseY + p.Y, p.W, p.H, rgb);
                    break;
                case PrimKind.Line:
                    var pts = new int[p.Pts!.Length];
                    for (int i = 0; i < pts.Length; i += 2)
                    {
                        pts[i] = x + p.Pts[i];
                        pts[i + 1] = baseY + p.Pts[i + 1];
                    }
                    sink.Line(pts, p.Thick, rgb);
                    break;
                case PrimKind.Disc:
                    sink.Disc(x + p.X, baseY + p.Y, p.W, rgb);
                    break;
                case PrimKind.Slice:
                    sink.Slice(x + p.X, baseY + p.Y, p.W, p.From, p.To, p.Total, rgb);
                    break;
            }
        }
    }

    const int ShadeOver = 3175;

    static void Line(PageSink sink, LayoutEngine eng, Paragraph p, int para, LineBox line, Pos? selStart, Pos? selEnd, List<int> chars, List<int> advs)
    {
        var doc = eng.Doc;
        string text = p.Text;
        int left = doc.Page.Left;
        int baseY = line.Top + line.Baseline;
        if (p.Toc) sink.Entry(p, left + line.XOffset(0), line.Top, line.XOffset(line.Length) - line.XOffset(0), line.Height);

        // Highlight lies under the selection and under all text of the line.
        foreach (var sp in eng.SpansOf(p))
        {
            int i0 = Math.Max(sp.Start, line.Start), i1 = Math.Min(sp.End, line.End);
            if (sp.Fmt.Highlight < 0 || i0 >= i1) continue;
            int x0 = left + line.XOffset(i0 - line.Start), x1 = left + line.XOffset(i1 - line.Start);
            sink.Fill(x0, baseY - sp.Asc, x1 - x0, sp.Asc + sp.Desc, sp.Fmt.Highlight);
        }

        if (selStart is { } S && selEnd is { } E && S != E)
        {
            int a = S.Para < para ? line.Start : S.Para == para ? Math.Max(S.Off, line.Start) : int.MaxValue;
            int b = E.Para > para ? line.End : E.Para == para ? Math.Min(E.Off, line.End) : -1;
            bool mark = E.Para > para && S.Para <= para && line.End == text.Length && ReferenceEquals(p.Lines![^1], line);
            if (a < b || (mark && a <= line.End))
            {
                int x0 = left + line.XOffset(a - line.Start);
                int x1 = left + line.XOffset(Math.Max(a, b) - line.Start) + (mark ? MarkWidth : 0);
                sink.Fill(x0, line.Top, x1 - x0, line.Height, Selection, 96);
            }
        }

        if (line.Mark != null)
        {
            // The bullet or number of a list paragraph, in the format of the paragraph's first item.
            var ms = eng.ListSpan(p);
            Plain(sink, ms.Font, ms.SizeEmu, left + line.MarkX, baseY, line.Mark, ms.Fmt.Color);
        }
        if (line.Length == 0) return;

        // A word the spelling check does not know gets a red wavy line under it.
        if (Spelling?.Invoke(p) is { Count: > 0 } unknown)
            foreach (var (start, length) in unknown)
            {
                int a = Math.Max(start, line.Start), b = Math.Min(start + length, line.End);
                if (a >= b) continue;
                int x0 = left + line.XOffset(a - line.Start), x1 = left + line.XOffset(b - line.Start);
                Underline(sink, "wave", x0, baseY + 13000, x1 - x0, 9000, 0xD23030);
            }

        bool hasObjects = p.Objects.Length > 0;
        int leader = p.Toc ? text.LastIndexOf('\t') : -1;
        foreach (var sp in eng.SpansOf(p))
        {
            int i0 = Math.Max(sp.Start, line.Start), i1 = Math.Min(sp.End, line.End);
            if (i0 >= i1) continue;
            var face = sp.Font.Face;
            int rgb = sp.Fmt.Color;
            int x = left + line.XOffset(i0 - line.Start);
            int spanX0 = x, runX = x, inkEnd = x;
            chars.Clear();
            advs.Clear();
            void Flush()
            {
                if (chars.Count == 0) return;
                sink.Text(sp.Font, sp.DrawSize, runX, baseY - sp.Shift, chars, advs, rgb);
                chars.Clear();
                advs.Clear();
            }
            for (int i = i0; i < i1; i++)
            {
                char c = text[i];
                int a = line.Adv[i - line.Start];
                var obj = hasObjects && c == Run.ObjectChar ? p.ObjectAt(i) : null;
                if (c == '\t' || c == Document.LineBreak || obj != null)
                {
                    Flush();
                    if (i == leader)
                    {
                        // The dots that lead from a title to its page number; on every line they stand in the same places.
                        long t = Math.Max(1, U.RoundDiv((long)face.UnderlineThick * sp.SizeEmu, face.UnitsPerEm));
                        for (long dot = (x + 6 * t - 1) / (4 * t) * (4 * t); dot + t <= x + a - 2 * t; dot += 4 * t)
                            sink.Fill(dot, baseY - t, t, t, rgb);
                    }
                    if (obj is Picture { Floating: true }) { }
                    else if (obj is Picture pic) sink.Image(doc, pic, x, baseY - pic.Height);
                    else if (obj is NoteRef nr) Number(sink, sp, eng.NoteNumbers.GetValueOrDefault(nr.Id), x, baseY);
                    else if (obj is Cite ci) Plain(sink, sp.Font, sp.DrawSize, x, baseY - sp.Shift, eng.CiteText(ci), rgb);
                    else if (obj != null) Prims(sink, Objects.Measure(obj, sp), x, baseY, sp);
                    x += a;
                    runX = x;
                    if (c != Document.LineBreak) inkEnd = x;
                    continue;
                }
                if (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(text[i - 1])) { x += a; continue; }
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(c, text[i + 1]);
                chars.Add(cp);
                if (line.Hyphen && i == line.End - 1)
                {
                    // The hyphen of a word divided here stands in the room the last item's advance holds for it.
                    int ha = face.Advance('-', sp.DrawSize);
                    advs.Add(a - ha);
                    chars.Add('-');
                    advs.Add(ha);
                }
                else advs.Add(a);
                x += a;
                inkEnd = x;
            }
            Flush();
            if (sp.Fmt.Strike && inkEnd > spanX0)
            {
                long top = baseY - sp.Shift - U.RoundDiv((long)face.StrikePos * sp.DrawSize, face.UnitsPerEm);
                long thick = Math.Max(1, U.RoundDiv((long)face.StrikeSize * sp.DrawSize, face.UnitsPerEm));
                sink.Fill(spanX0, top, inkEnd - spanX0, thick, rgb);
            }
            if (sp.Fmt.Underline && inkEnd > spanX0)
            {
                long top = baseY + U.RoundDiv((long)-face.UnderlinePos * sp.SizeEmu, face.UnitsPerEm);
                long thick = Math.Max(1, U.RoundDiv((long)face.UnderlineThick * sp.SizeEmu, face.UnitsPerEm));
                Underline(sink, sp.Fmt.Under, spanX0, top, inkEnd - spanX0, thick, rgb);
            }
            if (sp.Fmt.Link.Length > 0 && inkEnd > spanX0) sink.Link(spanX0, line.Top, inkEnd - spanX0, line.Height, sp.Fmt.Link);
        }
    }

    /// <summary>An underline of one of the kinds, w wide from x, with t the thickness the font gives (FORMAT.md §7).</summary>
    static void Underline(PageSink sink, string kind, long x, long top, long w, long t, int rgb)
    {
        switch (kind)
        {
            case "double":
                sink.Fill(x, top, w, t, rgb);
                sink.Fill(x, top + 2 * t, w, t, rgb);
                break;
            case "thick":
                sink.Fill(x, top, w, 2 * t, rgb);
                break;
            case "dotted":
            case "dashed":
                long on = kind == "dotted" ? t : 6 * t, off = kind == "dotted" ? t : 3 * t;
                for (long at = x; at < x + w; at += on + off) sink.Fill(at, top, Math.Min(on, x + w - at), t, rgb);
                break;
            case "wave":
                long half = 2 * t;
                int n = (int)(w / half);
                if (n < 1) { sink.Fill(x, top, w, t, rgb); break; }
                var pts = new int[2 * (n + 1)];
                for (int k = 0; k <= n; k++)
                {
                    pts[2 * k] = (int)(x + k * half);
                    pts[2 * k + 1] = (int)(top + t / 2 + (k % 2 == 1 ? half : 0));
                }
                sink.Line(pts, (int)t, rgb);
                break;
            default:
                sink.Fill(x, top, w, t, rgb);
                break;
        }
    }

    /// <summary>One page. The selection belongs to selStory (0 = body, else a footnote id).</summary>
    public static void Page(PageSink sink, LayoutEngine eng, int page, int selStory, Pos? selStart, Pos? selEnd)
    {
        var doc = eng.Doc;
        var pg = eng.Pages[page];
        var chars = new List<int>();
        var advs = new List<int>();
        foreach (var b in pg.Bands)
        {
            // Header and footer, in the format of the paragraph style of that name.
            var sp = eng.BandSpan(b.Footer);
            Plain(sink, sp.Font, sp.SizeEmu, b.X, b.Baseline, b.Text, sp.Fmt.Color);
        }
        foreach (var b in pg.BandPics) sink.Image(doc, b.Pic, b.X, b.Y);
        // Floating pictures: those behind the text first, all others over it.
        foreach (var f in pg.Floats)
            if (f.Pic.Wrap == "behind") sink.Image(doc, f.Pic, f.X, f.Y);
        for (int q = 0; q < pg.Lines.Count; q++)
        {
            var (para, line) = pg.Lines[q];
            var bp = doc.Body[para];
            if (bp.Table == null && !doc.BordersOf(bp).None)
            {
                // Lines at the sides run on through the room below the paragraph when the next one has the same ones.
                long through = 0;
                if (line.End >= bp.Length && q + 1 < pg.Lines.Count && pg.Lines[q + 1].Para == para + 1 && doc.Body[para + 1].Table == null
                    && SidesGoOn(eng, bp, doc.Body[para + 1])) through = Math.Max(0, pg.Lines[q + 1].Line.Top - (line.Top + line.Height));
                Borders(sink, eng, bp, line, doc.Page.Left, doc.Page.ContentWidth, through);
            }
            Line(sink, eng, bp, para, line, selStory == 0 ? selStart : null, selStory == 0 ? selEnd : null, chars, advs);
        }
        // The shading of the cells lies under all their lines and texts. Towards a cell to its right or below, a shading
        // reaches a little further, so that no seam shows between two shaded cells.
        foreach (var cb in pg.Cells)
            if (doc.Body[cb.Para].Table is { } shaded && shaded.Shades.TryGetValue(cb.Cell.Id, out int shade))
                sink.Fill(cb.X, cb.Y, cb.W + (cb.Col + cb.Cols < shaded.Columns ? ShadeOver : 0), cb.H + (cb.Row + cb.Rows < shaded.Rows.Count ? ShadeOver : 0), shade);
        foreach (var cb in pg.Cells)
        {
            // A table cell: its text, then the lines its table gives it, each centred on an edge.
            int cellStory = -cb.Cell.Id;
            bool inSelection = selStory == 0 && selStart is { } ts && selEnd is { } te && ts.Para <= cb.Para && te.Para > cb.Para;
            if (inSelection) sink.Fill(cb.X, cb.Y, cb.W, cb.H, Selection, 96);
            for (int k = 0; k < cb.Cell.Paras.Count; k++)
                foreach (var line in cb.Cell.Paras[k].Lines!)
                {
                    Borders(sink, eng, cb.Cell.Paras[k], line, doc.Page.Left + cb.Cell.Paras[k].CellX, cb.Cell.Paras[k].CellW, 0);
                    Line(sink, eng, cb.Cell.Paras[k], k, line, selStory == cellStory ? selStart : null, selStory == cellStory ? selEnd : null, chars, advs);
                }
            var grid = doc.Body[cb.Para].Table;
            string kind = grid?.Lines ?? "all";
            int t = grid?.LineWidth ?? TableGrid.DefaultLine, half = t / 2;
            bool all = kind == "all", across = all || kind == "horizontal", outer = kind == "outer";
            if (across || outer && cb.Row == 0) sink.Fill(cb.X - half, cb.Y - half, cb.W + t, t, 0);
            if (across || outer && grid != null && cb.Row + cb.Rows == grid.Rows.Count) sink.Fill(cb.X - half, cb.Y + cb.H - half, cb.W + t, t, 0);
            if (all || outer && cb.Col == 0) sink.Fill(cb.X - half, cb.Y - half, t, cb.H + t, 0);
            if (all || outer && grid != null && cb.Col + cb.Cols == grid.Columns) sink.Fill(cb.X + cb.W - half, cb.Y - half, t, cb.H + t, 0);
        }
        foreach (var f in pg.Floats)
            if (f.Pic.Wrap != "behind") sink.Image(doc, f.Pic, f.X, f.Y);
        if (pg.Notes.Count == 0) return;

        sink.Fill(doc.Page.Left, pg.NoteTop + LayoutEngine.NoteGapAbove, doc.Page.ContentWidth / 3, LayoutEngine.NoteRule, 0);
        foreach (var (note, number) in pg.Notes)
        {
            bool sel = selStory == note.Id;
            for (int k = 0; k < note.Paras.Count; k++)
            {
                var p = note.Paras[k];
                if (k == 0)
                {
                    var st = doc.StyleOf(p);
                    var first = p.Lines![0];
                    Number(sink, eng.StyleSpan(st), number, doc.Page.Left + first.X - eng.MarkWidth(st, number), first.Top + first.Baseline);
                }
                foreach (var line in p.Lines!)
                {
                    Borders(sink, eng, p, line, doc.Page.Left, doc.Page.ContentWidth, 0);
                    Line(sink, eng, p, k, line, sel ? selStart : null, sel ? selEnd : null, chars, advs);
                }
            }
        }
    }

    /// <summary>The lines at the edges of a paragraph, as far as they belong to one line of its text: the one above with the
    /// first line, the one below with the last, those at the sides beside every line (and on through what follows it).</summary>
    static void Borders(PageSink sink, LayoutEngine eng, Paragraph p, LineBox line, int areaLeft, int areaWidth, long through)
    {
        var b = eng.Doc.BordersOf(p);
        if (b.None) return;
        var (xl, xr) = eng.BorderEdges(p, areaLeft, areaWidth);
        if (line.Start == 0 && b.Top > 0) sink.Fill(xl, line.Top, xr - xl, b.Top, b.Color);
        if (line.End >= p.Length && b.Bottom > 0) sink.Fill(xl, line.Top + line.Height - b.Bottom, xr - xl, b.Bottom, b.Color);
        if (b.Left > 0) sink.Fill(xl, line.Top, b.Left, line.Height + through, b.Color);
        if (b.Right > 0) sink.Fill(xr - b.Right, line.Top, b.Right, line.Height + through, b.Color);
    }

    static bool SidesGoOn(LayoutEngine eng, Paragraph a, Paragraph b)
    {
        ParaBorders x = eng.Doc.BordersOf(a), y = eng.Doc.BordersOf(b);
        return (x.Left > 0 || x.Right > 0) && x.Left == y.Left && x.Right == y.Right && x.SideGap == y.SideGap && x.Color == y.Color
            && eng.BorderEdges(a, 0, 0) == eng.BorderEdges(b, 0, 0);
    }
}

/// <summary>Draws pages with WPF: on screen, into pictures, and for the printer.</summary>
static class PageRenderer
{
    static readonly Dictionary<ResolvedFont, GlyphTypeface> typefaces = new();
    static readonly Dictionary<int, Brush> brushes = new();
    static readonly Dictionary<string, BitmapSource?> images = new(StringComparer.Ordinal);
    static readonly Brush MissingBrush = Frozen(Color.FromRgb(0xE0, 0xE0, 0xE0));
    static readonly Pen MissingPen = FrozenPen(Color.FromRgb(0x90, 0x90, 0x90));

    /// <summary>Set while pages are made for printing. Windows cannot embed every font in a print job
    /// (on some systems not Calibri or Cambria); text in such a font is then drawn as outlines, which look the same.</summary>
    [ThreadStatic] public static bool ForPrint;
    static readonly Dictionary<GlyphTypeface, bool> embeds = new();

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Pen FrozenPen(Color c)
    {
        var p = new Pen(Frozen(c), 1);
        p.Freeze();
        return p;
    }

    static Brush BrushOf(int rgb)
    {
        if (!brushes.TryGetValue(rgb, out var b))
            brushes[rgb] = b = Frozen(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return b;
    }

    static bool Embeds(GlyphTypeface face)
    {
        if (embeds.TryGetValue(face, out bool ok)) return ok;
        try
        {
            face.ComputeSubset(new ushort[] { 0, 1, 2, 3 });
            ok = true;
        }
        catch (Exception) { ok = false; }
        return embeds[face] = ok;
    }

    public static GlyphTypeface Typeface(ResolvedFont f)
    {
        if (typefaces.TryGetValue(f, out var g)) return g;
        string uri = new Uri(f.Face.Info.Path).AbsoluteUri;
        if (f.Face.Info.Index > 0) uri += "#" + f.Face.Info.Index;
        var sim = (StyleSimulations)((f.SimBold ? 1 : 0) | (f.SimItalic ? 2 : 0));
        return typefaces[f] = new GlyphTypeface(new Uri(uri), sim);
    }

    /// <summary>The characters as a WPF glyph run, with its origin at x, y (in pixels at 96 per inch).</summary>
    public static GlyphRun? Run(ResolvedFont font, int size, double x, double y, List<int> chars, List<int> advances, float pixelsPerDip)
    {
        if (chars.Count == 0) return null;
        var glyphs = new ushort[chars.Count];
        var advs = new double[chars.Count];
        for (int i = 0; i < glyphs.Length; i++)
        {
            glyphs[i] = font.Face.Lookup(chars[i]).Glyph;
            advs[i] = U.Dip(advances[i]);
        }
        return new GlyphRun(Typeface(font), 0, false, U.Dip(size), pixelsPerDip, glyphs, new Point(x, y), advs, null, null, null, null, null, null);
    }

    /// <summary>Decodes PNG or JPEG bytes; null if they are neither. jpeg tells which it was.</summary>
    public static BitmapSource? Decode(byte[] data, out bool jpeg)
    {
        jpeg = false;
        try
        {
            var dec = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            jpeg = dec is JpegBitmapDecoder;
            if (!jpeg && dec is not PngBitmapDecoder) return null;
            var frame = dec.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    static BitmapSource? ImageOf(Document doc, string name)
    {
        if (images.TryGetValue(name, out var img)) return img;
        img = doc.Media.TryGetValue(name, out var data) ? Decode(data, out _) : null;
        if (img != null || doc.Media.ContainsKey(name)) images[name] = img;
        return img;
    }

    sealed class WpfSink : PageSink
    {
        readonly DrawingContext dc;
        readonly float ppd;
        /// <summary>The sheet is dark: colours are shown with their lightness turned round. Only the page on the screen has this.</summary>
        readonly bool dark;

        public WpfSink(DrawingContext context, float pixelsPerDip, bool darkSheet = false)
        {
            dc = context;
            ppd = pixelsPerDip;
            dark = darkSheet;
        }

        Brush Ink(int rgb) => BrushOf(dark ? Theme.OnDarkSheet(rgb) : rgb);

        public override void Text(ResolvedFont font, int size, int x, int baseline, List<int> chars, List<int> advances, int rgb)
        {
            if (Run(font, size, U.Dip(x), U.Dip(baseline), chars, advances, ppd) is not { } run) return;
            var brush = Ink(rgb);
            if (!ForPrint || Embeds(run.GlyphTypeface)) { dc.DrawGlyphRun(brush, run); return; }
            var outline = run.BuildGeometry();
            if (!outline.IsEmpty()) dc.DrawGeometry(brush, null, outline);
        }

        public override void TextUp(ResolvedFont font, int size, int x, int baseline, List<int> chars, List<int> advances, int rgb)
        {
            dc.PushTransform(new RotateTransform(-90, U.Dip(x), U.Dip(baseline)));
            Text(font, size, x, baseline, chars, advances, rgb);
            dc.Pop();
        }

        public override void Fill(long x, long y, long w, long h, int rgb, byte alpha = 255)
        {
            Brush brush = alpha == 255 ? Ink(rgb) : Frozen(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            dc.DrawRectangle(brush, null, new Rect(U.Dip(x), U.Dip(y), U.Dip(w), U.Dip(h)));
        }

        public override void Line(int[] points, int thickness, int rgb)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(U.Dip(points[0]), U.Dip(points[1])), false, false);
                for (int i = 2; i < points.Length; i += 2)
                    g.LineTo(new Point(U.Dip(points[i]), U.Dip(points[i + 1])), true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen(Ink(rgb), U.Dip(thickness)) { LineJoin = PenLineJoin.Round }, geo);
        }

        public override void Disc(int cx, int cy, int r, int rgb) =>
            dc.DrawEllipse(Ink(rgb), null, new Point(U.Dip(cx), U.Dip(cy)), U.Dip(r), U.Dip(r));

        public override void Slice(int cx, int cy, int radius, long from, long to, long total, int rgb)
        {
            var at = new Point(U.Dip(cx), U.Dip(cy));
            double r = U.Dip(radius);
            if (to - from >= total) { dc.DrawEllipse(Ink(rgb), null, at, r, r); return; }
            double a0 = 2 * Math.PI * from / total, a1 = 2 * Math.PI * to / total;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(at, true, true);
                g.LineTo(new Point(at.X + r * Math.Sin(a0), at.Y - r * Math.Cos(a0)), true, false);
                g.ArcTo(new Point(at.X + r * Math.Sin(a1), at.Y - r * Math.Cos(a1)), new Size(r, r), 0,
                    (to - from) * 2 > total, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            // A thin outline in the slice's own colour closes the hair-fine seam that smoothing leaves between neighbours.
            dc.DrawGeometry(Ink(rgb), new Pen(Ink(rgb), 0.6) { LineJoin = PenLineJoin.Round }, geo);
        }

        public override void Image(Document doc, Picture pic, int x, int y)
        {
            var rect = new Rect(U.Dip(x), U.Dip(y), U.Dip(pic.Width), U.Dip(pic.Height));
            if (ImageOf(doc, pic.Media) is { } img) dc.DrawImage(img, rect);
            else
            {
                dc.DrawRectangle(MissingBrush, MissingPen, rect);
                dc.DrawLine(MissingPen, rect.TopLeft, rect.BottomRight);
                dc.DrawLine(MissingPen, rect.TopRight, rect.BottomLeft);
            }
        }
    }

    /// <summary>Draws one page. The selection belongs to selStory (0 = body, else a footnote id).</summary>
    public static void DrawPage(DrawingContext dc, LayoutEngine eng, int page, int selStory, Pos? selStart, Pos? selEnd, float ppd, bool darkSheet = false) =>
        PageDrawer.Page(new WpfSink(dc, ppd, darkSheet), eng, page, selStory, selStart, selEnd);

    /// <summary>An equation or chart as a picture for a dialog.</summary>
    /// <summary>Draws an equation or a chart with the left end of its baseline at the origin.</summary>
    public static void DrawBox(DrawingContext dc, ObjBox box, Span sp) => PageDrawer.Prims(new WpfSink(dc, 1f), box, 0, 0, sp);

    public static ImageSource Preview(ObjBox box, Span sp)
    {
        var dg = new DrawingGroup();
        using (var dc = dg.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, U.Dip(Math.Max(box.W, 9525)), U.Dip(Math.Max(box.H + box.D, 9525))));
            PageDrawer.Prims(new WpfSink(dc, 1f), box, 0, box.H, sp);
        }
        dg.Freeze();
        var img = new DrawingImage(dg);
        img.Freeze();
        return img;
    }

    public static BitmapSource RenderPage(LayoutEngine eng, int page, double scale)
    {
        double w = U.Dip(eng.Doc.Page.Width), h = U.Dip(eng.Doc.Page.Height);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            DrawPage(dc, eng, page, 0, null, null, 1f);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    public static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
