using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ReiksDocument;

sealed class OpenResult
{
    public Document Doc = null!;
    public LayoutEngine Engine = null!;
    /// <summary>True when the layout stored in the file is shown as it is.</summary>
    public bool Stored;
    public string Message = "";
}

/// <summary>Reading and writing Reiks documents, .docu files (see FORMAT.md).</summary>
static class RksFile
{
    public const int FormatVersion = 26;
    // The format as it was before it was called docu is no longer read.
    const int OldestFormat = 18;
    // Version 18 is this format without pictures, equations and charts in table cells; it is read as it is.
    static readonly DateTimeOffset ZipTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly JsonWriterOptions Pretty = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonWriterOptions Compact = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>The name a picture file has inside a document: its fingerprint and its kind.</summary>
    public static string MediaName(byte[] data, bool jpeg) => Sha(data) + (jpeg ? ".jpg" : ".png");

    static string AlignName(Align a) => a switch { Align.Center => "center", Align.Right => "right", Align.Justify => "justify", _ => "left" };
    static Align AlignOf(string? s) => s switch { "center" => Align.Center, "right" => Align.Right, "justify" => Align.Justify, _ => Align.Left };
    static string Hex(int color) => (color & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>The lines at the edges of a paragraph or of a style. An object with no line in it says: none.</summary>
    static void WriteBorders(Utf8JsonWriter w, ParaBorders b)
    {
        w.WriteStartObject("borders");
        if (b.Top > 0) w.WriteNumber("top", b.Top);
        if (b.Bottom > 0) w.WriteNumber("bottom", b.Bottom);
        if (b.Left > 0) w.WriteNumber("left", b.Left);
        if (b.Right > 0) w.WriteNumber("right", b.Right);
        if (!b.None)
        {
            w.WriteNumber("gap", b.Gap);
            w.WriteNumber("sideGap", b.SideGap);
            w.WriteString("color", Hex(b.Color));
        }
        w.WriteEndObject();
    }

    static ParaBorders? ReadBorders(JsonElement e) =>
        e.TryGetProperty("borders", out var b) && b.ValueKind == JsonValueKind.Object
            ? new ParaBorders(Int(b, "top", 0), Int(b, "bottom", 0), Int(b, "left", 0), Int(b, "right", 0), Int(b, "gap", 0), Int(b, "sideGap", 0), Color(b) ?? 0).Checked()
            : null;

    // ---- content.json ----

    static void WriteProps(Utf8JsonWriter w, CharProps p)
    {
        if (p.Font != null) w.WriteString("font", p.Font);
        if (p.Size != null) w.WriteNumber("size", p.Size.Value);
        if (p.Bold != null) w.WriteBoolean("bold", p.Bold.Value);
        if (p.Italic != null) w.WriteBoolean("italic", p.Italic.Value);
        if (p.Underline != null) w.WriteBoolean("underline", p.Underline.Value);
        if (p.Under != null) w.WriteString("underlineStyle", p.Under);
        if (p.Link != null) w.WriteString("link", p.Link);
        if (p.Color != null) w.WriteString("color", Hex(p.Color.Value));
        if (p.Strike != null) w.WriteBoolean("strike", p.Strike.Value);
        if (p.Highlight != null) w.WriteString("highlight", p.Highlight.Value < 0 ? "none" : Hex(p.Highlight.Value));
        if (p.Vert != null) w.WriteString("vertical", p.Vert.Value == 1 ? "super" : p.Vert.Value == 2 ? "sub" : "normal");
    }

    static void WritePara(Utf8JsonWriter w, Paragraph p, Document? doc = null)
    {
        w.WriteStartObject();
        w.WriteString("style", p.Style);
        if (p.Table != null && doc != null)
        {
            if (p.Align != null) w.WriteString("align", AlignName(p.Align.Value));
            if (p.PageBreakBefore) w.WriteBoolean("pageBreakBefore", true);
            w.WriteStartObject("table");
            w.WriteStartArray("columns");
            foreach (int cw in p.Table.Widths) w.WriteNumberValue(cw);
            w.WriteEndArray();
            w.WriteStartArray("rows");
            for (int r = 0; r < p.Table.Rows.Count; r++)
            {
                w.WriteStartArray();
                for (int c = 0; c < p.Table.Columns; c++)
                {
                    // A place a joined cell only reaches holds nothing; a plain cell is the list of its paragraphs.
                    if (!p.Table.Starts(r, c))
                    {
                        w.WriteNullValue();
                        continue;
                    }
                    int id = p.Table.Rows[r][c];
                    var (cols, rows) = p.Table.Span(r, c);
                    bool shaded = p.Table.Shades.TryGetValue(id, out int shade), plain = cols == 1 && rows == 1 && !shaded;
                    if (!plain)
                    {
                        w.WriteStartObject();
                        if (cols > 1) w.WriteNumber("cols", cols);
                        if (rows > 1) w.WriteNumber("rows", rows);
                        if (shaded) w.WriteString("shade", Hex(shade));
                        w.WriteStartArray("paras");
                    }
                    else w.WriteStartArray();
                    if (doc.Cells.TryGetValue(id, out var cell))
                        foreach (var cp in cell.Paras) WritePara(w, cp);
                    w.WriteEndArray();
                    if (!plain) w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndArray();
            if (p.Table.Lines != "all") w.WriteString("lines", p.Table.Lines);
            if (p.Table.LineWidth != TableGrid.DefaultLine) w.WriteNumber("lineWidth", p.Table.LineWidth);
            w.WriteEndObject();
            w.WriteEndObject();
            return;
        }
        if (p.Align != null) w.WriteString("align", AlignName(p.Align.Value));
        if (p.PageBreakBefore) w.WriteBoolean("pageBreakBefore", true);
        if (p.Refs) w.WriteBoolean("refs", true);
        if (p.Toc) w.WriteBoolean("toc", true);
        if (p.Together) w.WriteBoolean("together", true);
        if (p.Borders is { } own) WriteBorders(w, own);
        if (p.Line != null) w.WriteNumber("line", p.Line.Value);
        if (p.IndentLeft != null) w.WriteNumber("indentLeft", p.IndentLeft.Value);
        if (p.Before != null) w.WriteNumber("before", p.Before.Value);
        if (p.After != null) w.WriteNumber("after", p.After.Value);
        if (p.IndentRight != null) w.WriteNumber("indentRight", p.IndentRight.Value);
        if (p.IndentFirst != null) w.WriteNumber("indentFirst", p.IndentFirst.Value);
        if (p.Table == null && p.ValidHyphens.Length > 0)
        {
            w.WriteStartArray("hyphens");
            foreach (int h in p.ValidHyphens) w.WriteNumberValue(h);
            w.WriteEndArray();
        }
        if (p.List != null) w.WriteString("list", p.List);
        if (p.List != null && p.ListLevel > 0) w.WriteNumber("level", p.ListLevel);
        if (p.Mark.Props != default || p.Mark.CharStyle != null)
        {
            w.WriteStartObject("mark");
            WriteProps(w, p.Mark.Props);
            if (p.Mark.CharStyle != null) w.WriteString("style", p.Mark.CharStyle);
            w.WriteEndObject();
        }
        w.WriteStartArray("runs");
        foreach (var r in p.Runs)
        {
            w.WriteStartObject();
            switch (r.Obj)
            {
                case Picture pic:
                    w.WriteStartObject("picture");
                    w.WriteString("media", pic.Media);
                    w.WriteNumber("width", pic.Width);
                    w.WriteNumber("height", pic.Height);
                    if (pic.Block) w.WriteBoolean("block", true);
                    if (pic.Floating)
                    {
                        w.WriteString("wrap", pic.Wrap);
                        w.WriteNumber("dx", pic.Dx);
                        w.WriteNumber("dy", pic.Dy);
                    }
                    w.WriteEndObject();
                    break;
                case NoteRef nr:
                    w.WriteNumber("note", nr.Id);
                    break;
                case Cite ci:
                    w.WriteString("cite", ci.Source);
                    break;
                case Equation eq:
                    w.WriteStartObject("equation");
                    w.WriteString("source", eq.Source);
                    if (eq.Block) w.WriteBoolean("block", true);
                    w.WritePropertyName("tree");
                    MathJson.Write(w, eq.Tree);
                    w.WriteEndObject();
                    break;
                case Chart ch:
                    w.WriteStartObject("chart");
                    w.WriteString("type", ch.Kind);
                    w.WriteString("title", ch.Title);
                    if (ch.XTitle.Length > 0) w.WriteString("xTitle", ch.XTitle);
                    if (ch.YTitle.Length > 0) w.WriteString("yTitle", ch.YTitle);
                    if (ch.AxisNames == "ends") w.WriteString("axisNames", "ends");
                    if (ch.Kind == "plot")
                    {
                        w.WriteString("formula", ch.Formula);
                        w.WritePropertyName("from");
                        w.WriteRawValue(ChartLayout.Fmt(ch.From, true));
                        w.WritePropertyName("to");
                        w.WriteRawValue(ChartLayout.Fmt(ch.To, true));
                    }
                    w.WriteNumber("width", ch.Width);
                    w.WriteNumber("height", ch.Height);
                    if (ch.Block) w.WriteBoolean("block", true);
                    w.WriteStartArray("categories");
                    foreach (string c in ch.Cats) w.WriteStringValue(c);
                    w.WriteEndArray();
                    w.WriteStartArray("series");
                    foreach (var s in ch.Series)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", s.Name);
                        w.WriteStartArray("values");
                        foreach (long v in s.Values)
                            if (v == ChartLayout.Missing) w.WriteNullValue();
                            else w.WriteRawValue(ChartLayout.Fmt(v, true));
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    break;
                default:
                    w.WriteString("text", r.Text);
                    break;
            }
            WriteProps(w, r.Props);
            if (r.CharStyle != null) w.WriteString("style", r.CharStyle);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static byte[] ContentJson(Document doc)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Pretty))
        {
            w.WriteStartObject();
            w.WriteStartObject("page");
            w.WriteNumber("width", doc.Page.Width);
            w.WriteNumber("height", doc.Page.Height);
            w.WriteNumber("top", doc.Page.Top);
            w.WriteNumber("bottom", doc.Page.Bottom);
            w.WriteNumber("left", doc.Page.Left);
            w.WriteNumber("right", doc.Page.Right);
            w.WriteEndObject();

            w.WriteStartArray("paragraphStyles");
            foreach (var s in doc.ParaStyles)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", s.Name);
                w.WriteString("font", s.Char.Font);
                w.WriteNumber("size", s.Char.Size);
                w.WriteBoolean("bold", s.Char.Bold);
                w.WriteBoolean("italic", s.Char.Italic);
                w.WriteBoolean("underline", s.Char.Underline);
                if (s.Char.Under != "single") w.WriteString("underlineStyle", s.Char.Under);
                w.WriteString("color", Hex(s.Char.Color));
                if (s.Char.Strike) w.WriteBoolean("strike", true);
                if (s.Char.Highlight >= 0) w.WriteString("highlight", Hex(s.Char.Highlight));
                if (s.Char.Vert != 0) w.WriteString("vertical", s.Char.Vert == 1 ? "super" : "sub");
                w.WriteString("align", AlignName(s.Align));
                w.WriteNumber("before", s.Before);
                w.WriteNumber("after", s.After);
                w.WriteNumber("line", s.Line);
                w.WriteNumber("indentLeft", s.IndentLeft);
                w.WriteNumber("indentRight", s.IndentRight);
                w.WriteNumber("indentFirst", s.IndentFirst);
                w.WriteBoolean("keepNext", s.KeepNext);
                if (!s.Borders.None) WriteBorders(w, s.Borders);
                w.WriteString("next", s.Next);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("characterStyles");
            foreach (var s in doc.CharStyles)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", s.Name);
                WriteProps(w, s.Props);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("body");
            foreach (var p in doc.Body) WritePara(w, p, doc);
            w.WriteEndArray();

            if (doc.Sources.Count > 0)
            {
                w.WriteStartArray("sources");
                foreach (var src in doc.Sources)
                {
                    w.WriteStartObject();
                    w.WriteString("id", src.Id);
                    w.WriteStartArray("authors");
                    foreach (string author in src.Authors) w.WriteStringValue(author);
                    w.WriteEndArray();
                    w.WriteString("year", src.Year);
                    w.WriteString("title", src.Title);
                    w.WriteString("in", src.In);
                    w.WriteString("url", src.Url);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (doc.CiteStyle != Citations.ByName) w.WriteString("citations", doc.CiteStyle);
            if (doc.WidowControl) w.WriteBoolean("widowControl", true);
            if (doc.AdaptSpacing) w.WriteBoolean("adaptSpacing", true);
            if (doc.Hyphenation.Length > 0) w.WriteString("hyphenation", doc.Hyphenation);

            if (!doc.Header.IsEmpty || !doc.Footer.IsEmpty || doc.BandsSkipFirst)
            {
                w.WriteStartObject("headerFooter");
                void WriteBands(Band header, Band footer)
                {
                    foreach (var (name, band) in new[] { ("header", header), ("footer", footer) })
                    {
                        w.WriteStartObject(name);
                        w.WriteString("left", band.Left);
                        w.WriteString("centre", band.Centre);
                        w.WriteString("right", band.Right);
                        if (band.Picture is { } logo)
                        {
                            w.WriteStartObject("picture");
                            w.WriteString("media", logo.Media);
                            w.WriteNumber("width", logo.Width);
                            w.WriteNumber("height", logo.Height);
                            w.WriteString("place", logo.Place == 1 ? "centre" : logo.Place == 2 ? "right" : "left");
                            w.WriteEndObject();
                        }
                        w.WriteEndObject();
                    }
                }
                WriteBands(doc.Header, doc.Footer);
                if (doc.BandsSkipFirst && !(doc.FirstHeader.IsEmpty && doc.FirstFooter.IsEmpty))
                {
                    w.WriteStartObject("first");
                    WriteBands(doc.FirstHeader, doc.FirstFooter);
                    w.WriteEndObject();
                }
                w.WriteBoolean("skipFirstPage", doc.BandsSkipFirst);
                w.WriteEndObject();
            }

            var notes = doc.NotesInOrder();
            if (notes.Count > 0)
            {
                w.WriteStartArray("notes");
                foreach (var n in notes)
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", n.Id);
                    w.WriteStartArray("paragraphs");
                    foreach (var p in n.Paras) WritePara(w, p);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    static int Int(JsonElement e, string name, int def) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : def;
    static bool Bool(JsonElement e, string name, bool def) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : def;
    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Color(JsonElement e) =>
        Str(e, "color") is { } s && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int c) ? c & 0xFFFFFF : null;

    static CharProps ReadProps(JsonElement e) => new(
        Str(e, "font"),
        e.TryGetProperty("size", out _) ? Int(e, "size", 22) : null,
        e.TryGetProperty("bold", out _) ? Bool(e, "bold", false) : null,
        e.TryGetProperty("italic", out _) ? Bool(e, "italic", false) : null,
        e.TryGetProperty("underline", out _) ? Bool(e, "underline", false) : null,
        Color(e),
        e.TryGetProperty("strike", out _) ? Bool(e, "strike", false) : null,
        Highlight(e),
        Vertical(e),
        UnderKind(e),
        Str(e, "link") is { Length: > 0 and <= 2000 } link ? link : null);

    static string? UnderKind(JsonElement e) => Str(e, "underlineStyle") is "single" or "double" or "thick" or "dotted" or "dashed" or "wave" ? Str(e, "underlineStyle") : null;

    static int? Highlight(JsonElement e) =>
        Str(e, "highlight") is not { } s ? null : int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int c) ? c & 0xFFFFFF : -1;

    static int? Vertical(JsonElement e) => Str(e, "vertical") switch { "super" => 1, "sub" => 2, "normal" => 0, _ => null };

    /// <summary>A decimal number as whole thousandths.</summary>
    public static long Thousandths(decimal d) =>
        (long)Math.Round(Math.Clamp(d, -1000000000m, 1000000000m) * 1000m, MidpointRounding.AwayFromZero);

    static Chart ReadChart(JsonElement e)
    {
        string kind = Str(e, "type") is "line" or "pie" or "plot" ? Str(e, "type")! : "bar";
        static long Num(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out decimal d) ? Thousandths(d) : 0;
        var cats = new List<string>();
        if (e.TryGetProperty("categories", out var cs))
            foreach (var c in cs.EnumerateArray()) cats.Add(c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "");
        var series = new List<Series>();
        if (e.TryGetProperty("series", out var ss))
            foreach (var s in ss.EnumerateArray())
            {
                var values = new List<long>();
                if (s.TryGetProperty("values", out var vs))
                    foreach (var v in vs.EnumerateArray())
                        values.Add(v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out decimal d) ? Thousandths(d)
                            : kind == "plot" ? ChartLayout.Missing : 0);
                series.Add(new Series(Str(s, "name") ?? "", values));
            }
        return new Chart(kind, Str(e, "title") ?? "", cats, series, Math.Clamp(Int(e, "width", 4320000), 360000, 100000000),
            Math.Clamp(Int(e, "height", 2700000), 360000, 100000000), Bool(e, "block", false),
            kind == "plot" ? Str(e, "formula") ?? "" : "", kind == "plot" ? Num(e, "from") : 0, kind == "plot" ? Num(e, "to") : 0,
            Str(e, "xTitle") ?? "", Str(e, "yTitle") ?? "", Str(e, "axisNames") == "ends" ? "ends" : "middle");
    }

    /// <summary>A header or footer text is one line: no control characters.</summary>
    public const int MaxBandLines = 8;

    /// <summary>A text of a header or footer: up to eight lines, each without control characters. With tidy, as for a text
    /// the user typed, the lines lose the spaces at their ends and empty lines at the end are dropped.</summary>
    public static string BandLines(string? s, bool tidy = false)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (tidy) s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = s.Split('\n').Take(MaxBandLines).Select(l => tidy ? BandText(l).Trim() : BandText(l)).ToList();
        if (tidy)
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    public static string BandText(string? s) => s == null ? "" : new string(s.Where(c => c >= ' ' && c != (char)0x7F).ToArray());

    /// <summary>objects: a paragraph of the body, which can hold everything. inCell: a paragraph of a table cell, which can hold
    /// pictures, equations and charts in its lines, but no footnotes, citations or pictures that float.</summary>
    static Paragraph ReadPara(JsonElement pe, string defaultStyle, bool objects, bool inCell = false)
    {
        var p = new Paragraph { Style = Str(pe, "style") ?? defaultStyle };
        if (Str(pe, "align") is { } al) p.Align = AlignOf(al);
        p.PageBreakBefore = objects && Bool(pe, "pageBreakBefore", false);
        p.Refs = objects && Bool(pe, "refs", false);
        p.Toc = objects && Bool(pe, "toc", false);
        p.Together = objects && Bool(pe, "together", false);
        p.Borders = ReadBorders(pe);
        if (pe.TryGetProperty("line", out _)) p.Line = Math.Clamp(Int(pe, "line", 1000), 100, 10000);
        if (pe.TryGetProperty("indentLeft", out _)) p.IndentLeft = Math.Max(-LayoutEngine.ListMarkAt, Int(pe, "indentLeft", 0));
        if (pe.TryGetProperty("before", out _)) p.Before = Math.Max(0, Int(pe, "before", 0));
        if (pe.TryGetProperty("after", out _)) p.After = Math.Max(0, Int(pe, "after", 0));
        if (pe.TryGetProperty("indentRight", out _)) p.IndentRight = Math.Max(0, Int(pe, "indentRight", 0));
        if (pe.TryGetProperty("indentFirst", out _)) p.IndentFirst = Int(pe, "indentFirst", 0);
        if (objects && LayoutEngine.IsListKind(Str(pe, "list")))
        {
            p.List = Str(pe, "list");
            int level = Int(pe, "level", 0);
            p.ListLevel = level >= 0 && level <= LayoutEngine.MaxListLevel ? level : 0;
        }
        if (pe.TryGetProperty("mark", out var mk)) p.Mark = new Run("", ReadProps(mk), Str(mk, "style"));
        if (pe.TryGetProperty("runs", out var runs))
            foreach (var re in runs.EnumerateArray())
            {
                var fmt = new Run("", ReadProps(re), Str(re, "style"));
                if (re.TryGetProperty("picture", out var pic))
                {
                    if ((objects || inCell) && Str(pic, "media") is { Length: > 0 } media)
                    {
                        string wrap = objects && Str(pic, "wrap") is "topbottom" or "square" or "behind" or "front" ? Str(pic, "wrap")! : "inline";
                        p.Runs.Add(Run.Object(new Picture(media, Math.Clamp(Int(pic, "width", 1), 1, 100000000),
                            Math.Clamp(Int(pic, "height", 1), 1, 100000000), wrap == "inline" && Bool(pic, "block", false), wrap,
                            wrap == "inline" ? 0 : Int(pic, "dx", 0), wrap == "inline" ? 0 : Int(pic, "dy", 0)), fmt));
                    }
                }
                else if (re.TryGetProperty("cite", out _))
                {
                    if (objects) p.Runs.Add(Run.Object(new Cite(Str(re, "cite") ?? ""), fmt));
                }
                else if (re.TryGetProperty("note", out _))
                {
                    if (objects) p.Runs.Add(Run.Object(new NoteRef(Int(re, "note", 0)), fmt));
                }
                else if (re.TryGetProperty("equation", out var eq))
                {
                    if (!objects && !inCell) continue;
                    string source = Str(eq, "source") ?? "";
                    var tree = eq.TryGetProperty("tree", out var tr) ? MathJson.ReadRow(tr) : MathParser.Parse(source);
                    p.Runs.Add(Run.Object(new Equation(source, tree, Bool(eq, "block", false)), fmt));
                }
                else if (re.TryGetProperty("chart", out var chart))
                {
                    if (objects || inCell) p.Runs.Add(Run.Object(ReadChart(chart), fmt));
                }
                else if (Str(re, "text") is { } text)
                {
                    text = text.Replace(Run.ObjectChar.ToString(), "");
                    if (text.Length > 0) p.Runs.Add(fmt with { Text = text });
                }
            }
        // Only a list can stand further left than the text.
        if (p.IndentLeft < 0 && p.List == null) p.IndentLeft = 0;
        // The places where words may be divided: rising, each inside the text.
        var places = new List<int>();
        if (pe.TryGetProperty("hyphens", out var hy) && hy.ValueKind == JsonValueKind.Array)
            foreach (var he in hy.EnumerateArray())
                if (he.TryGetInt32(out int at) && at > (places.Count > 0 ? places[^1] : 0) && at < p.Length) places.Add(at);
        p.Hyphens = places.ToArray();
        p.HyphenText = p.Text;
        return p;
    }

    public static Document ParseContent(byte[] json)
    {
        using var jd = JsonDocument.Parse(json);
        var root = jd.RootElement;
        var doc = new Document();
        if (root.TryGetProperty("page", out var pg))
        {
            var d = doc.Page;
            d.Width = Math.Clamp(Int(pg, "width", d.Width), 360000, 100000000);
            d.Height = Math.Clamp(Int(pg, "height", d.Height), 360000, 100000000);
            d.Top = Math.Clamp(Int(pg, "top", d.Top), 0, d.Height / 3);
            d.Bottom = Math.Clamp(Int(pg, "bottom", d.Bottom), 0, d.Height / 3);
            d.Left = Math.Clamp(Int(pg, "left", d.Left), 0, d.Width / 3);
            d.Right = Math.Clamp(Int(pg, "right", d.Right), 0, d.Width / 3);
        }
        if (root.TryGetProperty("paragraphStyles", out var pss))
            foreach (var s in pss.EnumerateArray())
            {
                var st = new ParaStyle { Id = Str(s, "id") ?? "", Name = Str(s, "name") ?? Str(s, "id") ?? "" };
                st.Char = new CharFormat(Str(s, "font") ?? "Calibri", Int(s, "size", 22), Bool(s, "bold", false), Bool(s, "italic", false), Bool(s, "underline", false), Color(s) ?? 0,
                    Bool(s, "strike", false), Highlight(s) ?? -1, Vertical(s) ?? 0, UnderKind(s) ?? "single");
                st.Align = AlignOf(Str(s, "align"));
                st.Before = Math.Max(0, Int(s, "before", 0));
                st.After = Math.Max(0, Int(s, "after", 0));
                st.Line = Math.Clamp(Int(s, "line", 1000), 100, 10000);
                st.IndentLeft = Math.Max(0, Int(s, "indentLeft", 0));
                st.IndentRight = Math.Max(0, Int(s, "indentRight", 0));
                st.IndentFirst = Int(s, "indentFirst", 0);
                st.KeepNext = Bool(s, "keepNext", false);
                st.Borders = ReadBorders(s) ?? default;
                st.Next = Str(s, "next") ?? st.Id;
                if (st.Id.Length > 0) doc.ParaStyles.Add(st);
            }
        if (doc.ParaStyles.Count == 0) doc.ParaStyles.AddRange(Document.NewDefault().ParaStyles);
        if (root.TryGetProperty("characterStyles", out var css))
            foreach (var s in css.EnumerateArray())
                if (Str(s, "id") is { Length: > 0 } id)
                    doc.CharStyles.Add(new CharStyle { Id = id, Name = Str(s, "name") ?? id, Props = ReadProps(s) });
        string first = doc.ParaStyles[0].Id;
        if (root.TryGetProperty("body", out var body))
            foreach (var pe in body.EnumerateArray())
            {
                var p = ReadPara(pe, first, true);
                if (pe.TryGetProperty("table", out var te) && te.TryGetProperty("columns", out var cols) && te.TryGetProperty("rows", out var rows))
                {
                    // A table: the widths of its columns and, row by row, the paragraphs of each cell.
                    var grid = new TableGrid { Widths = cols.EnumerateArray().Select(c => c.TryGetInt32(out int v) ? Math.Clamp(v, 90000, 100000000) : 914400).ToArray() };
                    if (Str(te, "lines") is "horizontal" or "outer" or "none") grid.Lines = Str(te, "lines")!;
                    grid.LineWidth = Math.Clamp(Int(te, "lineWidth", TableGrid.DefaultLine), TableGrid.LeastLine, TableGrid.MostLine);
                    var rowList = rows.EnumerateArray().ToList();
                    foreach (var _ in rowList) grid.Rows.Add(new int[grid.Columns]);
                    // 0 stands for a place no cell has taken yet.
                    for (int r = 0; r < rowList.Count; r++)
                    {
                        var ids = grid.Rows[r];
                        int c = 0;
                        if (rowList[r].ValueKind == JsonValueKind.Array)
                            foreach (var ce in rowList[r].EnumerateArray())
                            {
                                if (c >= ids.Length) break;
                                // What stands at a place a joined cell reaches is not looked at.
                                if (ids[c] != 0) { c++; continue; }
                                var cell = doc.NewCell();
                                cell.Paras.Clear();
                                int wide = 1, down = 1;
                                var paras = ce;
                                if (ce.ValueKind == JsonValueKind.Object)
                                {
                                    wide = Math.Clamp(Int(ce, "cols", 1), 1, ids.Length - c);
                                    down = Math.Clamp(Int(ce, "rows", 1), 1, rowList.Count - r);
                                    for (int k = 1; k < wide; k++)
                                        if (ids[c + k] != 0) { wide = k; break; }
                                    if (Str(ce, "shade") is { Length: 6 } sh && int.TryParse(sh, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) grid.Shades[cell.Id] = rgb;
                                    paras = ce.TryGetProperty("paras", out var inner) ? inner : default;
                                }
                                if (paras.ValueKind == JsonValueKind.Array)
                                    foreach (var cp in paras.EnumerateArray()) cell.Paras.Add(ReadPara(cp, first, false, true));
                                if (cell.Paras.Count == 0) cell.Paras.Add(new Paragraph { Style = first });
                                for (int a = 0; a < down; a++)
                                    for (int b = 0; b < wide; b++)
                                        if (grid.Rows[r + a][c + b] == 0) grid.Rows[r + a][c + b] = cell.Id;
                                c++;
                            }
                        for (c = 0; c < ids.Length; c++)
                            if (ids[c] == 0) ids[c] = doc.NewCell().Id;
                    }
                    if (grid.Columns > 0 && grid.Rows.Count > 0)
                    {
                        p.Runs.Clear();
                        p.Table = grid;
                        p.Together = false;
                    }
                }
                doc.Body.Add(p);
            }
        if (doc.Body.Count == 0) doc.Body.Add(new Paragraph { Style = first });
        if (root.TryGetProperty("sources", out var srcs) && srcs.ValueKind == JsonValueKind.Array)
            foreach (var se in srcs.EnumerateArray())
            {
                if (se.ValueKind != JsonValueKind.Object || Str(se, "id") is not { Length: > 0 } sid || doc.Sources.Exists(x => x.Id == sid)) continue;
                var authors = new List<string>();
                if (se.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.Array)
                    foreach (var ae in au.EnumerateArray())
                        if (ae.ValueKind == JsonValueKind.String && ae.GetString()!.Trim() is { Length: > 0 } name) authors.Add(name);
                doc.Sources.Add(new Source(sid, authors, (Str(se, "year") ?? "").Trim(), (Str(se, "title") ?? "").Trim(), (Str(se, "in") ?? "").Trim(), (Str(se, "url") ?? "").Trim()));
            }
        doc.CiteStyle = Citations.Known(Str(root, "citations"));
        doc.WidowControl = Bool(root, "widowControl", false);
        doc.AdaptSpacing = Bool(root, "adaptSpacing", false);
        doc.Hyphenation = Str(root, "hyphenation") is { Length: > 0 and <= 20 } lang ? lang : "";
        if (root.TryGetProperty("headerFooter", out var hf) && hf.ValueKind == JsonValueKind.Object)
        {
            static Band ReadBand(JsonElement e, string name)
            {
                if (!e.TryGetProperty(name, out var b) || b.ValueKind != JsonValueKind.Object) return Band.Empty;
                BandPicture? logo = null;
                if (b.TryGetProperty("picture", out var pe) && pe.ValueKind == JsonValueKind.Object && Str(pe, "media") is { Length: > 0 } media)
                    logo = new BandPicture(media, Math.Clamp(Int(pe, "width", 1), 1, 100000000), Math.Clamp(Int(pe, "height", 1), 1, 100000000),
                        Str(pe, "place") switch { "centre" => 1, "right" => 2, _ => 0 });
                return new Band(BandLines(Str(b, "left")), BandLines(Str(b, "centre")), BandLines(Str(b, "right")), logo);
            }
            doc.Header = ReadBand(hf, "header");
            doc.Footer = ReadBand(hf, "footer");
            doc.BandsSkipFirst = Bool(hf, "skipFirstPage", false);
            if (doc.BandsSkipFirst && hf.TryGetProperty("first", out var onFirst) && onFirst.ValueKind == JsonValueKind.Object)
            {
                doc.FirstHeader = ReadBand(onFirst, "header");
                doc.FirstFooter = ReadBand(onFirst, "footer");
            }
        }
        if (root.TryGetProperty("notes", out var notes))
            foreach (var ne in notes.EnumerateArray())
            {
                var n = new Note { Id = Int(ne, "id", 0) };
                if (n.Id <= 0 || doc.Notes.ContainsKey(n.Id)) continue;
                if (ne.TryGetProperty("paragraphs", out var ps))
                    foreach (var pe in ps.EnumerateArray()) n.Paras.Add(ReadPara(pe, "Footnote", false));
                if (n.Paras.Count == 0) n.Paras.Add(new Paragraph { Style = "Footnote" });
                doc.Notes[n.Id] = n;
            }
        // Every reference has exactly one note: a second reference to a note is dropped, a missing note is made.
        var seen = new HashSet<int>();
        foreach (var p in doc.Body)
        {
            if (p.Objects.Length == 0) continue;
            p.Runs.RemoveAll(r => r.Obj is NoteRef nr && (nr.Id <= 0 || !seen.Add(nr.Id)));
            p.Touch();
            foreach (var (_, o) in p.Objects)
                if (o is NoteRef nr && !doc.Notes.ContainsKey(nr.Id))
                    doc.Notes[nr.Id] = new Note { Id = nr.Id, Paras = { new Paragraph { Style = "Footnote" } } };
        }
        return doc;
    }

    // ---- layout.json ----

    static void WriteLine(Utf8JsonWriter w, Document doc, int para, LineBox l)
    {
        w.WriteStartObject();
        w.WriteNumber("p", para);
        w.WriteNumber("s", l.Start);
        w.WriteNumber("n", l.Length);
        w.WriteNumber("x", doc.Page.Left + l.X);
        w.WriteNumber("y", l.Top);
        w.WriteNumber("h", l.Height);
        w.WriteNumber("b", l.Top + l.Baseline);
        if (l.Row >= 0) w.WriteNumber("row", l.Row);
        if (l.Hyphen) w.WriteBoolean("hy", true);
        if (l.Mark != null)
        {
            w.WriteStartObject("mark");
            w.WriteNumber("x", doc.Page.Left + l.MarkX);
            w.WriteString("text", l.Mark);
            w.WriteEndObject();
        }
        w.WriteStartArray("a");
        foreach (int a in l.Adv) w.WriteNumberValue(a);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>What an equation or chart draws, in page coordinates.</summary>
    static void WritePrims(Utf8JsonWriter w, ObjBox box, int x, int y, int textColor)
    {
        w.WriteStartArray("draw");
        foreach (var p in box.Prims)
        {
            w.WriteStartArray();
            switch (p.Kind)
            {
                case PrimKind.Glyph:
                    w.WriteStringValue("glyph");
                    w.WriteNumberValue(x + p.X);
                    w.WriteNumberValue(y + p.Y);
                    w.WriteNumberValue(p.Size);
                    w.WriteStringValue(char.ConvertFromUtf32(p.Cp));
                    break;
                case PrimKind.Text:
                case PrimKind.TextUp:
                    w.WriteStringValue(p.Kind == PrimKind.TextUp ? "textup" : "text");
                    w.WriteNumberValue(x + p.X);
                    w.WriteNumberValue(y + p.Y);
                    w.WriteNumberValue(p.Size);
                    w.WriteStringValue(p.Text);
                    break;
                case PrimKind.Rect:
                    w.WriteStringValue("rect");
                    w.WriteNumberValue(x + p.X);
                    w.WriteNumberValue(y + p.Y);
                    w.WriteNumberValue(p.W);
                    w.WriteNumberValue(p.H);
                    break;
                case PrimKind.Line:
                    w.WriteStringValue("line");
                    w.WriteNumberValue(p.Thick);
                    for (int i = 0; i < p.Pts!.Length; i += 2)
                    {
                        w.WriteNumberValue(x + p.Pts[i]);
                        w.WriteNumberValue(y + p.Pts[i + 1]);
                    }
                    break;
                case PrimKind.Disc:
                    w.WriteStringValue("disc");
                    w.WriteNumberValue(x + p.X);
                    w.WriteNumberValue(y + p.Y);
                    w.WriteNumberValue(p.W);
                    break;
                case PrimKind.Slice:
                    w.WriteStringValue("slice");
                    w.WriteNumberValue(x + p.X);
                    w.WriteNumberValue(y + p.Y);
                    w.WriteNumberValue(p.W);
                    w.WriteNumberValue(p.From);
                    w.WriteNumberValue(p.To);
                    w.WriteNumberValue(p.Total);
                    break;
            }
            w.WriteStringValue(Hex(p.Color < 0 ? textColor : p.Color));
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    public static byte[] LayoutJson(Document doc, LayoutEngine eng)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Compact))
        {
            w.WriteStartObject();
            w.WriteStartArray("pages");
            foreach (var pg in eng.Pages)
            {
                w.WriteStartObject();
                w.WriteStartArray("lines");
                foreach (var (para, l) in pg.Lines) WriteLine(w, doc, para, l);
                w.WriteEndArray();

                bool any = false;
                foreach (var f in pg.Floats)
                {
                    if (!any) { w.WriteStartArray("objects"); any = true; }
                    w.WriteStartObject();
                    w.WriteString("kind", "picture");
                    w.WriteString("wrap", f.Pic.Wrap);
                    w.WriteNumber("p", f.Para);
                    w.WriteNumber("i", f.Off);
                    w.WriteNumber("x", f.X);
                    w.WriteNumber("y", f.Y);
                    w.WriteNumber("w", f.Pic.Width);
                    w.WriteNumber("h", f.Pic.Height);
                    w.WriteNumber("b", f.Y + f.Pic.Height);
                    w.WriteEndObject();
                }
                foreach (var (para, l) in pg.Lines)
                    foreach (var (off, o) in doc.Body[para].Objects)
                    {
                        if (off < l.Start || off >= l.End || eng.BoxAt(new Pos(para, off)) is not { } box) continue;
                        if (!any) { w.WriteStartArray("objects"); any = true; }
                        int ox = doc.Page.Left + l.XOffset(off - l.Start), oy = l.Top + l.Baseline;
                        w.WriteStartObject();
                        w.WriteString("kind", o is Picture ? "picture" : o is Equation ? "equation" : "chart");
                        w.WriteNumber("p", para);
                        w.WriteNumber("i", off);
                        w.WriteNumber("x", ox);
                        w.WriteNumber("y", oy - box.H);
                        w.WriteNumber("w", box.W);
                        w.WriteNumber("h", box.H + box.D);
                        w.WriteNumber("b", oy);
                        if (box.Prims.Count > 0) WritePrims(w, box, ox, oy, eng.SpanAt(doc.Body[para], off)?.Fmt.Color ?? 0);
                        w.WriteEndObject();
                    }
                if (any) w.WriteEndArray();

                if (pg.Cells.Count > 0)
                {
                    w.WriteStartArray("cells");
                    foreach (var cb in pg.Cells)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("p", cb.Para);
                        w.WriteNumber("r", cb.Row);
                        w.WriteNumber("c", cb.Col);
                        w.WriteNumber("x", cb.X);
                        w.WriteNumber("y", cb.Y);
                        w.WriteNumber("w", cb.W);
                        w.WriteNumber("h", cb.H);
                        w.WriteStartArray("lines");
                        for (int k = 0; k < cb.Cell.Paras.Count; k++)
                            foreach (var l in cb.Cell.Paras[k].Lines!) WriteLine(w, doc, k, l);
                        w.WriteEndArray();
                        // What stands in the lines of the cell besides text, like the objects of the page.
                        bool anyInCell = false;
                        for (int k = 0; k < cb.Cell.Paras.Count; k++)
                            foreach (var l in cb.Cell.Paras[k].Lines!)
                                foreach (var (off, o) in cb.Cell.Paras[k].Objects)
                                {
                                    if (off < l.Start || off >= l.End || eng.BoxAt(new Pos(k, off), -cb.Cell.Id) is not { } box) continue;
                                    if (!anyInCell) { w.WriteStartArray("objects"); anyInCell = true; }
                                    int ox = doc.Page.Left + l.XOffset(off - l.Start), oy = l.Top + l.Baseline;
                                    w.WriteStartObject();
                                    w.WriteString("kind", o is Picture ? "picture" : o is Equation ? "equation" : "chart");
                                    w.WriteNumber("p", k);
                                    w.WriteNumber("i", off);
                                    w.WriteNumber("x", ox);
                                    w.WriteNumber("y", oy - box.H);
                                    w.WriteNumber("w", box.W);
                                    w.WriteNumber("h", box.H + box.D);
                                    w.WriteNumber("b", oy);
                                    if (box.Prims.Count > 0) WritePrims(w, box, ox, oy, eng.SpanAt(cb.Cell.Paras[k], off)?.Fmt.Color ?? 0);
                                    w.WriteEndObject();
                                }
                        if (anyInCell) w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                if (pg.Bands.Count > 0)
                {
                    w.WriteStartArray("bands");
                    foreach (var b in pg.Bands)
                    {
                        w.WriteStartObject();
                        w.WriteString("band", b.Footer ? "footer" : "header");
                        w.WriteNumber("x", b.X);
                        w.WriteNumber("b", b.Baseline);
                        w.WriteString("text", b.Text);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                if (pg.BandPics.Count > 0)
                {
                    w.WriteStartArray("bandPictures");
                    foreach (var b in pg.BandPics)
                    {
                        w.WriteStartObject();
                        w.WriteString("band", b.Footer ? "footer" : "header");
                        w.WriteString("media", b.Pic.Media);
                        w.WriteNumber("x", b.X);
                        w.WriteNumber("y", b.Y);
                        w.WriteNumber("w", b.Pic.Width);
                        w.WriteNumber("h", b.Pic.Height);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                if (pg.Notes.Count > 0)
                {
                    w.WriteStartObject("notes");
                    w.WriteNumber("y", pg.NoteTop + LayoutEngine.NoteGapAbove);
                    w.WriteStartArray("items");
                    foreach (var (note, number) in pg.Notes)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("id", note.Id);
                        w.WriteNumber("number", number);
                        w.WriteStartArray("lines");
                        for (int k = 0; k < note.Paras.Count; k++)
                            foreach (var l in note.Paras[k].Lines!) WriteLine(w, doc, k, l);
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    static LineBox ReadLine(JsonElement le, Document doc, int page, out int para)
    {
        para = le.GetProperty("p").GetInt32();
        int y = le.GetProperty("y").GetInt32();
        var l = new LineBox
        {
            Start = le.GetProperty("s").GetInt32(),
            Length = le.GetProperty("n").GetInt32(),
            X = le.GetProperty("x").GetInt32() - doc.Page.Left,
            Top = y,
            Height = le.GetProperty("h").GetInt32(),
            Baseline = le.GetProperty("b").GetInt32() - y,
            Page = page,
        };
        l.Row = Int(le, "row", -1);
        l.Hyphen = Bool(le, "hy", false);
        if (le.TryGetProperty("mark", out var mk))
        {
            l.Mark = Str(mk, "text");
            l.MarkX = Int(mk, "x", 0) - doc.Page.Left;
        }
        var a = le.GetProperty("a");
        if (l.Length < 0 || a.GetArrayLength() != l.Length) throw new InvalidDataException();
        l.Adv = new int[l.Length];
        int i = 0;
        foreach (var v in a.EnumerateArray()) l.Adv[i++] = v.GetInt32();
        return l;
    }

    /// <summary>True when the lines are contiguous and cover each paragraph.</summary>
    static bool Covers(List<Paragraph> paras, List<LineBox>?[] per)
    {
        for (int i = 0; i < per.Length; i++)
        {
            var lines = per[i];
            if (lines == null) return false;
            int pos = 0;
            foreach (var l in lines)
            {
                if (l.Start != pos) return false;
                pos += l.Length;
            }
            if (pos != paras[i].Length) return false;
        }
        return true;
    }

    /// <summary>Reads a stored layout onto the paragraphs; null if it does not fit the content.</summary>
    static List<PageBox>? ParseLayout(Document doc, LayoutEngine eng, byte[] json)
    {
        try
        {
            eng.SyncNumbers(false);
            using var jd = JsonDocument.Parse(json);
            var pages = new List<PageBox>();
            var per = new List<LineBox>?[doc.Body.Count];
            var notePer = new Dictionary<int, List<LineBox>?[]>();
            var cellLines = new Dictionary<int, List<LineBox>?[]>();
            int lastPara = 0;
            foreach (var pe in jd.RootElement.GetProperty("pages").EnumerateArray())
            {
                var pg = new PageBox();
                foreach (var le in pe.GetProperty("lines").EnumerateArray())
                {
                    var l = ReadLine(le, doc, pages.Count, out int para);
                    if (para < lastPara || para >= doc.Body.Count) return null;
                    lastPara = para;
                    (per[para] ??= new List<LineBox>()).Add(l);
                    pg.Lines.Add((para, l));
                }
                if (pg.Lines.Count == 0) return null;
                if (pe.TryGetProperty("objects", out var objects))
                    foreach (var oe in objects.EnumerateArray())
                    {
                        if (Str(oe, "wrap") == null) continue;
                        int para = Int(oe, "p", -1), off = Int(oe, "i", -1);
                        if (para < 0 || para >= doc.Body.Count || doc.Body[para].ObjectAt(off) is not Picture { Floating: true } pic) return null;
                        pg.Floats.Add(new FloatBox { Para = para, Off = off, Pic = pic, Page = pages.Count, X = Int(oe, "x", 0), Y = Int(oe, "y", 0) });
                    }
                if (pe.TryGetProperty("cells", out var cells))
                    foreach (var ce in cells.EnumerateArray())
                    {
                        int para = Int(ce, "p", -1), r = Int(ce, "r", -1), c = Int(ce, "c", -1);
                        if (para < 0 || para >= doc.Body.Count || doc.Body[para].Table is not { } grid || r < 0 || r >= grid.Rows.Count || c < 0 || c >= grid.Columns || !grid.Starts(r, c)) return null;
                        var cell = doc.Cells[grid.Rows[r][c]];
                        var (spanCols, spanRows) = grid.Span(r, c);
                        var cp = new List<LineBox>?[cell.Paras.Count];
                        foreach (var le in ce.GetProperty("lines").EnumerateArray())
                        {
                            var l = ReadLine(le, doc, pages.Count, out int k);
                            if (k < 0 || k >= cp.Length) return null;
                            (cp[k] ??= new List<LineBox>()).Add(l);
                        }
                        if (!Covers(cell.Paras, cp) || cellLines.ContainsKey(cell.Id)) return null;
                        cellLines[cell.Id] = cp;
                        pg.Cells.Add(new CellBox { Para = para, Row = r, Col = c, Cell = cell, X = Int(ce, "x", 0), Y = Int(ce, "y", 0), W = Int(ce, "w", 0), H = Int(ce, "h", 0), Cols = spanCols, Rows = spanRows });
                    }
                if (pe.TryGetProperty("bands", out var bands))
                    foreach (var be in bands.EnumerateArray())
                        pg.Bands.Add(new BandText { Footer = Str(be, "band") == "footer", X = Int(be, "x", 0), Baseline = Int(be, "b", 0), Text = Str(be, "text") ?? "" });
                if (pe.TryGetProperty("bandPictures", out var bandPics))
                    foreach (var be in bandPics.EnumerateArray())
                    {
                        bool foot = Str(be, "band") == "footer";
                        if ((foot ? doc.FooterOf(pages.Count) : doc.HeaderOf(pages.Count)).Picture is not { } logo || Str(be, "media") != logo.Media || Int(be, "w", 0) != logo.Width || Int(be, "h", 0) != logo.Height) return null;
                        pg.BandPics.Add(new BandPic { Footer = foot, Pic = new Picture(logo.Media, logo.Width, logo.Height, false), X = Int(be, "x", 0), Y = Int(be, "y", 0) });
                    }
                if (pe.TryGetProperty("notes", out var ns))
                {
                    pg.NoteTop = ns.GetProperty("y").GetInt32() - LayoutEngine.NoteGapAbove;
                    foreach (var ne in ns.GetProperty("items").EnumerateArray())
                    {
                        int id = ne.GetProperty("id").GetInt32(), number = ne.GetProperty("number").GetInt32();
                        if (!eng.NoteNumbers.TryGetValue(id, out int want) || want != number || notePer.ContainsKey(id)) return null;
                        var note = doc.Notes[id];
                        var np = notePer[id] = new List<LineBox>?[note.Paras.Count];
                        foreach (var le in ne.GetProperty("lines").EnumerateArray())
                        {
                            var l = ReadLine(le, doc, pages.Count, out int para);
                            if (para < 0 || para >= np.Length) return null;
                            (np[para] ??= new List<LineBox>()).Add(l);
                        }
                        pg.Notes.Add((note, number));
                    }
                }
                pages.Add(pg);
            }
            if (!Covers(doc.Body, per) || notePer.Count != eng.NoteOrder.Count) return null;
            foreach (var (id, np) in notePer)
                if (!Covers(doc.Notes[id].Paras, np)) return null;
            foreach (var cell in doc.CellsInOrder())
                if (!cellLines.ContainsKey(cell.Id)) return null;
            for (int i = 0; i < per.Length; i++) doc.Body[i].Lines = per[i];
            foreach (var (id, cp) in cellLines)
                for (int k = 0; k < cp.Length; k++) doc.Cells[id].Paras[k].Lines = cp[k];
            foreach (var (id, np) in notePer)
                for (int k = 0; k < np.Length; k++) doc.Notes[id].Paras[k].Lines = np[k];
            return pages;
        }
        catch (Exception) { return null; }
    }

    // ---- manifest.json ----

    static string Describe(ResolvedFont f)
    {
        string s = "\"" + f.Family + "\"";
        if (f.Bold && f.Italic) s += " bold italic";
        else if (f.Bold) s += " bold";
        else if (f.Italic) s += " italic";
        return s;
    }

    static byte[] ManifestJson(LayoutEngine eng, byte[] content, byte[] layout)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Pretty))
        {
            w.WriteStartObject();
            w.WriteString("format", "docu");
            w.WriteNumber("formatVersion", FormatVersion);
            w.WriteNumber("layoutRules", LayoutEngine.RulesVersion);
            w.WriteString("content", Sha(content));
            w.WriteString("layout", Sha(layout));
            w.WriteStartArray("fonts");
            foreach (var f in eng.UsedFonts())
            {
                w.WriteStartObject();
                w.WriteString("family", f.Family);
                w.WriteBoolean("bold", f.Bold);
                w.WriteBoolean("italic", f.Italic);
                w.WriteString("usedFamily", f.Face.Info.Family);
                w.WriteString("file", Path.GetFileName(f.Face.Info.Path));
                w.WriteNumber("face", f.Face.Info.Index);
                w.WriteString("sha256", f.Face.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    // ---- the zip ----

    static void Add(ZipArchive zip, string name, byte[] data, bool compress = true, bool quick = false)
    {
        var e = zip.CreateEntry(name, !compress ? CompressionLevel.NoCompression : quick ? CompressionLevel.Fastest : CompressionLevel.Optimal);
        e.LastWriteTime = ZipTime;
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }

    static byte[]? Read(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e == null) return null;
        if (e.Length > 1L << 30) throw new InvalidDataException(name + " is too large.");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static SortedSet<string> UsedMedia(Document doc)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in doc.Body.Concat(doc.CellsInOrder().SelectMany(c => c.Paras)))
            foreach (var (_, o) in p.Objects)
                if (o is Picture pic) set.Add(pic.Media);
        foreach (var band in doc.BandsSkipFirst ? new[] { doc.Header, doc.Footer, doc.FirstHeader, doc.FirstFooter } : new[] { doc.Header, doc.Footer })
            if (band.Picture is { } logo) set.Add(logo.Media);
        return set;
    }

    static bool SafeMediaName(string name) =>
        name.Length is > 4 and < 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') && !name.StartsWith('.');

    /// <summary>Everything a file holds, as bytes. Making it needs the document; writing it to a file does not,
    /// so that can happen while the document is being worked on.</summary>
    public sealed record Packed(byte[] Manifest, byte[] Content, byte[] Layout, List<(string Name, byte[] Data)> Media);

    public static Packed Pack(Document doc, LayoutEngine eng)
    {
        byte[] content = ContentJson(doc), layout = LayoutJson(doc, eng);
        var media = new List<(string, byte[])>();
        foreach (string name in UsedMedia(doc))
            if (SafeMediaName(name) && doc.Media.TryGetValue(name, out var data)) media.Add((name, data));
        return new Packed(ManifestJson(eng, content, layout), content, layout, media);
    }

    /// <summary>Writes the file. With quick it is packed less tightly, which is faster; it holds the same.</summary>
    public static void Write(Packed f, string path, bool quick = false)
    {
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Add(zip, "manifest.json", f.Manifest, true, quick);
            Add(zip, "content.json", f.Content, true, quick);
            Add(zip, "layout.json", f.Layout, true, quick);
            foreach (var (name, data) in f.Media) Add(zip, "media/" + name, data, false);
        }
        File.Move(tmp, path, true);
    }

    public static void Save(Document doc, LayoutEngine eng, string path) => Write(Pack(doc, eng), path);

    public static OpenResult Open(string path)
    {
        byte[]? manifest, content, layout;
        var media = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Document doc;
        using (var zip = ZipFile.OpenRead(path))
        {
            manifest = Read(zip, "manifest.json");
            content = Read(zip, "content.json");
            layout = Read(zip, "layout.json");
            if (manifest == null || content == null) throw new InvalidDataException("This is not a Reiks document (manifest.json or content.json is missing).");
            doc = ParseContent(content);
            // A picture is taken only if its bytes have the fingerprint its name states.
            foreach (string name in UsedMedia(doc))
                if (SafeMediaName(name) && Read(zip, "media/" + name) is { } data && name.StartsWith(Sha(data) + ".", StringComparison.Ordinal))
                    doc.Media[name] = data;
        }
        using var mj = JsonDocument.Parse(manifest);
        var m = mj.RootElement;
        if (Str(m, "format") is "udoc" or "rks" or "rwd")
            throw new InvalidDataException("This document was written by an earlier version of the program, in a format that is no longer read. This version opens .docu documents.");
        if (Str(m, "format") != "docu") throw new InvalidDataException("This is not a Reiks document.");
        int fv = Int(m, "formatVersion", 0);
        if (fv < OldestFormat || fv > FormatVersion)
            throw new InvalidDataException($"The file has format version {fv}; this program reads versions {OldestFormat} to {FormatVersion}.");

        var eng = new LayoutEngine(doc);
        string? why = null;
        int rules = Int(m, "layoutRules", 0);
        if (layout == null) why = "The file holds no layout.";
        else if (rules != LayoutEngine.RulesVersion) why = $"The layout was made with layout rules version {rules}; this program uses version {LayoutEngine.RulesVersion}.";
        else if (Str(m, "content") != Sha(content)) why = "The content was changed after the layout was stored.";
        else if (Str(m, "layout") != Sha(layout)) why = "The stored layout was changed after it was made.";
        else
        {
            var listed = new Dictionary<(string, bool, bool), (string? Sha, int Face)>();
            if (m.TryGetProperty("fonts", out var fonts))
                foreach (var f in fonts.EnumerateArray())
                    listed[((Str(f, "family") ?? "").ToLowerInvariant(), Bool(f, "bold", false), Bool(f, "italic", false))] = (Str(f, "sha256"), Int(f, "face", 0));
            foreach (var f in eng.UsedFonts())
            {
                if (!listed.TryGetValue((f.Family.ToLowerInvariant(), f.Bold, f.Italic), out var e))
                    why = $"The file does not say which font file was used for {Describe(f)}.";
                else if (e.Sha != f.Face.Sha256 || e.Face != f.Face.Info.Index)
                    why = f.Substituted
                        ? $"The font {Describe(f)} is not installed on this PC ({f.Face.Info.Family} is used instead)."
                        : $"The font {Describe(f)} on this PC differs from the one the layout was made with.";
                if (why != null) break;
            }
        }
        if (why == null)
        {
            var pages = ParseLayout(doc, eng, layout!);
            if (pages == null) why = "The stored layout does not fit the content.";
            else eng.LoadStored(pages);
        }
        if (why != null)
        {
            eng.InvalidateAll();
            eng.Update();
        }
        return new OpenResult
        {
            Doc = doc, Engine = eng, Stored = why == null,
            Message = why == null ? "Stored layout shown: fonts and content match." : why + " Laid out again.",
        };
    }
}
