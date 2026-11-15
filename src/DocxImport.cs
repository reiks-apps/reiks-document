using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace ReiksDocument;

/// <summary>Reads a Word document (.docx) into a document of this program. A Word file says what a document holds and how
/// it is formatted, not where things stand; so the text, its formats, lists, tables, pictures, footnotes, links and
/// equations are taken over, and the layout of this program places them anew. What this program has no place for is
/// put where it reads well (a text box becomes text, a picture over the text stands on a line of its own) or left out,
/// and the reader is told. The Word file is only read.</summary>
static class DocxImport
{
    public sealed record Result(Document Doc, List<string> Notes);

    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    static readonly XNamespace WPS = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    static readonly XNamespace V = "urn:schemas-microsoft-com:vml";

    /// <summary>A text box of Word: what it holds, whether it has a line around it, its background, its width and the side it stood on.</summary>
    sealed record Box(XElement Content, bool Framed, int? Fill, long Width, Align Side);

    const int Twip = 635;
    const long MostBytes = 64L << 20;

    /// <summary>A drawing as EMF or WMF, drawn once and written as PNG; null if the data is no such drawing.</summary>
    public static byte[]? Drawn(byte[] data) => Reader.Drawn(data);

    public static Result Read(string path)
    {
        // Read without keeping others out, so that a document that is open in Word can be opened here as well.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        return new Reader(zip).Run();
    }

    /// <summary>What a style or a run says about the look; null where it says nothing.</summary>
    sealed class Look
    {
        public string? Font, Under;
        public int? Size, Color, Highlight, Vert;
        public bool? Bold, Italic, Underline, Strike, Caps, Hidden;
        public Align? Align;
        public int? Before, After, LineRaw, Left, Right, First, NumId, Level;
        public string? LineRule;
        public bool? KeepNext, PageBreak, Contextual;
        public ParaBorders? Borders;

        public Look Copy() => (Look)MemberwiseClone();

        public void Take(Look o)
        {
            Font = o.Font ?? Font; Under = o.Under ?? Under; Size = o.Size ?? Size; Color = o.Color ?? Color; Highlight = o.Highlight ?? Highlight; Vert = o.Vert ?? Vert;
            Bold = o.Bold ?? Bold; Italic = o.Italic ?? Italic; Underline = o.Underline ?? Underline; Strike = o.Strike ?? Strike; Caps = o.Caps ?? Caps; Hidden = o.Hidden ?? Hidden;
            Align = o.Align ?? Align; Before = o.Before ?? Before; After = o.After ?? After; Left = o.Left ?? Left; Right = o.Right ?? Right; First = o.First ?? First;
            if (o.LineRaw != null) { LineRaw = o.LineRaw; LineRule = o.LineRule; }
            NumId = o.NumId ?? NumId; Level = o.Level ?? Level; KeepNext = o.KeepNext ?? KeepNext; PageBreak = o.PageBreak ?? PageBreak; Contextual = o.Contextual ?? Contextual;
            Borders = o.Borders ?? Borders;
        }

        /// <summary>Line spacing in thousandths of the single spacing.</summary>
        public int? Line(int halfPoints)
        {
            if (LineRaw is not { } raw || raw <= 0) return null;
            if (LineRule is "exact" or "atLeast")
            {
                // A fixed height in twentieths of a point, set against the usual height of a line of that size.
                double single = Math.Max(1, halfPoints) / 2.0 * 1.2 * 20;
                return Math.Clamp((int)Math.Round(raw / single * 1000), 500, 10000);
            }
            return Math.Clamp((int)Math.Round(raw * 1000.0 / 240), 100, 10000);
        }
    }

    sealed class Field
    {
        public readonly StringBuilder Instr = new();
        public bool Shown;
        public string? Link;
    }

    sealed class Reader
    {
        readonly ZipArchive zip;
        readonly Document doc = Document.NewDefault();
        readonly Dictionary<string, XElement> styles = new(StringComparer.Ordinal);
        readonly Dictionary<string, Look> looks = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> styleIds = new(StringComparer.Ordinal);
        readonly Dictionary<string, (string Target, bool External)> rels = new(StringComparer.Ordinal);
        readonly Dictionary<string, XElement> footnotes = new(StringComparer.Ordinal), endnotes = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> numToAbstract = new(StringComparer.Ordinal);
        readonly Dictionary<string, XElement> abstracts = new(StringComparer.Ordinal);
        readonly List<Field> fields = new();
        readonly List<Box> boxes = new();
        readonly HashSet<Paragraph> close = new();
        readonly SortedDictionary<string, int> left = new(StringComparer.Ordinal);
        Look defaults = new();
        string defaultStyle = "", majorFont = "Calibri Light", minorFont = "Calibri", language = "";
        bool addRoom;
        bool breakNext;
        /// <summary>Outside the text of the document itself: how wide a picture, equation or chart may be where the reading is now
        /// (in a table cell or a box); 0 where only text can stand (in a footnote).</summary>
        long roomHere;
        int tocAt = -1, boxCount, boxFramed, eqSimple, fromLatex, floats, charts, chartsSimple, blocks, refitted, shrunk, logos, widened, arranged, shortened, oldPictures;

        public Reader(ZipArchive z) { zip = z; }

        readonly List<Paragraph> latexParts = new();
        void Count(string what) => left[what] = left.GetValueOrDefault(what) + 1;

        // ---- the parts of the file ----

        byte[]? Bytes(string name)
        {
            name = name.Replace('\\', '/').TrimStart('/');
            var e = zip.Entries.FirstOrDefault(x => string.Equals(x.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
            if (e == null || e.Length > MostBytes) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        XElement? Part(string name)
        {
            if (Bytes(name) is not { } data) return null;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 200_000_000 };
                using var reader = XmlReader.Create(new MemoryStream(data), settings);
                return XDocument.Load(reader).Root;
            }
            catch (XmlException) { return null; }
        }

        static string Join(string folder, string target)
        {
            if (target.StartsWith('/')) return target.TrimStart('/');
            var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (string p in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (p != ".") parts.Add(p);
            return string.Join("/", parts);
        }

        Dictionary<string, (string, bool)> Rels(string part)
        {
            var map = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
            int slash = part.LastIndexOf('/');
            string folder = slash < 0 ? "" : part.Substring(0, slash), file = part.Substring(slash + 1);
            if (Part(Join(folder, "_rels/" + file + ".rels")) is not { } root) return map;
            foreach (var r in root.Elements(PR + "Relationship"))
            {
                string id = (string?)r.Attribute("Id") ?? "", target = (string?)r.Attribute("Target") ?? "";
                bool external = (string?)r.Attribute("TargetMode") == "External";
                if (id.Length > 0) map[id] = (external ? target : Join(folder, target), external);
            }
            return map;
        }

        static string? Val(XElement? e) => (string?)e?.Attribute(W + "val");
        static int? Num(XElement? e, string attr) => e != null && int.TryParse((string?)e.Attribute(W + attr), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
        static bool? OnOff(XElement? e) => e == null ? null : Val(e) is not ("0" or "false" or "off");

        static int? Hex(string? s) => s is { Length: 6 } && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : null;

        static readonly Dictionary<string, int> Highlights = new()
        {
            ["yellow"] = 0xFFFF00, ["green"] = 0x00FF00, ["cyan"] = 0x00FFFF, ["magenta"] = 0xFF00FF, ["blue"] = 0x0000FF, ["red"] = 0xFF0000,
            ["darkBlue"] = 0x000080, ["darkCyan"] = 0x008080, ["darkGreen"] = 0x008000, ["darkMagenta"] = 0x800080, ["darkRed"] = 0x800000,
            ["darkYellow"] = 0x808000, ["darkGray"] = 0x808080, ["lightGray"] = 0xC0C0C0, ["black"] = 0x000000, ["white"] = 0xFFFFFF,
        };

        // ---- looks ----

        /// <summary>The lines Word draws at the edges of a paragraph, as plain lines: every kind of line becomes a single one of its thickness.</summary>
        ParaBorders BordersOf(XElement pBdr)
        {
            int gap = -1, sideGap = -1, color = -1;
            int Side(string name, bool side)
            {
                if (pBdr.Element(W + name) is not { } e || Val(e) is null or "nil" or "none") return 0;
                int room = (Num(e, "space") ?? (side ? 4 : 1)) * U.PerPoint;
                if (side && sideGap < 0) sideGap = room;
                if (!side && gap < 0) gap = room;
                if (color < 0) color = int.TryParse((string?)e.Attribute(W + "color"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int c) ? c & 0xFFFFFF : 0;
                // Thickness in eighths of a point.
                return Math.Max(ParaBorders.Thinnest, (Num(e, "sz") ?? 4) * U.PerPoint / 8);
            }
            int top = Side("top", false), bottom = Side("bottom", false), left = Side("left", true), right = Side("right", true);
            return new ParaBorders(top, bottom, left, right, Math.Max(0, gap), Math.Max(0, sideGap), Math.Max(0, color)).Checked();
        }

        void ReadRun(XElement? rPr, Look k)
        {
            if (rPr == null) return;
            if (rPr.Element(W + "rFonts") is { } f)
            {
                string? name = (string?)f.Attribute(W + "ascii") ?? (string?)f.Attribute(W + "hAnsi");
                string? theme = (string?)f.Attribute(W + "asciiTheme") ?? (string?)f.Attribute(W + "hAnsiTheme");
                // Where a font of the theme is asked for, Word uses it and not the font named beside it.
                if (theme != null) name = theme.StartsWith("major", StringComparison.Ordinal) ? majorFont : minorFont;
                if (!string.IsNullOrWhiteSpace(name)) k.Font = name.Trim();
            }
            if (Num(rPr.Element(W + "sz"), "val") is { } sz) k.Size = Math.Clamp(sz, 2, 3276);
            k.Bold = OnOff(rPr.Element(W + "b")) ?? k.Bold;
            k.Italic = OnOff(rPr.Element(W + "i")) ?? k.Italic;
            k.Strike = OnOff(rPr.Element(W + "strike")) ?? OnOff(rPr.Element(W + "dstrike")) ?? k.Strike;
            k.Caps = OnOff(rPr.Element(W + "caps")) ?? k.Caps;
            k.Hidden = OnOff(rPr.Element(W + "vanish")) ?? k.Hidden;
            if (rPr.Element(W + "u") is { } u)
            {
                string kind = Val(u) ?? "single";
                k.Underline = kind != "none";
                k.Under = kind switch
                {
                    "double" => "double", "thick" => "thick", "dotted" or "dottedHeavy" => "dotted", "wave" or "wavyHeavy" or "wavyDouble" => "wave",
                    "dash" or "dashedHeavy" or "dashLong" or "dotDash" or "dotDotDash" => "dashed", _ => "single",
                };
            }
            if (rPr.Element(W + "color") is { } c) k.Color = Hex(Val(c)) ?? 0;
            if (rPr.Element(W + "highlight") is { } h) k.Highlight = Highlights.TryGetValue(Val(h) ?? "", out int rgb) ? rgb : -1;
            else if (rPr.Element(W + "shd") is { } shade && Hex((string?)shade.Attribute(W + "fill")) is { } fill) k.Highlight = fill;
            if (rPr.Element(W + "vertAlign") is { } v) k.Vert = Val(v) switch { "superscript" => 1, "subscript" => 2, _ => 0 };
        }

        void ReadPara(XElement? pPr, Look k)
        {
            if (pPr == null) return;
            if (pPr.Element(W + "jc") is { } jc)
                k.Align = Val(jc) switch { "center" => Align.Center, "right" or "end" => Align.Right, "both" or "distribute" => Align.Justify, _ => Align.Left };
            if (pPr.Element(W + "spacing") is { } sp)
            {
                if (Num(sp, "before") is { } b) k.Before = Math.Max(0, b) * Twip;
                if (Num(sp, "after") is { } a) k.After = Math.Max(0, a) * Twip;
                if (Num(sp, "line") is { } l) { k.LineRaw = l; k.LineRule = (string?)sp.Attribute(W + "lineRule") ?? "auto"; }
            }
            if (pPr.Element(W + "ind") is { } ind)
            {
                if ((Num(ind, "left") ?? Num(ind, "start")) is { } l) k.Left = l * Twip;
                if ((Num(ind, "right") ?? Num(ind, "end")) is { } r) k.Right = r * Twip;
                if (Num(ind, "hanging") is { } hang) k.First = -hang * Twip;
                else if (Num(ind, "firstLine") is { } fl) k.First = fl * Twip;
            }
            k.KeepNext = OnOff(pPr.Element(W + "keepNext")) ?? k.KeepNext;
            k.PageBreak = OnOff(pPr.Element(W + "pageBreakBefore")) ?? k.PageBreak;
            k.Contextual = OnOff(pPr.Element(W + "contextualSpacing")) ?? k.Contextual;
            if (pPr.Element(W + "pBdr") is { } around) k.Borders = BordersOf(around);
            if (pPr.Element(W + "numPr") is { } num)
            {
                if (Num(num.Element(W + "numId"), "val") is { } id) k.NumId = id;
                if (Num(num.Element(W + "ilvl"), "val") is { } lvl) k.Level = lvl;
            }
        }

        /// <summary>What a style says, with everything it takes from the styles it is based on. Paragraph styles start from the defaults of the document.</summary>
        Look LookOf(string? id, bool paragraph)
        {
            if (id == null || !styles.TryGetValue(id, out var style)) return paragraph ? defaults.Copy() : new Look();
            string key = (paragraph ? "p:" : "c:") + id;
            if (looks.TryGetValue(key, out var have)) return have.Copy();
            var chain = new List<XElement>();
            for (var s = style; s != null && chain.Count < 20; s = Val(s.Element(W + "basedOn")) is { } b && styles.TryGetValue(b, out var next) && !chain.Contains(next) ? next : null) chain.Add(s);
            var k = paragraph ? defaults.Copy() : new Look();
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                ReadRun(chain[i].Element(W + "rPr"), k);
                if (paragraph) ReadPara(chain[i].Element(W + "pPr"), k);
            }
            looks[key] = k;
            return k.Copy();
        }

        static string Canonical(XElement style) => (Val(style.Element(W + "name")) ?? (string?)style.Attribute(W + "styleId") ?? "").Trim().ToLowerInvariant();

        /// <summary>The style of this program that stands for a paragraph style of Word; it is made on first use.</summary>
        string StyleFor(string? wordId)
        {
            wordId ??= defaultStyle;
            if (styleIds.TryGetValue(wordId, out string? known)) return known;
            if (!styles.TryGetValue(wordId, out var style)) return styleIds[wordId] = "Normal";
            string name = Canonical(style);
            string id = name switch
            {
                "normal" => "Normal", "title" => "Title", "no spacing" => "NoSpacing", "heading 1" => "Heading1", "heading 2" => "Heading2", "heading 3" => "Heading3",
                _ => "W" + new string(wordId.Where(char.IsLetterOrDigit).ToArray()),
            };
            if (id == "W") id = "W" + styleIds.Count;
            var k = LookOf(wordId, true);
            int size = k.Size ?? 20;
            var made = new ParaStyle
            {
                Id = id, Name = id.StartsWith('W') ? Shown(Val(style.Element(W + "name")) ?? wordId) : doc.FindPara(id)?.Name ?? id,
                Char = new CharFormat(k.Font ?? "Times New Roman", size, k.Bold == true, k.Italic == true, k.Underline == true, k.Color ?? 0, k.Strike == true, k.Highlight ?? -1, k.Vert ?? 0, k.Under ?? "single"),
                Align = k.Align ?? Align.Left, Before = k.Before ?? 0, After = k.After ?? 0, Line = k.Line(size) ?? 1000,
                IndentLeft = Math.Max(0, k.Left ?? 0), IndentRight = Math.Max(0, k.Right ?? 0), IndentFirst = k.First ?? 0,
                KeepNext = k.KeepNext == true || name.StartsWith("heading ", StringComparison.Ordinal), Next = name.StartsWith("heading ", StringComparison.Ordinal) || name == "title" ? "Normal" : id,
                Borders = k.Borders ?? default,
            };
            if (made.IndentLeft + Math.Min(0, made.IndentFirst) < 0) made.IndentFirst = -made.IndentLeft;
            int at = doc.ParaStyles.FindIndex(s => s.Id == id);
            if (at >= 0) doc.ParaStyles[at] = made;
            else doc.ParaStyles.Add(made);
            return styleIds[wordId] = id;
        }

        static string Shown(string name) => name.Length == 0 ? "Style" : char.ToUpperInvariant(name[0]) + name.Substring(1);

        // ---- lists ----

        string? ListKind(int numId, int level)
        {
            if (numId <= 0 || !numToAbstract.TryGetValue(numId.ToString(CultureInfo.InvariantCulture), out string? abs) || !abstracts.TryGetValue(abs, out var a)) return null;
            var lvl = a.Elements(W + "lvl").FirstOrDefault(l => Num(l, "ilvl") == level) ?? a.Elements(W + "lvl").FirstOrDefault();
            if (lvl == null) return "bullet";
            string fmt = Val(lvl.Element(W + "numFmt")) ?? "decimal", text = Val(lvl.Element(W + "lvlText")) ?? "";
            bool paren = text.Contains(')'), many = text.Count(ch => ch == '%') > 1;
            switch (fmt)
            {
                case "none": return null;
                case "bullet":
                    char mark = text.Length > 0 ? text[0] : '•';
                    return mark is 'o' or '○' or '◦' ? "circle" : mark is '-' or '–' or '—' ? "dash" : mark is '▪' or '■' or '§' or (char)0xF0A7 ? "square" : "bullet";
                case "upperRoman": return "roman-upper";
                case "lowerRoman": return "roman";
                case "upperLetter": return "alpha-upper";
                case "lowerLetter": return paren ? "alpha-paren" : "alpha";
                default: return many ? (paren ? "outline-paren" : "outline") : paren ? "number-paren" : "number";
            }
        }

        // ---- the whole document ----

        public Result Run()
        {
            string main = "word/document.xml";
            if (Part("_rels/.rels")?.Elements(PR + "Relationship").FirstOrDefault(r => ((string?)r.Attribute("Type") ?? "").EndsWith("/officeDocument", StringComparison.Ordinal)) is { } named
                && (string?)named.Attribute("Target") is { Length: > 0 } target) main = Join("", target);
            var root = Part(main);
            var body = root?.Element(W + "body");
            if (body == null) throw new InvalidDataException("This is not a Word document of the kind that can be read here (.docx).");
            foreach (var kv in Rels(main)) rels[kv.Key] = kv.Value;
            string folder = main.Contains('/') ? main.Substring(0, main.LastIndexOf('/')) : "";
            string Named(string type, string fallback) =>
                rels.Values.FirstOrDefault(r => !r.External && r.Target.EndsWith(fallback, StringComparison.OrdinalIgnoreCase)).Target ?? Join(folder, fallback);

            // Word takes the larger of the room below a paragraph and the room above the next one, unless the document asks for both.
            addRoom = Part(Named("settings", "settings.xml"))?.Descendants(W + "doNotUseHTMLParagraphAutoSpacing").Any(e => OnOff(e) != false) == true;
            if (Part(Named("theme", "theme/theme1.xml")) is { } theme)
            {
                var scheme = theme.Descendants(A + "fontScheme").FirstOrDefault();
                majorFont = (string?)scheme?.Element(A + "majorFont")?.Element(A + "latin")?.Attribute("typeface") is { Length: > 0 } maj ? maj : majorFont;
                minorFont = (string?)scheme?.Element(A + "minorFont")?.Element(A + "latin")?.Attribute("typeface") is { Length: > 0 } min ? min : minorFont;
            }
            defaults = new Look { Font = "Times New Roman", Size = 20 };
            if (Part(Named("styles", "styles.xml")) is { } st)
            {
                ReadRun(st.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr"), defaults);
                language = Val(st.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr")?.Element(W + "lang")) ?? "";
                ReadPara(st.Element(W + "docDefaults")?.Element(W + "pPrDefault")?.Element(W + "pPr"), defaults);
                foreach (var s in st.Elements(W + "style"))
                {
                    string id = (string?)s.Attribute(W + "styleId") ?? "";
                    if (id.Length == 0) continue;
                    styles[id] = s;
                    if ((string?)s.Attribute(W + "type") == "paragraph" && (string?)s.Attribute(W + "default") is "1" or "true") defaultStyle = id;
                }
            }
            if (Part(Named("numbering", "numbering.xml")) is { } nums)
            {
                foreach (var a in nums.Elements(W + "abstractNum")) abstracts[(string?)a.Attribute(W + "abstractNumId") ?? ""] = a;
                foreach (var n in nums.Elements(W + "num")) numToAbstract[(string?)n.Attribute(W + "numId") ?? ""] = Val(n.Element(W + "abstractNumId")) ?? "";
            }
            foreach (var (part, into) in new[] { ("footnotes.xml", footnotes), ("endnotes.xml", endnotes) })
                if (Part(Named(part, part)) is { } notes)
                    foreach (var n in notes.Elements())
                        if (n.Attribute(W + "type") == null && (string?)n.Attribute(W + "id") is { } id) into[id] = n;

            // The page of the last section is the page of the document.
            var section = body.Elements(W + "sectPr").LastOrDefault() ?? body.Descendants(W + "sectPr").LastOrDefault();
            ReadPage(section);

            // The styles a reader looks for are there even if no paragraph uses them.
            foreach (var s in styles.Values)
                if ((string?)s.Attribute(W + "type") == "paragraph" && Canonical(s) is "normal" or "title" or "heading 1" or "heading 2" or "heading 3")
                    StyleFor((string?)s.Attribute(W + "styleId"));

            doc.Body.Clear();
            Blocks(body, doc.Body, true);
            if (doc.Body.Count == 0 || doc.Body[0].Table != null) doc.Body.Insert(0, new Paragraph { Style = "Normal" });
            if (doc.Body[^1].Table != null) doc.Body.Add(new Paragraph { Style = "Normal" });
            FitTables();
            // A numbered task with its parts is a block that should not begin at the bottom of a page with only a little of it there.
            // A task is a numbered item of the first level, or a paragraph that begins with a number typed by hand, as in "4. Solve".
            var typed = new System.Text.RegularExpressions.Regex(@"^\s*\d{1,3}\s*[.)]\s*\p{L}");
            var tasks = doc.Body.FindAll(p => p.Table == null && !p.Toc && p.Length > 0
                && (p.List is "number" or "number-paren" or "roman-upper" or "alpha-upper" or "outline" or "outline-paren" && p.ListLevel == 0 || p.List == null && typed.IsMatch(p.Text)));
            // A plain numbered list needs none of this: only where an item has paragraphs of its own behind it.
            bool withParts = false;
            for (int i = 0; i + 1 < doc.Body.Count && !withParts; i++)
                if (tasks.Contains(doc.Body[i]))
                    for (int q = i + 1; q < doc.Body.Count && !tasks.Contains(doc.Body[q]) && !doc.Body[q].PageBreakBefore && !withParts; q++)
                        withParts = doc.Body[q].Table != null || doc.Body[q].Length > 0;
            if (tasks.Count >= 2 && withParts)
            {
                foreach (var task in tasks) task.Together = true;
                blocks = tasks.Count;
            }
            // Between two paragraphs of one style that Word sets close together there is no room.
            for (int i = 0; i < doc.Body.Count; i++)
            {
                var one = doc.Body[i];
                if (!close.Contains(one) || one.Table != null) continue;
                if (i + 1 < doc.Body.Count && doc.Body[i + 1].Table == null && doc.Body[i + 1].Style == one.Style) one.After = 0;
                if (i > 0 && doc.Body[i - 1].Table == null && doc.Body[i - 1].Style == one.Style && !one.PageBreakBefore) one.Before = 0;
            }
            // Paragraphs one after the other with the same lines at their edges stand in one frame: the line above stays with
            // the first of them and the line below with the last, and none is drawn between them.
            var framed = doc.Body.Select(p => p.Table == null ? doc.BordersOf(p) : default).ToList();
            (int, int, int) Indents(Paragraph p) => (p.IndentLeft ?? doc.StyleOf(p).IndentLeft, p.IndentRight ?? doc.StyleOf(p).IndentRight, p.IndentFirst ?? doc.StyleOf(p).IndentFirst);
            bool OneFrame(int a, int b) => !framed[a].None && framed[a] == framed[b] && !doc.Body[b].PageBreakBefore
                && doc.Body[a].List == doc.Body[b].List && Indents(doc.Body[a]) == Indents(doc.Body[b]);
            for (int i = 0; i < doc.Body.Count; i++)
            {
                bool above = i > 0 && OneFrame(i - 1, i), below = i + 1 < doc.Body.Count && OneFrame(i, i + 1);
                if (above || below) doc.Body[i].Borders = (framed[i] with { Top = above ? 0 : framed[i].Top, Bottom = below ? 0 : framed[i].Bottom }).Checked();
            }
            // The larger of the room below a paragraph and the room above the next one, as Word counts it: what the first one
            // has below is taken off what the second one has above.
            if (!addRoom)
                for (int i = 1; i < doc.Body.Count; i++)
                {
                    Paragraph above = doc.Body[i - 1], below = doc.Body[i];
                    if (above.Table != null || below.Table != null || below.PageBreakBefore) continue;
                    int after = above.After ?? doc.StyleOf(above).After, before = below.Before ?? doc.StyleOf(below).Before;
                    if (after > 0 && before > 0) below.Before = Math.Max(0, before - after);
                }
            // The styles this program adds of its own (for the table of contents, footnotes, header and footer) take the font of the document.
            if (doc.FindPara("Normal") is { } normal)
                foreach (string own in new[] { "NoSpacing", "Footnote", "Header", "Footer" })
                    if (!styleIds.ContainsValue(own) && doc.FindPara(own) is { } mine)
                        mine.Char = mine.Char with { Font = normal.Char.Font, Size = own == "NoSpacing" ? normal.Char.Size : mine.Char.Size };
            // A paragraph that became nothing but equations, and was set like program code in a style with a font of its own,
            // takes the look of ordinary text: its size and the room around it.
            var plain = doc.FindPara("Normal") ?? doc.ParaStyles[0];
            foreach (var p in latexParts)
            {
                if (doc.StyleOf(p).Char.Font == plain.Char.Font || !p.Runs.All(r => r.Obj is Equation { Block: true } || r.Obj == null && string.IsNullOrWhiteSpace(r.Text))) continue;
                p.Style = plain.Id;
                p.Before = p.After = null;
                p.Line = null;
                p.Runs = p.Runs.Where(r => r.Obj != null).Select(r => r with { Props = r.Props with { Size = null } }).ToList();
                p.Touch();
            }
            // Header and footer are read behind this: the size of a picture in them goes by the height of their line.
            ReadBands(section, main);
            foreach (var p in doc.Body) p.Touch();
            if (tocAt >= 0 && Contents.Any(doc)) Contents.Put(doc, Math.Min(tocAt, doc.Body.Count));
            // The room between paragraphs gives at the ends of pages as the layout goes (FORMAT.md §6).
            doc.AdaptSpacing = true;
            return new Result(doc, Notes());
        }

        void ReadPage(XElement? section)
        {
            if (section == null) return;
            var pg = doc.Page;
            if (section.Element(W + "pgSz") is { } size && Num(size, "w") is { } w && Num(size, "h") is { } h && w >= 1440 && h >= 1440)
                (pg.Width, pg.Height) = (Math.Min(w, 31680) * Twip, Math.Min(h, 31680) * Twip);
            if (section.Element(W + "pgMar") is { } mar)
            {
                pg.Top = Math.Clamp(Math.Abs(Num(mar, "top") ?? 1440) * Twip, 0, pg.Height / 3);
                pg.Bottom = Math.Clamp(Math.Abs(Num(mar, "bottom") ?? 1440) * Twip, 0, pg.Height / 3);
                pg.Left = Math.Clamp((Num(mar, "left") ?? 1440) * Twip, 0, pg.Width / 3);
                pg.Right = Math.Clamp((Num(mar, "right") ?? 1440) * Twip, 0, pg.Width / 3);
            }
            if (Num(section.Element(W + "cols"), "num") > 1) Count("columns");
        }

        // ---- paragraphs and tables, one after the other ----

        void Blocks(XElement container, List<Paragraph> into, bool body)
        {
            foreach (var e in container.Elements())
            {
                if (e.Name == W + "p") Paragraph(e, into, body);
                else if (e.Name == W + "tbl")
                {
                    if (body) Table(e, into);
                    else
                    {
                        // A table where this program can hold text only: its cells, one paragraph after the other.
                        Count("tables inside");
                        foreach (var cell in e.Descendants(W + "tc")) Blocks(cell, into, false);
                    }
                }
                else if (e.Name == W + "sdt" && e.Element(W + "sdtContent") is { } content) Blocks(content, into, body);
                else if (e.Name == W + "customXml" || e.Name == W + "ins" || e.Name == W + "smartTag") Blocks(e, into, body);
                else if (e.Name == MC + "AlternateContent" && (e.Element(MC + "Choice") ?? e.Element(MC + "Fallback")) is { } choice) Blocks(choice, into, body);
                else if (e.Name == W + "altChunk") Count("embedded files");
            }
        }

        void Paragraph(XElement e, List<Paragraph> into, bool body)
        {
            var pPr = e.Element(W + "pPr");
            string? wordStyle = Val(pPr?.Element(W + "pStyle")) ?? (defaultStyle.Length > 0 ? defaultStyle : null);
            var styleLook = LookOf(wordStyle, true);
            var own = new Look();
            ReadPara(pPr, own);
            string canonical = wordStyle != null && styles.TryGetValue(wordStyle, out var ws) ? Canonical(ws) : "";
            bool toc = canonical.StartsWith("toc ", StringComparison.Ordinal) || canonical == "table of figures";

            var p = new Paragraph { Style = StyleFor(wordStyle) };
            if (own.Align is { } al && al != (styleLook.Align ?? Align.Left)) p.Align = al;
            if (own.Before is { } before && before != (styleLook.Before ?? 0)) p.Before = before;
            if (own.After is { } after && after != (styleLook.After ?? 0)) p.After = after;
            if (own.Borders is { } edges && edges != (styleLook.Borders ?? default)) p.Borders = edges;
            int size = styleLook.Size ?? 20;
            if (own.Line(size) is { } line && line != (styleLook.Line(size) ?? 1000)) p.Line = line;

            int numId = own.NumId ?? styleLook.NumId ?? 0, level = Math.Clamp(own.Level ?? styleLook.Level ?? 0, 0, 8);
            string? kind = body ? ListKind(numId, level) : null;
            if (kind != null)
            {
                p.List = kind;
                p.ListLevel = level;
                // Word takes the indent of a list from the paragraph, else from the list itself, else from the style. This program
                // places a number a quarter of an inch in and each level half an inch further; what Word has beyond that is the indent.
                var listLook = new Look();
                if (numToAbstract.TryGetValue(numId.ToString(CultureInfo.InvariantCulture), out string? abs) && abstracts.TryGetValue(abs, out var a))
                    ReadPara(a.Elements(W + "lvl").FirstOrDefault(l => Num(l, "ilvl") == level)?.Element(W + "pPr"), listLook);
                int textAt = own.Left ?? listLook.Left ?? styleLook.Left ?? 0;
                int numberAt = textAt + Math.Min(0, own.Left != null ? own.First ?? 0 : listLook.First ?? own.First ?? styleLook.First ?? 0);
                p.IndentLeft = Math.Max(-LayoutEngine.ListMarkAt, numberAt - LayoutEngine.ListMarkAt - level * LayoutEngine.ListTextAt);
                if (own.Right is { } listRight) p.IndentRight = Math.Max(0, listRight);
            }
            else
            {
                if (own.Left is { } l) p.IndentLeft = Math.Max(0, l);
                if (own.Right is { } r) p.IndentRight = Math.Max(0, r);
                if (own.First is { } f) p.IndentFirst = f;
                int leftNow = p.IndentLeft ?? Math.Max(0, styleLook.Left ?? 0);
                if (leftNow + Math.Min(0, p.IndentFirst ?? styleLook.First ?? 0) < 0) p.IndentFirst = -leftNow;
            }
            if (pPr?.Element(W + "rPr") is { } markFormat)
            {
                var markLook = new Look();
                if (Val(markFormat.Element(W + "rStyle")) is { } markStyle) markLook.Take(LookOf(markStyle, false));
                ReadRun(markFormat, markLook);
                if (markLook.Font != null || markLook.Size != null) p.Mark = new Run("", new CharProps(Font: markLook.Font, Size: markLook.Size));
            }
            // Paragraphs of one style that Word sets close together, as the items of a list: the room between two of them is dropped below.
            if ((own.Contextual ?? styleLook.Contextual) == true) close.Add(p);
            if (body && (own.PageBreak == true || breakNext)) p.PageBreakBefore = true;
            if (body) breakNext = false;

            var parts = new List<Paragraph> { p };
            Inlines(e, parts, new Look(), null, body);
            // Mathematics that stands in the text as LaTeX becomes equations, where an equation can stand.
            if (body || roomHere > 0)
                foreach (var part in parts)
                {
                    int made = Latex.Apply(part, body);
                    fromLatex += made;
                    if (made > 0) latexParts.Add(part);
                }
            if (pPr?.Element(W + "sectPr") is { } sect && Val(sect.Element(W + "type")) is not ("continuous")) breakNext |= body;

            // The table of contents of Word is left out: this program writes its own in that place.
            if (toc)
            {
                if (body && tocAt < 0) tocAt = into.Count;
                boxes.Clear();
                return;
            }
            foreach (var part in parts)
            {
                // A paragraph that held nothing but a page break is not kept: the break goes to the next paragraph.
                if (body && part != parts[0] && part.Length == 0 && part == parts[^1]) { breakNext = true; continue; }
                if (body && parts.Count > 1 && part == parts[0] && part.Length == 0 && e.Descendants(W + "t").All(t => t.Value.Length == 0))
                {
                    if (part.PageBreakBefore) breakNext = true;
                    continue;
                }
                // A character the font of the text does not have gets a font that has it.
                FontCatalog.Cover(doc, part);
                part.Touch();
                into.Add(part);
            }
            // What stood in text boxes at this paragraph follows it as ordinary text.
            if (boxes.Count > 0)
            {
                var waiting = boxes.ToList();
                boxes.Clear();
                foreach (var box in waiting)
                {
                    boxCount++;
                    long roomBefore = roomHere;
                    if (!ReferenceEquals(into, doc.Body) || !(box.Framed || box.Fill != null))
                    {
                        // As ordinary text: in the text of the document it has the whole width, elsewhere what its place has.
                        if (ReferenceEquals(into, doc.Body)) roomHere = doc.Page.ContentWidth;
                        Blocks(box.Content, into, false);
                        roomHere = roomBefore;
                        continue;
                    }
                    // A box with a line around it or a background stays a box: a table of one cell, as wide as the box was.
                    var cell = doc.NewCell();
                    cell.Paras.Clear();
                    long room = doc.Page.ContentWidth;
                    int boxWide = (int)Math.Clamp(box.Width, Math.Min(914400, room), room);
                    roomHere = Math.Max(0, boxWide - 2L * LayoutEngine.CellPadX);
                    Blocks(box.Content, cell.Paras, false);
                    roomHere = roomBefore;
                    if (cell.Paras.Count == 0) cell.Paras.Add(new Paragraph { Style = "Normal" });
                    foreach (var cp in cell.Paras) cp.Touch();
                    var grid = new TableGrid { Widths = new[] { boxWide }, Lines = box.Framed ? "all" : "none" };
                    grid.Rows.Add(new[] { cell.Id });
                    if (box.Fill is { } fill) grid.Shades[cell.Id] = fill;
                    into.Add(new Paragraph { Style = "Normal", Table = grid, Align = box.Side });
                    boxFramed++;
                }
            }
        }

        static Paragraph Follow(Paragraph p) => new()
        {
            Style = p.Style, Align = p.Align, Line = p.Line, IndentLeft = p.IndentLeft, IndentRight = p.IndentRight, Before = p.Before, After = p.After, PageBreakBefore = true, Mark = p.Mark,
        };

        bool Visible => fields.TrueForAll(f => f.Shown);

        /// <summary>What stands inside a paragraph, in its order. A page break in the middle starts a paragraph of its own.</summary>
        void Inlines(XElement container, List<Paragraph> parts, Look outer, string? link, bool body)
        {
            foreach (var e in container.Elements())
            {
                if (e.Name == W + "r") Run(e, parts, outer, link, body);
                else if (e.Name == W + "hyperlink")
                {
                    string? to = (string?)e.Attribute(R + "id") is { } id && rels.TryGetValue(id, out var rel) && rel.External ? rel.Target : null;
                    Inlines(e, parts, outer, Address(to) ?? link, body);
                }
                else if (e.Name == W + "fldSimple")
                {
                    string instr = ((string?)e.Attribute(W + "instr") ?? "").Trim();
                    Inlines(e, parts, outer, LinkOf(instr) ?? link, body);
                }
                else if (e.Name == W + "ins" || e.Name == W + "smartTag" || e.Name == W + "customXml" || e.Name == W + "dir" || e.Name == W + "bdo") Inlines(e, parts, outer, link, body);
                else if (e.Name == W + "sdt" && e.Element(W + "sdtContent") is { } content) Inlines(content, parts, outer, link, body);
                else if (e.Name == W + "del" || e.Name == W + "moveFrom") Count("tracked changes");
                else if (e.Name == M + "oMathPara")
                {
                    foreach (var m in e.Elements(M + "oMath")) Equation(m, parts[^1], true, body);
                }
                else if (e.Name == M + "oMath") Equation(e, parts[^1], false, body);
                else if (e.Name == MC + "AlternateContent" && (e.Element(MC + "Choice") ?? e.Element(MC + "Fallback")) is { } choice) Inlines(choice, parts, outer, link, body);
                else if (e.Name == W + "commentRangeStart") Count("comments");
            }
        }

        static string? Address(string? to) => to is { Length: > 0 and <= 2000 } && !to.Any(c => c <= ' ' || c == 127) ? to : null;

        static string? LinkOf(string instr)
        {
            if (!instr.TrimStart().StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase)) return null;
            int a = instr.IndexOf('"'), b = a < 0 ? -1 : instr.IndexOf('"', a + 1);
            return b > a ? Address(instr.Substring(a + 1, b - a - 1)) : null;
        }

        void Run(XElement r, List<Paragraph> parts, Look outer, string? link, bool body)
        {
            var rPr = r.Element(W + "rPr");
            var k = outer.Copy();
            if (Val(rPr?.Element(W + "rStyle")) is { } charStyle) k.Take(LookOf(charStyle, false));
            ReadRun(rPr, k);
            foreach (var e in r.Elements())
            {
                string name = e.Name.LocalName;
                if (e.Name.Namespace == MC && name == "AlternateContent")
                {
                    var choice = e.Element(MC + "Choice") ?? e.Element(MC + "Fallback");
                    if (choice != null)
                        foreach (var d in choice.Elements()) Piece(d, parts, k, link, body);
                    continue;
                }
                if (e.Name.Namespace != W) continue;
                Piece(e, parts, k, link, body);
            }
        }

        void Piece(XElement e, List<Paragraph> parts, Look k, string? link, bool body)
        {
            string name = e.Name.LocalName;
            var p = parts[^1];
            switch (name)
            {
                case "fldChar":
                    string type = (string?)e.Attribute(W + "fldCharType") ?? "";
                    if (type == "begin") fields.Add(new Field());
                    else if (type == "separate" && fields.Count > 0)
                    {
                        fields[^1].Shown = true;
                        fields[^1].Link = LinkOf(fields[^1].Instr.ToString());
                    }
                    else if (type == "end" && fields.Count > 0) fields.RemoveAt(fields.Count - 1);
                    return;
                case "instrText":
                    if (fields.Count > 0 && !fields[^1].Shown) fields[^1].Instr.Append(e.Value);
                    return;
            }
            if (!Visible || k.Hidden == true) return;
            string? to = link;
            for (int i = fields.Count - 1; i >= 0 && to == null; i--) to = fields[i].Link;
            switch (name)
            {
                case "t": Text(p, e.Value, k, to); break;
                case "tab": Text(p, "\t", k, to); break;
                case "cr": Text(p, Document.LineBreak.ToString(), k, to); break;
                case "noBreakHyphen": Text(p, "-", k, to); break;
                case "sym":
                    int code = int.TryParse((string?)e.Attribute(W + "char"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int c) ? c : 0;
                    // Signs of the symbol fonts of Windows have no place of their own among the characters: the commonest become what they look like.
                    string sign = code switch { 0xF0B7 or 0xF0A8 => "•", 0xF0A7 => "▪", 0xF0FC => "✓", 0xF0E0 or 0xF0AE => "→", 0xF0D8 => "➢", > 31 and < 0xD800 => ((char)code).ToString(), _ => "" };
                    Text(p, sign, k, to);
                    break;
                case "br":
                    string kind = (string?)e.Attribute(W + "type") ?? "";
                    if (kind == "page" && body) parts.Add(Follow(parts[0]));
                    else if (kind != "page" && kind != "column") Text(p, Document.LineBreak.ToString(), k, to);
                    break;
                case "drawing": Drawing(e, p, k, body); break;
                case "pict":
                case "object":
                    if (e.Descendants(W + "txbxContent").FirstOrDefault() is { } old)
                    {
                        // The older way of writing a text box: its line and background are attributes of the shape.
                        var shape = old.Ancestors().FirstOrDefault(a => a.Name.Namespace == V && a.Name.LocalName is "shape" or "rect" or "roundrect");
                        int? back = (string?)shape?.Attribute("filled") is "f" or "false" ? null : Hex(((string?)shape?.Attribute("fillcolor"))?.TrimStart('#'));
                        boxes.Add(new Box(old, (string?)shape?.Attribute("stroked") is not ("f" or "false"), back is 0xFFFFFF ? null : back, doc.Page.ContentWidth / 2, Align.Left));
                    }
                    else if (!OldPicture(e, p, k, body)) Count("drawings and embedded objects");
                    break;
                case "footnoteReference":
                case "endnoteReference":
                    var source = name == "footnoteReference" ? footnotes : endnotes;
                    if (!body) Count("footnotes inside tables and text boxes");
                    else if ((string?)e.Attribute(W + "id") is { } id && source.TryGetValue(id, out var note)) Note(note, p, k);
                    break;
                case "commentReference": Count("comments"); break;
            }
        }

        static CharProps Props(Look k, string? link)
        {
            var props = new CharProps(k.Font, k.Size, k.Bold, k.Italic, k.Underline, k.Color, k.Strike, k.Highlight, k.Vert, k.Underline == true ? k.Under : null);
            if (link != null) props = props with { Link = link, Color = k.Color ?? Editor.LinkColour, Underline = true };
            return props;
        }

        static void Text(Paragraph p, string text, Look k, string? link)
        {
            if (text.Length == 0) return;
            var clean = new StringBuilder(text.Length);
            foreach (char c in text)
                if (c == '\t' || c == Document.LineBreak || c >= ' ' && c != (char)0x7F && c != ReiksDocument.Run.ObjectChar && c != (char)0xAD) clean.Append(c == (char)0xA0 ? ' ' : c);
            string s = k.Caps == true ? clean.ToString().ToUpperInvariant() : clean.ToString();
            if (s.Length > 0) p.Runs.Add(new Run(s, Props(k, link)));
        }

        void Note(XElement source, Paragraph p, Look k)
        {
            var note = doc.NewNote();
            note.Paras.Clear();
            var saved = fields.ToList();
            fields.Clear();
            long roomBefore = roomHere;
            roomHere = 0;
            Blocks(source, note.Paras, false);
            roomHere = roomBefore;
            fields.Clear();
            fields.AddRange(saved);
            foreach (var np in note.Paras)
            {
                np.Style = "Footnote";
                np.Align = null;
                np.IndentLeft = np.IndentRight = np.IndentFirst = np.Before = np.After = np.Line = null;
            }
            // Word begins the text of a note with a space behind its number.
            if (note.Paras.Count > 0 && note.Paras[0].Runs.Count > 0 && note.Paras[0].Runs[0].Text.StartsWith(' '))
                note.Paras[0].Runs[0] = note.Paras[0].Runs[0] with { Text = note.Paras[0].Runs[0].Text.TrimStart(' ') };
            if (note.Paras.Count == 0) note.Paras.Add(new Paragraph { Style = "Footnote" });
            foreach (var np in note.Paras) np.Touch();
            p.Runs.Add(ReiksDocument.Run.Object(new NoteRef(note.Id), new Run("", Props(k, null))));
        }

        // ---- pictures ----

        /// <summary>The picture of an older drawing, or the picture that stands for an embedded object (an equation of the old
        /// kind, a field of a form, a piece of another program): it goes into the line as a picture, at the size its shape has.</summary>
        bool OldPicture(XElement e, Paragraph p, Look k, bool body)
        {
            if (!body && roomHere <= 0) return false;
            var image = e.Descendants(V + "imagedata").FirstOrDefault();
            if (image == null || (string?)image.Attribute(R + "id") is not { } id || !rels.TryGetValue(id, out var rel) || rel.External || Bytes(rel.Target) is not { } data) return false;
            var img = PageRenderer.Decode(data, out bool jpeg);
            if (img == null && Raster(data) is { } png)
            {
                data = png;
                img = PageRenderer.Decode(data, out jpeg);
            }
            if (img == null) return false;
            // The size is in the style of the shape, as in "width:106pt;height:49.3pt".
            static long Length(string? style, string name)
            {
                foreach (string part in (style ?? "").Split(';'))
                {
                    int colon = part.IndexOf(':');
                    if (colon < 0 || part[..colon].Trim() != name) continue;
                    string v = part[(colon + 1)..].Trim();
                    int digits = 0;
                    while (digits < v.Length && (char.IsDigit(v[digits]) || v[digits] == '.')) digits++;
                    if (!double.TryParse(v[..digits], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return 0;
                    double emu = v[digits..].Trim() switch { "pt" => 12700, "in" => 914400, "cm" => 360000, "mm" => 36000, "px" or "" => 9525, "pc" => 152400, _ => 0 };
                    return (long)Math.Round(n * emu);
                }
                return 0;
            }
            string? style = (string?)image.Ancestors().FirstOrDefault(a => a.Name.Namespace == V && a.Attribute("style") != null)?.Attribute("style");
            long w = Length(style, "width"), h = Length(style, "height");
            if (w <= 0 || h <= 0) (w, h) = (img.PixelWidth * 9525L, img.PixelHeight * 9525L);
            var pg = doc.Page;
            long room = body ? pg.ContentWidth : roomHere, tall = (pg.Height - pg.Top - pg.Bottom) * 3L / 4;
            if (w > room) { h = Math.Max(1, h * room / w); w = room; }
            if (h > tall) { w = Math.Max(1, w * tall / h); h = tall; }
            string media = RksFile.MediaName(data, jpeg);
            doc.Media[media] = data;
            p.Runs.Add(ReiksDocument.Run.Object(new Picture(media, (int)w, (int)h, false), new Run("", Props(k, null))));
            oldPictures++;
            return true;
        }

        void Drawing(XElement drawing, Paragraph p, Look k, bool body)
        {
            var anchor = drawing.Element(WP + "anchor");
            var frame = drawing.Element(WP + "inline") ?? anchor;
            if (frame == null) return;
            if (frame.Descendants(W + "txbxContent").FirstOrDefault() is { } box)
            {
                var shape = frame.Descendants(WPS + "spPr").FirstOrDefault();
                var outline = shape?.Element(A + "ln");
                bool framed = outline != null ? outline.Element(A + "noFill") == null
                    : int.TryParse((string?)frame.Descendants(WPS + "style").FirstOrDefault()?.Element(A + "lnRef")?.Attribute("idx"), out int lineStyle) && lineStyle > 0;
                int? back = shape?.Element(A + "noFill") != null ? null : Hex((string?)shape?.Element(A + "solidFill")?.Element(A + "srgbClr")?.Attribute("val"));
                long wide = long.TryParse((string?)frame.Element(WP + "extent")?.Attribute("cx"), out long bx) && bx > 0 ? bx : doc.Page.ContentWidth / 2;
                var where = frame.Element(WP + "positionH");
                string at = (string?)where?.Element(WP + "align") ?? "";
                long off = long.TryParse((string?)where?.Element(WP + "posOffset"), out long bo) ? bo : 0;
                if ((string?)where?.Attribute("relativeFrom") is "page" or "leftMargin") off -= doc.Page.Left;
                long roomNow = doc.Page.ContentWidth;
                var side = at is "right" or "outside" ? Align.Right : at == "center" ? Align.Center
                    : at.Length == 0 && wide < roomNow && off + wide / 2 > roomNow * 2 / 3 ? Align.Right : at.Length == 0 && wide < roomNow && off + wide / 2 > roomNow / 3 ? Align.Center : Align.Left;
                boxes.Add(new Box(box, framed, back is 0xFFFFFF ? null : back, wide, side));
                return;
            }
            if (frame.Descendants(C + "chart").FirstOrDefault() is { } chartRef)
            {
                // A chart of Word keeps its numbers in the file: it is drawn again as a chart of this program.
                if (!body && roomHere < 360000) Count("charts in footnotes and in places too narrow for them");
                else if ((string?)chartRef.Attribute(R + "id") is { } cid && rels.TryGetValue(cid, out var crel) && !crel.External && Part(crel.Target) is { } space
                    && ChartOf(space, frame, body && anchor != null, body ? doc.Page.ContentWidth : roomHere) is { } chart)
                {
                    p.Runs.Add(ReiksDocument.Run.Object(chart, new Run("", Props(k, null))));
                    charts++;
                }
                else Count("charts of a kind that cannot be drawn here");
                return;
            }
            var blip = frame.Descendants(A + "blip").FirstOrDefault();
            if (blip == null)
            {
                Count("shapes and diagrams");
                return;
            }
            if (!body && roomHere <= 0)
            {
                Count("pictures in footnotes");
                return;
            }
            if ((string?)blip.Attribute(R + "embed") is not { } id || !rels.TryGetValue(id, out var rel) || rel.External || Bytes(rel.Target) is not { } data)
            {
                Count("pictures that are not inside the file");
                return;
            }
            var img = PageRenderer.Decode(data, out bool jpeg);
            if (img == null && Raster(data) is { } png)
            {
                data = png;
                img = PageRenderer.Decode(data, out jpeg);
            }
            if (img == null)
            {
                Count("pictures in a form that cannot be shown here (such as EMF or WMF)");
                return;
            }
            string media = RksFile.MediaName(data, jpeg);
            doc.Media[media] = data;
            var extent = frame.Element(WP + "extent");
            long w = long.TryParse((string?)extent?.Attribute("cx"), out long cx) && cx > 0 ? cx : img.PixelWidth * 9525L;
            long h = long.TryParse((string?)extent?.Attribute("cy"), out long cy) && cy > 0 ? cy : img.PixelHeight * 9525L;
            var pg = doc.Page;
            // In a table cell or a box a picture stands in the line and is no wider than its place.
            long room = body ? pg.ContentWidth : roomHere, tall = (pg.Height - pg.Top - pg.Bottom) * 3L / 4;
            if (w > room) { h = Math.Max(1, h * room / w); w = room; }
            if (h > tall) { w = Math.Max(1, w * tall / h); h = tall; }
            Picture pic;
            if (anchor == null || !body) pic = new Picture(media, (int)w, (int)h, false);
            else
            {
                floats++;
                bool beside = anchor.Element(WP + "wrapSquare") != null || anchor.Element(WP + "wrapTight") != null || anchor.Element(WP + "wrapThrough") != null;
                // Text runs beside a picture only where enough of the line is left for it; a picture over or under the text
                // gets a line of its own. On which side it stands is taken from Word; where exactly, the layout decides.
                if (beside && w <= room * 6 / 10 && room - w >= LayoutEngine.MinWrapWidth + LayoutEngine.FloatGap)
                {
                    var pos = anchor.Element(WP + "positionH");
                    string align = (string?)pos?.Element(WP + "align") ?? "";
                    long offset = long.TryParse((string?)pos?.Element(WP + "posOffset"), out long o) ? o : 0;
                    if ((string?)pos?.Attribute("relativeFrom") is "page" or "leftMargin") offset -= pg.Left;
                    bool right = align is "right" or "outside" || align.Length == 0 && offset + w / 2 > room / 2;
                    pic = new Picture(media, (int)w, (int)h, false, "square", right ? (int)(room - w) : 0, 0);
                }
                else pic = new Picture(media, (int)w, (int)h, true);
            }
            p.Runs.Add(ReiksDocument.Run.Object(pic, new Run("", Props(k, null))));
        }

        // ---- charts ----

        /// <summary>The values a chart keeps for one row of its sheet: the texts of its points, in their order.</summary>
        static List<string> Points(XElement? source)
        {
            var list = new List<string>();
            var cache = source?.Descendants().FirstOrDefault(e => e.Name.Namespace == C && e.Name.LocalName is "strCache" or "numCache" or "strLit" or "numLit" or "lvl");
            if (cache == null) return list;
            string format = cache.Element(C + "formatCode")?.Value ?? "";
            bool dates = format.Contains("yy", StringComparison.OrdinalIgnoreCase) || format.Contains("mmm", StringComparison.OrdinalIgnoreCase) || format.Contains("d/") || format.Contains("/d") || format.Contains("dd");
            foreach (var pt in cache.Elements(C + "pt"))
            {
                int at = int.TryParse((string?)pt.Attribute("idx"), out int i) ? i : list.Count;
                if (at < 0 || at >= 400) continue;
                while (list.Count <= at) list.Add("");
                string v = pt.Element(C + "v")?.Value ?? "";
                // A date is kept as the number of its day.
                if (dates && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double day) && day > 1 && day < 2958465)
                    v = DateTime.FromOADate(day).ToString("d MMM yyyy", CultureInfo.InvariantCulture);
                list[at] = v;
            }
            return list;
        }

        static string Words(XElement? e) => e == null ? "" : string.Join("", e.Descendants(A + "t").Select(t => t.Value)).Trim();

        Chart? ChartOf(XElement space, XElement frame, bool block, long room)
        {
            var plot = space.Descendants(C + "plotArea").FirstOrDefault();
            if (plot == null) return null;
            string kind = "";
            bool simpler = false;
            var names = new List<string>();
            var rows = new List<List<string>>();
            List<string>? labels = null;
            foreach (var group in plot.Elements())
            {
                if (group.Name.Namespace != C) continue;
                string local = group.Name.LocalName;
                string k = local switch
                {
                    "barChart" or "bar3DChart" or "areaChart" or "area3DChart" => "bar", "lineChart" or "line3DChart" or "radarChart" or "scatterChart" or "stockChart" => "line",
                    "pieChart" or "pie3DChart" or "doughnutChart" or "ofPieChart" => "pie", _ => "",
                };
                if (k.Length == 0) continue;
                if (kind.Length == 0) kind = k;
                // Kinds this program does not have are drawn as the nearest it has: plain bars, lines or a pie.
                simpler |= local is not ("barChart" or "lineChart" or "pieChart") || (string?)group.Element(C + "grouping")?.Attribute("val") is "stacked" or "percentStacked"
                    || (string?)group.Element(C + "barDir")?.Attribute("val") == "bar" || k != kind;
                foreach (var ser in group.Elements(C + "ser"))
                {
                    if (rows.Count >= 12) { simpler = true; break; }
                    var seriesName = Points(ser.Element(C + "tx"));
                    names.Add(seriesName.Count > 0 && seriesName[0].Length > 0 ? seriesName[0] : ser.Element(C + "tx")?.Element(C + "v")?.Value ?? "Series " + (rows.Count + 1));
                    var own = Points(ser.Element(C + "cat") ?? ser.Element(C + "xVal"));
                    if (labels == null && own.Count > 0) labels = own;
                    rows.Add(Points(ser.Element(C + "val") ?? ser.Element(C + "yVal")));
                }
            }
            if (kind.Length == 0 || rows.Count == 0) return null;
            int n = Math.Min(200, Math.Max(labels?.Count ?? 0, rows.Max(r => r.Count)));
            if (n == 0) return null;
            var cats = new List<string>();
            for (int i = 0; i < n; i++) cats.Add(labels != null && i < labels.Count && labels[i].Length > 0 ? RksFile.BandText(labels[i]) : (i + 1).ToString(CultureInfo.InvariantCulture));
            var series = new List<Series>();
            for (int s = 0; s < rows.Count && (kind != "pie" || s == 0); s++)
            {
                var values = new long[n];
                for (int i = 0; i < n; i++)
                    values[i] = i < rows[s].Count && decimal.TryParse(rows[s][i], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) ? RksFile.Thousandths(d) : 0;
                series.Add(new Series(RksFile.BandText(names[s]), values));
            }
            simpler |= kind == "pie" && rows.Count > 1;
            if (simpler) chartsSimple++;

            var extent = frame.Element(WP + "extent");
            long w = long.TryParse((string?)extent?.Attribute("cx"), out long cx) && cx > 0 ? cx : 4320000, h = long.TryParse((string?)extent?.Attribute("cy"), out long cy) && cy > 0 ? cy : 2700000;
            var pg = doc.Page;
            long tall = (pg.Height - pg.Top - pg.Bottom) * 3L / 4;
            if (w > room) { h = Math.Max(1, h * room / w); w = room; }
            if (h > tall) { w = Math.Max(1, w * tall / h); h = tall; }
            var whole = space.Descendants(C + "chart").FirstOrDefault();
            string title = RksFile.BandText(Words(whole?.Element(C + "title")));
            string across = kind == "pie" ? "" : RksFile.BandText(Words((plot.Element(C + "catAx") ?? plot.Element(C + "dateAx") ?? plot.Elements(C + "valAx").Skip(1).FirstOrDefault())?.Element(C + "title")));
            string up = kind == "pie" ? "" : RksFile.BandText(Words(plot.Element(C + "valAx")?.Element(C + "title")));
            return new Chart(kind, title, cats, series, (int)Math.Max(360000, w), (int)Math.Max(360000, h), block, "", 0, 0, across, up);
        }

        /// <summary>A drawing in one of the older forms of Windows (EMF or WMF, also packed as EMZ or WMZ), drawn once at 200 dots
        /// to the inch, no side longer than 3000 dots, and written as PNG. Null if it is none, or cannot be drawn.</summary>
        public static byte[]? Drawn(byte[] data)
        {
            try
            {
                if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
                {
                    using var packed = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
                    var plain = new MemoryStream();
                    packed.CopyTo(plain);
                    data = plain.ToArray();
                }
                bool emf = data.Length > 44 && BitConverter.ToUInt32(data, 0) == 1 && BitConverter.ToUInt32(data, 40) == 0x464D4520;
                bool wmf = data.Length > 22 && (BitConverter.ToUInt32(data, 0) == 0x9AC6CDD7 || data[0] is 1 or 2 && data[1] == 0 && data[2] == 9 && data[3] == 0);
                if (!emf && !wmf) return null;
                using var meta = new System.Drawing.Imaging.Metafile(new MemoryStream(data));
                double wide = meta.Width / (double)meta.HorizontalResolution, high = meta.Height / (double)meta.VerticalResolution;
                if (!(wide > 0 && high > 0)) return null;
                double dots = Math.Min(200, 3000 / Math.Max(wide, high));
                int w = Math.Max(16, (int)Math.Round(wide * dots)), h = Math.Max(16, (int)Math.Round(high * dots));
                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(meta, 0, 0, w, h);
                }
                using var png = new MemoryStream();
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                return png.ToArray();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException or OutOfMemoryException or IOException or InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>A picture in another form (GIF, TIFF, BMP; a drawing as EMF or WMF), written again as PNG.</summary>
        static byte[]? Raster(byte[] data)
        {
            if (Drawn(data) is { } drawn) return drawn;
            try
            {
                var frame = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(frame));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or OverflowException)
            {
                return null;
            }
        }

        // ---- tables ----

        void Table(XElement tbl, List<Paragraph> into)
        {
            var rows = tbl.Elements(W + "tr").ToList();
            var widths = tbl.Element(W + "tblGrid")?.Elements(W + "gridCol").Select(c => Math.Max(90000L, (Num(c, "w") ?? 1440) * (long)Twip)).ToList() ?? new List<long>();
            int Span(XElement tc) => Math.Clamp(Num(tc.Element(W + "tcPr")?.Element(W + "gridSpan"), "val") ?? 1, 1, 63);
            int columns = widths.Count;
            foreach (var tr in rows)
                columns = Math.Max(columns, (Num(tr.Element(W + "trPr")?.Element(W + "gridBefore"), "val") ?? 0) + tr.Elements(W + "tc").Sum(Span));
            columns = Math.Min(columns, 63);
            if (rows.Count == 0 || columns == 0) return;
            long room = doc.Page.ContentWidth;
            while (widths.Count < columns) widths.Add(widths.Count > 0 ? widths[^1] : room / columns);
            if (widths.Count > columns) widths.RemoveRange(columns, widths.Count - columns);
            // A table wider than the text is made to fit.
            long total = widths.Sum();
            var grid = new TableGrid { Widths = new int[columns] };
            long sum = 0, placed = 0;
            for (int c = 0; c < columns; c++)
            {
                // Each column ends where its share of the room ends, so that together they are as wide as the text.
                sum += widths[c];
                long edge = total > room ? U.RoundDiv(sum * room, total) : sum;
                grid.Widths[c] = (int)Math.Max(1, edge - placed);
                placed += grid.Widths[c];
            }

            foreach (var tr in rows)
            {
                var ids = new int[columns];
                int col = Math.Clamp(Num(tr.Element(W + "trPr")?.Element(W + "gridBefore"), "val") ?? 0, 0, columns);
                foreach (var tc in tr.Elements(W + "tc"))
                {
                    if (col >= columns) break;
                    int span = Math.Min(Span(tc), columns - col);
                    var tcPr = tc.Element(W + "tcPr");
                    var merge = tcPr?.Element(W + "vMerge");
                    bool goesOn = merge != null && Val(merge) is not "restart";
                    int above = grid.Rows.Count > 0 ? grid.Rows[^1][col] : 0;
                    // A cell that goes on from the row above must reach as far as that one does, or it is a cell of its own.
                    bool same = goesOn && above != 0 && Enumerable.Range(col, span).All(c => grid.Rows[^1][c] == above)
                        && (col == 0 || grid.Rows[^1][col - 1] != above) && (col + span >= columns || grid.Rows[^1][col + span] != above);
                    int id;
                    if (same) id = above;
                    else
                    {
                        var cell = doc.NewCell();
                        cell.Paras.Clear();
                        long roomBefore = roomHere;
                        roomHere = Math.Max(0, grid.SpanWidth(col, span) - 2L * LayoutEngine.CellPadX);
                        Blocks(tc, cell.Paras, false);
                        roomHere = roomBefore;
                        if (cell.Paras.Count == 0) cell.Paras.Add(new Paragraph { Style = "Normal" });
                        foreach (var cp in cell.Paras) cp.Touch();
                        id = cell.Id;
                        if (Hex((string?)tcPr?.Element(W + "shd")?.Attribute(W + "fill")) is { } fill && fill != 0xFFFFFF) grid.Shades[id] = fill;
                    }
                    for (int c = 0; c < span; c++) ids[col + c] = id;
                    col += span;
                }
                for (int c = 0; c < columns; c++)
                    if (ids[c] == 0) ids[c] = doc.NewCell().Id;
                grid.Rows.Add(ids);
            }

            var tblPr = tbl.Element(W + "tblPr");
            var borders = tblPr?.Element(W + "tblBorders");
            for (string? sid = Val(tblPr?.Element(W + "tblStyle")); borders == null && sid != null && styles.TryGetValue(sid, out var ts); sid = Val(ts.Element(W + "basedOn")))
            {
                borders = ts.Element(W + "tblPr")?.Element(W + "tblBorders");
                if (ReferenceEquals(ts, styles.GetValueOrDefault(Val(ts.Element(W + "basedOn")) ?? ""))) break;
            }
            bool Drawn(string side) => borders?.Element(W + side) is { } b && Val(b) is not ("none" or "nil");
            bool anyCell = tbl.Descendants(W + "tcBorders").Any();
            bool outer = Drawn("top") || Drawn("bottom") || Drawn("left") || Drawn("right") || Drawn("start") || Drawn("end");
            bool across = Drawn("insideH"), down = Drawn("insideV");
            grid.Lines = borders == null ? (anyCell ? "all" : "none") : outer && (across || down) ? (across && !down && !Drawn("left") && !Drawn("start") ? "horizontal" : "all")
                : outer ? "outer" : across && !down ? "horizontal" : across || down || anyCell ? "all" : "none";

            var tp = new Paragraph { Style = "Normal", Table = grid };
            if (Val(tblPr?.Element(W + "jc")) is "center") tp.Align = Align.Center;
            else if (Val(tblPr?.Element(W + "jc")) is "right" or "end") tp.Align = Align.Right;
            if (breakNext)
            {
                tp.PageBreakBefore = true;
                breakNext = false;
            }
            into.Add(tp);
        }

        /// <summary>Word sets the widths of the columns of a table by what stands in them; the widths it writes into the file are often
        /// not those. So where a word (or a picture) is wider than its column and would be cut, the columns are shared out again:
        /// each gets what its widest word needs, and what is left over goes to the columns in the measure of what they had beyond that.
        /// If the widest words do not fit side by side in the width of the text, the text of the table is made smaller until they do,
        /// but not below six tenths of its size; a table that needs more than that stays as it is.</summary>
        void FitTables()
        {
            if (!doc.Body.Exists(p => p.Table is { Columns: > 1 })) return;
            var eng = new LayoutEngine(doc);
            eng.Update();
            long room = doc.Page.ContentWidth;

            // What the widest word (or picture) of a cell needs, with the room at its sides.
            long Need(Cell cell)
            {
                long most = 0;
                foreach (var cp in cell.Paras)
                {
                    if (cp.Data is not { } data) continue;
                    string text = cp.Text;
                    var style = doc.StyleOf(cp);
                    long sides = 2L * LayoutEngine.CellPadX + Math.Max(0, cp.IndentLeft ?? style.IndentLeft) + Math.Max(0, cp.IndentRight ?? style.IndentRight);
                    long word = 0, widest = 0;
                    for (int i = 0; i < text.Length && i < data.Nat.Length; i++)
                    {
                        char ch = text[i];
                        if (ch == ' ' || ch == '\t' || ch == Document.LineBreak)
                        {
                            widest = Math.Max(widest, word);
                            word = 0;
                            continue;
                        }
                        word += data.Nat[i];
                        // A line can end behind a hyphen.
                        if (ch == '-')
                        {
                            widest = Math.Max(widest, word);
                            word = 0;
                        }
                    }
                    most = Math.Max(most, Math.Max(widest, word) + sides);
                }
                return most;
            }

            foreach (var p in doc.Body)
            {
                if (p.Table is not { Columns: > 1 } t) continue;
                for (int round = 0; round < 2; round++)
                {
                    var need = new long[t.Columns];
                    var joined = new List<(int Col, int Cols, long Need)>();
                    for (int r = 0; r < t.Rows.Count; r++)
                        for (int c = 0; c < t.Columns; c++)
                        {
                            if (!t.Starts(r, c) || !doc.Cells.TryGetValue(t.Rows[r][c], out var cell)) continue;
                            int cols = t.Span(r, c).Cols;
                            if (cols == 1) need[c] = Math.Max(need[c], Need(cell));
                            else joined.Add((c, cols, Need(cell)));
                        }
                    bool cut = false;
                    long all = 0, had = 0, total = t.Width;
                    for (int c = 0; c < t.Columns; c++)
                    {
                        cut |= need[c] > t.Widths[c];
                        all += need[c];
                        had += Math.Max(0, t.Widths[c] - need[c]);
                    }
                    if (!cut) break;
                    long target = Math.Min(room, Math.Max(total, all));
                    if (all > target)
                    {
                        // The words do not fit side by side: smaller text, once.
                        // The room at the sides of the cells stays as it is; only the words get narrower.
                        long sidesAll = 2L * LayoutEngine.CellPadX * t.Columns;
                        double by = all > sidesAll && target > sidesAll ? (double)(target - sidesAll) / (all - sidesAll) * 0.97 : 0;
                        if (round > 0 || by < 0.6) break;
                        foreach (var row in t.Rows)
                            foreach (int id in row.Distinct())
                                if (doc.Cells.TryGetValue(id, out var cell))
                                    foreach (var cp in cell.Paras)
                                    {
                                        int size = doc.StyleOf(cp).Char.Size;
                                        for (int k = 0; k < cp.Runs.Count; k++)
                                            cp.Runs[k] = cp.Runs[k] with { Props = cp.Runs[k].Props with { Size = Math.Max(12, (int)Math.Round((cp.Runs[k].Props.Size ?? size) * by)) } };
                                        cp.Mark = new Run("", new CharProps(Font: cp.Mark?.Props.Font, Size: Math.Max(12, (int)Math.Round((cp.Mark?.Props.Size ?? size) * by))));
                                        cp.Touch();
                                    }
                        shrunk++;
                        eng.InvalidateAll();
                        eng.Update();
                        continue;
                    }
                    long spare = target - all, placed = 0;
                    var widths = new int[t.Columns];
                    for (int c = 0; c < t.Columns; c++)
                    {
                        long extra = c == t.Columns - 1 ? spare - placed : had > 0 ? spare * Math.Max(0, t.Widths[c] - need[c]) / had : spare / t.Columns;
                        if (c < t.Columns - 1) placed += extra;
                        widths[c] = (int)Math.Max(1, need[c] + Math.Max(0, extra));
                    }
                    // A cell joined over several columns must still hold what stands in it.
                    bool holds = true;
                    foreach (var (col, cols, wide) in joined)
                    {
                        long sum = 0;
                        for (int k = 0; k < cols; k++) sum += widths[col + k];
                        holds &= sum >= Math.Min(wide, t.SpanWidth(col, cols));
                    }
                    if (!holds) break;
                    t.Widths = widths;
                    refitted++;
                    foreach (var row in t.Rows)
                        foreach (int id in row)
                            if (doc.Cells.TryGetValue(id, out var cell))
                                foreach (var cp in cell.Paras) cp.Touch();
                    p.Touch();
                    break;
                }
            }
            eng.InvalidateAll();
        }

        // ---- equations ----

        void Equation(XElement math, Paragraph p, bool block, bool body)
        {
            bool simple = false;
            string source = Linear(math, ref simple).Trim();
            if (source.Length == 0) return;
            if (!body && roomHere <= 0)
            {
                // Where an equation cannot stand, its line of text does.
                p.Runs.Add(new Run(source, new CharProps(Italic: true)));
                Count("equations in footnotes, shown as a line of text");
                return;
            }
            // The size and the colour Word gives the equation; without them, those of the text before it. The font stays
            // that of the paragraph: an equation is set in the font for mathematics whatever its run says.
            var look = new Look();
            ReadRun(math.Descendants(W + "rPr").FirstOrDefault(r => r.Element(W + "sz") != null) ?? math.Descendants(W + "rPr").FirstOrDefault(), look);
            int? size = look.Size ?? p.Runs.LastOrDefault(r => r.Props.Size != null)?.Props.Size;
            var format = new Run("", new CharProps(Size: size, Color: look.Color is { } ink && ink != 0 ? ink : null));
            try
            {
                p.Runs.Add(ReiksDocument.Run.Object(ReiksDocument.Equation.From(source, block && body), format));
                if (simple) eqSimple++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or IndexOutOfRangeException)
            {
                p.Runs.Add(new Run(source, new CharProps(Italic: true)));
                eqSimple++;
            }
        }

        static string Arg(XElement? e, ref bool simple) => e == null ? "" : Linear(e, ref simple).Trim();

        /// <summary>One element for an exponent or index to stand on: as it is if it is a plain letter, name or number, else in brackets.</summary>
        static string Base(string s)
        {
            if (s.Length == 0 || s.All(char.IsLetterOrDigit)) return s;
            // In brackets already, if the bracket it begins with is the one it ends with.
            if (s[0] == '(' && s[^1] == ')')
            {
                int level = 0, k = 0;
                for (; k < s.Length; k++)
                {
                    if (s[k] == '(') level++;
                    else if (s[k] == ')' && --level == 0) break;
                }
                if (k == s.Length - 1) return s;
            }
            return "(" + s + ")";
        }

        /// <summary>An equation of Word as the line this program's equations are typed in.</summary>
        static string Linear(XElement e, ref bool simple)
        {
            var sb = new StringBuilder();
            foreach (var c in e.Elements())
            {
                if (c.Name.Namespace != M) continue;
                switch (c.Name.LocalName)
                {
                    case "r":
                        foreach (var t in c.Elements(M + "t"))
                            foreach (char ch in t.Value)
                                if (ch is not (>= '\u2061' and <= '\u2064') and not (>= '\u200B' and <= '\u200D') and not '\uFEFF') sb.Append(ch == '\u00A0' ? ' ' : ch);
                        break;
                    case "f":
                        string num = Arg(c.Element(M + "num"), ref simple), den = Arg(c.Element(M + "den"), ref simple);
                        if ((string?)c.Element(M + "fPr")?.Element(M + "type")?.Attribute(M + "val") == "noBar")
                        {
                            sb.Append('(').Append(num).Append(", ").Append(den).Append(')');
                            simple = true;
                        }
                        else sb.Append(" (").Append(num).Append(")/(").Append(den).Append(") ");
                        break;
                    case "sSup": sb.Append(Base(Arg(c.Element(M + "e"), ref simple))).Append("^(").Append(Arg(c.Element(M + "sup"), ref simple)).Append(") "); break;
                    case "sSub": sb.Append(Base(Arg(c.Element(M + "e"), ref simple))).Append("_(").Append(Arg(c.Element(M + "sub"), ref simple)).Append(") "); break;
                    case "sSubSup":
                    case "sPre":
                        sb.Append(Base(Arg(c.Element(M + "e"), ref simple))).Append("_(").Append(Arg(c.Element(M + "sub"), ref simple)).Append(")^(").Append(Arg(c.Element(M + "sup"), ref simple)).Append(") ");
                        simple |= c.Name.LocalName == "sPre";
                        break;
                    case "rad":
                        string degree = Arg(c.Element(M + "deg"), ref simple), inside = Arg(c.Element(M + "e"), ref simple);
                        bool hidden = (string?)c.Element(M + "radPr")?.Element(M + "degHide")?.Attribute(M + "val") is "1" or "on" or "true";
                        // A root other than the square root is written as a power, which says the same.
                        if (hidden || degree.Length == 0 || degree == "2") sb.Append("\\sqrt(").Append(inside).Append(')');
                        else sb.Append('(').Append(inside).Append(")^(1/").Append(Base(degree)).Append(") ");
                        break;
                    case "nary":
                        string op = (string?)c.Element(M + "naryPr")?.Element(M + "chr")?.Attribute(M + "val") ?? "∫";
                        string from = Arg(c.Element(M + "sub"), ref simple), to = Arg(c.Element(M + "sup"), ref simple);
                        sb.Append(op);
                        if (from.Length > 0) sb.Append("_(").Append(from).Append(')');
                        if (to.Length > 0) sb.Append("^(").Append(to).Append(')');
                        sb.Append(' ').Append(Arg(c.Element(M + "e"), ref simple));
                        break;
                    case "d":
                        var pr = c.Element(M + "dPr");
                        string open = (string?)pr?.Element(M + "begChr")?.Attribute(M + "val") ?? "(", close = (string?)pr?.Element(M + "endChr")?.Attribute(M + "val") ?? ")";
                        string sep = (string?)pr?.Element(M + "sepChr")?.Attribute(M + "val") ?? "|";
                        var inner = new List<string>();
                        foreach (var part in c.Elements(M + "e")) inner.Add(Arg(part, ref simple));
                        sb.Append(open is "(" or "[" or "{" or "|" ? open : "(").Append(string.Join(sep, inner)).Append(close is ")" or "]" or "}" or "|" ? close : ")");
                        break;
                    case "func": sb.Append(Arg(c.Element(M + "fName"), ref simple)).Append(' ').Append(Arg(c.Element(M + "e"), ref simple)); break;
                    case "limLow": sb.Append(Base(Arg(c.Element(M + "e"), ref simple))).Append("_(").Append(Arg(c.Element(M + "lim"), ref simple)).Append(") "); break;
                    case "limUpp": sb.Append(Base(Arg(c.Element(M + "e"), ref simple))).Append("^(").Append(Arg(c.Element(M + "lim"), ref simple)).Append(") "); break;
                    case "m":
                        simple = true;
                        var lines = new List<string>();
                        foreach (var row in c.Elements(M + "mr"))
                        {
                            var cells = new List<string>();
                            foreach (var cell in row.Elements(M + "e")) cells.Add(Arg(cell, ref simple));
                            lines.Add(string.Join(", ", cells));
                        }
                        sb.Append(string.Join("; ", lines));
                        break;
                    case "eqArr":
                        simple = true;
                        var each = new List<string>();
                        foreach (var row in c.Elements(M + "e")) each.Add(Arg(row, ref simple));
                        sb.Append(string.Join("; ", each));
                        break;
                    case "acc":
                    case "bar":
                    case "groupChr":
                        simple = true;
                        sb.Append(Arg(c.Element(M + "e"), ref simple));
                        break;
                    case "box":
                    case "borderBox":
                    case "phant":
                    case "e":
                    case "oMath":
                        sb.Append(Linear(c.Name.LocalName is "e" or "oMath" ? c : c.Element(M + "e") ?? c, ref simple));
                        break;
                }
            }
            return sb.ToString();
        }

        // ---- header and footer ----

        void ReadBands(XElement? section, string main)
        {
            if (section == null) return;
            var sizes = new LayoutEngine(doc);
            Band One(string kind, bool first = false)
            {
                var use = section.Elements(W + kind).FirstOrDefault(r => first ? (string?)r.Attribute(W + "type") == "first" : (string?)r.Attribute(W + "type") is "default" or null);
                if ((string?)use?.Attribute(R + "id") is not { } id || !rels.TryGetValue(id, out var rel) || rel.External || Part(rel.Target) is not { } root) return Band.Empty;
                var own = Rels(rel.Target);
                bool footer = kind == "footerReference";
                // A header or footer written in one size throughout has that size here: it becomes the size of its style.
                var written = root.Descendants(W + "r").Where(r => !r.Ancestors().Any(a => a.Name.LocalName == "Fallback") && r.Elements(W + "t").Any(x => x.Value.Trim().Length > 0))
                    .Select(r => Num(r.Element(W + "rPr")?.Element(W + "sz"), "val")).Distinct().ToList();
                if (!first && written.Count == 1 && written[0] is { } half && doc.FindPara(footer ? "Footer" : "Header") is { } bandStyle)
                    bandStyle.Char = bandStyle.Char with { Size = Math.Clamp(half, 12, 48) };
                string[] t = { "", "", "" };
                // The lines of the three texts. A paragraph of Word is a line here, in all three places at once.
                var rows = new[] { new List<string>(), new List<string>(), new List<string>() };
                (string Media, long W, long H, int Place)? logo = null;
                if (root.Descendants(W + "pict").Any(p => !p.Ancestors().Any(a => a.Name.LocalName == "Fallback") && p.Descendants().Any(e => e.Name.LocalName == "imagedata")))
                    Count("pictures of an older kind in the header or footer");
                foreach (var p in root.Descendants(W + "p"))
                {
                    // What Word keeps a second time for older programs is not read twice.
                    if (p.Ancestors().Any(a => a.Name.LocalName == "Fallback")) continue;
                    // In a table, the first cell of a row is the left place, the last the right one, those between the middle.
                    int cell = -1;
                    if (p.Ancestors(W + "tc").FirstOrDefault() is { Parent: { } row } tc)
                    {
                        var cells = row.Elements(W + "tc").ToList();
                        int at = cells.IndexOf(tc);
                        if (cells.Count > 1) cell = at == 0 ? 0 : at == cells.Count - 1 ? 2 : 1;
                    }
                    var k = LookOf(Val(p.Element(W + "pPr")?.Element(W + "pStyle")), true);
                    ReadPara(p.Element(W + "pPr"), k);
                    int byAlign = k.Align == Align.Center ? 1 : k.Align == Align.Right ? 2 : 0;

                    int tabs = 0;
                    foreach (var e in p.Descendants())
                    {
                        if (e.Ancestors(W + "p").First() != p) continue;
                        if (e.Name == W + "tab" && e.Parent?.Name != W + "tabs") tabs++;
                        if (e.Name != W + "drawing") continue;
                        var anchor = e.Element(WP + "anchor");
                        var frame = e.Element(WP + "inline") ?? anchor;
                        if (frame == null || frame.Descendants(W + "txbxContent").Any()) continue;
                        if (frame.Descendants(A + "blip").FirstOrDefault() is not { } blip)
                        {
                            Count("shapes and diagrams in the header or footer");
                            continue;
                        }
                        if ((string?)blip.Attribute(R + "embed") is not { } pid || !own.TryGetValue(pid, out var prel) || prel.Item2 || Bytes(prel.Item1) is not { } data)
                        {
                            Count("pictures that are not inside the file");
                            continue;
                        }
                        var img = PageRenderer.Decode(data, out bool jpeg);
                        if (img == null && Raster(data) is { } png)
                        {
                            data = png;
                            img = PageRenderer.Decode(data, out jpeg);
                        }
                        if (img == null)
                        {
                            Count("pictures in a form that cannot be shown here (such as EMF or WMF)");
                            continue;
                        }
                        var extent = frame.Element(WP + "extent");
                        long w = long.TryParse((string?)extent?.Attribute("cx"), out long cx) && cx > 0 ? cx : img.PixelWidth * 9525L;
                        long h = long.TryParse((string?)extent?.Attribute("cy"), out long cy) && cy > 0 ? cy : img.PixelHeight * 9525L;
                        // A picture that fills much of the page, such as a printed letterhead or a mark behind the text, is no logo.
                        if (h > doc.Page.Height / 3)
                        {
                            Count("pictures of the header or footer that fill much of the page");
                            continue;
                        }
                        if (logo != null)
                        {
                            Count("further pictures in the header or footer");
                            continue;
                        }
                        int place = cell >= 0 ? cell : tabs >= 2 ? 2 : tabs == 1 ? 1 : byAlign;
                        if (anchor?.Element(WP + "positionH") is { } where)
                        {
                            string to = (string?)where.Element(WP + "align") ?? "";
                            if (to is "left" or "inside") place = 0;
                            else if (to == "center") place = 1;
                            else if (to is "right" or "outside") place = 2;
                            else if (long.TryParse((string?)where.Element(WP + "posOffset"), out long off) && cell < 0)
                            {
                                if ((string?)where.Attribute("relativeFrom") is not ("page" or "leftMargin")) off += doc.Page.Left;
                                long middle = off + w / 2;
                                place = middle > doc.Page.Width * 2L / 3 ? 2 : middle > doc.Page.Width / 3 ? 1 : 0;
                            }
                        }
                        string media = RksFile.MediaName(data, jpeg);
                        doc.Media[media] = data;
                        logo = (media, w, h, place);
                    }

                    foreach (string piece in BandLine(p).Split('\n'))
                    {
                        if (piece.Trim().Length == 0) continue;
                        int atRow = cell < 0 ? rows.Max(r => r.Count) : 0;
                        void Put(int place, string what)
                        {
                            what = what.Trim();
                            if (what.Length == 0) return;
                            while (rows[place].Count < atRow) rows[place].Add("");
                            rows[place].Add(what);
                        }
                        string[] f = piece.Split('\t');
                        if (cell >= 0) Put(cell, string.Join(" ", f));
                        else if (f.Length >= 3) { Put(0, f[0]); Put(1, f[1]); Put(2, string.Join(" ", f.Skip(2))); }
                        else if (f.Length == 2) { Put(0, f[0]); Put(2, f[1]); }
                        else Put(byAlign, piece);
                    }
                }

                var span = sizes.BandSpan(footer);
                int pitch = span.Asc + span.Desc;
                long room = doc.Page.ContentWidth, apart = 2L * LayoutEngine.BandGap;
                const string dots = "\u2026";
                long WideLine(string line) => line.Length == 0 ? 0 : ChartLayout.TextWidth(span.Font.Face, line.Replace("{pages}", "88").Replace("{page}", "88"), span.SizeEmu);
                long Wide(string text) => Band.LinesOf(text).Select(WideLine).DefaultIfEmpty(0).Max();
                int Rows(string[] a) => new Band(a[0], a[1], a[2]).Rows;
                // No more lines than leave the margin at a sixth of the page.
                int mostRows = (int)Math.Clamp((doc.Page.Height / 6 - LayoutEngine.BandDistance - LayoutEngine.BandGap) / Math.Max(1, pitch), 1, RksFile.MaxBandLines);
                // A line too long for its room goes on, from a space, on a line of its own.
                string Wrap(string text, long most)
                {
                    var made = new List<string>();
                    foreach (string line in Band.LinesOf(text))
                    {
                        string rest = line;
                        while (WideLine(rest) > most)
                        {
                            int cut = -1;
                            for (int at = rest.IndexOf(' '); at > 0; at = rest.IndexOf(' ', at + 1))
                            {
                                if (WideLine(rest.Substring(0, at)) > most) break;
                                cut = at;
                            }
                            if (cut <= 0) break;
                            made.Add(rest.Substring(0, cut).TrimEnd());
                            rest = rest.Substring(cut + 1).TrimStart();
                        }
                        made.Add(rest);
                    }
                    return string.Join("\n", made);
                }
                string Short(string line, long most)
                {
                    while (line.Length > 1 && WideLine(line + dots) > most)
                    {
                        int cut = line.LastIndexOf(' ');
                        line = cut > 0 ? line.Substring(0, cut).TrimEnd() : line.Substring(0, line.Length - 1);
                    }
                    return line + dots;
                }
                static string Stack(params string[] texts) => string.Join("\n", texts.Select(x => x.Trim('\n')).Where(x => x.Length > 0));
                // The logo gets the size that fits into the margin; where it stood in Word decides only its place.
                BandPicture? Sized(string[] a, int place)
                {
                    if (logo is not { } found) return null;
                    bool shares = Enumerable.Range(0, 3).Any(i => i != place && a[i].Length > 0);
                    var (fw, fh) = sizes.FitBandPicture(footer, found.W, found.H, shares, Rows(a));
                    return new BandPicture(found.Media, fw, fh, place);
                }
                // Whether, line by line, the three texts and the picture keep clear of each other.
                bool Fits(string[] a, BandPicture? pic)
                {
                    var lines = a.Select(Band.LinesOf).ToArray();
                    var widest = a.Select(Wide).ToArray();
                    int n = Rows(a);
                    for (int j = 0; j < n; j++)
                    {
                        var from = new long[3];
                        var to = new long[3];
                        var has = new bool[3];
                        for (int i = 0; i < 3; i++)
                        {
                            long w = j < lines[i].Length ? WideLine(lines[i][j]) : 0;
                            long g = pic != null && pic.Place == i ? pic.Width + (widest[i] > 0 ? LayoutEngine.BandGap + widest[i] : 0) : w;
                            if (g > room) return false;
                            has[i] = g > 0;
                            from[i] = i == 0 ? 0 : i == 1 ? (room - g) / 2 : room - g;
                            to[i] = from[i] + g;
                        }
                        if (has[0] && has[1] && to[0] + apart > from[1]) return false;
                        if (has[1] && has[2] && to[1] + apart > from[2]) return false;
                        if (has[0] && has[2] && to[0] + apart > from[2]) return false;
                    }
                    return true;
                }

                for (int i = 0; i < 3; i++) t[i] = Wrap(RksFile.BandLines(string.Join("\n", rows[i]), true), room);
                string[] chosen = t;
                BandPicture? picture = null;
                bool moved = false, cutShort = false, grown = false;
                // Word makes room for a header or footer that reaches into the text. Here the margin is made large enough for its
                // lines, and for a logo of some size; with a larger margin the logo can be larger, so this is gone through again.
                for (int pass = 0; pass < 3; pass++)
                {
                    // Texts that would run into each other, or into the picture, are put one under the other.
                    var tries = new[] { t, new[] { Stack(t[0], t[1]), "", t[2] }, new[] { t[0], "", Stack(t[1], t[2]) }, new[] { Stack(t[0], t[1], t[2]), "", "" } };
                    int[] places = logo is { } stood ? new[] { stood.Place, 2, 0, 1 } : new[] { 0 };
                    bool found = false;
                    moved = cutShort = false;
                    foreach (var a in tries)
                    {
                        if (Rows(a) > mostRows) continue;
                        foreach (int place in places)
                            if (Fits(a, Sized(a, place)))
                            {
                                (chosen, picture, found, moved) = (a, Sized(a, place), true, a != t);
                                break;
                            }
                        if (found) break;
                    }
                    if (!found)
                    {
                        // Everything at the left in lines that fit beside the picture, the picture at the right; lines too many are left out.
                        string all = tries[3][0];
                        long most = room - (Sized(new[] { all, "", "" }, 2) is { } beside ? beside.Width + apart : 0);
                        var kept = Band.LinesOf(Wrap(all, most)).ToList();
                        for (int q = 0; q < kept.Count; q++)
                            if (WideLine(kept[q]) > most) (kept[q], cutShort) = (Short(kept[q], most), true);
                        if (kept.Count > mostRows)
                        {
                            kept = kept.Take(mostRows).ToList();
                            if (!kept[^1].EndsWith(dots)) kept[^1] = Short(kept[^1], most);
                            cutShort = true;
                        }
                        chosen = new[] { string.Join("\n", kept), "", "" };
                        picture = Sized(chosen, 2);
                        moved = true;
                    }
                    if (picture == null && chosen.All(x => x.Length == 0)) break;
                    int need = Math.Min(doc.Page.Height / 6, Math.Max(picture != null ? 822960 : 0, LayoutEngine.BandDistance + Rows(chosen) * pitch + LayoutEngine.BandGap));
                    // The text keeps clear of header and footer by the gap, also where Word lets it end nearer to them.
                    bool grew = false;
                    if (footer && doc.Page.Bottom < need) (doc.Page.Bottom, grew) = (need, true);
                    if (!footer && doc.Page.Top < need) (doc.Page.Top, grew) = (need, true);
                    grown |= grew;
                    if (!grew) break;
                }
                if (grown) widened++;
                if (cutShort) shortened++;
                else if (moved) arranged++;
                if (picture != null) logos++;
                return new Band(chosen[0], chosen[1], chosen[2], picture);
            }
            static string Add(string have, string more) => more.Trim().Length == 0 ? have : have.Length == 0 ? more.Trim() : have + "  " + more.Trim();
            doc.Header = One("headerReference");
            doc.Footer = One("footerReference");
            // Where Word gives the first page a header and footer of its own, they are taken as those of the first page here.
            if (OnOff(section.Element(W + "titlePg")) == true)
            {
                doc.FirstHeader = One("headerReference", true);
                doc.FirstFooter = One("footerReference", true);
                doc.BandsSkipFirst = !(doc.Header.IsEmpty && doc.Footer.IsEmpty && doc.FirstHeader.IsEmpty && doc.FirstFooter.IsEmpty);
                if (!doc.BandsSkipFirst) doc.FirstHeader = doc.FirstFooter = Band.Empty;
            }
        }

        /// <summary>The text of a paragraph of a header or footer, with the page number where Word has a field for it.</summary>
        static string BandLine(XElement p)
        {
            var sb = new StringBuilder();
            var instr = new StringBuilder();
            int state = 0;   // 0 text, 1 inside the instruction of a field, 2 inside its shown result
            static string Token(string i) => i.TrimStart().StartsWith("NUMPAGES", StringComparison.OrdinalIgnoreCase) || i.TrimStart().StartsWith("SECTIONPAGES", StringComparison.OrdinalIgnoreCase) ? "{pages}"
                : i.TrimStart().StartsWith("PAGE", StringComparison.OrdinalIgnoreCase) ? "{page}" : "";
            foreach (var e in p.Descendants())
            {
                if (e.Name == W + "fldSimple") { string t = Token((string?)e.Attribute(W + "instr") ?? ""); if (t.Length > 0) { sb.Append(t); state = 3; } }
                else if (e.Name == W + "fldChar")
                {
                    string type = (string?)e.Attribute(W + "fldCharType") ?? "";
                    if (type == "begin") { state = 1; instr.Clear(); }
                    else if (type == "separate") { string t = Token(instr.ToString()); sb.Append(t); state = t.Length > 0 ? 2 : 0; }
                    else if (type == "end") { if (state == 1) sb.Append(Token(instr.ToString())); state = 0; }
                }
                else if (e.Ancestors(W + "p").First() != p) { }
                else if (e.Name == W + "instrText") instr.Append(e.Value);
                else if (e.Name == W + "t" && state == 0) sb.Append(e.Value);
                else if (e.Name == W + "t" && state == 3 && e.Ancestors(W + "fldSimple").Any()) { }
                else if (e.Name == W + "t" && state == 3) { state = 0; sb.Append(e.Value); }
                else if (e.Name == W + "tab" && state != 1 && e.Parent?.Name != W + "tabs") sb.Append('\t');
                else if ((e.Name == W + "br" || e.Name == W + "cr") && state != 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        // ---- what the reader is told ----

        List<string> Notes()
        {
            var notes = new List<string>
            {
                "This Word document was laid out anew: its text, formats, lists, tables and pictures are here, but lines and pages do not break where Word broke them. "
                + "Save it to keep it as a Reiks document. The Word file stays as it is.",
            };
            // A document in another language than the one that is checked would be full of marked words.
            string checking = Spell.Language;
            if (language.Length >= 2 && checking.Length >= 2 && !language.StartsWith(checking.Substring(0, 2), StringComparison.OrdinalIgnoreCase))
            {
                static string Named(string tag)
                {
                    try { return CultureInfo.GetCultureInfo(tag).EnglishName; }
                    catch (CultureNotFoundException) { return tag; }
                }
                notes.Add("The document says it is written in " + Named(language) + ", and spelling is checked in " + Named(checking)
                    + ", so many words will be marked. The language is chosen at the foot of the window, on the spelling button.");
            }
            if (floats > 0) notes.Add("Pictures that stood freely in Word (" + floats + ") were placed where they read well: beside the text, or on a line of their own.");
            if (boxCount > 0)
                notes.Add("Text boxes (" + boxCount + ") stand behind the paragraph they belonged to"
                    + (boxFramed == boxCount ? ", each as a box of its own." : boxFramed > 0 ? ": " + boxFramed + " with a line around them or a background as boxes of their own, the others as ordinary text." : ", as ordinary text; none had a line around it or a background."));
            if (fromLatex > 0) notes.Add("Mathematics that stood in the text as LaTeX was made into equations (" + fromLatex + ").");
            if (eqSimple > 0) notes.Add("Equations with parts this program does not have (" + eqSimple + "), such as matrices or accents, were written more simply.");
            if (tocAt >= 0) notes.Add("The table of contents was written again by this program.");
            if (logos > 0) notes.Add("The picture of the header or footer (a logo) stands in its line at the left, in the middle or at the right, as it did in Word, at a size that fits into the margin. The Header box changes its place and height.");
            if (oldPictures > 0) notes.Add("Embedded objects and older drawings (" + oldPictures + ") are shown as the pictures that stood for them in Word; they cannot be opened or changed here.");
            if (widened > 0) notes.Add("The margin at the top or bottom of the page was made larger, so that the header or footer does not stand in the text.");
            if (arranged > 0) notes.Add("Texts of a header or footer that would have run into each other were put one under the other.");
            if (shortened > 0) notes.Add("A header or footer too long for its place was shortened; where it was cut it ends with \u2026 The Header box shows what is left of it.");
            if (shrunk > 0) notes.Add("In " + shrunk + (shrunk == 1 ? " table" : " tables") + " too wide for the page the text was made smaller, so that its words fit into the columns.");
            if (refitted > 0) notes.Add("In " + refitted + (refitted == 1 ? " table" : " tables") + " the widths of the columns were shared out again, so that no word is cut in a narrow column.");
            if (blocks > 0) notes.Add("Numbered tasks (" + blocks + ") are kept together: one of which less than half would fit at the bottom of a page starts on the next page. "
                + "The Paragraph box switches this on and off for a paragraph.");
            if (charts > 0) notes.Add("Charts (" + charts + ") were drawn again by this program from their numbers; their colours and details are those of this program."
                + (chartsSimple > 0 ? " Of these, " + chartsSimple + " of a kind this program does not have (such as stacked or lying bars, areas, points or rings) became plain bars, lines or a pie." : ""));
            foreach (var (what, count) in left)
                notes.Add(what switch
                {
                    "columns" => "Text in columns runs in one column here.",
                    "comments" => "Comments were left out.",
                    "tracked changes" => "Tracked changes were taken as accepted; what they had deleted is not here.",
                    "tables inside" => "Tables inside table cells, text boxes or footnotes (" + count + ") became lines of text.",
                    _ => "Left out: " + what + " (" + count + ").",
                });
            return notes;
        }
    }
}
