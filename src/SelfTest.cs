using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ReiksDocument;

/// <summary>ReiksDocument.exe --selftest outdir: lays out sample documents and compares every position.</summary>
static class SelfTest
{
    sealed class Rng
    {
        ulong s;
        public Rng(ulong seed) { s = seed; }
        public int Next(int n)
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            return (int)((s >> 33) % (ulong)n);
        }
    }

    static readonly string[] Words =
    {
        "the", "page", "line", "of", "and", "a", "layout", "is", "stored", "with", "every", "position", "exact", "font", "paragraph",
        "word", "breaks", "where", "it", "must", "número", "página", "línea", "año", "canción", "well-known", "state-of-the-art",
        "typography", "measure", "baseline", "in", "to", "for", "on", "document", "writer", "reader", "simple", "private", "local",
        "x", "I", "2026", "3.14", "(note)", "“quoted”", "end.", "Then,", "so", "that", "nothing", "drifts",
    };

    static readonly string[] Fonts = { "Calibri", "Times New Roman", "Arial", "Consolas", "Georgia", "Segoe UI", "Cambria", "Calibri Light" };

    static readonly List<string> log = new();
    static int failures;

    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) failures++;
        log.Add((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  -- " + detail : ""));
    }

    static string Text(Rng r, int words)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < words; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Words[r.Next(Words.Length)]);
        }
        return sb.ToString();
    }

    static Paragraph Para(Document d, string style, Align? align, params Run[] runs)
    {
        var p = new Paragraph { Style = style, Align = align };
        p.Runs.AddRange(runs.Where(x => x.Text.Length > 0));
        d.Body.Add(p);
        return p;
    }

    static Document Basic()
    {
        var r = new Rng(7);
        var d = Document.NewDefault();
        d.Body.Clear();
        Para(d, "Title", null, new Run("Reiks Document sample"));
        Para(d, "Heading1", null, new Run("Plain text"));
        Para(d, "Normal", null, new Run(Text(r, 90)));
        Para(d, "Normal", Align.Justify, new Run(Text(r, 50) + " "), new Run("bold words", new(Bold: true)), new Run(" " + Text(r, 20) + " "),
            new Run(Text(r, 12), new(Italic: true)), new Run(" "), new Run(Text(r, 10), new(Underline: true)), new Run(" " + Text(r, 40)));
        Para(d, "Heading2", null, new Run("Fonts and sizes"));
        var mixed = new List<Run>();
        int[] sizes = { 16, 18, 20, 22, 24, 28, 36, 48 };
        for (int i = 0; i < 16; i++)
            mixed.Add(new Run(Text(r, 6) + " ", new(Font: Fonts[i % Fonts.Length], Size: sizes[r.Next(sizes.Length)], Bold: i % 3 == 0, Italic: i % 4 == 1)));
        Para(d, "Normal", null, mixed.ToArray());
        Para(d, "Normal", Align.Center, new Run(Text(r, 30)));
        Para(d, "Normal", Align.Right, new Run(Text(r, 30)));
        Para(d, "Heading2", null, new Run("Tabs, line breaks and long words"));
        Para(d, "Normal", null, new Run("Name\tValue\tUnit" + Document.LineBreak + "Width\t210\tmm" + Document.LineBreak + "Height\t297\tmm" + Document.LineBreak));
        Para(d, "Normal", Align.Justify, new Run(Text(r, 12) + " " + new string('m', 150) + " " + Text(r, 12)));
        Para(d, "Normal", null);
        Para(d, "Normal", null).Mark = new Run("", new(Size: 72));
        Para(d, "Normal", null, new Run("Outside the basic plane: \U0001D465 + \U0001D466, and spaces at the end of a line          " + Text(r, 30)));
        Para(d, "Normal", null, new Run("Character styles: "), new Run("emphasis", default, "Emphasis"), new Run(", "), new Run("strong", default, "Strong"),
            new Run(" and "), new Run("code()", default, "Code"), new Run("."));
        Para(d, "Heading1", null, new Run("A new page")).PageBreakBefore = true;
        for (int i = 0; i < 14; i++)
        {
            if (i % 5 == 2) Para(d, "Heading3", null, new Run(Text(r, 4)));
            Para(d, "Normal", (Align)(i % 4), new Run(Text(r, 40 + r.Next(80))));
        }
        return d;
    }

    static byte[] MakePicture(int w, int h, bool jpeg)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2B, 0x6C, 0xC4), Color.FromRgb(0xF2, 0xC1, 0x4E), 35), null, new Rect(0, 0, w, h));
            dc.DrawEllipse(Brushes.White, null, new Point(w / 2.0, h / 2.0), w / 4.0, h / 4.0);
            dc.DrawRectangle(null, new Pen(Brushes.Black, 2), new Rect(1, 1, w - 2, h - 2));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        BitmapEncoder enc = jpeg ? new JpegBitmapEncoder { QualityLevel = 85 } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static Run Pic(Document d, byte[] data, bool jpeg, int wPx, int hPx, bool block)
    {
        string name = RksFile.MediaName(data, jpeg);
        d.Media[name] = data;
        return ReiksDocument.Run.Object(new Picture(name, wPx * 9525, hPx * 9525, block), new Run(""));
    }

    static Run NoteRun(Document d, Rng r, int paragraphs, int words)
    {
        var n = d.NewNote();
        n.Paras.Clear();
        for (int k = 0; k < paragraphs; k++)
        {
            var p = new Paragraph { Style = "Footnote" };
            p.Runs.Add(new Run(Text(r, 3 + r.Next(words))));
            if (k == 0 && r.Next(3) == 0) p.Runs.Add(new Run(" " + Text(r, 4), new(Italic: true)));
            n.Paras.Add(p);
        }
        return ReiksDocument.Run.Object(new NoteRef(n.Id), new Run(""));
    }

    static Document Notes()
    {
        var r = new Rng(31);
        var d = Document.NewDefault();
        d.Body.Clear();
        byte[] png = MakePicture(320, 200, false), jpg = MakePicture(640, 360, true), icon = MakePicture(40, 40, false);
        Para(d, "Title", null, new Run("Pictures and footnotes"));
        for (int i = 0; i < 70; i++)
        {
            if (i % 10 == 0) Para(d, "Heading1", null, new Run(Text(r, 3)), NoteRun(d, r, 1, 12));
            var runs = new List<Run>();
            int parts = 1 + r.Next(3);
            for (int k = 0; k < parts; k++)
            {
                runs.Add(new Run(Text(r, 8 + r.Next(40))));
                if (r.Next(3) != 0) runs.Add(NoteRun(d, r, 1 + r.Next(2), i % 11 == 5 ? 220 : 40));
                runs.Add(new Run(" "));
                if (i % 7 == 3 && k == 0) { runs.Add(Pic(d, icon, false, 40, 40, false)); runs.Add(new Run(" ")); }
            }
            if (i % 13 == 6) { runs.Add(Pic(d, jpg, true, 400, 225, true)); runs.Add(new Run(Text(r, 30))); }
            Para(d, "Normal", i % 4 == 1 ? Align.Justify : null, runs.ToArray());
            if (i % 9 == 4) Para(d, "Normal", Align.Center, Pic(d, png, false, 320, 200, true));
            if (i % 16 == 8) Para(d, "Normal", null, new Run(Text(r, 6) + " "), Pic(d, png, false, 160, 100, false), new Run(" " + Text(r, 20)), NoteRun(d, r, 1, 20));
        }
        return d;
    }

    static readonly string[] Equations =
    {
        "x^2", "a/b", "(a+b)/(c+d)", "\\sqrt(x+1)", "\\alpha + \\beta \\le \\gamma", "e^(-x^2)", "x_i^2 + y_i^2 = r^2",
        "\\sum_(i=1)^n i^2 = n(n+1)(2n+1)/6", "\\int_0^\\infty e^(-x^2) dx = \\sqrt(\\pi)/2", "x = (-b \\pm \\sqrt(b^2 - 4ac))/(2a)",
        "sin^2 x + cos^2 x = 1", "lim_(n\\to\\infty) (1 + 1/n)^n = e", "f(x) = a_0 + a_1 x + a_2 x^2", "((a/b)/(c/d))^2",
        "\\prod_(k=1)^n k = n!", "[x, y] \\in {1, 2, 3}", "1/2 + 1/3 = 5/6", "\\partial f/\\partial x", "E = mc^2", "a/b/c",
        "x^2^3", "(a+b", "\\sqrt 2", "-x + -y", "\\unknown + 3.14", "\\sqrt(1 + \\sqrt(1 + x))", "x_(i,j)^(n+1)",
    };

    static Chart[] ChartSamples() => new[]
    {
        Chart.Default(),
        new Chart("bar", "Sales by quarter", new[] { "Q1", "Q2", "Q3", "Q4" }, new[]
        {
            new Series("North", new long[] { 12500, 14250, 9800, 16000 }),
            new Series("South", new long[] { 8000, -2500, 11000, 7250 }),
            new Series("West", new long[] { 3000, 4500, 5250, 6125 }),
        }, 5400000, 3000000, true),
        new Chart("line", "Temperature", new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }, new[]
        {
            new Series("Day", new long[] { 18500, 21000, 19250, 23750, 22000, 17500, 16000 }),
            new Series("Night", new long[] { 9000, 11500, 8250, 12000, 10750, 7000, -1500 }),
        }, 5400000, 2880000, true),
        new Chart("pie", "Shares", new[] { "Rent", "Food", "Travel", "Books", "Other" }, new[]
        {
            new Series("Share", new long[] { 42000, 23500, 12000, 8250, 14250 }),
        }, 3600000, 3000000, false),
        new Chart("bar", "", new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l" }, new[]
        {
            new Series("", new long[] { 1, 20, 300, 4000, 50000, 600000, 50000, 4000, 300, 20, 1, 0 }),
        }, 5760000, 2160000, false),
        new Chart("line", "One point", new[] { "only" }, new[] { new Series("s", new long[] { 2500 }) }, 2160000, 1800000, false),
        new Chart("pie", "", new[] { "all" }, new[] { new Series("s", new long[] { 7000 }) }, 1800000, 1800000, false),
        new Chart("bar", "Empty", Array.Empty<string>(), Array.Empty<Series>(), 2160000, 1440000, false),
        Plot.Make(Chart.Default() with { Title = "sin(x)/x", Width = 5040000, Height = 2700000, Block = true }, "sin(x)/x", -15000, 15000),
        Plot.Make(Chart.Default() with { Width = 4320000, Height = 2520000 }, "x^2 - 3x + 1; 2 cos x; \\sqrt(x)", -3000, 5000),
        Plot.Make(Chart.Default() with { Title = "1/x" }, "1/x", -2000, 2000),
    };

    static Run Obj(Inline o, int? size = null) => ReiksDocument.Run.Object(o, new Run("", new(Size: size)));

    static Document MathDoc()
    {
        var r = new Rng(5);
        var d = Document.NewDefault();
        d.Body.Clear();
        Para(d, "Title", null, new Run("Equations"));
        for (int i = 0; i < Equations.Length; i++)
        {
            int? size = i % 5 == 3 ? 32 : i % 7 == 6 ? 18 : null;
            Para(d, "Normal", i % 3 == 0 ? Align.Justify : null, new Run(Text(r, 6 + r.Next(20)) + " "), Obj(Equation.From(Equations[i], false), size),
                new Run(" " + Text(r, 4 + r.Next(30)) + " "), Obj(Equation.From(Equations[(i * 7 + 3) % Equations.Length], false)), new Run(" " + Text(r, 5)));
            Para(d, "Normal", Align.Center, Obj(Equation.From(Equations[i], true), size));
        }
        return d;
    }

    static Document ChartDoc()
    {
        var r = new Rng(6);
        var d = Document.NewDefault();
        d.Body.Clear();
        Para(d, "Title", null, new Run("Charts"));
        var charts = ChartSamples();
        for (int i = 0; i < charts.Length; i++)
        {
            Para(d, "Heading2", null, new Run(Text(r, 3)));
            if (charts[i].Block) Para(d, "Normal", Align.Center, Obj(charts[i]));
            else Para(d, "Normal", null, new Run(Text(r, 12) + " "), Obj(charts[i], i % 2 == 0 ? 18 : null), new Run(" " + Text(r, 25)));
            Para(d, "Normal", Align.Justify, new Run(Text(r, 60)));
        }
        Para(d, "Normal", null, new Run("In Times New Roman: ", new(Font: "Times New Roman")), ReiksDocument.Run.Object(charts[1] with { Block = false, Width = 4000000 }, new Run("", new(Font: "Times New Roman", Size: 28))));
        return d;
    }

    /// <summary>The structure of an equation in a few letters, to check the typing rules.</summary>
    static string Shape(MathNode n) => n switch
    {
        MRow r => string.Concat(r.Items.Select(Shape)),
        MSym s => s.Class == 'o' ? "." : s.Class.ToString(),
        MText => "fn",
        MFrac f => "F(" + Shape(f.Num) + "," + Shape(f.Den) + ")",
        MSqrt q => "Q(" + Shape(q.Body) + ")",
        MScript sc => "S(" + Shape(sc.Base) + "," + (sc.Sub == null ? "-" : Shape(sc.Sub)) + "," + (sc.Sup == null ? "-" : Shape(sc.Sup)) + ")",
        MDelim dl => "D(" + Shape(dl.Body) + ")",
        _ => "?",
    };

    static void Typing()
    {
        (string Source, string Shape)[] cases =
        {
            ("x^2", "S(.,-,.)"), ("a/b", "F(.,.)"), ("(a+b)/(c+d)", "F(.b.,.b.)"), ("x_i^2", "S(.,.,.)"),
            ("\\sum_(i=1)^n i^2", "S(O,.r.,.)S(.,-,.)"), ("\\sqrt(x+1)", "Q(.b.)"), ("2x/3y", "F(..,..)"), ("(a+b)c", "D(.b.)."),
            ("e^-x", "S(.,-,..)"), ("sin x", "fn."), ("a + b/c", ".bF(.,.)"), ("x^2y z", "S(.,-,..)."),
            ("\\int_0^\\infty e^(-x^2) dx", "S(O,.,.)S(.,-,bS(.,-,.)).."), ("a/b/c", "F(F(.,.),.)"), ("\\alpha\\le\\beta", ".r."),
            ("1 / 2", "F(.,.)"), ("\\Delta x/\\Delta t", "F(..,..)"), ("x^\\alpha y", "S(.,-,.)."), ("12.5x", "....."), ("", ""),
        };
        var wrong = cases.Where(c => Shape(MathParser.Parse(c.Source)) != c.Shape).Select(c => c.Source + " gives " + Shape(MathParser.Parse(c.Source)) + ", expected " + c.Shape).ToList();
        Check("typing: " + cases.Length + " typed equations give the expected structure", wrong.Count == 0, string.Join("; ", wrong));
        Check("typing: Cambria Math is installed", !MathLayout.Font.Substituted && MathLayout.Font.Face.Info.Family == MathLayout.Family, MathLayout.Font.Face.Info.Family);
        var box = MathLayout.Layout(MathParser.Parse("a/b"), 22 * U.PerHalfPoint, true);
        Check("typing: a fraction has a bar and reaches above and below the baseline", box.H > 0 && box.D > 0 && box.Prims.Count(p => p.Kind == PrimKind.Rect) == 1 && box.Prims.Count(p => p.Kind == PrimKind.Glyph) == 2, $"h {box.H} d {box.D}");
        Check("typing: chart numbers are written without loss", ChartLayout.Fmt(-12500) == ((char)0x2212) + "12.5" && ChartLayout.Fmt(3000) == "3" && RksFile.Thousandths(0.001m) == 1 && ChartLayout.Fmt(1, true) == "0.001");
    }

    static Document FormatDoc()
    {
        var r = new Rng(8);
        var d = Document.NewDefault();
        d.Body.Clear();
        Para(d, "Title", null, new Run("Formats"));
        Para(d, "Normal", Align.Justify, new Run(Text(r, 20) + " "), new Run(Text(r, 6), new(Strike: true)), new Run(" x"), new Run("2", new(Vert: 1)),
            new Run(" + H"), new Run("2", new(Vert: 2)), new Run("O " + Text(r, 10) + " "), new Run(Text(r, 8), new(Highlight: 0xFFFF00)), new Run(" "),
            new Run(Text(r, 30), new(Vert: 1, Color: 0xC00000)), new Run(" "), new Run(Text(r, 30), new(Vert: 2, Size: 40, Underline: true, Strike: true)),
            new Run(" " + Text(r, 20)), NoteRun(d, r, 1, 10), new Run(" ", new(Vert: 1)), Obj(Equation.From("x^2", false), 22), new Run(" end"));
        int[] lines = { 1000, 1150, 1500, 2000, 3000 };
        for (int i = 0; i < 12; i++)
        {
            var p = Para(d, "Normal", (Align)(i % 4), new Run(Text(r, 30 + r.Next(60))), new Run(" " + Text(r, 5), new(Vert: 1 + i % 2, Highlight: 0xBDD7EE)));
            p.Line = lines[i % 5];
            if (i % 3 != 0) p.IndentLeft = 457200 * (1 + i % 4);
        }
        return d;
    }

    /// <summary>Finding, replacing, copying and erasing formats, counting words.</summary>
    static void Formulas()
    {
        (string Source, double X, double Value)[] cases =
        {
            ("x^2 - 3x + 1", 2, -1), ("sin(x)/x", Math.PI / 2, 2 / Math.PI), ("2 cos x", 0, 2), ("e^(-x^2)", 0, 1), ("\\sqrt(x)", 4, 2),
            ("abs(x)", -3, 3), ("1/2 x", 4, 2), ("-x^2", 3, -9), ("2x^2", 3, 18), ("(x+1)(x-1)", 3, 8), ("sin^2 x + cos^2 x", 1.3, 1),
            ("ln x", Math.E, 1), ("log x", 1000, 3), ("12.5x", 2, 25), ("x*x", 5, 25), ("\\pi x", 2, 2 * Math.PI), ("x/(x+1)", 1, 0.5),
        };
        var wrong = cases.Where(c => !(Math.Abs(Plot.Eval(MathParser.Parse(c.Source), c.X) - c.Value) < 1e-9))
            .Select(c => c.Source + " at " + c.X + " gives " + Plot.Eval(MathParser.Parse(c.Source), c.X)).ToList();
        Check("graphs: " + cases.Length + " formulas are worked out correctly", wrong.Count == 0, string.Join("; ", wrong));
        var graph = Plot.Make(Chart.Default(), "1/x; \\sqrt(x)", -2000, 2000);
        Check("graphs: places without a value are left out, the rest is kept as whole thousandths",
            graph.Series.Count == 2 && graph.Series[0].Values.Count == Plot.Steps + 1 && graph.Series[0].Values[Plot.Steps / 2] == ChartLayout.Missing
            && graph.Series[0].Values[Plot.Steps] == 500 && graph.Series[1].Values[0] == ChartLayout.Missing && graph.Series[1].Values[Plot.Steps] == 1414);
    }

    static void Tools()
    {
        var d = Document.NewDefault();
        d.Body.Clear();
        Para(d, "Normal", null, new Run("One fish, two fish, "), new Run("red fish", new(Bold: true, Color: 0xC00000)), new Run(", blue Fish."));
        Para(d, "Normal", Align.Center, new Run("No fish here? Fish!"));
        var eng = new LayoutEngine(d);
        eng.Update();
        var ed = new Editor(d, eng);
        Check("tools: words are counted", ed.WordCount() == 12, ed.WordCount().ToString());

        int found = 0;
        Pos? first = null;
        while (found < 20 && ed.Find("fish", true))
        {
            if (first == ed.SelStart) break;
            first ??= ed.SelStart;
            found++;
        }
        Check("tools: find goes through every place and starts over", found == 4, found.ToString());

        byte[] before = RksFile.ContentJson(d);
        int n = ed.ReplaceAll("fish", "cat", false);
        bool bold = d.Body[0].Runs.Any(x => x.Text == "red cat" && x.Props.Bold == true);
        Check("tools: replace all changes every place and keeps the format", n == 6 && d.Body[1].Text == "No cat here? cat!" && bold, n + " / " + d.Body[1].Text);
        ed.Undo();
        Check("tools: one undo takes replace all back", RksFile.ContentJson(d).AsSpan().SequenceEqual(before));

        ed.MoveTo(new Pos(0, 0), false);
        ed.Find("two", true);
        bool hit = ed.Replace("two", "2", true);
        Check("tools: replace changes the place found", hit && d.Body[0].Text.StartsWith("One fish, 2 fish"), d.Body[0].Text);

        ed.Select(new Pos(0, 18), new Pos(0, 21));
        ed.CopyFormat(false);
        ed.Select(new Pos(1, 0), new Pos(1, 2));
        ed.PasteFormat();
        var took = d.Body[1].Runs[0];
        bool pasted = took.Text == "No" && took.Props.Bold == true && took.Props.Color == 0xC00000;
        ed.ClearFormat();
        bool erased = d.Body[1].Runs.Count == 1 && d.Body[1].Runs[0].Props == default;
        ed.MoveTo(new Pos(1, 3), false);
        ed.SetLine(2000);
        ed.ChangeIndent(457200);
        bool set = d.Body[1].Line == 2000 && d.Body[1].IndentLeft == 457200;
        ed.ClearFormat();
        bool plain = d.Body[1].Align == null && d.Body[1].Line == null && d.Body[1].IndentLeft == null;
        Check("tools: a format is copied onto other text and erased again", pasted && erased, took.Text);
        Check("tools: line spacing and indent are set and erased", set && plain);

        ed.Select(new Pos(1, 0), new Pos(1, 2));
        ed.ToggleCode();
        bool coded = d.Body[1].Runs[0].CharStyle == "Code" && eng.SpansOf(d.Body[1])[0].Fmt.Font == "Consolas";
        ed.ToggleCode();
        Check("tools: the code look is switched on and off", coded && d.Body[1].Runs.All(x => x.CharStyle == null));

        ed.MoveTo(new Pos(0, 0), false);
        ed.TypeText("H");
        ed.TypeVert("2", 2);
        ed.TypeText("O x");
        ed.TypeVert("2", 1);
        ed.TypeText(" ");
        var typed = d.Body[0].Runs;
        Check("tools: a number typed with Ctrl is lowered, with Ctrl+Shift raised, and the text goes on as before",
            d.Body[0].Text.StartsWith("H2O x2 ") && typed.Any(x => x.Text == "2" && x.Props.Vert == 2) && typed.Any(x => x.Text == "2" && x.Props.Vert == 1)
            && typed.Count(x => x.Props.Vert is 1 or 2) == 2, string.Join("|", typed.Take(6).Select(x => x.Text + ":" + x.Props.Vert)));
    }

    static Run Float(Document d, byte[] data, bool jpeg, int wPx, int hPx, string wrap, int dx, int dy)
    {
        string name = RksFile.MediaName(data, jpeg);
        d.Media[name] = data;
        return ReiksDocument.Run.Object(new Picture(name, wPx * 9525, hPx * 9525, false, wrap, dx, dy), new Run(""));
    }

    static Document FloatDoc()
    {
        var r = new Rng(12);
        var d = Document.NewDefault();
        d.Body.Clear();
        byte[] png = MakePicture(240, 160, false), jpg = MakePicture(400, 220, true), tall = MakePicture(160, 420, false), wide = MakePicture(560, 120, false);
        int w = d.Page.ContentWidth, right = w - 240 * 9525;
        Para(d, "Title", null, new Run("Pictures that float"));
        Para(d, "Normal", Align.Justify, Float(d, png, false, 240, 160, "square", 0, 0), new Run(Text(r, 160)));
        Para(d, "Normal", null, new Run(Text(r, 30) + " "), Float(d, png, false, 240, 160, "square", right, 60000), new Run(Text(r, 150)));
        Para(d, "Normal", null, Float(d, jpg, true, 400, 220, "topbottom", 600000, 200000), new Run(Text(r, 120)));
        Para(d, "Normal", Align.Justify, Float(d, png, false, 240, 160, "square", 1500000, 0), new Run(Text(r, 200)));
        Para(d, "Normal", null, Float(d, wide, false, 560, 120, "square", 300000, 100000), new Run(Text(r, 100)));
        Para(d, "Normal", null, Float(d, png, false, 240, 160, "behind", 1200000, -100000), new Run(Text(r, 90)));
        Para(d, "Normal", null, Float(d, png, false, 240, 160, "front", 2600000, 50000), new Run(Text(r, 90)));
        Para(d, "Normal", null, Float(d, tall, false, 160, 420, "square", 0, 0), Float(d, png, false, 240, 160, "square", right, 300000),
            new Run(Text(r, 260)), NoteRun(d, r, 1, 30), new Run(" " + Text(r, 80)));
        Para(d, "Normal", null, Float(d, png, false, 240, 160, "square", right, 0), new Run(Text(r, 70))).List = "bullet";
        for (int i = 0; i < 6; i++)
            Para(d, "Normal", (Align)(i % 4), new Run(Text(r, 80 + r.Next(60))),
                i % 2 == 0 ? Float(d, jpg, true, 400, 220, i % 4 == 0 ? "topbottom" : "square", 400000 * i, 150000 * i) : new Run(" "), new Run(Text(r, 60)));
        // A picture in the line that is wider than the room beside a floating one: its line must go below.
        Para(d, "Normal", null, Float(d, png, false, 240, 160, "square", 0, 0), new Run(Text(r, 12) + " "), Pic(d, wide, false, 400, 90, false), new Run(" " + Text(r, 60)));
        var marked = Para(d, "Normal", null, Float(d, png, false, 240, 160, "square", 0, 0), new Run(Text(r, 50), new(Vert: 1)));
        marked.List = "number";
        marked.IndentLeft = 228600;
        Para(d, "Heading1", null, new Run("A heading with a picture"), Float(d, png, false, 240, 160, "topbottom", 0, 0));
        Para(d, "Normal", null, new Run(Text(r, 100)));
        return d;
    }

    static Document ListDoc()
    {
        var r = new Rng(13);
        var d = Document.NewDefault();
        d.Body.Clear();
        Para(d, "Title", null, new Run("Lists"));
        for (int i = 0; i < 5; i++) Para(d, "Normal", null, new Run(Text(r, 4 + r.Next(40)))).List = "bullet";
        Para(d, "Normal", null, new Run(Text(r, 20)));
        for (int i = 0; i < 13; i++)
            Para(d, "Normal", i % 5 == 0 ? Align.Justify : null, new Run(Text(r, 3 + r.Next(50)), new(Size: i == 11 ? 40 : null))).List = "number";
        Para(d, "Heading2", null, new Run(Text(r, 3)));
        for (int i = 0; i < 4; i++)
        {
            var p = Para(d, "Normal", null, new Run(Text(r, 10 + r.Next(30))));
            p.List = "number";
            if (i >= 2) p.IndentLeft = 457200;
            if (i == 3) p.Line = 2000;
        }
        Para(d, "Normal", null).List = "bullet";
        Para(d, "Normal", Align.Center, new Run(Text(r, 12))).List = "bullet";
        Para(d, "Normal", Align.Right, new Run("Line one" + Document.LineBreak + "line two")).List = "number";
        return d;
    }

    static Document BandDoc()
    {
        var d = Basic();
        d.Header = new Band("Reiks Document", "", "{page} / {pages}");
        d.Footer = new Band("", "Page {page} of {pages}", "13 November 2026");
        d.BandsSkipFirst = true;
        return d;
    }

    /// <summary>The document of BandDoc with a picture in the header (at the right, beside a text) and one in the footer (in the middle, beside a text).</summary>
    static Document LogoDoc()
    {
        var d = BandDoc();
        byte[] logo = MakePicture(64, 48, false);
        string name = RksFile.MediaName(logo, false);
        d.Media[name] = logo;
        d.Header = d.Header with { Picture = new BandPicture(name, 400000, 300000, 2) };
        d.Footer = d.Footer with { Picture = new BandPicture(name, 480000, 360000, 1) };
        return d;
    }

    /// <summary>The document of LogoDoc with a header and footer of its own on the first page: a picture in the middle above, two texts below.</summary>
    static Document FirstDoc()
    {
        var d = LogoDoc();
        d.FirstHeader = new Band("", "", "", d.Header.Picture! with { Place = 1 });
        d.FirstFooter = new Band("Cover", "", "{pages} pages");
        return d;
    }

    /// <summary>The document of FirstDoc with a header of two lines beside its picture and a footer three lines high.</summary>
    static Document LinesDoc()
    {
        var d = FirstDoc();
        d.Header = d.Header with { Left = "Reiks Document\nA second line", Right = "{page} / {pages}\nof this sample" };
        d.Footer = d.Footer with { Left = "Street 1\nTown", Centre = "Page {page} of {pages}\n\nthird line", Right = "13 November 2026" };
        return d;
    }

    /// <summary>Header and footer on the right pages with the right numbers; pages handed to the printing path.</summary>
    static void Bands(string outDir)
    {
        var d = BandDoc();
        var eng = new LayoutEngine(d);
        eng.Update();
        var p2 = eng.Pages[1].Bands;
        var last = eng.Pages[^1].Bands;
        int right = d.Page.Width - d.Page.Right;
        Check("bands: the first page has none, the others have all their texts with their page numbers",
            eng.Pages.Count == 4 && eng.Pages[0].Bands.Count == 0 && p2.Count == 4 && p2[1].Text == "2 / 4" && p2[2].Text == "Page 2 of 4" && last[2].Text == "Page 4 of 4",
            string.Join(" | ", p2.Select(b => b.Text)));
        var sp = eng.BandSpan(false);
        int w = ChartLayout.TextWidth(sp.Font.Face, "2 / 4", sp.SizeEmu);
        Check("bands: left text at the left edge, right text ending at the right edge, header above and footer below the text area",
            p2[0].X == d.Page.Left && p2[1].X + w == right && p2[0].Baseline < d.Page.Top && p2[2].Baseline > d.Page.Height - d.Page.Bottom,
            $"x {p2[0].X} {p2[1].X} baseline {p2[0].Baseline} {p2[2].Baseline}");

        var dl = LogoDoc();
        var el = new LayoutEngine(dl);
        el.Update();
        var lp = el.Pages[1].BandPics;
        var lt = el.Pages[1].Bands;
        var hs = el.BandSpan(false);
        var fs = el.BandSpan(true);
        int fw = ChartLayout.TextWidth(fs.Font.Face, "Page 2 of 4", fs.SizeEmu), group = 480000 + LayoutEngine.BandGap + fw;
        int footTop = dl.Page.Height - LayoutEngine.BandDistance - fs.Asc - fs.Desc;
        Check("bands: a picture stands in the line of the header or footer, beside the text of its place, with its middle on the middle of the line",
            el.Pages[0].BandPics.Count == 0 && lp.Count == 2 && lt.Count == 4 && !lp[0].Footer && lp[1].Footer
            && lp[0].X + 400000 == right && lt[1].X + w + LayoutEngine.BandGap == lp[0].X && lt[0].X == dl.Page.Left
            && lp[0].Y == LayoutEngine.BandDistance + (hs.Asc + hs.Desc - 300000 - 1) / 2
            && lp[1].X == dl.Page.Left + (dl.Page.ContentWidth - group) / 2 && lt[2].X == lp[1].X + 480000 + LayoutEngine.BandGap
            && lp[1].Y == footTop + (fs.Asc + fs.Desc - 360000 - 1) / 2,
            $"header x {lp[0].X} y {lp[0].Y} text {lt[1].X}; footer x {lp[1].X} y {lp[1].Y} text {lt[2].X}; line {hs.Asc + hs.Desc}");
        var (bigW, bigH) = el.FitBandPicture(false, 4000000, 3000000, false);
        int bigY = LayoutEngine.BandDistance + (hs.Asc + hs.Desc - bigH - 1) / 2;
        var (wideW, wideH) = el.FitBandPicture(true, 40000000, 1000000, true);
        string logoFile = Path.Combine(outDir, "band-picture-kept.docu");
        RksFile.Save(dl, el, logoFile);
        var logoOpen = RksFile.Open(logoFile);
        Check("bands: a picture too large is made to fit into the margin, and the pictures come back from the file where they stood",
            bigH > 300000 && bigW * 3L / 4 == bigH && bigY >= LayoutEngine.BandEdge - 1 && bigY + bigH < dl.Page.Top && wideW == dl.Page.ContentWidth / 3 && wideH == wideW / 40
            && logoOpen.Stored && Diff(el, logoOpen.Engine) == null && logoOpen.Engine.Doc.Header.Picture == dl.Header.Picture && logoOpen.Engine.Doc.Media.Count == 1,
            $"fits {bigW} x {bigH} at {bigY}, wide {wideW} x {wideH}, stored {logoOpen.Stored}, diff {Diff(el, logoOpen.Engine)}");

        var df = FirstDoc();
        var ef = new LayoutEngine(df);
        ef.Update();
        string firstFile = Path.Combine(outDir, "band-first-kept.docu");
        RksFile.Save(df, ef, firstFile);
        var firstOpen = RksFile.Open(firstFile);
        var f0 = ef.Pages[0];
        Check("bands: the first page has a header and footer of its own, the other pages keep theirs, and both come back from the file",
            f0.BandPics.Count == 1 && !f0.BandPics[0].Footer && f0.BandPics[0].X == df.Page.Left + (df.Page.ContentWidth - 400000) / 2
            && f0.Bands.Count == 2 && f0.Bands[0].Text == "Cover" && f0.Bands[0].Footer && f0.Bands[1].Text == "4 pages"
            && ef.Pages[1].BandPics.Count == 2 && ef.Pages[1].Bands.Count == 4 && ef.Pages[1].Bands[1].Text == "2 / 4"
            && firstOpen.Stored && Diff(ef, firstOpen.Engine) == null && firstOpen.Engine.Doc.FirstHeader == df.FirstHeader && firstOpen.Engine.Doc.FirstFooter == df.FirstFooter,
            $"first page: {f0.BandPics.Count} pictures, texts {string.Join(" | ", f0.Bands.Select(b => b.Text))}; stored {firstOpen.Stored}, diff {Diff(ef, firstOpen.Engine)}");

        var dn = LinesDoc();
        var en = new LayoutEngine(dn);
        en.Update();
        var nt = en.Pages[1].Bands;
        var np = en.Pages[1].BandPics;
        int hp = hs.Asc + hs.Desc, fp = fs.Asc + fs.Desc, nTop = dn.Page.Height - LayoutEngine.BandDistance - 3 * fp;
        int w2 = ChartLayout.TextWidth(hs.Font.Face, "of this sample", hs.SizeEmu), w1 = ChartLayout.TextWidth(hs.Font.Face, "2 / 4", hs.SizeEmu);
        string linesFile = Path.Combine(outDir, "band-lines-kept.docu");
        RksFile.Save(dn, en, linesFile);
        var linesOpen = RksFile.Open(linesFile);
        Check("bands: a text of several lines stands line under line, the footer grows upwards, and a picture stands at the middle of all the lines",
            nt.Count == 9 && nt[0].Text == "Reiks Document" && nt[1].Text == "A second line" && nt[1].Baseline == nt[0].Baseline + hp && nt[0].Baseline == LayoutEngine.BandDistance + hs.Asc
            && nt[2].Text == "2 / 4" && nt[3].Text == "of this sample" && nt[3].X + w2 == nt[2].X + w1 && np[0].X == nt[3].X + w2 + LayoutEngine.BandGap && np[0].Y == LayoutEngine.BandDistance + (2 * hp - 300000) / 2
            && nt[4].Text == "Street 1" && nt[4].Baseline == nTop + fs.Asc && nt[5].Text == "Town" && nt[5].Baseline == nTop + fp + fs.Asc
            && nt[6].Text == "Page 2 of 4" && nt[7].Text == "third line" && nt[7].Baseline == nTop + 2 * fp + fs.Asc && nt[7].X == np[1].X + 480000 + LayoutEngine.BandGap
            && np[1].Y == nTop + (3 * fp - 360000) / 2 && nt[8].Baseline == nTop + fs.Asc && en.BandRoom(true) == LayoutEngine.BandDistance + 3 * fp + LayoutEngine.BandGap
            && linesOpen.Stored && Diff(en, linesOpen.Engine) == null && linesOpen.Engine.Doc.Footer == dn.Footer,
            $"{nt.Count} texts: {string.Join(" | ", nt.Select(b => b.Text + "@" + b.X + "," + b.Baseline))}; pictures {string.Join(" ", np.Select(b => b.X + "," + b.Y))}; stored {linesOpen.Stored}, diff {Diff(en, linesOpen.Engine)}");

        string xps = Path.Combine(outDir, "bands.xps");
        Printing.WriteXps(eng, xps);
        int pages = Printing.XpsPages(xps);
        PageRenderer.SavePng(Printing.XpsPage(xps, 1), Path.Combine(outDir, "bands-printed-p2.png"));
        string pdf = Path.Combine(outDir, "bands.pdf");
        PdfExport.Write(eng, pdf);
        Check("pdf: the file is well formed and has every page", PdfExport.Verify(pdf) == eng.Pages.Count, PdfExport.Verify(pdf) + " pages, " + new FileInfo(pdf).Length + " bytes");

        // A font cut down to a few glyphs keeps those glyphs as they were and empties the others.
        var face = FontCatalog.Resolve("Calibri", false, false).Face;
        int ga = face.Lookup('a').Glyph, gz = face.Lookup('z').Glyph, ge = face.Lookup(0xE9).Glyph;
        var small = new FontFace(face.Info, face.Subset(new[] { ga, ge }));
        Check("pdf: a font cut down to the glyphs used keeps them unchanged",
            small.GlyphY(ga) == face.GlyphY(ga) && small.GlyphY(ge) == face.GlyphY(ge) && small.AdvanceOfGlyph(ga) == face.AdvanceOfGlyph(ga)
            && small.GlyphY(gz) == (0, 0) && face.GlyphY(gz) != (0, 0) && small.UnitsPerEm == face.UnitsPerEm);

        Check("printing: every page goes through the printing path", pages == eng.Pages.Count && new FileInfo(xps).Length > 20000, pages + " pages, " + new FileInfo(xps).Length + " bytes");
    }

    /// <summary>A table: one text per cell; a | in a text starts a new paragraph in the cell.</summary>
    static Paragraph Table(Document d, int[] widths, Align? align, params string[][] rows)
    {
        var grid = new TableGrid { Widths = widths };
        foreach (var row in rows)
        {
            var ids = new int[widths.Length];
            for (int c = 0; c < ids.Length; c++)
            {
                var cell = d.NewCell();
                cell.Paras.Clear();
                foreach (string part in (c < row.Length ? row[c] : "").Split('|'))
                {
                    var cp = new Paragraph { Style = "Normal" };
                    if (part.Length > 0) cp.Runs.Add(new Run(part));
                    cell.Paras.Add(cp);
                }
                ids[c] = cell.Id;
            }
            grid.Rows.Add(ids);
        }
        var p = new Paragraph { Style = "Normal", Align = align, Table = grid };
        d.Body.Add(p);
        return p;
    }

    static Document TableDoc()
    {
        var r = new Rng(21);
        var d = Document.NewDefault();
        d.Body.Clear();
        int w = d.Page.ContentWidth;
        Para(d, "Title", null, new Run("Tables"));
        Table(d, new[] { w / 3, w / 3, w / 3 }, null, new[] { "Name", "Value", "Unit" }, new[] { "Width", "210", "mm" }, new[] { "Height", "297", "mm" });
        Para(d, "Normal", null, new Run(Text(r, 30)));
        var mixed = Table(d, new[] { 1200000, 3000000, 1000000 }, Align.Center,
            new[] { Text(r, 2), Text(r, 30), Text(r, 1) }, new[] { Text(r, 6) + "|" + Text(r, 4), "", Text(r, 9) }, new[] { "", Text(r, 50) + "|" + Text(r, 5) + "|", "x" });
        var shaped = d.Cells[mixed.Table!.Rows[0][1]].Paras[0];
        shaped.Runs.Add(new Run(" bold and large", new(Bold: true, Size: 32)));
        shaped.Align = Align.Justify;
        shaped.Line = 1500;
        d.Cells[mixed.Table.Rows[1][2]].Paras[0].Align = Align.Right;
        Para(d, "Normal", Align.Justify, new Run(Text(r, 40)));
        Table(d, new[] { 900000, 900000 }, Align.Right, new[] { "a", "b" }, new[] { "c", "d" });
        Para(d, "Heading2", null, new Run("A heading that stays with its table"));
        var rows = new List<string[]>();
        for (int i = 0; i < 70; i++) rows.Add(new[] { (i + 1).ToString(), Text(r, 1 + r.Next(i % 9 == 0 ? 40 : 6)), Text(r, 2), i % 5 == 0 ? Text(r, 12) : "" });
        Table(d, new[] { 500000, 2400000, 1300000, 1500000 }, null, rows.ToArray());
        byte[] png = MakePicture(240, 160, false);
        Para(d, "Normal", null, Float(d, png, false, 240, 160, "square", 0, 0), new Run(Text(r, 20)));
        Table(d, new[] { w / 2, w / 2 }, null, new[] { Text(r, 8), Text(r, 8) }, new[] { Text(r, 8), Text(r, 30) });
        Para(d, "Normal", null, new Run(Text(r, 30)));
        Table(d, new[] { w }, null, new[] { Text(r, 25) });
        Para(d, "Normal", null);
        return d;
    }

    /// <summary>Making a table, typing in its cells, adding and taking away rows and columns, widths, undo.</summary>
    static void Tables()
    {
        var r = new Rng(77);
        var d = Document.NewDefault();
        var eng = new LayoutEngine(d);
        eng.Update();
        var ed = new Editor(d, eng);
        byte[] before = RksFile.ContentJson(d);
        ed.InsertTable(2, 3);
        ed.TypeText("alpha");
        ed.NextCell(1);
        ed.TypeText("beta");
        var grid = d.Body[1].Table!;
        Check("tables: a new table stands between two paragraphs and takes typing cell by cell",
            d.Body.Count == 3 && grid.Columns == 3 && grid.Rows.Count == 2 && d.Cells[grid.Rows[0][0]].Paras[0].Text == "alpha" && d.Cells[grid.Rows[0][1]].Paras[0].Text == "beta" && ed.Story < 0);
        int h = d.Body[1].Lines![0].Height;
        ed.MoveEnd(false, true);
        ed.TypeText(" " + Text(r, 60));
        Check("tables: a row grows with the tallest of its cells", d.Body[1].Lines![0].Height > 3 * h && d.Body[1].Lines![1].Height == h, d.Body[1].Lines![0].Height + " / " + h);
        for (int k = 0; k < 5; k++) ed.NextCell(1);
        Check("tables: Tab behind the last cell adds a row", d.Body[1].Table!.Rows.Count == 3);
        int width = d.Body[1].Table!.Width;
        ed.InsertColumn();
        bool added = d.Body[1].Table!.Columns == 4 && d.Body[1].Table!.Width == width;
        ed.DeleteRow();
        ed.DeleteColumn();
        Check("tables: columns and rows are added and taken away, and the table keeps its width",
            added && d.Body[1].Table!.Columns == 3 && d.Body[1].Table!.Rows.Count == 2 && d.Body[1].Table!.Width == width);
        int first = d.Body[1].Table!.Widths[0];
        ed.MoveColumnEdge(1, 0, 300000);
        Check("tables: the line between two columns can be moved", d.Body[1].Table!.Widths[0] == first + 300000 && d.Body[1].Table!.Width == width);
        ed.MoveTo(new Pos(0, 0), false, story: -d.Body[1].Table!.Rows[0][0]);
        for (int k = 0; k < 70; k++) ed.InsertRow();
        var pages = d.Body[1].Lines!.Select(l => l.Page).Distinct().Count();
        Check("tables: a long table goes on over the pages, row by row", pages >= 2 && Invariants(eng) == null && Diff(eng, Fresh(d)) == null, pages + " pages; " + (Invariants(eng) ?? Diff(eng, Fresh(d)) ?? ""));
        ed.LeaveNote();
        ed.MoveTo(new Pos(1, 0), false);
        ed.Backspace(false);
        bool gone = d.Body.All(p => p.Table == null);
        while (ed.CanUndo) ed.Undo();
        Check("tables: a table is deleted like a character, and undoing everything gives back the empty document", gone && RksFile.ContentJson(d).AsSpan().SequenceEqual(before));
    }

    static Document Long()
    {
        var r = new Rng(2026);
        var d = Document.NewDefault();
        d.Body.Clear();
        string[] heads = { "Heading1", "Heading2", "Heading3" };
        for (int i = 0; i < 420; i++)
        {
            int kind = r.Next(20);
            if (kind < 3) { Para(d, heads[kind], null, new Run(Text(r, 2 + r.Next(6)))); continue; }
            if (kind == 3) { Para(d, "Normal", null); continue; }
            var runs = new List<Run>();
            int n = 1 + r.Next(4);
            for (int k = 0; k < n; k++)
            {
                var props = new CharProps();
                if (r.Next(3) == 0) props = props with { Font = Fonts[r.Next(Fonts.Length)] };
                if (r.Next(4) == 0) props = props with { Size = 16 + 2 * r.Next(12) };
                if (r.Next(5) == 0) props = props with { Bold = true };
                if (r.Next(5) == 0) props = props with { Italic = true };
                if (r.Next(8) == 0) props = props with { Underline = true };
                runs.Add(new Run(Text(r, 3 + r.Next(60)) + " ", props));
            }
            var p = Para(d, "Normal", r.Next(3) == 0 ? (Align)r.Next(4) : null, runs.ToArray());
            if (kind == 4) p.PageBreakBefore = true;
        }
        return d;
    }

    internal static LayoutEngine Fresh(Document doc)
    {
        var d = RksFile.ParseContent(RksFile.ContentJson(doc));
        var e = new LayoutEngine(d);
        e.Update();
        return e;
    }

    /// <summary>Null when both layouts place everything at the same positions.</summary>
    internal static string? Diff(LayoutEngine a, LayoutEngine b)
    {
        if (a.Pages.Count != b.Pages.Count) return $"pages {a.Pages.Count} / {b.Pages.Count}";
        for (int pg = 0; pg < a.Pages.Count; pg++)
        {
            var la = a.Pages[pg].Lines;
            var lb = b.Pages[pg].Lines;
            if (la.Count != lb.Count) return $"page {pg + 1}: lines {la.Count} / {lb.Count}";
            for (int i = 0; i < la.Count; i++)
            {
                var (pa, x) = la[i];
                var (pb, y) = lb[i];
                if (pa != pb || !SameLine(x, y))
                    return $"page {pg + 1} line {i + 1}: para {pa}/{pb} start {x.Start}/{y.Start} len {x.Length}/{y.Length} x {x.X}/{y.X} top {x.Top}/{y.Top} h {x.Height}/{y.Height} base {x.Baseline}/{y.Baseline}";
            }
            var ca = a.Pages[pg].Cells;
            var cb = b.Pages[pg].Cells;
            if (ca.Count != cb.Count) return $"page {pg + 1}: table cells {ca.Count} / {cb.Count}";
            for (int i = 0; i < ca.Count; i++)
            {
                var (u, v) = (ca[i], cb[i]);
                if (u.Para != v.Para || u.Row != v.Row || u.Col != v.Col || u.X != v.X || u.Y != v.Y || u.W != v.W || u.H != v.H || u.Cell.Paras.Count != v.Cell.Paras.Count)
                    return $"page {pg + 1} cell {u.Row},{u.Col} of paragraph {u.Para}: x {u.X}/{v.X} y {u.Y}/{v.Y} w {u.W}/{v.W} h {u.H}/{v.H}";
                for (int k = 0; k < u.Cell.Paras.Count; k++)
                {
                    var lu = u.Cell.Paras[k].Lines!;
                    var lv = v.Cell.Paras[k].Lines;
                    if (lv == null || lu.Count != lv.Count) return $"page {pg + 1} cell {u.Row},{u.Col} of paragraph {u.Para}: line counts differ";
                    for (int j = 0; j < lu.Count; j++)
                        if (!SameLine(lu[j], lv[j])) return $"page {pg + 1} cell {u.Row},{u.Col} of paragraph {u.Para}, paragraph {k} line {j + 1}: x {lu[j].X}/{lv[j].X} top {lu[j].Top}/{lv[j].Top} len {lu[j].Length}/{lv[j].Length}";
                }
            }
            var ba = a.Pages[pg].Bands;
            var bb = b.Pages[pg].Bands;
            if (ba.Count != bb.Count) return $"page {pg + 1}: header and footer texts {ba.Count} / {bb.Count}";
            for (int i = 0; i < ba.Count; i++)
                if (ba[i].Footer != bb[i].Footer || ba[i].X != bb[i].X || ba[i].Baseline != bb[i].Baseline || ba[i].Text != bb[i].Text)
                    return $"page {pg + 1} header or footer text {i + 1}: x {ba[i].X}/{bb[i].X} baseline {ba[i].Baseline}/{bb[i].Baseline} text {ba[i].Text}/{bb[i].Text}";
            var ka = a.Pages[pg].BandPics;
            var kb = b.Pages[pg].BandPics;
            if (ka.Count != kb.Count) return $"page {pg + 1}: header and footer pictures {ka.Count} / {kb.Count}";
            for (int i = 0; i < ka.Count; i++)
                if (ka[i].Footer != kb[i].Footer || ka[i].X != kb[i].X || ka[i].Y != kb[i].Y || ka[i].Pic != kb[i].Pic)
                    return $"page {pg + 1} header or footer picture {i + 1}: x {ka[i].X}/{kb[i].X} y {ka[i].Y}/{kb[i].Y}";
            var fa = a.Pages[pg].Floats;
            var fb = b.Pages[pg].Floats;
            if (fa.Count != fb.Count) return $"page {pg + 1}: floating pictures {fa.Count} / {fb.Count}";
            for (int i = 0; i < fa.Count; i++)
                if (fa[i].Para != fb[i].Para || fa[i].Off != fb[i].Off || fa[i].X != fb[i].X || fa[i].Y != fb[i].Y)
                    return $"page {pg + 1} floating picture {i + 1}: para {fa[i].Para}/{fb[i].Para} x {fa[i].X}/{fb[i].X} y {fa[i].Y}/{fb[i].Y}";
            var na = a.Pages[pg].Notes;
            var nb = b.Pages[pg].Notes;
            if (na.Count != nb.Count) return $"page {pg + 1}: footnotes {na.Count} / {nb.Count}";
            if (na.Count > 0 && a.Pages[pg].NoteTop != b.Pages[pg].NoteTop) return $"page {pg + 1}: footnote area top {a.Pages[pg].NoteTop} / {b.Pages[pg].NoteTop}";
            for (int i = 0; i < na.Count; i++)
            {
                if (na[i].Note.Id != nb[i].Note.Id || na[i].Number != nb[i].Number) return $"page {pg + 1}: footnote {i + 1} differs";
                var qa = na[i].Note.Paras;
                var qb = nb[i].Note.Paras;
                if (qa.Count != qb.Count) return $"page {pg + 1} footnote {na[i].Number}: paragraphs {qa.Count} / {qb.Count}";
                for (int k = 0; k < qa.Count; k++)
                {
                    var x = qa[k].Lines!;
                    var y = qb[k].Lines;
                    if (y == null || x.Count != y.Count) return $"page {pg + 1} footnote {na[i].Number}: line counts differ";
                    for (int j = 0; j < x.Count; j++)
                        if (!SameLine(x[j], y[j])) return $"page {pg + 1} footnote {na[i].Number} paragraph {k} line {j + 1}: x {x[j].X}/{y[j].X} top {x[j].Top}/{y[j].Top} len {x[j].Length}/{y[j].Length}";
                }
            }
        }
        return null;
    }

    static bool SameLine(LineBox x, LineBox y) =>
        x.Start == y.Start && x.Length == y.Length && x.X == y.X && x.Top == y.Top && x.Height == y.Height
        && x.Baseline == y.Baseline && x.Page == y.Page && x.Row == y.Row && x.Mark == y.Mark && x.MarkX == y.MarkX && x.Adv.AsSpan().SequenceEqual(y.Adv);

    internal static string? Invariants(LayoutEngine e)
    {
        var doc = e.Doc;
        int bottom = doc.Page.Height - doc.Page.Bottom;
        var next = new int[doc.Body.Count];
        int lastPara = 0, number = 0;
        var seenNotes = new HashSet<int>();
        for (int pg = 0; pg < e.Pages.Count; pg++)
        {
            var lines = e.Pages[pg].Lines;
            if (lines.Count == 0) return $"page {pg + 1} is empty";
            int y = doc.Page.Top;
            for (int i = 0; i < lines.Count; i++)
            {
                var (para, l) = lines[i];
                if (para < lastPara) return $"page {pg + 1} line {i + 1}: paragraphs out of order";
                lastPara = para;
                if (l.Start != next[para]) return $"page {pg + 1} line {i + 1}: starts at {l.Start}, expected {next[para]}";
                next[para] += l.Length;
                // An empty line, which shows nothing, may reach into the line below where the room between paragraphs gave,
                // and the empty paragraph that ends a document into the line above.
                var bp = doc.Body[para];
                bool empty = doc.AdaptSpacing && bp.Table == null && bp.Length == 0 && l.Mark == null && doc.BordersOf(bp).None;
                if (l.Top < y && !empty) return $"page {pg + 1} line {i + 1}: overlaps the line above";
                if (i > 0 && l.Top + l.Height > bottom) return $"page {pg + 1} line {i + 1}: below the text area";
                if (l.Page != pg) return $"page {pg + 1} line {i + 1}: wrong page number";
                y = empty ? Math.Max(y, l.Top) : l.Top + l.Height;
            }
            // The text of a table cell stays inside the cell, between its left and right edge and its top and bottom.
            foreach (var cell in e.Pages[pg].Cells)
                foreach (var cp in cell.Cell.Paras)
                    foreach (var l in cp.Lines!)
                        if (l.Page != pg || l.Top < cell.Y || l.Top + l.Height > cell.Y + cell.H || doc.Page.Left + l.X < cell.X)
                            return $"page {pg + 1} cell {cell.Row},{cell.Col} of paragraph {cell.Para}: a line lies outside the cell";
            foreach (var f in e.Pages[pg].Floats)
            {
                if (f.Pic.Wrap is not ("topbottom" or "square")) continue;
                for (int i = 0; i < lines.Count; i++)
                {
                    var (para, l) = lines[i];
                    if (l.Top >= f.Y + f.Pic.Height || l.Top + l.Height <= f.Y) continue;
                    // An empty line shows nothing; where the room gave, it may come to lie over a picture.
                    var fp = doc.Body[para];
                    if (doc.AdaptSpacing && fp.Table == null && fp.Length == 0 && l.Mark == null && doc.BordersOf(fp).None) continue;
                    string t = doc.Body[para].Text;
                    int end = l.End;
                    while (end > l.Start && (t[end - 1] == ' ' || t[end - 1] == Document.LineBreak)) end--;
                    int x0 = doc.Page.Left + l.X, x1 = doc.Page.Left + l.XOffset(end - l.Start);
                    if (f.Pic.Wrap == "topbottom" || (x0 < f.X + f.Pic.Width && x1 > f.X))
                        return $"page {pg + 1} line {i + 1}: lies over a picture that text should keep clear of";
                }
            }
            // The footnotes of a page are those its lines refer to; they end at the bottom; no line reaches into them.
            var box = e.Pages[pg];
            var want = new List<int>();
            foreach (var (para, l) in lines)
                foreach (var (off, o) in doc.Body[para].Objects)
                    if (off >= l.Start && off < l.End && o is NoteRef nr && seenNotes.Add(nr.Id)) want.Add(nr.Id);
            if (!want.SequenceEqual(box.Notes.Select(n => n.Note.Id))) return $"page {pg + 1}: its footnotes are not those its lines refer to";
            if (box.Notes.Count == 0) continue;
            long ny = box.NoteTop + LayoutEngine.NoteGapAbove + LayoutEngine.NoteGapBelow;
            foreach (var (note, num) in box.Notes)
            {
                if (num != ++number) return $"page {pg + 1}: footnote number {num}, expected {number}";
                foreach (var p in note.Paras)
                {
                    foreach (var l in p.Lines!)
                    {
                        if (l.Top != ny || l.Page != pg) return $"page {pg + 1} footnote {num}: line at {l.Top}, expected {ny}";
                        ny += l.Height;
                    }
                    ny += doc.StyleOf(p).After;
                }
            }
            if (ny != bottom) return $"page {pg + 1}: the footnotes end at {ny}, not at the bottom {bottom}";
            for (int i = 1; i < lines.Count; i++)
                if (lines[i].Line.Top + lines[i].Line.Height > box.NoteTop) return $"page {pg + 1} line {i + 1}: reaches into the footnotes";
        }
        for (int i = 0; i < next.Length; i++)
            if (next[i] != doc.Body[i].Length || doc.Body[i].Lines == null) return $"paragraph {i}: lines cover {next[i]} of {doc.Body[i].Length}";
        return null;
    }

    static Document LatexDoc()
    {
        var d = Document.NewDefault();
        Para(d, "Normal", null, new Run("Mathematics pasted as LaTeX becomes equations:"));
        var eng = new LayoutEngine(d);
        eng.Update();
        var ed = new Editor(d, eng);
        ed.MoveTo(new Pos(d.Body.Count - 1, d.Body[^1].Length), false);
        ed.ToggleLatex();
        ed.Paste("\n" + @"V = \frac{4\pi r^{3}}{3}" + "\n" + @"The area is $A = \pi r^{2}$ and the root is \(x = \frac{-b \pm \sqrt{b^{2} - 4ac}}{2a}\)." + "\n"
            + @"\frac{dV}{dt} = 4\pi\cdot 6^{2}\cdot(-0.04) = -5.76\pi \approx -18.1 \text{ dm}^{3}\text{/h}" + "\n"
            + @"$$V = \pi\Big[\frac{x^{5}}{5}\Big]_{0}^{2} = \frac{32\pi}{5} \quad\text{and}\quad \lim_{x \to 0} \frac{\sin x}{x} = 1$$" + "\n"
            + "The ticket costs $5 and the book $12.");
        return d;
    }

    /// <summary>A title with a line below it from its style, two paragraphs in one frame, one with lines above and below, and a title that has none.</summary>
    static Document BordersDoc()
    {
        var d = Document.NewDefault();
        d.Body.Clear();
        d.FindPara("Title")!.Borders = new ParaBorders(Bottom: 12700, Gap: 50800, Color: 0x4F81BD);
        Para(d, "Title", null, new Run("A title with a line below it"));
        Para(d, "Normal", null, new Run("Ordinary text between the title and the frame."));
        var box = new ParaBorders(9525, 9525, 9525, 9525, 12700, 50800, 0);
        Para(d, "Normal", null, new Run("The first paragraph in a frame: the line above belongs to it, and the lines at its sides run on to the next one. "
            + "It is long enough to take more than one line of the page, so that the lines at the sides pass several lines of text.")).Borders = box with { Bottom = 0 };
        Para(d, "Normal", null, new Run("The last paragraph in the frame, with the line below.")).Borders = box with { Top = 0 };
        Para(d, "Normal", null, new Run("Lines above and below only.")).Borders = new ParaBorders(Top: 19050, Bottom: 19050, Gap: 25400);
        Para(d, "Title", null, new Run("A title without its line")).Borders = default(ParaBorders);
        return d;
    }

    /// <summary>Sections of different lengths over several pages, with an empty paragraph as a spacer now and then, laid out
    /// with the room between paragraphs giving at the ends of pages.</summary>
    static Document SpacingDoc()
    {
        var d = Document.NewDefault();
        d.Body.Clear();
        string[] words = { "room", "page", "line", "gap", "text", "section", "end", "paragraph", "gives", "fills", "the", "a", "of" };
        int[] lengths = { 9, 3, 14, 6, 2, 11, 5, 8, 1, 12, 4, 7 };
        for (int n = 0; n < lengths.Length; n++)
        {
            Para(d, "Heading1", null, new Run("Section " + (n + 1)));
            if (n % 4 == 1) Para(d, "Heading2", null, new Run("A part of it"));
            for (int j = 0; j < lengths[n]; j++)
            {
                int count = 5 + (j * 37 + n * 11) % 70;
                Para(d, "Normal", null, new Run(string.Join(" ", Enumerable.Range(0, count).Select(t => words[(t * 7 + j + n) % words.Length])) + "."));
                if ((j + n) % 9 == 4) Para(d, "Normal", null);
            }
        }
        return d;
    }

    static void SpacingChecks(string outDir)
    {
        static LayoutEngine Laid(Document d, bool adapt)
        {
            d.AdaptSpacing = adapt;
            var e = new LayoutEngine(d);
            e.Update();
            return e;
        }
        // Kept as numbers: two layouts of one document share their lines.
        static List<int> Tops(LayoutEngine e) => e.Pages[0].Lines.Select(l => l.Line.Top).ToList();
        // The gaps between paragraphs on the first page.
        static List<long> Gaps(LayoutEngine e)
        {
            var g = new List<long>();
            var ls = e.Pages[0].Lines;
            for (int j = 1; j < ls.Count; j++)
                if (ls[j].Para != ls[j - 1].Para) g.Add(ls[j].Line.Top - ((long)ls[j - 1].Line.Top + ls[j - 1].Line.Height));
            return g;
        }
        static long Hole(LayoutEngine e)
        {
            var low = e.Pages[0].Lines[^1].Line;
            return e.Doc.Page.Height - e.Doc.Page.Bottom - ((long)low.Top + low.Height);
        }
        // A section of one heading and so many one-line paragraphs that exactly `over` of its lines stand on a second page when nothing gives.
        static Document Made(int over)
        {
            var d = Document.NewDefault();
            d.AdaptSpacing = false;
            d.Body.Clear();
            Para(d, "Heading1", null, new Run("A section"));
            LayoutEngine eng;
            do
            {
                Para(d, "Normal", null, new Run("One line of the section."));
                eng = new LayoutEngine(d);
                eng.Update();
            } while (eng.Pages.Count < 2 || eng.Pages[1].Lines.Count < over);
            return d;
        }
        // Two lines over at the end of the section come back; no gap gives more than a fifth of itself.
        var two = Made(2);
        var off = Laid(two, false);
        var offGaps = Gaps(off);
        int offPages = off.Pages.Count;
        var on = Laid(two, true);
        var onGaps = Gaps(on);
        bool back = offPages == 2 && on.Pages.Count == 1 && onGaps.Count >= offGaps.Count;
        bool little = back && offGaps.Select((g, j) => onGaps[j] <= g && onGaps[j] >= g - g * LayoutEngine.GiveMost / 1000).All(x => x)
            && onGaps.Zip(offGaps).Any(t => t.First < t.Second);
        // Eight lines over are more than the page can give: they stay on the next page.
        var many = Made(8);
        int manyFirst = Laid(many, false).Pages[0].Lines.Count;
        var manyOn = Laid(many, true);
        bool left = manyOn.Pages.Count == 2 && manyOn.Pages[0].Lines.Count == manyFirst;
        // A section that begins on the next page by a page break: nothing gives.
        var second = Made(1);
        second.Body.RemoveAt(second.Body.Count - 1);
        Para(second, "Heading1", null, new Run("The next section")).PageBreakBefore = true;
        Para(second, "Normal", null, new Run("Its text."));
        var secondOff = Tops(Laid(second, false));
        bool apart = Tops(Laid(second, true)).SequenceEqual(secondOff);
        // A paragraph of several lines in the middle of a long section, cut by the end of the page.
        static (Document Doc, Paragraph Long) Cut(int fewer, int times = 6)
        {
            int onFirst = Made(1).Body.Count - 1;
            var d = Document.NewDefault();
            d.Body.Clear();
            Para(d, "Heading1", null, new Run("A section"));
            for (int i = 1; i < onFirst - fewer; i++) Para(d, "Normal", null, new Run("One line of the section."));
            var longOne = Para(d, "Normal", null, new Run(string.Join(" ", Enumerable.Repeat("A paragraph of several lines that the end of the page cuts in two.", times))));
            for (int i = 0; i < 12; i++) Para(d, "Normal", null, new Run("One line of the section."));
            Laid(d, false);
            return (d, longOne);
        }
        // It comes back whole.
        bool found = false, whole = false;
        for (int fewer = 1; fewer <= 5 && !found; fewer++)
        {
            var (d, longOne) = Cut(fewer);
            if (longOne.Lines![0].Page == longOne.Lines![^1].Page) continue;
            found = true;
            Laid(d, true);
            whole = longOne.Lines![0].Page == 0 && longOne.Lines![^1].Page == 0;
        }
        // Too much of it over to bring back: it begins on the next page whole, and the page it left is filled down to its
        // lower edge by more room between its paragraphs, two fifths of each at most.
        bool pushed = false, full = false, tried = false;
        string trace = "";
        for (int fewer = 1; fewer <= 6 && !tried; fewer++)
        {
            var (d, longOne) = Cut(fewer, 11);
            int onFirst = longOne.Lines!.Count(l => l.Page == 0);
            trace += $" [{fewer}: {onFirst}/{longOne.Lines!.Count}]";
            if (onFirst == 0 || onFirst == longOne.Lines!.Count || longOne.Lines!.Count - onFirst < 5) continue;
            tried = true;
            var offG = Gaps(Laid(d, false));
            var eng = Laid(d, true);
            pushed = longOne.Lines!.All(l => l.Page == 1) && d.Body.All(p => !p.Together && !p.PageBreakBefore && p.Before == null && p.After == null);
            long gap = Hole(eng);
            var onG = Gaps(eng);
            full = gap >= 0 && gap < 12700 && onG.Count < offG.Count && onG.Select((g, j) => g >= offG[j] && g <= offG[j] + offG[j] * LayoutEngine.GrowMost / 1000).All(x => x);
            trace += $" gap {gap}";
        }
        // The largest gap gives first: a section with an empty paragraph as a spacer near its top and one line over.
        static Document Spaced()
        {
            var d = Document.NewDefault();
            d.AdaptSpacing = false;
            d.Body.Clear();
            Para(d, "Heading1", null, new Run("A section"));
            Para(d, "Normal", null, new Run("Its first line."));
            Para(d, "Normal", null);
            LayoutEngine eng;
            do
            {
                Para(d, "Normal", null, new Run("One line of the section."));
                eng = new LayoutEngine(d);
                eng.Update();
            } while (eng.Pages.Count < 2);
            return d;
        }
        // The spacer's gap: from the foot of "Its first line." to the top of the line below the empty paragraph.
        static long SpacerGap(LayoutEngine e)
        {
            var ls = e.Pages[0].Lines;
            return ls[3].Line.Top - ((long)ls[1].Line.Top + ls[1].Line.Height);
        }
        var spaced = Spaced();
        var sOff = Laid(spaced, false);
        long bigOff = SpacerGap(sOff);
        var restOff = Gaps(sOff).Skip(3).ToList();
        var sOn = Laid(spaced, true);
        bool one = sOn.Pages.Count == 1;
        double big = (double)SpacerGap(sOn) / bigOff;
        var blankLine = sOn.Pages[0].Lines[2].Line;
        bool inside = blankLine.Top >= sOn.Pages[0].Lines[1].Line.Top + sOn.Pages[0].Lines[1].Line.Height && blankLine.Top < sOn.Pages[0].Lines[3].Line.Top;
        double least = Gaps(sOn).Skip(3).Zip(restOff).Min(t => (double)t.First / t.Second);
        bool first = one && inside && big < 0.999 && big < least && big >= 0.59 && least >= 0.79;
        // A short section that did not fit below a hole of a line or more comes back whole.
        bool holeFound = false, cameBack = false;
        for (int fewer = 1; fewer <= 4 && !holeFound; fewer++)
        {
            var d = Made(1);
            for (int i = 0; i < fewer; i++) d.Body.RemoveAt(d.Body.Count - 1);
            var head = Para(d, "Heading1", null, new Run("The next section"));
            Para(d, "Normal", null, new Run("Its only line."));
            var eng = Laid(d, false);
            if (eng.Pages.Count != 2 || head.Lines![0].Page != 1 || Hole(eng) < eng.Pages[0].Lines[^1].Line.Height) continue;
            holeFound = true;
            cameBack = Laid(d, true).Pages.Count == 1;
        }
        // A letter: a picture beside its first lines, a row of logos as a picture near the foot, two short lines below it, and an
        // empty paragraph at the end; laid out without giving, the last line and the empty paragraph stand on a second page.
        static Picture Floating(Document d, int w, int h, int dx)
        {
            var data = MakePicture(40, 20, false);
            string name = RksFile.MediaName(data, false);
            d.Media[name] = data;
            return new Picture(name, w, h, false, "square", dx, 0);
        }
        static (Document Doc, Picture Head, Picture Logos)? Letter()
        {
            for (int n = 20; n < 70; n++)
            {
                var d = Document.NewDefault();
                d.AdaptSpacing = false;
                d.Body.Clear();
                var head = Floating(d, 1371600, 914400, 0);
                Para(d, "Normal", Align.Right, ReiksDocument.Run.Object(head, new Run("")), new Run("A school, Some Road, A Town"));
                for (int j = 0; j < n; j++) Para(d, "Normal", null, new Run(j % 7 == 3 ? "" : "One line of the letter."));
                var logos = Floating(d, 914400, 457200, d.Page.ContentWidth - 914400);
                // The row also holds a picture in the line, taller than the floating one, as the row of logos in a letter does.
                Para(d, "Normal", null, ReiksDocument.Run.Object(logos, new Run("")), Pic(d, MakePicture(40, 20, false), false, 120, 66, false), new Run(" Logos"));
                Para(d, "Normal", null, new Run("Chair of the board."));
                Para(d, "Normal", null, new Run("Teacher for child protection."));
                Para(d, "Normal", null);
                var e = Laid(d, false);
                int c = d.Body.Count;
                if (e.Pages.Count == 2 && d.Body[c - 3].Lines![0].Page == 0 && d.Body[c - 2].Lines![0].Page == 1) return (d, head, logos);
            }
            return null;
        }
        bool letterFound = false, letterOne = false, logosMoved = false, clear = false, headStays = false;
        if (Letter() is var (ld, headPic, logoPic))
        {
            letterFound = true;
            var le = Laid(ld, false);
            var logoOff = le.Pages[0].Floats.First(f => f.Pic == logoPic);
            int logoLine = le.Pages[0].Lines.FindIndex(l => l.Para == logoOff.Para);
            long offFromLine = logoOff.Y - le.Pages[0].Lines[logoLine].Line.Top, logoYOff = logoOff.Y;
            long headY = le.Pages[0].Floats.First(f => f.Pic == headPic).Y;
            var besideOff = le.Pages[0].Lines.Where(l => l.Line.Top < headY + 914400 + LayoutEngine.FloatGap).Select(l => l.Line.Top).ToList();
            var lo = Laid(ld, true);
            var pg0 = lo.Pages[0];
            var logoOn = pg0.Floats.First(f => f.Pic == logoPic);
            int lineOn = pg0.Lines.FindIndex(l => l.Para == logoOn.Para);
            letterOne = lo.Pages.Count == 1 && Invariants(lo) == null;
            logosMoved = logoOn.Y < logoYOff && logoOn.Y - pg0.Lines[lineOn].Line.Top == offFromLine;
            clear = pg0.Lines[lineOn + 1].Line.Top >= logoOn.Y + logoOn.Pic.Height + LayoutEngine.FloatGap;
            headStays = pg0.Floats.First(f => f.Pic == headPic).Y == headY && pg0.Lines.Take(besideOff.Count).Select(l => l.Line.Top).SequenceEqual(besideOff);
            Sample("letter", ld, outDir, 1);
        }
        Check("spacing: on a page with pictures the text keeps clear of, a picture moves with the line that holds it, what stands beside it moves with it, "
            + "and nothing moves up into it: a letter with logos near its foot comes back to one page",
            letterFound && letterOne && logosMoved && clear && headStays, $"found {letterFound} one {letterOne} moved {logosMoved} clear {clear} head {headStays}");
        // The empty paragraph that ends a document does not make a page of its own, also with a page break before it.
        var tailDoc = Made(1);
        tailDoc.Body.RemoveAt(tailDoc.Body.Count - 1);
        var lastEmpty = Para(tailDoc, "Normal", null);
        var tOff = Laid(tailDoc, false);
        if (tOff.Pages.Count == 1)
        {
            lastEmpty.Before = (int)Math.Min(int.MaxValue / 4, Hole(tOff));
            tOff = Laid(tailDoc, false);
        }
        bool tailAlone = tOff.Pages.Count == 2 && tOff.Pages[1].Lines.Count == 1;
        var tOn = Laid(tailDoc, true);
        bool tailKept = tOn.Pages.Count == 1 && Invariants(tOn) == null;
        lastEmpty.PageBreakBefore = true;
        tailKept &= Laid(tailDoc, true).Pages.Count == 1 && Laid(tailDoc, false).Pages.Count == 2;
        tailDoc.AdaptSpacing = true;
        Sample("tail", tailDoc, outDir, 1);
        Check("spacing: the empty paragraph that ends a document stays at the foot of the last page with text instead of making a page of its own, also after a page break",
            tailAlone && tailKept, $"alone without the rule {tailAlone}, kept {tailKept}");
        Check("spacing: a short section that did not fit below a hole of a line or more comes back whole, where the page can give that much room",
            holeFound && cameBack, $"hole {holeFound} back {cameBack}");
        Check("spacing: where a page has to give room, the largest gap gives first, with an empty paragraph in it, which stays inside the gap; "
            + "no gap gives more than two fifths of itself, an ordinary one no more than a fifth",
            first, $"one {one} inside {inside} big {big:0.###} least {least:0.###}");
        Check("spacing: a paragraph that the end of a page cut, with too much of it over to bring back, begins on the next page whole where a little more room "
            + "between the paragraphs of the page fills the page down to its lower edge; nothing in the document changes for it",
            tried && pushed && full, $"tried {tried} pushed {pushed} full {full}{trace}");
        Check("spacing: what the end of a page cut is kept together by a little less room between the paragraphs of the page, where a fifth of that room is enough: "
            + "the end of a section, or a paragraph alone; more than the page can take, and a section that begins on the next page, are left as they are",
            back && little && left && apart && found && whole, $"back {back} little {little} left {left} apart {apart} cut {found} whole {whole}");
    }

    /// <summary>A small Word file with the given body, in the usual form or (with strict) in the strict form of the format.</summary>
    static void WriteDocx(string file, string body, bool strict = false)
    {
        File.Delete(file);
        using var zip = System.IO.Compression.ZipFile.Open(file, System.IO.Compression.ZipArchiveMode.Create);
        void Put(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(xml);
        }
        string ns = strict ? "http://purl.oclc.org/ooxml/wordprocessingml/main" : "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Put("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\""
            + (strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument" : "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument")
            + "\" Target=\"word/document.xml\"/></Relationships>");
        Put("word/document.xml", "<w:document xmlns:w=\"" + ns + "\"><w:body>" + body + "</w:body></w:document>");
    }

    static void WordRobustChecks(string outDir)
    {
        static string P(string text) => "<w:p><w:r><w:t>" + text + "</w:t></w:r></w:p>";
        static IEnumerable<string> Texts(Document d) => d.Body.Concat(d.Cells.Values.SelectMany(c => c.Paras)).Select(p => p.Text);
        // Tables nested twenty thousand deep in a cell: read without running out of stack, every text once.
        var deep = new StringBuilder();
        for (int i = 0; i < 20000; i++) deep.Append("<w:tbl><w:tr><w:tc>");
        deep.Append(P("Deep text."));
        for (int i = 0; i < 20000; i++) deep.Append("</w:tc></w:tr></w:tbl>");
        string deepFile = Path.Combine(outDir, "word-deep.docx");
        WriteDocx(deepFile, "<w:tbl><w:tr><w:tc>" + deep + P("In the cell.") + "</w:tc></w:tr></w:tbl>" + P("After."));
        bool deepRead = false;
        string deepNote = "";
        try
        {
            var dr = DocxImport.Read(deepFile);
            var texts = Texts(dr.Doc).ToList();
            deepRead = texts.Count(t => t == "Deep text.") == 1 && texts.Contains("In the cell.") && texts.Contains("After.");
            deepNote = string.Join(" ", dr.Notes);
        }
        catch (InvalidDataException ex) { deepNote = ex.Message; }
        // A table inside a cell: each of its cells once.
        string twoFile = Path.Combine(outDir, "word-nested.docx");
        WriteDocx(twoFile, "<w:tbl><w:tr><w:tc><w:tbl><w:tr><w:tc>" + P("A") + "</w:tc><w:tc>" + P("B") + "</w:tc></w:tr><w:tr><w:tc>"
            + "<w:tbl><w:tr><w:tc>" + P("C") + "</w:tc></w:tr></w:tbl>" + P("D") + "</w:tc></w:tr></w:tbl>" + P("E") + "</w:tc></w:tr></w:tbl>" + P("After."));
        var nested = Texts(DocxImport.Read(twoFile).Doc).ToList();
        bool once = new[] { "A", "B", "C", "D", "E" }.All(t => nested.Count(x => x == t) == 1);
        Check("word: tables nested very deep inside a cell are read without the program running out of stack, and each nested cell once",
            deepRead && once, $"deep {deepRead} once {once} / {deepNote}");
        // The strict form of the format.
        string strictFile = Path.Combine(outDir, "word-strict.docx");
        WriteDocx(strictFile, P("Written in the strict form.") + "<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>Bold and centred.</w:t></w:r></w:p>", strict: true);
        bool strictRead = false;
        try
        {
            var sd = DocxImport.Read(strictFile).Doc;
            strictRead = sd.Body.Any(p => p.Text == "Written in the strict form.") && sd.Body.Any(p => p.Text == "Bold and centred." && p.Align == Align.Center && p.Runs[0].Props.Bold == true);
        }
        catch (InvalidDataException) { }
        Check("word: a document saved in the strict form of the format is read like any other", strictRead);
        // Files that cannot be read are refused in plain words: with a password, of the older kind, damaged.
        string Refusal(string name, byte[] data)
        {
            string f = Path.Combine(outDir, name);
            File.WriteAllBytes(f, data);
            try { DocxImport.Read(f); return "read"; }
            catch (InvalidDataException ex) { return ex.Message; }
        }
        byte[] ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        var locked = ole.Concat(new byte[504]).Concat(Encoding.Unicode.GetBytes("EncryptionInfo")).Concat(new byte[1000]).ToArray();
        var older = ole.Concat(new byte[2000]).ToArray();
        var whole = File.ReadAllBytes(strictFile);
        var damaged = whole.Take(whole.Length / 2).ToArray();
        string a = Refusal("word-password.docx", locked), b = Refusal("word-old.docx", older), c = Refusal("word-damaged.docx", damaged);
        Check("word: a document with a password, one of the older kind, and a damaged file are refused in plain words",
            a == DocxImport.PasswordText && b == DocxImport.OldText && c == DocxImport.DamagedText, $"{a} | {b} | {c}");
    }

    static void BorderChecks()
    {
        var d = BordersDoc();
        var eng = new LayoutEngine(d);
        eng.Update();
        var plain = BordersDoc();
        plain.FindPara("Title")!.Borders = default;
        foreach (var p in plain.Body) p.Borders = null;
        var bare = new LayoutEngine(plain);
        bare.Update();
        // What the first and the last line of paragraph k gained in height, and its first line in the place of its baseline.
        (int First, int Base, int Last) Gain(int k) =>
            (d.Body[k].Lines![0].Height - plain.Body[k].Lines![0].Height, d.Body[k].Lines![0].Baseline - plain.Body[k].Lines![0].Baseline,
                d.Body[k].Lines![^1].Height - plain.Body[k].Lines![^1].Height);
        bool several = d.Body[2].Lines!.Count >= 2 && d.Body[2].Lines!.Count == plain.Body[2].Lines!.Count;
        bool room = Gain(0) == (63500, 0, 63500) && Gain(1) == (0, 0, 0) && several && Gain(2) == (22225, 22225, 0) && Gain(3) == (22225, 0, 22225)
            && Gain(4) == (88900, 44450, 88900) && Gain(5) == (0, 0, 0);
        var (xl, xr) = eng.BorderEdges(d.Body[2], d.Page.Left, d.Page.ContentWidth);
        bool edges = xl == d.Page.Left - 50800 - 9525 && xr == d.Page.Left + d.Page.ContentWidth + 50800 + 9525
            && eng.BorderEdges(d.Body[0], d.Page.Left, d.Page.ContentWidth) == (d.Page.Left, d.Page.Left + d.Page.ContentWidth);
        var ed = new Editor(d, eng);
        ed.MoveTo(new Pos(1, 3), false);
        var look = ed.CurrentLook();
        ed.SetLook(look with { Borders = new ParaBorders(Bottom: 9525, Gap: 12700) });
        bool set = d.Body[1].Borders is { Bottom: 9525, Top: 0 } && d.Body[1].Lines![^1].Height == plain.Body[1].Lines![^1].Height + 22225;
        ed.Undo();
        bool undone = d.Body[1].Borders == null;
        ed.MoveTo(new Pos(0, 3), false);
        ed.SetLook(ed.CurrentLook() with { Borders = d.FindPara("Title")!.Borders });
        bool asStyle = d.Body[0].Borders == null;
        ed.SetLook(ed.CurrentLook() with { Borders = default });
        bool none = d.Body[0].Borders is { None: true } && d.Body[0].Lines![0].Height == plain.Body[0].Lines![0].Height;
        Check("borders: a line above a paragraph takes its room above the first line of text, a line below it under the last; lines at the sides stand outside the text; "
            + "a paragraph has the lines of its style, its own, or none; the Paragraph box sets them in one step for undo",
            room && edges && set && undone && asStyle && none, $"room {room} ({Gain(0)} {Gain(2)} {Gain(3)} {Gain(4)}) edges {edges} set {set} undone {undone} style {asStyle} none {none}");
    }

    /// <summary>A text that cites sources of one, two, three and four authors and one without, in a reference system, with its list of references.</summary>
    static Document RefsDoc(string system)
    {
        var d = Document.NewDefault();
        d.Body.Clear();
        d.CiteStyle = system;
        d.Sources = new List<Source>
        {
            new("s1", new[] { "Smith, Anna Maria" }, "2020", "One Author", "Publisher", ""),
            new("s2", new[] { "Smith, Anna Maria", "Jones, Bob" }, "2021", "Two Authors", "Journal of Things", "https://example.org/two"),
            new("s3", new[] { "Lee, Chen", "Diaz, Eva", "Brown, Tom" }, "2019", "Three Authors", "", ""),
            new("s4", new[] { "Lee, Chen", "Diaz, Eva", "Brown, Tom", "Kim, Ji" }, "", "Four Authors", "", ""),
            new("s5", System.Array.Empty<string>(), "2001", "No Author", "", ""),
        };
        Para(d, "Title", null, new Run("References as " + Citations.Systems.First(x => x.Id == system).Name));
        foreach (var src in d.Sources)
        {
            var p = Para(d, "Normal", null, new Run("A sentence that rests on a source "));
            p.Runs.Add(ReiksDocument.Run.Object(new Cite(src.Id), new Run("")));
            p.Runs.Add(new Run(" and goes on behind the citation."));
        }
        d.Body.AddRange(Citations.BuildRefs(d));
        return d;
    }

    static void PageNumberChecks()
    {
        var none = new Band("", "", "");
        var (h1, f1) = PageNumbers.Put(none, none, 1, 3);
        bool put = f1.Centre == "Page {page} of {pages}" && h1 == none && PageNumbers.Of(h1, f1) == (1, 3);
        // Moved to the top on the right in another look: gone from the foot, other text stays.
        var withText = f1 with { Left = "My report" };
        var (h2, f2) = PageNumbers.Put(none with { Right = "Draft" }, withText, 4, 0);
        bool moved = f2.Centre == "" && f2.Left == "My report" && h2.Right == "Draft\n{page}" && PageNumbers.Of(h2, f2) == (4, 0);
        var (h3, f3) = PageNumbers.Put(h2, f2, 0, 0);
        bool gone = h3.Right == "Draft" && f3.Left == "My report" && PageNumbers.Of(h3, f3).Place == 0;
        Check("page numbers: a place and a look put a line with the number into the header or footer, move it, and take it out again; other text there stays",
            put && moved && gone && PageNumbers.Places.Length == 7 && PageNumbers.Looks.Length == 4, $"put {put} moved {moved} gone {gone}");
    }

    static void ReferenceChecks()
    {
        var two = new Source("a", new[] { "Smith, Anna Maria", "Jones, Bob" }, "2020", "The Title", "Publisher", "https://example.org");
        var three = new Source("b", new[] { "Lee, Chen", "Diaz, Eva", "Brown, Tom" }, "2019", "Three", "", "");
        var four = new Source("c", new[] { "Lee, Chen", "Diaz, Eva", "Brown, Tom", "Kim, Ji" }, "", "Four", "", "");
        var none = new Source("d", System.Array.Empty<string>(), "2001", "No Author", "", "");
        var d = Document.NewDefault();
        d.Body.Clear();
        d.Sources = new List<Source> { two, three, four, none };
        var p = Para(d, "Normal", null, new Run("Text "));
        p.Runs.Add(ReiksDocument.Run.Object(new Cite("a"), new Run("")));
        var numbers = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["c"] = 3, ["d"] = 4 };
        string In(string style, Source s) { d.CiteStyle = style; return Citations.Text(d, new Cite(s.Id), numbers); }
        string Entry(string style) { d.CiteStyle = style; var refs = Citations.BuildRefs(d); return refs[0].Text + " / " + refs[1].Text; }
        var bad = new List<string>();
        void Same(string what, string got, string want) { if (got != want) bad.Add(what + ": " + got); }
        Same("apa 2", In(Citations.ByName, two), "(Smith & Jones, 2020)");
        Same("apa 3", In(Citations.ByName, three), "(Lee et al., 2019)");
        Same("harvard 2", In(Citations.Harvard, two), "(Smith and Jones 2020)");
        Same("harvard 3", In(Citations.Harvard, three), "(Lee et al. 2019)");
        Same("chicago 3", In(Citations.Chicago, three), "(Lee, Diaz, and Brown 2019)");
        Same("chicago 4", In(Citations.Chicago, four), "(Lee et al. n.d.)");
        Same("mla 2", In(Citations.Mla, two), "(Smith and Jones)");
        Same("mla none", In(Citations.Mla, none), "(No Author)");
        Same("ieee", In(Citations.Ieee, two), "[1]");
        Same("vancouver", In(Citations.Vancouver, three), "[2]");
        Same("apa list", Entry(Citations.ByName), "References / Smith, A. M., & Jones, B. (2020). The Title. Publisher. https://example.org");
        Same("harvard list", Entry(Citations.Harvard), "Reference list / Smith, A.M. and Jones, B. (2020) The Title. Publisher. Available at: https://example.org");
        Same("chicago list", Entry(Citations.Chicago), "References / Smith, Anna Maria, and Bob Jones. 2020. The Title. Publisher. https://example.org");
        Same("mla list", Entry(Citations.Mla), "Works Cited / Smith, Anna Maria, and Bob Jones. The Title. Publisher, 2020. https://example.org");
        Same("ieee list", Entry(Citations.Ieee), "References / [1] A. M. Smith and B. Jones, The Title. Publisher, 2020. [Online]. Available: https://example.org");
        Same("vancouver list", Entry(Citations.Vancouver), "References / 1. Smith AM, Jones B. The Title. Publisher; 2020. Available from: https://example.org");
        d.CiteStyle = Citations.Vancouver;
        bool plain = Citations.BuildRefs(d)[1].Runs.All(r => r.Props.Italic != true);
        d.CiteStyle = Citations.Mla;
        bool slanted = Citations.BuildRefs(d)[1].Runs.Any(r => r.Props.Italic == true && r.Text == "The Title");
        Check("references: citations and the list of references are written as APA, Harvard, Chicago, MLA, IEEE and Vancouver have them; "
            + "a name that is no system counts as APA", bad.Count == 0 && plain && slanted && Citations.Known("nothing") == Citations.ByName && Citations.Known("mla") == Citations.Mla,
            string.Join(" | ", bad));
    }

    static void StyleChecks()
    {
        var d = Document.NewDefault();
        d.Body.Clear();
        Para(d, "Normal", null, new Run(""));
        var eng = new LayoutEngine(d);
        eng.Update();
        var ed = new Editor(d, eng);
        ed.MoveTo(new Pos(0, 0), false);
        ed.TypeText("The first paragraph of the text.");
        ed.Enter();
        ed.TypeText("The second one with some words.");
        ed.Enter();
        ed.TypeText("The third.");
        // Without a selection the style goes to the paragraph of the caret, and to no other.
        ed.SetParaStyle("Heading1");
        bool onlyThat = d.Body[0].Style == "Normal" && d.Body[1].Style == "Normal" && d.Body[2].Style == "Heading1";
        // Words selected inside a paragraph take the look of the style; the paragraph and the words around them stay.
        ed.MoveTo(new Pos(1, 4), false);
        ed.MoveTo(new Pos(1, 14), true);
        ed.SetParaStyle("Heading1");
        var head = d.FindPara("Heading1")!.Char;
        var mid = d.Resolve(d.StyleOf(d.Body[1]), d.Body[1].FormatOfChar(6));
        var rest = d.Resolve(d.StyleOf(d.Body[1]), d.Body[1].FormatOfChar(20));
        var normal = d.FindPara("Normal")!.Char;
        bool words = d.Body[1].Style == "Normal" && d.Body[1].Text == "The second one with some words." && mid.Font == head.Font && mid.Size == head.Size && mid.Color == head.Color
            && rest.Font == normal.Font && rest.Size == normal.Size && rest.Color == normal.Color && d.Body[0].Style == "Normal";
        // The words follow the style: when the heading is made larger and green, so are they.
        bool linked = d.Body[1].FormatOfChar(6).CharStyle == "Heading1" + Editor.LinkedEnd;
        var changed = d.ParaStyles.Select(StylesDialog.Copy).ToList();
        var h1 = changed.First(x => x.Id == "Heading1");
        h1.Char = h1.Char with { Size = 60, Color = 0x008000 };
        ed.SetStyles(changed);
        var after = d.Resolve(d.StyleOf(d.Body[1]), d.Body[1].FormatOfChar(6));
        linked &= after.Size == 60 && after.Color == 0x008000 && d.Resolve(d.StyleOf(d.Body[1]), d.Body[1].FormatOfChar(20)).Size == normal.Size;
        words &= linked;
        var back = d.ParaStyles.Select(StylesDialog.Copy).ToList();
        back.First(x => x.Id == "Heading1").Char = head;
        ed.SetStyles(back);
        ed.MoveTo(new Pos(1, 4), false);
        ed.MoveTo(new Pos(1, 14), true);
        ed.SetParaStyle("Normal");
        bool undone = d.Resolve(d.StyleOf(d.Body[1]), d.Body[1].FormatOfChar(6)).Size == normal.Size;
        // A selection of the whole paragraph, or over several, gives the paragraphs the style.
        ed.MoveTo(new Pos(0, 0), false);
        ed.MoveTo(new Pos(0, d.Body[0].Length), true);
        ed.SetParaStyle("Heading2");
        ed.MoveTo(new Pos(0, 5), false);
        ed.MoveTo(new Pos(1, 3), true);
        ed.SetParaStyle("Heading3");
        bool whole = d.Body[0].Style == "Heading3" && d.Body[1].Style == "Heading3" && d.Body[2].Style == "Heading1";
        Check("styles: without a selection a style goes to the paragraph of the caret alone; words selected inside a paragraph take the look of the style "
            + "and the paragraph keeps its own; a whole paragraph selected, or several, get the style", onlyThat && words && undone && whole,
            $"only {onlyThat} words {words} undone {undone} whole {whole}");

        // Update a style to match the text: the last paragraph, a heading, is underlined and made red by hand; the style takes
        // that look, another heading follows it, what was set by hand is let go, and undo and redo take it all back and forth.
        ed.MoveTo(new Pos(0, 0), false);
        ed.MoveTo(new Pos(0, d.Body[0].Length), true);
        ed.SetParaStyle("Heading1");
        ed.MoveTo(new Pos(2, 0), false);
        ed.MoveTo(new Pos(2, d.Body[2].Length), true);
        ed.ToggleUnderline();
        ed.SetProps(p => p with { Color = 0xC00000 });
        ed.MoveTo(new Pos(2, 3), false);
        var target = ed.MatchTarget();
        ed.MatchStyle();
        var h = d.FindPara("Heading1")!.Char;
        var first = d.Resolve(d.StyleOf(d.Body[0]), d.Body[0].FormatOfChar(3));
        var own = d.Body[2].FormatOfChar(3).Props;
        bool matched = target.Style.Id == "Heading1" && !target.Words && target.Differs && h.Underline && h.Color == 0xC00000 && h.Font == head.Font
            && first.Underline && first.Color == 0xC00000 && own.Underline == null && own.Color == null && !ed.MatchTarget().Differs;
        ed.Undo();
        var firstBack = d.Resolve(d.StyleOf(d.Body[0]), d.Body[0].FormatOfChar(3));
        var thirdBack = d.Resolve(d.StyleOf(d.Body[2]), d.Body[2].FormatOfChar(3));
        bool undoneMatch = d.FindPara("Heading1")!.Char == head && !firstBack.Underline && thirdBack.Underline && thirdBack.Color == 0xC00000;
        ed.Redo();
        undoneMatch &= d.FindPara("Heading1")!.Char.Underline && d.Body[2].FormatOfChar(3).Props.Underline == null;
        // Words that carry the look of a style: the style they follow is the one updated, not that of their paragraph.
        ed.MoveTo(new Pos(1, 4), false);
        ed.MoveTo(new Pos(1, 10), true);
        ed.SetParaStyle("Heading2");
        ed.ToggleBold();
        ed.MoveTo(new Pos(1, 6), false);
        var wordsTarget = ed.MatchTarget();
        ed.MatchStyle();
        bool wordsMatch = wordsTarget.Style.Id == "Heading2" && wordsTarget.Words && d.FindPara("Heading2")!.Char.Bold && !d.FindPara("Heading3")!.Char.Bold
            && d.Body[1].FormatOfChar(6).Props.Bold == null && d.Resolve(d.StyleOf(d.Body[1]), d.Body[1].FormatOfChar(6)).Bold;
        // A change in the box of styles can be undone as well.
        var bigger = d.ParaStyles.Select(StylesDialog.Copy).ToList();
        bigger.First(x => x.Id == "Heading3").Char = bigger.First(x => x.Id == "Heading3").Char with { Size = 70 };
        ed.SetStyles(bigger);
        bool boxUndo = d.FindPara("Heading3")!.Char.Size == 70;
        ed.Undo();
        boxUndo &= d.FindPara("Heading3")!.Char.Size != 70 && d.FindPara("Heading2")!.Char.Bold;
        Check("styles: a style updated to match the text takes its look, other text of the style follows, what was set by hand is let go; "
            + "words carrying a style's look update that style; undo and redo, also for the box of styles", matched && undoneMatch && wordsMatch && boxUndo,
            $"matched {matched} undo {undoneMatch} words {wordsMatch} box {boxUndo}");
    }

    static void CoverChecks()
    {
        // A drawing in the older form of Windows, made here: a red square on a sheet two inches wide and one high.
        byte[] emf;
        using (var ms = new MemoryStream())
        {
            using (var screen = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
            {
                IntPtr hdc = screen.GetHdc();
                using (var meta = new System.Drawing.Imaging.Metafile(ms, hdc, new System.Drawing.RectangleF(0, 0, 5080, 2540), System.Drawing.Imaging.MetafileFrameUnit.GdiCompatible))
                using (var g = System.Drawing.Graphics.FromImage(meta))
                    g.FillRectangle(System.Drawing.Brushes.Red, 10, 10, 30, 30);
                screen.ReleaseHdc(hdc);
            }
            emf = ms.ToArray();
        }
        var drawn = DocxImport.Drawn(emf);
        var shown = drawn == null ? null : PageRenderer.Decode(drawn, out _);
        Check("word: a drawing in the older form of Windows (EMF, WMF) is drawn once and kept as a picture; what is no such drawing is refused",
            emf.Length > 100 && shown != null && shown.PixelWidth > shown.PixelHeight && shown.PixelWidth >= 100 && DocxImport.Drawn(new byte[64]) == null && DocxImport.Drawn(Encoding.UTF8.GetBytes("not a picture at all, only some text of some length")) == null,
            $"emf {emf.Length} bytes, picture {shown?.PixelWidth}x{shown?.PixelHeight}");

        var d = Document.NewDefault();
        Para(d, "Normal", null, new Run("a) f(x) = cos(2x)  \u21D2  f\u2032(x) = \u22122 sin(2x) \u00E5\u00E4\u00F6", new CharProps(Bold: true)));
        var p = d.Body[^1];
        string before = p.Text;
        var own = FontCatalog.Resolve("Calibri", true, false).Face;
        int missing = before.Count(c => c > 0x7E && own.Lookup(c).Glyph == 0);
        int got = FontCatalog.Cover(d, p);
        bool all = p.Runs.All(r => { var f = d.Resolve(d.StyleOf(p), r); var face = FontCatalog.Resolve(f.Font, f.Bold, f.Italic).Face; return r.Text.All(c => c <= 0x7E || face.Lookup(c).Glyph != 0); });
        bool kept = p.Text == before && p.Runs.All(r => r.Props.Bold == true) && p.Runs.Count(r => r.Props.Font != null) == missing && FontCatalog.Cover(d, p) == 0;
        var eng = new LayoutEngine(d);
        eng.Update();
        var ed = new Editor(d, eng);
        ed.MoveTo(new Pos(d.Body.Count - 1, p.Length), false);
        ed.TypeText(" \u21D2");
        ed.TypeText("x");
        var typed = d.Body[^1];
        bool okTyped = typed.Text.EndsWith(" \u21D2x") && typed.FormatOfChar(typed.Length - 2).Props.Font != null && typed.FormatOfChar(typed.Length - 1).Props.Font == null;
        Check("fonts: a character the font of the text does not have gets a font that has it, when a Word file is read and when it is typed; the text around it keeps its font",
            missing >= 1 && got == missing && all && kept && okTyped, $"missing {missing} got {got} all {all} kept {kept} typed {okTyped}");
    }

    static void LatexChecks()
    {
        static string Tree(string line)
        {
            using var ms = new MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(ms)) MathJson.Write(w, MathParser.Parse(line));
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        static Paragraph P(params Run[] runs)
        {
            var p = new Paragraph();
            p.Runs.AddRange(runs);
            return p;
        }

        (string Tex, string Line)[] same =
        {
            (@"V = \frac{4\pi r^{3}}{3}", @"V = (4\pi r^3)/3"),
            (@"\frac{dV}{dt} = 4\pi\cdot 6^{2}\cdot(-0.04) = -5.76\pi \approx -18.1 \text{ dm}^{3}\text{/h}", "(dV)/(dt) = 4\\pi \\cdot 6^2 \\cdot (-0.04) = -5.76\\pi \\approx -18.1 \" dm\"^3 \"/h\""),
            (@"V = \pi\Big[\frac{x^{5}}{5}\Big]_{0}^{2}", @"V = \pi [(x^5)/5]_0^2"),
            (@"2\frac{a}{b}x", @"2 (a)/(b) x"),
            (@"\pi\frac{a}{b}", @"\pi  (a)/(b)"),
            (@"\sqrt{x+1} + \sqrt[3]{y}", @"\sqrt(x+1) + (y)^(1/3) "),
            (@"\lim_{x \to 0} \frac{\sin x}{x} = 1", @"lim_(x \to 0) (sin x)/(x) = 1"),
            (@"\left( x_{i}^{2} \right) \in \mathbb{R}", "(x_i^2 ) \\in \u211D"),
        };
        var bad = new List<string>();
        foreach (var (tex, line) in same)
        {
            var got = Latex.ToLines(tex);
            if (got is not { Count: 1 } || Tree(got[0]) != Tree(line)) bad.Add(tex + " -> " + (got == null ? "null" : string.Join(" | ", got)));
        }
        var rows = Latex.ToLines(@"\begin{aligned} 2x + 3 &= 11 \\ x &= 4 \end{aligned}");
        if (rows is not { Count: 2 } || Tree(rows[0]) != Tree("2x+3=11") || Tree(rows[1]) != Tree("x=4")) bad.Add("rows");
        foreach (string no in new[] { @"\foo{x}", @"\frac{a}{b", @"C:\Users\Public" })
            if (Latex.ToLines(no) != null) bad.Add("read: " + no);
        Check("latex: fractions, roots, powers, limits, brackets, units and rows are read as the line of the equation box; what is not understood is refused",
            bad.Count == 0, string.Join("; ", bad));

        var whole = P(new Run(@"V = \fr", new CharProps(Size: 24)), new Run(@"ac{1}{2}", new CharProps(Bold: true)));
        bool okWhole = Latex.Apply(whole, true) == 1 && whole.Text == "￼"
            && whole.Objects.First().Obj is Equation { Block: true } we && Tree(we.Source) == Tree("V = 1/2");
        var inl = P(new Run(@"The area is $A = \pi r^{2}$ here, and \(x^{2}\) too."));
        bool okInline = Latex.Apply(inl, true) == 2 && inl.Text == "The area is \uFFFC here, and \uFFFC too." && inl.Objects.All(o => o.Obj is Equation { Block: false });
        var cell = P(new Run(@"x = \frac{1}{2}"));
        bool okCell = Latex.Apply(cell, false) == 1 && cell.Objects.First().Obj is Equation { Block: false };
        string[] keep =
        {
            "The ticket costs $5 and the book $12, together $17.", @"The file is in C:\Users\Public\notes.txt here.", @"An unknown \foo{x} stays.",
            @"We get \frac{a}{b} from this.", "x^2 + y^2 = r^2",
        };
        var touched = keep.Where(k => { var p = P(new Run(k)); return Latex.Apply(p, true) != 0 || p.Text != k; }).ToList();
        Check("latex: a line of LaTeX alone becomes an equation on its own line, LaTeX between marks one inside the line; prices, file paths and sentences stay text",
            okWhole && okInline && okCell && touched.Count == 0, $"whole {okWhole} inline {okInline} cell {okCell} touched {string.Join(" | ", touched)}");

        string br = Document.LineBreak.ToString();
        var set = P(new Run(@"\begin{aligned}" + br + @"y' &= 4x^{3} - 15x^{2} \\" + br + @"y'' &= 12x^{2} - 30x" + br + @"\end{aligned}"));
        bool okSet = Latex.Apply(set, true) == 2 && set.Text == "\uFFFC\uFFFC" && set.Objects.All(o => o.Obj is Equation { Block: true })
            && set.Objects.Last().Obj is Equation se && Tree(se.Source) == Tree("y\u2032\u2032 = 12x^2 - 30x");
        var marks = P(new Run(@"\[" + br + @"a^{2} + b^{2} = c^{2}" + br + @"\]"));
        bool okMarks = Latex.Apply(marks, true) == 1 && !marks.Text.Contains('\\');
        var list = P(new Run(@"\begin{itemize}" + br + @"\item one" + br + @"\end{itemize}"));
        bool okList = Latex.Apply(list, true) == 0 && list.Text.Split(Document.LineBreak).Length == 3;
        var d2 = Document.NewDefault();
        Para(d2, "Normal", null, new Run("Start"));
        var eng2 = new LayoutEngine(d2);
        eng2.Update();
        var ed2 = new Editor(d2, eng2);
        ed2.MoveTo(new Pos(d2.Body.Count - 1, 5), false);
        ed2.ToggleLatex();
        ed2.Paste("\n\\begin{aligned}\ny' &= 4x^{3} \\\\\ny'' &= 12x^{2}\n\\end{aligned}\n\\begin{itemize}\n\\item one\n\\end{itemize}");
        string setPasted = string.Join("|", d2.Body.Select(p => p.Text));
        bool okPaste = d2.Body.Sum(p => p.Objects.Count(o => o.Obj is Equation)) == 2 && !setPasted.Contains("aligned")
            && setPasted.EndsWith(@"|\begin{itemize}|\item one|\end{itemize}");
        Check("latex: rows between \\begin and \\end on lines of their own are read together, in a paragraph and in setPasted text; lines of another kind stay as they are",
            okSet && okMarks && okList && okPaste, $"set {okSet} marks {okMarks} list {okList} paste {okPaste}: {setPasted}");

        var d = Document.NewDefault();
        Para(d, "Normal", null, new Run("Start"));
        var eng = new LayoutEngine(d);
        eng.Update();
        var ed = new Editor(d, eng);
        int last = d.Body.Count - 1;
        ed.MoveTo(new Pos(last, 5), false);
        ed.ToggleLatex();
        ed.Paste("\n$$E = mc^{2}$$\nend");
        bool pasted = d.Body.Count == last + 3 && d.Body[last].Text == "Start" && d.Body[last + 1].Objects.Count(o => o.Obj is Equation { Block: true }) == 1 && d.Body[last + 2].Text == "end";
        ed.Undo();
        bool undone = d.Body.Count == last + 1 && d.Body[last].Text == "Start";
        var full = LatexDoc();
        int eqs = full.Body.Sum(p => p.Objects.Count(o => o.Obj is Equation));
        // Without the LaTeX mode, pasted LaTeX stays the text it is.
        var pd = Document.NewDefault();
        pd.Body.Clear();
        Para(pd, "Normal", null, new Run("Start"));
        var peng = new LayoutEngine(pd);
        peng.Update();
        var ped = new Editor(pd, peng);
        ped.MoveTo(new Pos(0, 5), false);
        ped.Paste("\n" + @"V = \frac{1}{2}" + "\n$$E = mc^{2}$$");
        Check("latex mode: without the mode, pasted LaTeX stays as text", !ped.LatexMode && pd.Body.Count == 3 && pd.Body[1].Text == @"V = \frac{1}{2}" && pd.Body[2].Text == "$$E = mc^{2}$$"
            && pd.Body.All(q => q.Objects.Length == 0), string.Join("|", pd.Body.Select(q => q.Text)));
        Check("latex: text pasted while the LaTeX mode is on has its LaTeX made into equations, in one step for undo",
            pasted && undone && eqs == 5 && full.Body[^1].Text == "The ticket costs $5 and the book $12.", $"pasted {pasted} undone {undone} equations {eqs}");

        // The LaTeX mode: typed text stands in the LaTeX style until Enter, or the end of the mode, makes an equation of it.
        var md = Document.NewDefault();
        md.Body.Clear();
        Para(md, "Normal", null, new Run("Start"));
        var meng = new LayoutEngine(md);
        meng.Update();
        var med = new Editor(md, meng);
        med.MoveTo(new Pos(0, 5), false);
        med.Enter();
        // Switching the mode tells the window, so that the button shows it at once, also after the keys of the shortcut.
        int told = 0;
        med.SelectionChanged += () => told++;
        med.ToggleLatex();
        Check("latex mode: switching the mode on is made known at once, so that its button shows it; its keys are Ctrl+Shift+E",
            told >= 1 && med.LatexMode && Shortcuts.Find("Ctrl+Shift+E", false) == "fmt.latex", $"told {told}");
        foreach (char c in @"V = \frac{4\pi r^{3}}{3}") med.TypeText(c.ToString());
        bool typedLook = med.LatexMode && md.Body[1].Text == @"V = \frac{4\pi r^{3}}{3}" && md.Body[1].Runs.All(r => r.CharStyle == Editor.LatexStyle) && md.FindChar(Editor.LatexStyle) != null;
        med.Enter();
        // Enter made the equation and left the caret right behind it; a second Enter begins the next paragraph.
        bool onEnter = md.Body.Count == 2 && md.Body[1].Text == "\uFFFC" && md.Body[1].Objects.First().Obj is Equation { Block: true } me1 && Tree(me1.Source) == Tree(@"V = (4\pi r^3)/3")
            && med.Caret == new Pos(1, 1) && med.LatexMode;
        med.Enter();
        onEnter &= md.Body.Count == 3 && md.Body[2].Length == 0 && med.Caret == new Pos(2, 0) && med.LatexMode;
        med.TypeText(@"\foo{x}");
        med.Enter();
        bool refused = md.Body[2].Text == @"\foo{x}" && md.Body[2].Runs.All(r => r.CharStyle == Editor.LatexStyle) && md.Body.Count == 4;
        med.ToggleLatex();
        med.TypeText("The area is ");
        bool plain = !med.LatexMode && md.Body[3].Runs.All(r => r.CharStyle == null);
        med.ToggleLatex();
        med.TypeText(@"A = \pi r^{2}");
        med.ToggleLatex();
        bool inLine = !med.LatexMode && md.Body[3].Text == "The area is \uFFFC" && md.Body[3].Objects.First().Obj is Equation { Block: false } me2 && Tree(me2.Source) == Tree(@"A = \pi r^2")
            && med.Caret == new Pos(3, md.Body[3].Length);
        med.Undo();
        bool typedBack = md.Body[3].Text == @"The area is A = \pi r^{2}";
        med.Redo();
        Check("latex mode: what is typed while the mode is on stands in the LaTeX style; Enter makes an equation on its own line of a line that is nothing else, "
            + "switching the mode off one inside the line; what is not understood stays as typed; one undo gives the typed text back",
            typedLook && onEnter && refused && plain && inLine && typedBack, $"look {typedLook} enter {onEnter} refused {refused} plain {plain} inline {inLine} undone {typedBack}");

        // Where the equation stands can be chosen: in the line although it is alone, on a line of its own although it is in a sentence.
        var fd = Document.NewDefault();
        fd.Body.Clear();
        Para(fd, "Normal", null, new Run(""));
        var feng = new LayoutEngine(fd);
        feng.Update();
        var fed = new Editor(fd, feng);
        fed.MoveTo(new Pos(0, 0), false);
        fed.ToggleLatex();
        Editor.LatexForm = 1;
        fed.TypeText(@"x^{2}");
        fed.Enter();
        bool inLineAlone = fd.Body.Count == 1 && fd.Body[0].Objects.First().Obj is Equation { Block: false } && fed.Caret == new Pos(0, 1);
        // The line can be written on behind the equation.
        fed.ToggleLatex();
        fed.TypeText(" is a square");
        inLineAlone &= fd.Body[0].Text == "\uFFFC is a square" && fd.Body[0].Runs.Where(r => r.Obj == null).All(r => r.CharStyle == null);
        fed.ToggleLatex();
        fed.Enter();
        Editor.LatexForm = 2;
        fed.ToggleLatex();
        fed.TypeText("So ");
        fed.ToggleLatex();
        fed.TypeText(@"y^{2}");
        fed.Enter();
        bool ownLineInText = fd.Body[1].Text == "So \uFFFC" && fd.Body[1].Objects.First().Obj is Equation { Block: true };
        fed.Enter();
        fed.Paste(@"A = \pi r^{2} is $B = 2$ here.");
        bool pastedOwn = fd.Body[2].Objects.Length == 1 && fd.Body[2].Objects.All(o => o.Obj is Equation { Block: true });
        Editor.LatexForm = 0;
        Check("latex mode: the menu beside the button chooses where the equations stand: in the line also when the LaTeX is alone on its line, "
            + "or on a line of its own also inside a sentence, typed and pasted alike", inLineAlone && ownLineInText && pastedOwn, $"alone {inLineAlone} in text {ownLineInText} pasted {pastedOwn}");

        var sd = Document.NewDefault();
        sd.Body.Clear();
        Para(sd, "Normal", null, new Run(@"We get \frac{a}{b} from this."));
        var seng = new LayoutEngine(sd);
        seng.Update();
        var sed = new Editor(sd, seng);
        sed.MoveTo(new Pos(0, 7), false);
        sed.MoveTo(new Pos(0, 18), true);
        sed.ToggleLatex();
        Check("latex mode: with text selected, the button makes an equation of the selected LaTeX and does not switch the mode on",
            !sed.LatexMode && sd.Body[0].Text == "We get \uFFFC from this." && sd.Body[0].Objects.First().Obj is Equation { Block: false } se2 && Tree(se2.Source) == Tree("(a)/(b)"),
            sd.Body[0].Text);
    }

    static void Sample(string name, Document doc, string outDir, int pngPages)
    {
        var sw = Stopwatch.StartNew();
        var a = new LayoutEngine(doc);
        a.Update();
        long ms = sw.ElapsedMilliseconds;
        int lineCount = a.Pages.Sum(p => p.Lines.Count);
        log.Add($"      {name}: {doc.Body.Count} paragraphs, {a.Pages.Count} pages, {lineCount} lines, laid out in {ms} ms");
        Check(name + ": layout is well formed", Invariants(a) == null, Invariants(a) ?? "");

        var b = Fresh(doc);
        Check(name + ": second layout equals the first", Diff(a, b) == null, Diff(a, b) ?? "");

        string file = Path.Combine(outDir, name + ".docu");
        RksFile.Save(doc, a, file);
        var opened = RksFile.Open(file);
        Check(name + ": reopened file shows the stored layout", opened.Stored, opened.Message);
        Check(name + ": stored layout equals the first", Diff(a, opened.Engine) == null, Diff(a, opened.Engine) ?? "");
        Check(name + ": the layout written again is the same file, byte for byte",
            RksFile.LayoutJson(doc, a).AsSpan().SequenceEqual(RksFile.LayoutJson(opened.Doc, opened.Engine)));
        var again = Fresh(opened.Doc);
        Check(name + ": layout of the reopened content equals the first", Diff(a, again) == null, Diff(a, again) ?? "");
        Check(name + ": content is unchanged by save and reopen",
            RksFile.ContentJson(doc).AsSpan().SequenceEqual(RksFile.ContentJson(opened.Doc)));

        var names = doc.Body.SelectMany(p => p.Objects).Select(o => o.Obj).OfType<Picture>().Select(p => p.Media).Distinct().ToList();
        if (names.Count > 0)
            Check(name + ": the " + names.Count + " pictures come back unchanged",
                names.All(n => opened.Doc.Media.TryGetValue(n, out var d) && d.AsSpan().SequenceEqual(doc.Media[n])));

        // A file whose font fingerprint differs must be laid out again.
        string tampered = Path.Combine(outDir, name + "-otherfont.docu");
        File.Copy(file, tampered, true);
        using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            string json;
            using (var rd = new StreamReader(entry.Open())) json = rd.ReadToEnd();
            int at = json.IndexOf("\"sha256\": \"", StringComparison.Ordinal) + 11;
            json = json[..at] + new string('0', 64) + json[(at + 64)..];
            entry.Delete();
            using var wr = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            wr.Write(json);
        }
        var other = RksFile.Open(tampered);
        Check(name + ": a different font fingerprint is reported and laid out again", !other.Stored && other.Message.Contains("differs"), other.Message);
        Check(name + ": that new layout equals the first", Diff(a, other.Engine) == null, Diff(a, other.Engine) ?? "");
        File.Delete(tampered);

        for (int pg = 0; pg < Math.Min(pngPages, a.Pages.Count); pg++)
            PageRenderer.SavePng(PageRenderer.RenderPage(a, pg, 1.0), Path.Combine(outDir, $"{name}-p{pg + 1}.png"));
    }

    static void Edits(string outDir)
    {
        var doc = Basic();
        var eng = new LayoutEngine(doc);
        eng.Update();
        var ed = new Editor(doc, eng);
        var r = new Rng(99);
        byte[] initial = RksFile.ContentJson(doc);
        string[] styles = { "Normal", "Heading1", "Heading2", "Title" };
        int ops = 0, bad = 0;
        string firstBad = "";

        byte[] png = MakePicture(120, 80, false), jpg = MakePicture(200, 120, true);
        string pngName = RksFile.MediaName(png, false), jpgName = RksFile.MediaName(jpg, true);
        doc.Media[pngName] = png;
        doc.Media[jpgName] = jpg;
        int notesMade = 0, picsMade = 0, objsMade = 0;
        var charts = ChartSamples();

        string[] wraps = { "inline", "topbottom", "square", "behind", "front" };
        int floatsMoved = 0, tablesMade = 0;
        List<Pos> Pics(bool floating)
        {
            var list = new List<Pos>();
            for (int pi = 0; pi < doc.Body.Count; pi++)
                foreach (var (off, o) in doc.Body[pi].Objects)
                    if (o is Picture p && (!floating || p.Floating)) list.Add(new Pos(pi, off));
            return list;
        }

        Pos Rand()
        {
            var paras = eng.Paras(ed.Story);
            int para = r.Next(paras.Count);
            return new Pos(para, r.Next(paras[para].Length + 1));
        }
        void Place(bool body)
        {
            ed.Story = 0;
            if (!body && r.Next(4) == 0)
            {
                // Now and then in a footnote or in a table cell.
                var cells = doc.CellsInOrder().ToList();
                if (cells.Count > 0 && r.Next(2) == 0) ed.Story = -cells[r.Next(cells.Count)].Id;
                else if (eng.NoteOrder.Count > 0) ed.Story = eng.NoteOrder[r.Next(eng.NoteOrder.Count)].Id;
            }
            ed.Caret = Rand();
            ed.Anchor = !body && r.Next(3) == 0 ? Near() : ed.Caret;
        }
        Pos Near()
        {
            var paras = eng.Paras(ed.Story);
            int para = Math.Min(paras.Count - 1, ed.Caret.Para + r.Next(3));
            return new Pos(para, r.Next(paras[para].Length + 1));
        }

        for (int i = 0; i < 1100; i++)
        {
            Place(false);
            int op = i % 8 == 5 ? 200 + r.Next(7) : r.Next(100);
            if (op == 200) ed.ToggleList(r.Next(2) == 0 ? "bullet" : "number");
            else if (op == 201)
            {
                // A picture starts to float, or changes how it floats.
                var pics = Pics(false);
                if (pics.Count > 0)
                {
                    var at = pics[r.Next(pics.Count)];
                    string wrap = wraps[r.Next(wraps.Length)];
                    int dx = r.Next(4500000), dy = r.Next(700000) - 100000;
                    ed.Story = 0;
                    ed.Anchor = at;
                    ed.Caret = new Pos(at.Para, at.Off + 1);
                    ed.ChangeObject(o => o is Picture p ? p with { Wrap = wrap, Block = false, Dx = wrap == "inline" ? 0 : dx, Dy = wrap == "inline" ? 0 : dy } : o);
                }
            }
            else if (op == 202)
            {
                var pics = Pics(true);
                if (pics.Count > 0)
                {
                    ed.Story = 0;
                    ed.Caret = ed.Anchor = new Pos(0, 0);
                    ed.MoveFloat(pics[r.Next(pics.Count)], Rand(), r.Next(4500000), r.Next(900000));
                    floatsMoved++;
                }
            }
            else if (op == 203)
            {
                Place(true);
                ed.InsertObject(new Picture(pngName, (60 + r.Next(260)) * 9525, (40 + r.Next(200)) * 9525, false, wraps[1 + r.Next(4)], r.Next(4500000), r.Next(500000)));
                picsMade++;
            }
            else if (op == 204)
            {
                Place(true);
                if (ed.InsertTable(1 + r.Next(3), 1 + r.Next(4)))
                {
                    ed.TypeText(Text(r, 1 + r.Next(6)));
                    ed.NextCell(1);
                    ed.TypeText(Text(r, 3));
                    tablesMade++;
                }
            }
            else if (op == 205)
            {
                var cells = doc.CellsInOrder().ToList();
                if (cells.Count > 0)
                {
                    ed.Story = -cells[r.Next(cells.Count)].Id;
                    ed.Caret = ed.Anchor = new Pos(0, 0);
                    switch (r.Next(6))
                    {
                        case 0: ed.InsertRow(r.Next(2) == 0); break;
                        case 1: ed.InsertColumn(); break;
                        case 2: ed.DeleteRow(); break;
                        case 3: ed.DeleteColumn(); break;
                        case 4: ed.NextCell(r.Next(2) == 0 ? 1 : -1); break;
                        default: ed.TypeText(Text(r, 2 + r.Next(20))); break;
                    }
                }
            }
            else if (op == 206)
            {
                var tables = Enumerable.Range(0, doc.Body.Count).Where(k => doc.Body[k].Table != null).ToList();
                if (tables.Count > 0)
                {
                    int tp = tables[r.Next(tables.Count)];
                    ed.MoveColumnEdge(tp, r.Next(doc.Body[tp].Table!.Columns), r.Next(1200000) - 600000);
                }
            }
            else if (op < 19) ed.TypeText(Text(r, 1 + r.Next(4)) + " ");
            else if (op < 22)
            {
                Place(true);
                ed.InsertObject(Equation.From(Equations[r.Next(Equations.Length)], r.Next(3) == 0));
                objsMade++;
            }
            else if (op < 24)
            {
                Place(true);
                ed.InsertObject(charts[r.Next(charts.Length)] with { Block = r.Next(2) == 0 });
                objsMade++;
            }
            else if (op < 29)
            {
                Place(true);
                if (ed.InsertFootnote()) { ed.TypeText(Text(r, 2 + r.Next(30))); notesMade++; }
            }
            else if (op < 32)
            {
                Place(true);
                ed.InsertObject(new Picture(r.Next(2) == 0 ? jpgName : pngName, (40 + r.Next(300)) * 9525, (30 + r.Next(200)) * 9525, r.Next(3) == 0));
                picsMade++;
            }
            else if (op < 35)
            {
                var pics = new List<Pos>();
                for (int pi = 0; pi < doc.Body.Count; pi++)
                    foreach (var (off, o) in doc.Body[pi].Objects)
                        if (o is Picture) pics.Add(new Pos(pi, off));
                if (pics.Count > 0)
                {
                    var at = pics[r.Next(pics.Count)];
                    int w = (30 + r.Next(400)) * 9525;
                    ed.Story = 0;
                    ed.Anchor = at;
                    ed.Caret = new Pos(at.Para, at.Off + 1);
                    ed.ChangeObject(o => o is Picture p ? p with { Width = w, Height = w * 2 / 3, Block = !p.Floating && !p.Block } : o);
                }
            }
            else if (op < 40) ed.Enter(r.Next(8) == 0);
            else if (op < 49) ed.Backspace(r.Next(4) == 0);
            else if (op < 58) ed.Delete(r.Next(4) == 0);
            else if (op < 63) ed.ToggleBold();
            else if (op < 64) ed.ToggleItalic();
            else if (op < 65) ed.ToggleStrike();
            else if (op < 66) ed.SetVert(1 + r.Next(2));
            else if (op < 70) { int size = 14 + 2 * r.Next(20); ed.SetProps(p => p with { Size = size }); }
            else if (op < 74) { string f = Fonts[r.Next(Fonts.Length)]; ed.SetProps(p => p with { Font = f }); }
            else if (op < 78) ed.SetParaStyle(styles[r.Next(styles.Length)]);
            else if (op < 80) ed.SetAlign((Align)r.Next(4));
            else if (op < 81) ed.SetLine(1000 + 250 * r.Next(5));
            else if (op < 82) ed.ChangeIndent(r.Next(3) == 0 ? -457200 : 457200);
            else if (op < 86) ed.Paste(Text(r, 5) + "\r\n" + Text(r, 8) + "\r\n" + Text(r, 3));
            else if (op < 89) { ed.Anchor = Near(); if (ed.Copy() is { } t) { Place(false); ed.Paste(t); } }
            else if (op < 90) ed.InsertLineBreak();
            else if (op < 91)
            {
                int which = r.Next(4);
                if (which == 0) ed.ClearFormat();
                else if (which == 1) { ed.CopyFormat(false); Place(false); ed.PasteFormat(); }
                else if (which == 2) ed.ReplaceAll("the", "THE", r.Next(2) == 0);
                else ed.SetProps(p => p with { Highlight = 0xFFFF00, Color = 0xC00000 });
            }
            else if (op < 96) ed.Undo();
            else ed.Redo();
            ops++;
            string? d = Invariants(eng) ?? Diff(eng, Fresh(doc));
            if (d != null && bad++ == 0) firstBad = $"after operation {i} (kind {op}): {d}";
        }
        Check("edits: after each of " + ops + " operations the layout equals a layout from scratch", bad == 0, firstBad);
        log.Add($"      edits: {notesMade} footnotes, {picsMade} pictures, {objsMade} equations and charts and {tablesMade} tables were put in; the last document has {eng.NoteOrder.Count} footnotes on {eng.Pages.Count} pages");

        byte[] final = RksFile.ContentJson(doc);
        int undone = 0;
        while (ed.CanUndo) { ed.Undo(); undone++; }
        Check("edits: undoing everything gives back the first document", RksFile.ContentJson(doc).AsSpan().SequenceEqual(initial), undone + " steps");
        while (ed.CanRedo) ed.Redo();
        Check("edits: redoing everything gives back the last document", RksFile.ContentJson(doc).AsSpan().SequenceEqual(final));
        Check("edits: layout after undo and redo equals a layout from scratch", Diff(eng, Fresh(doc)) == null, Diff(eng, Fresh(doc)) ?? "");
        RksFile.Save(doc, eng, Path.Combine(outDir, "edited.docu"));
    }

    static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>Opens the real window off screen, types into it and saves a picture of what it draws.</summary>
    static void WindowCheck(string outDir)
    {
        // Recovery copies go to a folder of the test, which starts empty.
        Recovery.Folder = Path.Combine(outDir, "recovery");
        Directory.CreateDirectory(Recovery.Folder);
        foreach (string old in Directory.GetFiles(Recovery.Folder)) File.Delete(old);
        // The choices go to a file of the test, never to the user's own.
        string own = Settings.FilePath;
        Settings.FilePath = Path.Combine(outDir, "settings.json");
        Settings.Metric = false; Settings.Colors = "custom"; Settings.AllowCapture = true; Settings.WindowColor = 0xD3E4CD; Settings.PaperColor = 0xF3EBD6;
        Settings.Save();
        Settings.Metric = true; Settings.Colors = "system"; Settings.AllowCapture = false; Settings.WindowColor = 0; Settings.PaperColor = 0;
        Settings.Load();
        Check("preferences: kept in a file and read back",
            !Settings.Metric && Settings.Colors == "custom" && Settings.AllowCapture && Settings.WindowColor == 0xD3E4CD && Settings.PaperColor == 0xF3EBD6, File.ReadAllText(Settings.FilePath).Trim());
        Settings.Metric = true; Settings.Colors = "light"; Settings.AllowCapture = false; Settings.WindowColor = 0xDCDCD8; Settings.PaperColor = 0xFFFFFF;
        Settings.FilePath = own;
        if (Application.Current == null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply();

        var win = new MainWindow(Path.Combine(outDir, "basic.docu"))
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000, Top = -30000, Width = 1120, Height = 720,
        };
        win.Show();
        Idle();
        var ed = win.Editor;
        int pages = ed.Layout.Pages.Count;
        ed.MoveTo(new Pos(2, 10), false);
        ed.TypeText("typed ");
        ed.Select(new Pos(2, 4), new Pos(3, 60));
        ed.ToggleBold();
        ed.MoveTo(new Pos(1, 5), false);
        ed.InsertFootnote();
        ed.TypeText("A note typed in the window.");
        bool inNote = ed.Story != 0;
        ed.LeaveNote();
        bool added = win.AddPicture(MakePicture(160, 100, false));
        Idle();
        PageRenderer.SavePng(PageRenderer.RenderPage(ed.Layout, 0, 1.0), Path.Combine(outDir, "window-p1.png"));
        bool picSelected = ed.SelectedBox() != null;
        ed.MoveTo(new Pos(3, 0), false);
        ed.InsertObject(Equation.From(Equations[9], false));
        ed.InsertObject(ChartSamples()[3]);
        Idle();
        var span = ed.Layout.StyleSpan(ed.Doc.ParaStyles[0]);
        ShootDialog(new EquationDialog(null, Equations[8], span), Path.Combine(outDir, "dialog-equation.png"));
        ShootDialog(new ChartDialog(null, ChartSamples()[1], span), Path.Combine(outDir, "dialog-chart.png"));
        Check("window: an equation and a chart go in", ed.Doc.Body[3].Objects.Length == 2);
        Check("window: a footnote and a picture go in", inNote && added && ed.Story == 0 && picSelected && ed.Layout.Pages[0].Notes.Count == 1);
        ShootDialog(new SourcesDialog(null, new List<Source>(), Citations.ByName), Path.Combine(outDir, "dialog-sources.png"));
        var root = (FrameworkElement)win.Content;
        static IEnumerable<DependencyObject> Visuals(DependencyObject o)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
            {
                var c = VisualTreeHelper.GetChild(o, i);
                yield return c;
                foreach (var d in Visuals(c)) yield return d;
            }
        }
        var sizeText = Visuals(root).OfType<System.Windows.Controls.ComboBox>().FirstOrDefault(c => c.ToolTip is string tip && tip.StartsWith("Font size")) is { } sizeCombo
            ? Visuals(sizeCombo).OfType<System.Windows.Controls.TextBox>().FirstOrDefault() : null;
        double sizeWide = sizeText == null ? 0 : new FormattedText("100.5", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(sizeText.FontFamily, sizeText.FontStyle, sizeText.FontWeight, sizeText.FontStretch), sizeText.FontSize, Brushes.Black, 1.0).Width;
        // The list of styles shows each style in its own look, and follows a style that is changed.
        var styleList = Visuals(root).OfType<System.Windows.Controls.ComboBox>().FirstOrDefault(b => b.ToolTip is string aboutStyles && aboutStyles.StartsWith("Paragraph style"));
        System.Windows.Controls.TextBlock? Entry(string id) => styleList?.Items.OfType<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == id)?.Content as System.Windows.Controls.TextBlock;
        var wasStyles = ed.Doc.ParaStyles.Select(StylesDialog.Copy).ToList();
        bool shownAs = Entry("Heading1") is { } e1 && e1.Foreground is SolidColorBrush b1 && b1.Color == Color.FromRgb(0x2F, 0x54, 0x96) && Entry("Normal") is { } e0 && e0.FontSize < e1.FontSize;
        var newStyles = ed.Doc.ParaStyles.Select(StylesDialog.Copy).ToList();
        var newHead = newStyles.First(x => x.Id == "Heading1");
        newHead.Char = newHead.Char with { Color = 0x008000, Bold = true, Italic = true, Underline = true, Font = "Georgia" };
        ed.SetStyles(newStyles);
        bool listFollows = Entry("Heading1") is { } e2 && e2.Foreground is SolidColorBrush b2 && b2.Color == Color.FromRgb(0, 0x80, 0) && e2.FontWeight == FontWeights.Bold && e2.FontStyle == FontStyles.Italic
            && e2.TextDecorations.Count > 0 && e2.FontFamily.Source == "Georgia";
        // A picture of the open list, to be looked at.
        if (styleList != null)
        {
            styleList.IsDropDownOpen = true;
            Idle();
            if (styleList.Template.FindName("PART_Popup", styleList) is System.Windows.Controls.Primitives.Popup { Child: FrameworkElement open } && open.ActualWidth > 0)
            {
                var shot = new RenderTargetBitmap((int)open.ActualWidth, (int)open.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                shot.Render(open);
                PageRenderer.SavePng(shot, Path.Combine(outDir, "list-styles.png"));
            }
            styleList.IsDropDownOpen = false;
            Idle();
        }
        ed.SetStyles(wasStyles);
        Check("window: the list of styles shows each style in its own font, weight, slant, underline and colour, and shows a changed style as it is now",
            shownAs && listFollows && Entry("Heading1") is { } e3 && e3.FontFamily.Source != "Georgia", $"shown {shownAs} follows {listFollows}");

        // The spelling button at the foot of the window says which language is checked.
        var spellButton = Visuals(root).OfType<System.Windows.Controls.Button>().FirstOrDefault(b => b.ToolTip is string aboutSpell && aboutSpell.StartsWith("Spelling:"));
        // The words to check are counted in the background: the window is given the time for it, so that its foot is seen as in use.
        string SpellText() => spellButton == null ? "" : Visuals(spellButton).OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text).FirstOrDefault(t => t.Length > 0) ?? "";
        var patience = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < patience && Spell.Language.Length > 0 && !SpellText().Contains("Spelling:"))
        {
            Idle();
            System.Threading.Thread.Sleep(15);
        }
        string spellSays = SpellText();
        var spellArrows = Visuals(root).OfType<System.Windows.Controls.Button>().Where(b => b.ToolTip is string tipArrow && tipArrow.Contains("word to check")).ToList();
        bool counted = spellSays.Contains("Spelling:");
        Check("window: two small arrows beside the spelling button lead to the word to check before and after; they are there while there are such words",
            spellArrows.Count == 2 && spellArrows.All(b => b.Visibility == Visibility.Visible == counted), $"{spellArrows.Count} arrows, counted {counted}: {spellSays}");
        // The number follows the typing: a word that no language knows is typed into the window, and after the pause it is counted.
        static int Counted(string says)
        {
            int at = says.IndexOf("Spelling:", StringComparison.Ordinal);
            return at < 0 ? 0 : int.Parse(new string(says[(at + 9)..].Where(char.IsDigit).ToArray()));
        }
        // First the count is let come to rest: earlier steps of this test changed the text, and their count may still be on its way.
        var rest = DateTime.UtcNow.AddMilliseconds(1200);
        while (DateTime.UtcNow < rest)
        {
            Idle();
            System.Threading.Thread.Sleep(15);
        }
        int hadUnknown = Counted(SpellText());
        ed.MoveTo(new Pos(0, 0), false, story: 0);
        ed.TypeText(" qwzxvkj ");
        patience = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < patience && Spell.Language.Length > 0 && Counted(SpellText()) == hadUnknown)
        {
            Idle();
            System.Threading.Thread.Sleep(15);
        }
        int nowUnknown = Counted(SpellText());
        ed.Undo();
        Check("window: the number of words to check follows the typing, a moment after it pauses", Spell.Language.Length == 0 || nowUnknown == hadUnknown + 1, $"{hadUnknown} -> {nowUnknown}");
        string spellWant = Settings.Spelling == "off" ? "Spelling: off" : Spell.Language.Length == 0 ? "Spelling: no dictionary" : Spell.NameOf(Spell.Language);
        Check("window: the spelling button at the foot of the window names the language that is checked", spellButton != null && spellSays.StartsWith(spellWant), spellSays);

        // How many words the check does not know, in the text, in a table cell and in a footnote alike; none when the check is off.
        string keptSpelling = Settings.Spelling;
        if (Spell.Languages().Contains("en-US"))
        {
            Settings.Spelling = "en-US";
            var sp = Document.NewDefault();
            sp.Body.Clear();
            Para(sp, "Normal", null, new Run("This is a tezt with two mistaks, and the rest is right."));
            Para(sp, "Normal", null, new Run("Nothing wrong here."));
            int inText = Spell.Count(sp);
            Para(sp, "Normal", null, new Run("One wrnog wurd."));
            int more = Spell.Count(sp);
            // From a place, the next word to check and the one before; past the end it goes on at the beginning, and back.
            var onward = Spell.Step(sp.Body, new Pos(0, 0), new Pos(0, 0), 1);
            var second = onward is { } w1 ? Spell.Step(sp.Body, new Pos(0, w1.Start), new Pos(0, w1.Start + w1.Length), 1) : null;
            var round = Spell.Step(sp.Body, new Pos(2, 15), new Pos(2, 15), 1);
            var before = Spell.Step(sp.Body, new Pos(2, 0), new Pos(2, 0), -1);
            var backRound = Spell.Step(sp.Body, new Pos(0, 0), new Pos(0, 0), -1);
            bool steps = onward is { Para: 0 } o1 && sp.Body[0].Text.Substring(o1.Start, o1.Length) == "tezt"
                && second is { Para: 0 } o2 && sp.Body[0].Text.Substring(o2.Start, o2.Length) == "mistaks"
                && round is { Para: 0 } o3 && sp.Body[0].Text.Substring(o3.Start, o3.Length) == "tezt"
                && before is { Para: 0 } o4 && sp.Body[0].Text.Substring(o4.Start, o4.Length) == "mistaks"
                && backRound is { Para: 2 } o5 && sp.Body[2].Text.Substring(o5.Start, o5.Length) == "wurd";
            Check("spelling: from a place the next word to check is found and the one before it, going round at the ends of the text", steps,
                $"{onward} {second} {round} {before} {backRound}");
            Settings.Spelling = "off";
            int off = Spell.Count(sp);
            Settings.Spelling = keptSpelling;
            Check("spelling: the words the check does not know are counted over the whole document; none when the check is off; the language has a name",
                inText == 2 && more == 4 && off == 0 && Spell.NameOf("en-US") == "English (United States)" && Spell.NameOf("es-ES") == "Spanish (Spain)", $"{inText} {more} {off}");
        }
        else Check("spelling: the words the check does not know are counted (no English dictionary on this PC: not tried)", true);

        // The LaTeX button shows the mode the moment it is switched, whichever way: the keys of the shortcut run the same command.
        var latexButton = Visuals(root).OfType<System.Windows.Controls.Primitives.ToggleButton>().FirstOrDefault(b => b.ToolTip is string about && about.StartsWith("LaTeX"));
        ed.MoveTo(new Pos(0, 0), false);
        bool dark = latexButton is { IsChecked: false };
        ed.ToggleLatex();
        bool lit = latexButton is { IsChecked: true };
        ed.ToggleLatex();
        Check("window: the LaTeX button shows the mode as soon as it is switched on, and off again", dark && lit && latexButton is { IsChecked: false }, $"button {latexButton != null} off {dark} on {lit}");
        Check("window: the font size box shows a size with a half point, such as 10.5, whole", sizeText != null && sizeText.ViewportWidth >= sizeWide,
            $"room {sizeText?.ViewportWidth:0.#} text {sizeWide:0.#}");
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        PageRenderer.SavePng(bmp, Path.Combine(outDir, "window.png"));
        Check("window: opens a file, takes typing and formatting, and draws", pages == 4 && win.Title.StartsWith("basic.docu*") && root.ActualWidth > 500, win.Title);

        // Screenshots: left out unless allowed, and the choice reaches the open window.
        bool wasOut = ScreenCapture.IsExcluded(win);
        Settings.AllowCapture = true;
        ScreenCapture.Apply(win);
        bool nowIn = !ScreenCapture.IsExcluded(win);
        Settings.AllowCapture = false;
        ScreenCapture.Apply(win);
        Check("preferences: the window is out of screenshots unless they are allowed", wasOut && nowIn && ScreenCapture.IsExcluded(win), $"{wasOut} {nowIn}");

        // Colours: dark and back, with the window open.
        var bars = (FrameworkElement)((System.Windows.Controls.Panel)root).Children[0];
        double barsLight = bars.ActualHeight;
        int deskLight = Brightness(bmp, 3, bmp.PixelHeight / 2);
        Settings.Colors = "dark";
        Theme.Apply();
        Idle();
        var darkBmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        darkBmp.Render(root);
        PageRenderer.SavePng(darkBmp, Path.Combine(outDir, "window-dark.png"));
        int deskDark = Brightness(darkBmp, 3, darkBmp.PixelHeight / 2), pageDark = Brightness(darkBmp, darkBmp.PixelWidth / 2 - 200, darkBmp.PixelHeight - 80);
        // The dark page: the sheet turns dark and what stands on it light, on the screen; a picture of the page as it is printed stays light.
        int Lightest(BitmapSource shot, int x0, int y0, int x1, int y1)
        {
            int best = 0;
            for (int y = y0; y < y1; y += 2)
                for (int x = x0; x < x1; x += 2) best = Math.Max(best, Brightness(shot, x, y));
            return best;
        }
        Settings.DarkPage = true;
        Theme.Apply();
        win.InvalidateVisual();
        foreach (var v in Visuals(root).OfType<PageView>()) v.InvalidateVisual();
        Idle();
        var nightBmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        nightBmp.Render(root);
        PageRenderer.SavePng(nightBmp, Path.Combine(outDir, "window-darkpage.png"));
        int midX = nightBmp.PixelWidth / 2, sheetNight = Brightness(nightBmp, midX - 200, nightBmp.PixelHeight - 80);
        int inkNight = Lightest(nightBmp, midX - 300, 140, midX + 300, nightBmp.PixelHeight - 60);
        var printed = PageRenderer.RenderPage(ed.Layout, 0, 1.0);
        int paperPrinted = Brightness(printed, 4, 4);
        bool turned = Theme.Light(Theme.OnDarkSheet(0x000000)) > 200 && Theme.OnDarkSheet(0xFFFFFF) == Theme.DarkSheet
            && Theme.Light(Theme.OnDarkSheet(0xFFFF00)) < 120 && Theme.Light(Theme.OnDarkSheet(0x2F5496)) > 150 && (Theme.OnDarkSheet(0x2F5496) & 255) > ((Theme.OnDarkSheet(0x2F5496) >> 16) & 255);
        Settings.DarkPage = false;
        Theme.Apply();
        foreach (var v in Visuals(root).OfType<PageView>()) v.InvalidateVisual();
        Idle();
        Check("dark page: on the screen the sheet is dark and what stands on it light, with the lightness of every colour turned round and its kind kept; "
            + "the page as it is printed stays as the document is", sheetNight < 200 && inkNight > 600 && paperPrinted > 720 && turned,
            $"sheet {sheetNight} ink {inkNight} printed {paperPrinted} colours {turned}");
        ShootDialog(new PreferencesDialog(null), Path.Combine(outDir, "dialog-preferences-dark.png"));
        ShootDialog(new ChartDialog(null, ChartSamples()[1] with { XTitle = "Quarter", YTitle = "Thousands" }, span), Path.Combine(outDir, "dialog-chart-dark.png"));
        ShootDialog(new ChartDialog(null, ChartSamples()[3], span), Path.Combine(outDir, "dialog-chart-pie-dark.png"));
        ShootDialog(new EquationDialog(null, "", span), Path.Combine(outDir, "dialog-equation-dark.png"));
        ShootDialog(new SymbolDialog(null, cp => span.Font.Face.Lookup(cp).Item1 != 0, _ => { }), Path.Combine(outDir, "dialog-symbols-dark.png"));
        // The print box, with a printer that writes files; nothing is printed here.
        var printBox = new PrintBox(null, ed.Layout, "Microsoft Print to PDF");
        var waited = Stopwatch.StartNew();
        ShootDialog(printBox, Path.Combine(outDir, "dialog-print-dark.png"), () =>
        {
            // The box is open; its printers arrive from the background.
            while (!printBox.Ready && waited.ElapsedMilliseconds < 15000)
            {
                System.Threading.Thread.Sleep(20);
                Idle();
            }
        });
        Check("print: the box opens before the printers are asked, and they answer in the background", printBox.Ready, waited.ElapsedMilliseconds + " ms");
        ShootDialog(MsgBox.Make(null, "Save the changes to this document?", "Reiks Document", MessageBoxButton.YesNoCancel, MessageBoxImage.Question), Path.Combine(outDir, "dialog-question-dark.png"));
        var someSources = new[]
        {
            new Source("s1", new[] { "Smith, Anna", "Jones, Bo" }, "2020", "First things", "Example Press", ""),
            new Source("s2", new[] { "Abe, Kei" }, "", "Second things", "Journal of Examples 3", "example.org/second"),
        };
        ShootDialog(new SourcesDialog(null, someSources, Citations.ByName), Path.Combine(outDir, "dialog-sources-dark.png"));
        ShootDialog(new SourceDialog(null, someSources[0]), Path.Combine(outDir, "dialog-source-dark.png"));
        ShootDialog(new ChartDialog(null, Plot.Make(Chart.Default() with { Kind = "plot" }, "Wave = sin(x); x^2/10", -5000, 5000) with { XTitle = "Time", YTitle = "Height", AxisNames = "ends" }, span), Path.Combine(outDir, "dialog-chart-plot-dark.png"));
        Check("colours: dark makes the window dark and leaves the page white", Theme.Dark && deskLight > 500 && deskDark < 200 && pageDark > 700, $"{deskLight} {deskDark} {pageDark}");
        Settings.Colors = "light";
        Theme.Apply();
        Idle();
        var again = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        again.Render(root);
        Check("colours: back to light gives the window as before", !Theme.Dark && bars.ActualHeight == barsLight && Brightness(again, 3, again.PixelHeight / 2) == deskLight,
            $"{barsLight} {bars.ActualHeight}");
        ShootDialog(new PreferencesDialog(null), Path.Combine(outDir, "dialog-preferences.png"));

        // Colours of the user's own: a soft green window and a sheet like natural paper; then back to light and white.
        Settings.Colors = "custom";
        Settings.WindowColor = 0xD3E4CD;
        Settings.PaperColor = 0xF3EBD6;
        Theme.Apply();
        win.View.InvalidateVisual();
        Idle();
        var ownBmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        ownBmp.Render(root);
        PageRenderer.SavePng(ownBmp, Path.Combine(outDir, "window-own.png"));
        int ownPage = Brightness(ownBmp, ownBmp.PixelWidth / 2 - 200, ownBmp.PixelHeight - 80), ownBar = Brightness(ownBmp, 3, ownBmp.PixelHeight - 3);
        ShootDialog(new PreferencesDialog(null, 1), Path.Combine(outDir, "dialog-preferences-own.png"));
        ShootDialog(new PreferencesDialog(null, 3), Path.Combine(outDir, "dialog-preferences-keys.png"));
        ShootDialog(new PreferencesDialog(null, 4), Path.Combine(outDir, "dialog-preferences-spelling.png"));
        ShootDialog(new PageDialog(null, ed.Doc.Page), Path.Combine(outDir, "dialog-page.png"));
        ShootDialog(new StylesDialog(null, ed.Doc.ParaStyles, "Heading1"), Path.Combine(outDir, "dialog-styles.png"));
        ShootDialog(new ParagraphDialog(null, new ParaLook(0, 101600, 1080, 0, 0, 0), new ParaLook(0, 101600, 1080, 0, 0, 0), ed.Doc.Page.ContentWidth), Path.Combine(outDir, "dialog-paragraph.png"));
        ShootDialog(new LinkDialog(null, true, ""), Path.Combine(outDir, "dialog-link.png"));
        var bandShot = LinesDoc();
        var bandEng = new LayoutEngine(bandShot);
        bandEng.Update();
        ShootDialog(new BandDialog(null, bandShot, bandEng), Path.Combine(outDir, "dialog-header.png"));
        Settings.WindowColor = 0x2C3138;
        Theme.Apply();
        bool darkOwn = Theme.Dark;
        Settings.Colors = "light";
        Settings.WindowColor = 0xDCDCD8;
        Settings.PaperColor = 0xFFFFFF;
        Theme.Apply();
        win.View.InvalidateVisual();
        Idle();
        Check("colours: a colour of the user's own on the window and on the sheet; a dark one turns the buttons dark",
            ownPage == 0xF3 + 0xEB + 0xD6 && ownBar == 0xD3 + 0xE4 + 0xCD && darkOwn && !Theme.Dark, $"{ownPage} {ownBar} {darkOwn}");

        // Titles of the axes: drawn for bars, lines and graphs, not for a pie; kept in the file.
        var barChart = ChartSamples()[1];
        var plainBox = ChartLayout.Layout(barChart, span);
        var titledBox = ChartLayout.Layout(barChart with { XTitle = "Quarter", YTitle = "Thousands" }, span);
        var pieChart = ChartSamples()[3];
        Check("charts: the titles of the axes are drawn, and a pie has none",
            titledBox.Prims.Count == plainBox.Prims.Count + 2 && titledBox.Prims.Any(p => p.Kind == PrimKind.Text && p.Text == "Quarter") && titledBox.Prims.Any(p => p.Kind == PrimKind.TextUp && p.Text == "Thousands")
            && ChartLayout.Layout(barChart with { XTitle = "Quarter", YTitle = "Thousands", AxisNames = "ends" }, span).Prims.Count(p => p.Kind == PrimKind.TextUp) == 0
            && ChartLayout.Layout(pieChart with { XTitle = "x", YTitle = "y" }, span).Prims.Count == ChartLayout.Layout(pieChart, span).Prims.Count,
            $"{plainBox.Prims.Count} {titledBox.Prims.Count}");
        var named = Plot.Make(Chart.Default() with { Kind = "plot" }, "Height = 20x - 5x^2; sin(x)", 0, 4000);
        Check("charts: a formula can have a name of its own", named.Series.Count == 2 && named.Series[0].Name == "Height" && named.Series[1].Name == "sin(x)",
            string.Join(" | ", named.Series.Select(se => se.Name)));
        string titledFile = Path.Combine(outDir, "axis-titles.docu");
        SampleDoc.Write(titledFile);
        var titledDoc = RksFile.Open(titledFile);
        Check("charts: the titles of the axes come back from the file",
            titledDoc.Stored && titledDoc.Engine.Doc.Body.SelectMany(p => p.Objects).Select(o => o.Obj).OfType<Chart>().Any(c => c.XTitle == "Season" && c.YTitle == "Visitors" && c.AxisNames == "middle")
            && titledDoc.Engine.Doc.Body.SelectMany(p => p.Objects).Select(o => o.Obj).OfType<Chart>().Any(c => c.YTitle == "Degrees" && c.AxisNames == "ends"), titledDoc.Message);

        // Printing: the pages typed, and chosen pages on other paper through the printing path into a file.
        string typed = string.Join(",", PrintBox.ParsePages("1-3, 5; 9", 6) ?? new List<int> { -1 });
        string chosenXps = Path.Combine(outDir, "print-chosen.xps");
        Printing.WriteXps(ed.Layout, chosenXps, new[] { 0, 2 }, new Size(816, 1056));
        var letter = Printing.XpsPage(chosenXps, 1);
        PageRenderer.SavePng(letter, Path.Combine(outDir, "print-chosen-p2.png"));
        Check("print: typed pages are read, and chosen pages go out on other paper",
            typed == "0,1,2,4" && PrintBox.ParsePages("3-1", 6) == null && PrintBox.ParsePages("x", 6) == null
            && Printing.XpsPages(chosenXps) == 2 && letter.PixelWidth == 816 && letter.PixelHeight == 1056, typed + " " + letter.PixelWidth + "x" + letter.PixelHeight);

        // New: a second window with its own empty document, while the first keeps its document.
        {
            string titleBefore = win.Title;
            var second = win.Another(null, false);
            second.Left = second.Top = -30000;
            second.ShowActivated = false;
            second.ShowInTaskbar = false;
            second.Show();
            Idle();
            int windows = Application.Current.Windows.OfType<MainWindow>().Count();
            bool ownDoc = !ReferenceEquals(second.Editor.Doc, win.Editor.Doc) && second.Editor.Doc.Body.Count == 1 && second.Title.StartsWith("Untitled");
            second.Close();
            Idle();
            Check("new: a second window with its own empty document; the first keeps its own", windows == 2 && ownDoc && win.Title == titleBefore && win.IsVisible, windows + " " + ownDoc);
        }

        // A new page from the caret: the text behind it starts the next page.
        {
            var nd = Document.NewDefault();
            nd.Body[0].Runs.Add(new Run("before after"));
            nd.Body[0].Touch();
            var ne = new LayoutEngine(nd);
            ne.Update();
            var ned = new Editor(nd, ne);
            ned.MoveTo(new Pos(0, 7), false);
            ned.Enter(true);
            Check("new page: what stands after the caret starts the next page",
                ne.Pages.Count == 2 && nd.Body.Count == 2 && nd.Body[1].Text == "after" && nd.Body[1].PageBreakBefore && ne.Pages[1].Lines.Count == 1, ne.Pages.Count + " " + nd.Body.Count);
        }

        // Equation shapes: every shape is understood by the typed line, selected text goes into the first box,
        // and the next empty box is ready to be typed over. The table of symbols remembers what was used last.
        {
            char gap = EquationDialog.Gap;
            bool shapesRead = EquationDialog.Templates.All(t => MathLayout.Layout(MathParser.Parse(t.Text.Replace('#', gap)), 22 * U.PerHalfPoint, true).Prims.Count > 0);
            var plain = EquationDialog.Insert("x+1", 3, 0, "(#)/(#)");
            var wrapped = EquationDialog.Insert("x+1", 0, 3, "(#)/(#)");
            bool gapDrawn = MathLayout.Font.Face.Lookup(gap).Item1 != 0;
            string last = SymbolDialog.Recent(SymbolDialog.Recent(SymbolDialog.Recent("", "a"), "b"), "a");
            int offered = SymbolDialog.Groups.Sum(g => g.Signs.Count(c => span.Font.Face.Lookup(c).Item1 != 0)), all = SymbolDialog.Groups.Sum(g => g.Signs.Length);
            Check("equations: shapes with empty boxes go into the line; symbols: the table and what was used last",
                shapesRead && gapDrawn && plain == ("x+1(" + gap + ")/(" + gap + ")", 4, 1) && wrapped == ("(x+1)/(" + gap + ")", 7, 1) && last == "ab" && offered > 150,
                $"{shapesRead} {gapDrawn} {plain} {wrapped} {last} {offered} of {all}");
        }

        // Typing inside the drawn equation: shapes, boxes, the keys that make shapes, and taking things away again.
        {
            var pad = new EquationPad(span);
            pad.InsertTemplate("(#)/(#)");
            pad.TypeText("a");
            pad.NextGap(false);
            pad.TypeText("b");
            string p1 = pad.Source;
            pad.Move(1);
            pad.TypeText("+c");
            string p2 = pad.Source;
            for (int i = 0; i < 4; i++) pad.Back();
            string p3 = pad.Source;
            pad.Back();
            pad.Back();
            string p4 = pad.Source;
            pad.Back();
            string p5 = pad.Source;
            pad.TypeText("x^2 y");
            string p6 = pad.Source;
            pad.TypeText("/3");
            string p7 = pad.Source;
            var rootPad = new EquationPad(span) { Source = "\\sqrt(x+1)" };
            rootPad.Back();
            rootPad.Back();
            string p8 = rootPad.Source;
            bool greek = SignNames.ForKey('d', false) == (char)0x3B4 && SignNames.ForKey('D', true) == (char)0x394 && SignNames.ForKey('j', false) == null
                && SignNames.Of((char)0x3B1) == "alpha  (Alt+A)" && SignNames.Of((char)0x394) == "capital delta  (Alt+Shift+D)" && SignNames.Of((char)0x221E) == "infinity  (Alt+8)";

            // Shortcuts: the standard keys, a change that takes keys from another command, and what the old keys do then.
            var mine = new Dictionary<string, string>();
            string? lost = Shortcuts.Set(mine, "fmt.bold", "Ctrl+I");
            bool swapped = lost == "Italic" && Shortcuts.Of("fmt.bold", mine) == "Ctrl+I" && Shortcuts.Of("fmt.italic", mine) == "" && Shortcuts.Find("Ctrl+I", false, mine) == "fmt.bold";
            Shortcuts.Set(mine, "fmt.bold", "Ctrl+B");
            Shortcuts.Set(mine, "fmt.italic", "Ctrl+I");
            var kept = Shortcuts.Custom;
            Shortcuts.Custom = new Dictionary<string, string> { ["fmt.bold"] = "Ctrl+Shift+B", ["sym." + (char)0x2208] = "Alt+F9" };
            bool live = Shortcuts.Find("Ctrl+Shift+B", false) == "fmt.bold" && Shortcuts.Freed("Ctrl+B", false) && !Shortcuts.Freed("Ctrl+I", false)
                && Shortcuts.Find("Alt+F9", true) == "sym." + (char)0x2208 && Shortcuts.Find("Alt+F9", false) == null;
            Settings.FilePath = Path.Combine(outDir, "settings-keys.json");
            Settings.Save();
            Shortcuts.Custom = new Dictionary<string, string>();
            Settings.Load();
            bool stored = Shortcuts.Custom.Count == 2 && Shortcuts.Of("fmt.bold") == "Ctrl+Shift+B";
            Shortcuts.Custom = kept;
            Settings.FilePath = own;
            Settings.Metric = true; Settings.Colors = "light"; Settings.AllowCapture = false; Settings.WindowColor = 0xDCDCD8; Settings.PaperColor = 0xFFFFFF;
            int commands = Shortcuts.Items.Count(i => !i.Symbol), symbols = Shortcuts.Items.Count(i => i.Symbol);
            bool unique = Shortcuts.Items.Where(i => i.Standard.Length > 0).GroupBy(i => (i.Symbol, i.Standard)).All(gr => gr.Count() == 1);
            // No keys mean two things: not a command and a sign, not a command and what the page keeps for itself, and no
            // standard is keys that type a character on a common keyboard. The user's own keys win over a later standard.
            var commandKeys = Shortcuts.Items.Where(i => !i.Symbol && i.Standard.Length > 0).Select(i => i.Standard).ToList();
            var signKeys = Shortcuts.Items.Where(i => i.Symbol && i.Standard.Length > 0).Select(i => i.Standard).ToList();
            var clash = commandKeys.Intersect(signKeys).Concat(commandKeys.Intersect(Shortcuts.OfThePage)).Concat(commandKeys.Concat(signKeys).Where(Shortcuts.TypesACharacter)).ToList();
            var ownKeys = new Dictionary<string, string> { ["ins.image"] = "Ctrl+Shift+E" };
            var gaveWay = Shortcuts.Settle(ownKeys);
            bool settled = gaveWay.Count == 1 && Shortcuts.Find("Ctrl+Shift+E", false, ownKeys) == "ins.image" && Shortcuts.Of("fmt.latex", ownKeys) == "" && Shortcuts.Settle(ownKeys).Count == 0;
            Check("shortcuts: no keys are both a command and a sign, none are keys the page keeps for itself, none type a character on a common keyboard; "
                + "keys the user gave away stay where the user put them when a later version makes them a standard",
                clash.Count == 0 && settled && Shortcuts.TypesACharacter("Ctrl+Alt+E") && !Shortcuts.TypesACharacter("Ctrl+Alt+F"), $"clash {string.Join(", ", clash)} settled {settled}");
            Check("shortcuts: standard keys are all different; a change takes keys from another, is kept in the file, and frees the old keys",
                mine.Count == 0 && swapped && live && stored && unique && commands > 25 && symbols > 80, $"{mine.Count} {swapped} {live} {stored} {unique} {commands} {symbols}");
            Check("equations: Alt and a letter give the Greek letter, and signs have names", greek);
            Check("equations: typing inside the equation makes the same line as typing the line",
                p1 == "(a)/(b)" && p2 == "(a)/(b)+c" && p3 == "(a)/()" && p4 == "()/()" && p5 == ""
                && p6 == "x^(2) y" && p7 == "x^(2) (y)/(3)" && p8 == "\\sqrt(x+)",
                string.Join(" | ", p1, p2, p3, p4, p5, p6, p7, p8));
            var marks = new List<MathMark>();
            var parsed = MathParser.Parse("(a)/(b)+x^(2) ");
            MathLayout.Layout(parsed, 22 * U.PerHalfPoint, true, marks);
            Check("equations: every part of a laid-out equation knows its place and its stretch of the line",
                parsed.Items.Count == 3 && parsed.Items[0].From == 0 && parsed.Items[0].To == 7 && parsed.Items[2] is MScript { From: 8, To: 13 }
                && parsed.Items.All(n => marks.Any(m => ReferenceEquals(m.Node, n))) && marks.Count >= 9, $"{parsed.Items.Count} {marks.Count}");
        }

        // The file ending: what Windows is told when the user asks for it (nothing is told here), and the icon of a document beside the program.
        {
            var told = FileType.Entries(@"C:\Apps\ReiksDocument.exe", @"C:\Apps\assets\DocuFile.ico");
            Check("files: what Windows is told about .docu files, and the icon of a document is there",
                told.Count == 4 && told[0] == (".docu", "ReiksDocument.Document") && told[2].Value.EndsWith("DocuFile.ico")
                && told[3] == (@"ReiksDocument.Document\shell\open\command", "\"C:\\Apps\\ReiksDocument.exe\" \"%1\"") && File.Exists(FileType.IconPath), FileType.IconPath);
        }

        // Spelling: Windows checks a text in a language it has; without one, the check is simply off.
        {
            var have = Spell.Languages();
            // A sentence with two wrong words, in the first language of these that Windows can check here.
            var samples = new[] { ("es-ES", "Esta frase tiene dos erores en una palbra.", "erores,palbra"), ("en-US", "This sentense has two mistaks in it.", "sentense,mistaks"), ("en-GB", "This sentense has two mistaks in it.", "sentense,mistaks") };
            var use = samples.FirstOrDefault(x => have.Contains(x.Item1, StringComparer.OrdinalIgnoreCase));
            string words = "";
            if (use.Item1 != null)
            {
                var wrong = Spell.Check(use.Item2, use.Item1);
                words = string.Join(",", wrong.Select(w => use.Item2.Substring(w.Start, w.Length)));
            }
            var other = use.Item1 != null ? Spell.Check("erores", use.Item1) : new List<(int Start, int Length)>();
            Check("spelling: the checker of Windows finds the unknown words of a text",
                use.Item1 == null || (words == use.Item3 && other.Count == 1), $"languages: {string.Join(" ", have)}; checked {use.Item1}; found: {words}");
        }

        // The page and the styles: both lay the whole document out again.
        {
            var pd = Document.NewDefault();
            for (int i = 0; i < 40; i++) pd.Body.Add(new Paragraph { Runs = { new Run("A line of text that is long enough to reach the right margin and to be broken into two lines on an upright page of the usual size.") } });
            var pe = new LayoutEngine(pd);
            pe.Update();
            var ped = new Editor(pd, pe);
            int pagesBefore = pe.Pages.Count, linesBefore = pe.Pages.Sum(pg => pg.Lines.Count);
            ped.SetPage(new PageSetup { Width = 10692000, Height = 7560000, Top = 720000, Bottom = 720000, Left = 720000, Right = 720000 });
            int linesWide = pe.Pages.Sum(pg => pg.Lines.Count);
            var changed = pd.ParaStyles.Select(StylesDialog.Copy).ToList();
            changed[0].After = 0;
            changed[0].IndentFirst = 360000;
            changed[0].Char = changed[0].Char with { Size = 40 };
            ped.SetStyles(changed);
            var firstLine = pe.Pages[0].Lines[1].Line;
            string pf = Path.Combine(outDir, "page-styles.docu");
            RksFile.Save(pd, pe, pf);
            var po = RksFile.Open(pf);
            Check("page and styles: a page on its side and a changed style lay everything out again and come back from the file",
                pagesBefore > 1 && linesWide < linesBefore && firstLine.X == 360000 && pd.Page.ContentWidth == 10692000 - 1440000
                && po.Stored && po.Doc.Page.Width == 10692000 && po.Doc.ParaStyles[0].IndentFirst == 360000 && Diff(pe, po.Engine) == null,
                $"{pagesBefore} {linesBefore} {linesWide} {firstLine.X}");
        }

        // Recovery copy: written whole, found again when its session is gone, and it opens with the stored layout.
        Recovery.Write("test", ed.Doc, ed.Layout, @"C:\somewhere\Report.docu", 999999999);
        var left = Recovery.Find();
        bool foundCopy = left.Count == 1 && left[0].Id == "test" && left[0].Original == @"C:\somewhere\Report.docu" && RksFile.Open(left[0].File).Stored;
        Recovery.Write("mine", ed.Doc, ed.Layout, null);
        bool ownHidden = Recovery.Find().Count == 1;
        Recovery.Remove("test");
        Recovery.Remove("mine");
        Check("recovery: a copy is written, found after its session is gone, opens, and is removed", foundCopy && ownHidden && Recovery.Find().Count == 0, $"{left.Count} {foundCopy} {ownHidden}");
        // The copy of a long document is written beside the typing.
        var longDoc = Bench.Make(450);
        var longEng = new LayoutEngine(longDoc);
        longEng.Update();
        bool behindStarted = Recovery.WriteBehind("behind", longDoc, longEng, null);
        Recovery.Wait("behind");
        string behindFile = Path.Combine(Recovery.Folder, "behind.docu");
        bool behindThere = File.Exists(behindFile) && !File.Exists(Path.Combine(Recovery.Folder, "behind.tmp"));
        var behindOpen = behindThere ? RksFile.Open(behindFile) : null;
        bool behindSame = behindOpen is { Stored: true } && Diff(longEng, behindOpen.Engine) == null && RksFile.ContentJson(behindOpen.Doc).AsSpan().SequenceEqual(RksFile.ContentJson(longDoc));
        Recovery.Remove("behind");
        Check("recovery: the copy of a long document, written beside the typing, holds the document and its layout, and is removed",
            behindStarted && behindThere && behindSame && !File.Exists(behindFile), $"started {behindStarted} there {behindThere} same {behindSame} pages {longEng.Pages.Count}");

        // Lists: kinds and levels.
        {
            var ld = Document.NewDefault();
            ld.Body.Clear();
            void L(string? kind, int level) => ld.Body.Add(new Paragraph { List = kind, ListLevel = level, Runs = { new Run("item") } });
            L("decimal", 0); L("decimal", 1); L("decimal", 1); L("decimal", 2); L("decimal", 0);
            L(null, 0);
            L("outline", 0); L("outline", 1); L("outline", 2); L("circle", 1); L("outline", 1); L("outline", 0);
            L("roman-upper", 0); L("roman-upper", 0); L("roman-upper", 0); L("roman-upper", 0);
            L(null, 0);
            L("alpha-paren", 0);
            var le = new LayoutEngine(ld);
            le.Update();
            string marks = string.Join(" ", ld.Body.Select(p => p.ListMark ?? "-"));
            string want = "1. 1.1. 1.2. 1.2.1. 2. - 1. a. i. " + (char)0x25E6 + " a. 2. I. II. III. IV. - a)";
            int x0 = le.Pages[0].Lines[0].Line.MarkX, x1 = le.Pages[0].Lines[1].Line.MarkX;
            string lf = Path.Combine(outDir, "list-levels.docu");
            RksFile.Save(ld, le, lf);
            var lo = RksFile.Open(lf);
            Check("lists: numbers of every kind and level, deeper levels further in, kept in the file",
                marks == want && x1 - x0 == LayoutEngine.ListTextAt && LayoutEngine.Alpha(28) == "ab" && LayoutEngine.Roman(1994) == "MCMXCIV"
                && lo.Stored && lo.Doc.Body[3].ListLevel == 2 && lo.Doc.Body[3].List == "decimal" && Diff(le, lo.Engine) == null, marks);
            var calibri = le.StyleSpan(ld.ParaStyles[0]).Font.Face;
            Check("lists: Calibri has the four bullet signs", new[] { 0x2022, 0x25E6, 0x25AA, 0x2013 }.All(cp => calibri.Lookup(cp).Item1 != 0));
        }

        // Citations, the list of references and the kinds of underline.
        {
            var cd = Document.NewDefault();
            cd.Sources.Add(new Source("s1", new[] { "Smith, Anna" }, "2020", "First things", "Example Press", ""));
            cd.Sources.Add(new Source("s2", new[] { "Smith, Anna", "Jones, Bo Li" }, "", "Second things", "", "example.org/second"));
            cd.Sources.Add(new Source("s3", new[] { "Abe, Kei", "Berg, Ola", "Cruz, Ana" }, "1999", "Third things", "Journal of Examples 3", ""));
            var cp = cd.Body[0];
            foreach (string id in new[] { "s3", "s1", "s2", "s3", "gone" })
            {
                cp.Runs.Add(new Run("see "));
                cp.Runs.Add(ReiksDocument.Run.Object(new Cite(id), new Run("")));
                cp.Runs.Add(new Run(" "));
            }
            foreach (string kind in new[] { "single", "double", "thick", "dotted", "dashed", "wave" })
            {
                cp.Runs.Add(new Run(kind + " underline", new CharProps(Underline: true, Under: kind)));
                cp.Runs.Add(new Run("   "));
            }
            cp.Touch();
            var ce = new LayoutEngine(cd);
            ce.Update();
            string Texts() => string.Join(" ", cd.Body[0].Objects.Select(o => ce.CiteText((Cite)o.Obj)));
            string byName = Texts();
            var ced = new Editor(cd, ce);
            ced.MoveTo(new Pos(0, 0), false);
            bool listed = ced.UpdateRefs(true);
            int refs = cd.Body.Count(p => p.Refs);
            string firstByName = cd.Body.Count > 2 ? cd.Body[2].Text : "";
            ced.SetSources(new List<Source>(cd.Sources), Citations.Numbered);
            string byNumber = Texts();
            string firstByNumber = cd.Body.Count > 2 ? cd.Body[2].Text : "";
            string cf = Path.Combine(outDir, "citations.docu");
            RksFile.Save(cd, ce, cf);
            var co = RksFile.Open(cf);
            Check("citations: texts by author and year and by number, the list of references, kept in the file",
                byName == "(Abe et al., 1999) (Smith, 2020) (Smith & Jones, n.d.) (Abe et al., 1999) [?]" && byNumber == "[1] [2] [3] [1] [?]"
                && listed && refs == 4 && firstByName.StartsWith("Abe, K., Berg, O., & Cruz, A. (1999). Third things. Journal of Examples 3.")
                && firstByNumber.StartsWith("[1] Abe, Kei; Berg, Ola; Cruz, Ana. Third things. Journal of Examples 3, 1999.")
                && co.Stored && co.Doc.Sources.Count == 3 && co.Doc.CiteStyle == Citations.Numbered && co.Doc.Body.Count(p => p.Refs) == 4
                && co.Doc.Body[0].Runs.Any(r => r.Props.Under == "wave") && Diff(ce, co.Engine) == null,
                byName + " / " + byNumber + " / " + firstByName + " / " + firstByNumber + " / " + co.Message);
            PageRenderer.SavePng(PageRenderer.RenderPage(ce, 0, 1.0), Path.Combine(outDir, "citations-p1.png"));

            var td = Document.NewDefault();
            td.Body[0].Style = "Title";
            td.Body[0].Runs.Add(new Run("A report"));
            td.Body[0].Touch();
            string[] heads = { "Heading1|First part", "Heading2|A detail of the first part", "Heading1|Second part", "Heading3|Deep down" };
            for (int hi = 0; hi < heads.Length; hi++)
            {
                var hp = new Paragraph { Style = heads[hi].Split('|')[0], PageBreakBefore = hi == 2 };
                hp.Runs.Add(new Run(heads[hi].Split('|')[1]));
                td.Body.Add(hp);
                var bp = new Paragraph { Style = "Normal" };
                bp.Runs.Add(new Run("Some text below the heading."));
                td.Body.Add(bp);
            }
            var te = new LayoutEngine(td);
            te.Update();
            var ted = new Editor(td, te);
            ted.MoveTo(new Pos(1, 0), false);
            bool tocMade = ted.UpdateToc(true);
            var tocParas = td.Body.Where(p => p.Toc).ToList();
            string tocText = string.Join(" | ", tocParas.Select(p => p.Text.Replace('\t', '>')));
            bool tocRight = tocParas.Count > 1 && tocParas.Skip(1).Where(p => p.Length > 0).All(p => p.Lines is { Count: 1 } tl && tl[0].X + tl[0].Adv.Sum() == td.Page.ContentWidth);
            int tocIndent = tocParas.Count == 6 ? tocParas[2].IndentLeft ?? 0 : -1;
            bool tocCaret = ted.Caret.Para == 7 && td.Body[7].Text == "First part";
            var lastHead = new Paragraph { Style = "Heading1", PageBreakBefore = true };
            lastHead.Runs.Add(new Run("Third part"));
            td.Body.Add(lastHead);
            te.InvalidateAll();
            te.Update();
            ted.UpdateToc(false);
            string tocLast = td.Body.Last(p => p.Toc && p.Length > 0).Text.Replace('\t', '>');
            string tf = Path.Combine(outDir, "contents.docu");
            RksFile.Save(td, te, tf);
            var to = RksFile.Open(tf);
            PageRenderer.SavePng(PageRenderer.RenderPage(te, 0, 1.0), Path.Combine(outDir, "contents-p1.png"));
            bool tocKept = to.Stored && to.Doc.Body.Count(p => p.Toc) == 7 && Diff(te, to.Engine) == null;
            ted.Undo();
            ted.Undo();
            bool tocGone = !td.Body.Any(p => p.Toc);
            Check("contents: the headings with their pages, the numbers at the right edge, written again, kept in the file, undone",
                tocMade && tocText == "Contents | First part>1 | A detail of the first part>1 | Second part>2 | Deep down>2 | " && tocRight && tocIndent == Contents.Step
                && tocCaret && tocLast == "Third part>3" && tocKept && tocGone,
                tocText + " / right " + tocRight + " / indent " + tocIndent + " / caret " + tocCaret + " / " + tocLast + " / gone " + tocGone + " / " + to.Message);

            var clipPic = System.Windows.Media.Imaging.BitmapSource.Create(40, 30, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[40 * 30 * 4], 160);
            byte[] clipPng = PageView.PngOf(clipPic);
            var clipBack = PageRenderer.Decode(clipPng, out bool clipJpeg);
            Check("paste: a picture from the clipboard becomes a PNG of the same size, without transparency",
                clipBack is { PixelWidth: 40, PixelHeight: 30 } && !clipJpeg && clipBack.Format.BitsPerPixel == 24, clipPng.Length + " bytes");

            var ld = Document.NewDefault();
            ld.Body[0].Runs.Add(new Run("See the site for more."));
            ld.Body[0].Touch();
            var le = new LayoutEngine(ld);
            le.Update();
            var led = new Editor(ld, le);
            led.Select(new Pos(0, 8), new Pos(0, 12));
            led.SetLink("https://example.org/page");
            var linkFmt = led.CurrentFormat();
            led.MoveTo(new Pos(0, 10), false);
            var linkAt = led.LinkAround();
            led.MoveTo(new Pos(0, 12), false);
            led.TypeText("s");
            bool typedPlain = ld.Body[0].FormatOfChar(12).Props.Link == null && ld.Body[0].FormatOfChar(11).Props.Link != null;
            led.MoveTo(led.DocEnd, false);
            led.InsertLink("mail us", LinkDialog.Tidy(" info@example.org "));
            string lf = Path.Combine(outDir, "links.docu"), lpdf = Path.Combine(outDir, "links.pdf");
            RksFile.Save(ld, le, lf);
            var lo = RksFile.Open(lf);
            PdfExport.Write(le, lpdf);
            string lraw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(lpdf));
            int Times(string hay, string what) { int c = 0; for (int i = hay.IndexOf(what, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(what, i + 1, StringComparison.Ordinal)) c++; return c; }
            bool linksKept = lo.Stored && Diff(le, lo.Engine) == null && lo.Doc.Body[0].Runs.Count(r => r.Props.Link != null) == 2;
            led.MoveTo(new Pos(0, 10), false);
            led.SetLink(null);
            bool linkGone = ld.Body[0].FormatOfChar(10).Props is { Link: null, Color: null, Underline: null };
            Check("links: a link on selected text, typing behind it, a new link, kept in the file, working in the PDF, removed",
                linkFmt.Link == "https://example.org/page" && linkFmt.Underline && linkFmt.Color == Editor.LinkColour && linkAt is { From: 8, To: 12 }
                && typedPlain && ld.Body[0].Text == "See the sites for more.mail us" && linksKept && linkGone
                && Times(lraw, "/S /URI /URI (https://example.org/page)") == 1 && Times(lraw, "/URI (mailto:info@example.org)") == 1
                && LinkDialog.Tidy("www.example.org") == "https://www.example.org" && LinkDialog.CanOpen("https://example.org") && !LinkDialog.CanOpen("file:///C:/x.exe") && !LinkDialog.CanOpen("C:\\x.exe"),
                linkFmt.Link + " / at " + linkAt + " / plain " + typedPlain + " / " + ld.Body[0].Text + " / kept " + linksKept + " / gone " + linkGone + " / uri " + Times(lraw, "/S /URI"));

            var sd2 = SampleDoc.Build();
            var se2 = new LayoutEngine(sd2);
            se2.Update();
            string spdf = Path.Combine(outDir, "bookmarks.pdf");
            PdfExport.Write(se2, spdf);
            string sraw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(spdf));
            int headCount = sd2.Body.Count(p => Contents.Level(p) > 0), entryCount = sd2.Body.Count(p => p.Toc && p.Text.Contains('\t'));
            Check("pdf: every heading is a bookmark, every entry of the table of contents leads to its page, the file is sound",
                headCount > 5 && entryCount == headCount && Times(sraw, "/Title <FEFF") == headCount && Times(sraw, "/Subtype /Link /Rect") == entryCount + 1
                && Times(sraw, "] /Border [0 0 0] /Dest [") == entryCount && Times(sraw, "/Outlines ") == 2 && PdfExport.Verify(spdf) == se2.Pages.Count,
                $"headings {headCount}, entries {entryCount}, bookmarks {Times(sraw, "/Title <FEFF")}, links {Times(sraw, "/Subtype /Link /Rect")}, verify {PdfExport.Verify(spdf)}, pages {se2.Pages.Count}");

            var jd = Document.NewDefault();
            var je = new LayoutEngine(jd);
            je.Update();
            var jed = new Editor(jd, je);
            jed.InsertTable(3, 3);
            TableGrid JG() => jd.Body[1].Table!;
            void Into(int r, int c) => jed.MoveTo(new Pos(0, 0), false, story: -JG().Rows[r][c]);
            int colW = JG().Widths[0];
            Into(0, 0);
            jed.TypeText("left");
            Into(0, 1);
            jed.TypeText("right");
            Into(0, 0);
            bool couldJoin = jed.CanJoin(false) && jed.CanJoin(true) && !jed.CellJoined;
            jed.JoinCell(false);
            var g1 = JG();
            bool across = g1.Rows[0][0] == g1.Rows[0][1] && g1.Rows[0][1] != g1.Rows[0][2] && jed.CellJoined && !jed.CanJoin(true)
                && string.Join("|", jd.Cells[g1.Rows[0][0]].Paras.Select(p => p.Text)) == "left|right";
            var box1 = je.Pages[0].Cells.First(cb => cb.Row == 0 && cb.Col == 0);
            bool acrossBox = box1.W == 2 * colW && box1.Cols == 2 && je.Pages[0].Cells.Count == 8;
            Into(1, 2);
            jed.TypeText("one");
            for (int k = 0; k < 5; k++) { jed.Enter(); jed.TypeText("more"); }
            Into(1, 2);
            jed.JoinCell(true);
            var g2 = JG();
            var rowsNow = jd.Body[1].Lines!;
            var box2 = je.Pages[0].Cells.First(cb => cb.Row == 1 && cb.Col == 2);
            bool downBox = g2.Rows[1][2] == g2.Rows[2][2] && box2.Rows == 2 && box2.H == rowsNow[1].Height + rowsNow[2].Height && rowsNow[1].Tied && !rowsNow[2].Tied
                && rowsNow[2].Height > 3 * rowsNow[1].Height;
            Into(0, 0);
            jed.NextCell(1);
            bool tabOver = jed.Story == -JG().Rows[0][2];
            jed.ShadeCells("row", 0xDDEBF7);
            jed.SetTableLines("horizontal", 19050);
            bool caretKept = jed.Story == -JG().Rows[0][2];
            Into(1, 0);
            jed.InsertRow();
            bool rowThrough = JG().Rows.Count == 4 && JG().Rows[1][2] == JG().Rows[2][2] && JG().Rows[2][2] == JG().Rows[3][2] && JG().Rows[2][0] != JG().Rows[1][0];
            string jf = Path.Combine(outDir, "joined.docu");
            RksFile.Save(jd, je, jf);
            var jo = RksFile.Open(jf);
            var og = jo.Doc.Body[1].Table!;
            bool joinedKept = jo.Stored && Diff(je, jo.Engine) == null && og.Rows[0][0] == og.Rows[0][1] && og.Rows[1][2] == og.Rows[3][2] && og.Lines == "horizontal" && og.LineWidth == 19050
                && og.Shades.Count == 2 && og.Shades.Values.All(v => v == 0xDDEBF7) && Invariants(je) == null && Diff(je, Fresh(jd)) == null;
            PageRenderer.SavePng(PageRenderer.RenderPage(je, 0, 1.0), Path.Combine(outDir, "joined-p1.png"));
            Into(0, 0);
            jed.SplitCell();
            bool split = JG().Rows[0][0] != JG().Rows[0][1] && JG().Shades.Count == 3 && !jed.CellJoined;
            jed.Undo();
            bool splitUndone = JG().Rows[0][0] == JG().Rows[0][1];
            int full = jd.Page.ContentWidth;
            var narrow = new PageSetup { Width = jd.Page.Width, Height = jd.Page.Height, Top = jd.Page.Top, Bottom = jd.Page.Bottom, Left = jd.Page.Left + 360000, Right = jd.Page.Right + 360000 };
            bool wasFull = JG().Width == full;
            jed.SetPage(narrow);
            bool follows = wasFull && JG().Width == full - 720000 && jd.Page.ContentWidth == full - 720000 && Diff(je, Fresh(jd)) == null;
            Check("tables: cells joined to the right and downward, Tab over them, a row through a joined cell, shading and lines, kept in the file, split, widths follow the margins",
                couldJoin && across && acrossBox && downBox && tabOver && caretKept && rowThrough && joinedKept && split && splitUndone && follows,
                $"could {couldJoin} across {across} box {acrossBox} down {downBox} tab {tabOver} caret {caretKept} row {rowThrough} kept {joinedKept} split {split} undone {splitUndone} follows {follows} / {jo.Message}");

            var pd = Document.NewDefault();
            string filler = string.Join(" ", Enumerable.Repeat("words that fill the line and go on", 12));
            pd.Body[0].Runs.Add(new Run(filler));
            pd.Body[0].Touch();
            for (int k = 0; k < 2; k++)
            {
                var more = new Paragraph { Style = "Normal" };
                more.Runs.Add(new Run(filler));
                pd.Body.Add(more);
            }
            var pe = new LayoutEngine(pd);
            pe.Update();
            var ped = new Editor(pd, pe);
            int gapBefore = pd.Body[2].Lines![0].Top - (pd.Body[1].Lines![^1].Top + pd.Body[1].Lines![^1].Height);
            ped.MoveTo(new Pos(1, 0), false);
            var styleLook = ped.CurrentLook(true);
            ped.SetLook(new ParaLook(12 * U.PerPoint, 24 * U.PerPoint, styleLook.Line, 360000, -180000, 540000));
            var mid = pd.Body[1];
            var l0 = mid.Lines![0];
            var l1 = mid.Lines![1];
            int above = l0.Top - (pd.Body[0].Lines![^1].Top + pd.Body[0].Lines![^1].Height);
            int below = pd.Body[2].Lines![0].Top - (mid.Lines![^1].Top + mid.Lines![^1].Height);
            bool lookSet = mid.Before == 12 * U.PerPoint && mid.After == 24 * U.PerPoint && mid.Line == null && mid.IndentLeft == 360000 && mid.IndentFirst == -180000 && mid.IndentRight == 540000;
            bool lookLaid = l0.X == 180000 && l1.X == 360000 && l1.XOffset(l1.Length) <= pd.Page.ContentWidth - 540000 + 60000
                && above == styleLook.After + 12 * U.PerPoint && below == 24 * U.PerPoint && gapBefore == styleLook.After && Invariants(pe) == null && Diff(pe, Fresh(pd)) == null;
            string pf = Path.Combine(outDir, "paragraphs.docu");
            RksFile.Save(pd, pe, pf);
            var po = RksFile.Open(pf);
            bool lookKept = po.Stored && Diff(pe, po.Engine) == null && po.Doc.Body[1].Before == 12 * U.PerPoint && po.Doc.Body[1].IndentFirst == -180000 && po.Doc.Body[0].Before == null;
            PageRenderer.SavePng(PageRenderer.RenderPage(pe, 0, 1.0), Path.Combine(outDir, "paragraphs-p1.png"));
            ped.SetLook(styleLook);
            bool lookBack = mid.Before == null && mid.After == null && mid.IndentLeft == null && mid.IndentFirst == null && mid.IndentRight == null;
            ped.Undo();
            bool lookUndone = mid.Before == 12 * U.PerPoint || pd.Body[1].Before == 12 * U.PerPoint;
            Check("paragraphs: room above and below and indents for one paragraph, laid out, kept in the file, back to the style, undone",
                lookSet && lookLaid && lookKept && lookBack && lookUndone,
                $"set {lookSet} laid {lookLaid} (x {l0.X} {l1.X}, above {above}, below {below}, gap {gapBefore}) kept {lookKept} back {lookBack} undone {lookUndone}");

            string Parted(string word)
            {
                var at = Hyphenator.Points(word, "es");
                var sb = new System.Text.StringBuilder(word);
                for (int k = at.Length - 1; k >= 0; k--) sb.Insert(at[k], '-');
                return sb.ToString();
            }
            string[] spanish =
            {
                "com-pu-ta-do-ra", "cons-truc-ción", "pe-rro", "ins-tan-te", "ex-traor-di-na-rio", "aho-ra", "inha-lar", "atlas", "aé-reo",
                "de-sa-rro-llo", "gui-ta-rra", "bue-no", "trans-por-te", "pue-blo", "mu-cha-cho", "Ca-rre-te-ra", "casa", "ac-ción",
            };
            var partedWrong = spanish.Where(w => Parted(w.Replace("-", "")) != w).Select(w => Parted(w.Replace("-", ""))).ToList();
            Check("hyphens: Spanish words are divided between their syllables", partedWrong.Count == 0, string.Join(" ", partedWrong));

            // A language that goes by patterns, with a made-up list in a folder of the test.
            string ownFolder = Hyphenator.Folder, patFolder = Path.Combine(outDir, "hyph-test");
            Directory.CreateDirectory(patFolder);
            File.WriteAllText(Path.Combine(patFolder, "languages.txt"), "# test\nxx|Made up|2|2\nyy|Not there|2|2\n");
            File.WriteAllText(Path.Combine(patFolder, "hyph-xx.pat.txt"), "a1b\nc1d xxa2b\n% a comment\n.zz3q\n");
            File.WriteAllText(Path.Combine(patFolder, "hyph-xx.hyp.txt"), "yy-ab-zz-xx\n");
            Hyphenator.Folder = patFolder;
            Hyphenator.Reload();
            string Pat(string word) => string.Join(",", Hyphenator.Points(word, "xx"));
            bool patternsWork = Hyphenator.Knows("xx") && !Hyphenator.Knows("yy") && Hyphenator.Knows("es")
                && Pat("yyabyy") == "3" && Pat("xxabxxcdxx") == "7" && Pat("yyabzzxx") == "2,4,6" && Pat("zzqqq") == "2" && Pat("qzzqq") == "" && Pat("abab") == ""
                && Pat("see yyabyy, YYABYY and 9yyabyy") == "7,15" && Hyphenator.Points("yyabyy", "yy").Length == 0;
            string patSeen = Pat("yyabyy") + " / " + Pat("xxabxxcdxx") + " / " + Pat("yyabzzxx") + " / " + Pat("zzqqq") + " / " + Pat("see yyabyy, YYABYY and 9yyabyy");
            Hyphenator.Folder = ownFolder;
            Hyphenator.Reload();
            Check("hyphens: a language with a list of patterns divides by them, and by its words divided by hand", patternsWork, patSeen);

            // The lists that come with the program.
            string Real(string word, string lang)
            {
                var at = Hyphenator.Points(word, lang);
                var sb = new System.Text.StringBuilder(word);
                for (int k = at.Length - 1; k >= 0; k--) sb.Insert(at[k], '-');
                return sb.ToString();
            }
            string[] englishWords = { "hyphenation", "associate", "computer", "international", "typesetting", "university", "algorithm", "table" };
            string[] swedishWords = { "universitet", "sjuksköterska", "möjligheter", "arbetsmarknaden", "företag", "hej" };
            string englishSeen = string.Join(" ", englishWords.Select(w => Real(w, "en-us"))), swedishSeen = string.Join(" ", swedishWords.Select(w => Real(w, "sv")));
            bool mins = englishWords.All(w => Hyphenator.Points(w, "en-us").All(at => at >= 2 && w.Length - at >= 3))
                && swedishWords.All(w => Hyphenator.Points(w, "sv").All(at => at >= 2 && w.Length - at >= 2));
            Check("hyphens: English and Swedish are there and divide words by their lists",
                Hyphenator.Knows("en-us") && Hyphenator.Knows("sv") && Real("hyphenation", "en-us") == "hy-phen-ation" && Real("associate", "en-us") == "as-so-ciate"
                && Real("international", "en-us") == "in-ter-na-tional" && Real("typesetting", "en-us") == "type-set-ting" && Real("table", "en-us") == "ta-ble"
                && Real("tree", "en-us") == "tree" && Real("hej", "sv") == "hej" && Real("universitet", "sv") == "uni-ver-si-tet" && Real("sjuksköterska", "sv") == "sjuk-skö-ters-ka" && mins,
                englishSeen + " / " + swedishSeen);

            // A document in the format of earlier versions is told apart and not read.
            string nowFile = Path.Combine(outDir, "ending.docu"), earlierFile = Path.Combine(outDir, "ending-earlier.udoc");
            var endDoc = SampleDoc.Build();
            var endEng = new LayoutEngine(endDoc);
            endEng.Update();
            RksFile.Save(endDoc, endEng, nowFile);
            File.Copy(nowFile, earlierFile, true);
            bool renamed;
            using (var zip = System.IO.Compression.ZipFile.Open(earlierFile, System.IO.Compression.ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("manifest.json")!;
                string manifest;
                using (var reader = new StreamReader(entry.Open())) manifest = reader.ReadToEnd();
                renamed = manifest.Contains("\"docu\"");
                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                writer.Write(manifest.Replace("\"docu\"", "\"udoc\""));
            }
            string refused = "";
            try { RksFile.Open(earlierFile); }
            catch (InvalidDataException ex) { refused = ex.Message; }
            var nowOpen = RksFile.Open(nowFile);
            Check("files: a document says docu and ends in .docu; one in the format of an earlier version is refused in plain words",
                renamed && refused.Contains("earlier version") && nowOpen.Stored && Diff(endEng, nowOpen.Engine) == null, refused);

            // A block that begins near the bottom of a page: with little of it there it moves on, with most of it there it stays.
            {
                (int First, int Third, int Fifth, LayoutEngine Eng, Document Doc) Block(int before, bool marked)
                {
                    var bd = Document.NewDefault();
                    bd.Body.Clear();
                    for (int k = 0; k < before + 6; k++)
                    {
                        var bp = new Paragraph { Style = "Normal", Together = marked && k == before };
                        bp.Runs.Add(new Run(k < before ? "A line before." : "Part " + (k - before + 1) + " of the task."));
                        bd.Body.Add(bp);
                    }
                    bd.Body.Add(new Paragraph { Style = "Normal" });
                    var be = new LayoutEngine(bd);
                    be.Update();
                    return (bd.Body[before].Lines![0].Page, bd.Body[before + 2].Lines![0].Page, bd.Body[before + 4].Lines![0].Page, be, bd);
                }
                int twoFit = Enumerable.Range(1, 80).FirstOrDefault(n => Block(n, false) is { First: 0, Third: 1 });
                int fourFit = Enumerable.Range(1, 80).FirstOrDefault(n => Block(n, false) is { First: 0, Third: 0, Fifth: 1 });
                var moved = Block(twoFit, true);
                var stays = Block(fourFit, true);
                // A list whose number stands at the edge of the text, as Word often has it.
                var edgeList = new Paragraph { Style = "Normal", List = "number", IndentLeft = -LayoutEngine.ListMarkAt };
                edgeList.Runs.Add(new Run("At the edge."));
                var notList = new Paragraph { Style = "Normal", IndentLeft = -LayoutEngine.ListMarkAt };
                notList.Runs.Add(new Run("Not a list."));
                moved.Doc.Body.Add(edgeList);
                moved.Doc.Body.Add(notList);
                moved.Eng.Update();
                bool atEdge = edgeList.Lines![0].MarkX == 0 && edgeList.Lines![0].X == LayoutEngine.ListMarkAt && notList.Lines![0].X == 0;
                string blockFile = Path.Combine(outDir, "blocks.docu");
                RksFile.Save(moved.Doc, moved.Eng, blockFile);
                var blockOpen = RksFile.Open(blockFile);
                Check("pages: a block of which less than half fits at the bottom of a page starts on the next page; one that mostly fits stays",
                    atEdge && blockOpen.Doc.Body[^2].IndentLeft == -LayoutEngine.ListMarkAt && blockOpen.Doc.Body[^1].IndentLeft == 0 && twoFit > 0 && fourFit > 0 && moved.First == 1 && moved.Doc.Body[twoFit - 1].Lines![0].Page == 0 && stays.First == 0 && stays.Fifth == 1
                    && blockOpen.Stored && blockOpen.Doc.Body[twoFit].Together && blockOpen.Doc.Body.Count(bp => bp.Together) == 1 && Diff(moved.Eng, blockOpen.Engine) == null
                    && Invariants(moved.Eng) == null && Diff(moved.Eng, Fresh(moved.Doc)) == null,
                    $"at the edge {atEdge}; two fit after {twoFit} lines, four after {fourFit}; marked: first on page {moved.First + 1} and {stays.First + 1}; {blockOpen.Message}");
            }

            // A picture and an equation inside table cells.
            {
                var cellDoc = Document.NewDefault();
                var cellEng = new LayoutEngine(cellDoc);
                cellEng.Update();
                var cellEd = new Editor(cellDoc, cellEng);
                cellEd.InsertTable(2, 2);
                var cg = cellDoc.Body[1].Table!;
                byte[] cellBytes = MakePicture(160, 100, false);
                PageRenderer.Decode(cellBytes, out bool cellJpeg);
                string cellMedia = RksFile.MediaName(cellBytes, cellJpeg);
                cellDoc.Media[cellMedia] = cellBytes;
                cellEd.MoveTo(new Pos(0, 0), false, story: -cg.Rows[0][0]);
                cellEd.TypeText("A picture: ");
                bool putPicture = cellEd.InsertObject(new Picture(cellMedia, 160 * 9525, 100 * 9525, false));
                cellEd.MoveTo(new Pos(0, 0), false, story: -cg.Rows[1][1]);
                bool putEquation = cellEd.InsertObject(Equation.From("(a)/(b)", false));
                bool noFloat = !cellEd.InsertObject(new Picture(cellMedia, 100000, 100000, false, "square", 0, 0));
                var cellRows = cellDoc.Body[1].Lines!;
                var firstCell = cellEng.Pages[0].Cells.First(cb => cb.Row == 0 && cb.Col == 0);
                var picAt = new Pos(0, "A picture: ".Length);
                var picRect = cellEng.ObjectRect(picAt, -cg.Rows[0][0]);
                bool inPlace = picRect is { } pr && pr.W == 160 * 9525 && pr.H == 100 * 9525 && pr.X >= firstCell.X + LayoutEngine.CellPadX && pr.Y >= firstCell.Y
                    && pr.Y + pr.H <= firstCell.Y + firstCell.H && cellRows[0].Height >= 100 * 9525 + 2 * LayoutEngine.CellPadY;
                bool found = picRect is { } hr && cellEng.ObjectAtAny(0, hr.X + hr.W / 2, hr.Y + hr.H / 2) is { } hit && hit.Story == -cg.Rows[0][0] && hit.Pos == picAt;
                cellEd.MoveTo(picAt, false, story: -cg.Rows[0][0]);
                cellEd.Select(picAt, new Pos(0, picAt.Off + 1));
                bool selected = cellEd.SelectedBox() is { Obj: Picture };
                string cellFile = Path.Combine(outDir, "cell-objects.docu");
                RksFile.Save(cellDoc, cellEng, cellFile);
                var cellOpen = RksFile.Open(cellFile);
                var savedGrid = cellOpen.Doc.Body[1].Table!;
                bool cellKept = cellOpen.Stored && Diff(cellEng, cellOpen.Engine) == null && cellOpen.Doc.Media.ContainsKey(cellMedia)
                    && cellOpen.Doc.Cells[savedGrid.Rows[0][0]].Paras[0].Objects.Any(o => o.Obj is Picture { Floating: false, Width: 160 * 9525 })
                    && cellOpen.Doc.Cells[savedGrid.Rows[1][1]].Paras[0].Objects.Any(o => o.Obj is Equation) && Invariants(cellEng) == null && Diff(cellEng, Fresh(cellDoc)) == null;
                PageRenderer.SavePng(PageRenderer.RenderPage(cellEng, 0, 1.0), Path.Combine(outDir, "cell-objects-p1.png"));
                PdfExport.Write(cellEng, Path.Combine(outDir, "cell-objects.pdf"));
                Check("tables: a picture and an equation stand inside cells, are found by a click and selected, and are kept in the file",
                    putPicture && putEquation && noFloat && inPlace && found && selected && cellKept,
                    $"picture {putPicture} equation {putEquation} no float {noFloat} in place {inPlace} found {found} selected {selected} kept {cellKept} / {cellOpen.Message}");
            }

            // A Word document made here by hand, with one of each thing the import takes over.
            {
                const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" "
                    + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
                    + "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" "
                    + "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";
                string Wp(string runs, string ppr = "") => "<w:p>" + (ppr.Length > 0 ? "<w:pPr>" + ppr + "</w:pPr>" : "") + runs + "</w:p>";
                string Wr(string text, string rpr = "") => "<w:r><w:rPr>" + rpr + "</w:rPr><w:t xml:space=\"preserve\">" + text + "</w:t></w:r>";
                string Pic(long cx, long cy, string anchor = "") => "<w:r><w:drawing>" + (anchor.Length == 0 ? "<wp:inline>" : "<wp:anchor><wp:positionH relativeFrom=\"margin\"><wp:align>right</wp:align></wp:positionH>")
                    + "<wp:extent cx=\"" + cx + "\" cy=\"" + cy + "\"/>" + (anchor.Length > 0 ? "<wp:" + anchor + "/>" : "") + "<a:graphic><a:graphicData><a:blip r:embed=\"rImg\"/></a:graphicData></a:graphic>"
                    + (anchor.Length == 0 ? "</wp:inline>" : "</wp:anchor>") + "</w:drawing></w:r>";
                string Tc(string text, string pr = "") => "<w:tc><w:tcPr>" + pr + "</w:tcPr>" + Wp(Wr(text)) + "</w:tc>";
                string wordBody =
                    Wp(Wr("First part\t9"), "<w:pStyle w:val=\"TDC1\"/>")
                    + Wp(Wr("First part"), "<w:pStyle w:val=\"Ttulo1\"/>")
                    + Wp(Wr("Plain, ") + Wr("bold", "<w:b/>") + Wr(" and ") + "<w:hyperlink r:id=\"rLink\">" + Wr("a link") + "</w:hyperlink>" + "<w:r><w:footnoteReference w:id=\"2\"/></w:r>",
                        "<w:jc w:val=\"center\"/><w:spacing w:before=\"240\" w:line=\"360\" w:lineRule=\"auto\"/><w:ind w:left=\"720\" w:hanging=\"360\"/>")
                    + Wp(Wr("One"), "<w:numPr><w:ilvl w:val=\"1\"/><w:numId w:val=\"1\"/></w:numPr>")
                    + "<w:tbl><w:tblPr><w:tblBorders><w:top w:val=\"single\"/><w:bottom w:val=\"single\"/><w:insideH w:val=\"single\"/></w:tblBorders></w:tblPr>"
                    + "<w:tblGrid><w:gridCol w:w=\"8000\"/><w:gridCol w:w=\"8000\"/><w:gridCol w:w=\"8000\"/></w:tblGrid>"
                    + "<w:tr>" + Tc("across", "<w:gridSpan w:val=\"2\"/><w:shd w:val=\"clear\" w:fill=\"DDEBF7\"/>") + Tc("tall", "<w:vMerge w:val=\"restart\"/>") + "</w:tr>"
                    + "<w:tr><w:tc><w:tcPr></w:tcPr>" + Wp(Wr("a") + Pic(9000000, 3000000)) + "</w:tc>" + Tc("b") + Tc("", "<w:vMerge/>") + "</w:tr></w:tbl>"
                    + Wp(Wr("In the line ") + Pic(381000, 190500) + Pic(1524000, 1143000, "wrapSquare") + Pic(9000000, 3000000, "wrapTopAndBottom"))
                    + Wp("<m:oMathPara><m:oMath><m:f><m:num><m:r><w:rPr><w:sz w:val=\"40\"/></w:rPr><m:t>a&#x2061;</m:t></m:r></m:num><m:den><m:sSup><m:e><m:r><m:t>b</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup>"
                        + "<m:rad><m:deg><m:r><m:t>3</m:t></m:r></m:deg><m:e><m:r><m:t>x</m:t></m:r></m:e></m:rad></m:den></m:f></m:oMath></m:oMathPara>")
                    + Wp("<m:oMath><m:r><m:t>k=2</m:t></m:r><m:f><m:num><m:r><m:t>p</m:t></m:r></m:num><m:den><m:r><m:t>q</m:t></m:r></m:den></m:f><m:r><m:t>y</m:t></m:r></m:oMath>")
                    + Wp("<w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"8\" w:space=\"4\" w:color=\"4F81BD\"/></w:pBdr></w:pPr>" + Wr("A line below this one."))
                    + Wp("<w:r><w:drawing><wp:inline><wp:extent cx=\"4572000\" cy=\"2743200\"/><a:graphic><a:graphicData><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rChart\"/>"
                        + "</a:graphicData></a:graphic></wp:inline></w:drawing></w:r>")
                    + Wp(Wr("Before the box.") + "<w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><wp:anchor><a:graphic><a:graphicData><wps:wsp><wps:txbx><w:txbxContent>"
                        + Wp(Wr("From a text box.")) + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice><mc:Fallback><w:pict/></mc:Fallback></mc:AlternateContent></w:r>")
                    + Wp(Wr("Before the frame.") + "<w:r><w:drawing><wp:anchor><wp:positionH relativeFrom=\"margin\"><wp:align>right</wp:align></wp:positionH><wp:extent cx=\"1800000\" cy=\"600000\"/><a:graphic><a:graphicData><wps:wsp>"
                        + "<wps:spPr><a:solidFill><a:srgbClr val=\"FFF2CC\"/></a:solidFill><a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln></wps:spPr>"
                        + "<wps:txbx><w:txbxContent>" + Wp(Wr("In a frame.") + Pic(381000, 190500)) + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>")
                    + Wp(Wr("2. A task typed by hand")) + Wp(Wr("3.5 is a number, not a task")) + Wp(Wr("3) Another task"))
                    + "<w:tbl><w:tblGrid><w:gridCol w:w=\"500\"/><w:gridCol w:w=\"8000\"/></w:tblGrid><w:tr>" + Tc("Characteristics") + Tc("wide enough") + "</w:tr></w:tbl>"
                    + Wp("<w:r><w:t></w:t></w:r>", "<w:rPr><w:sz w:val=\"4\"/></w:rPr>")
                    + Wp(Wr("Before the break.") + "<w:r><w:br w:type=\"page\"/></w:r>" + Wr("Behind it.") + "<w:del><w:r><w:delText>gone</w:delText></w:r></w:del>")
                    + Wp(Wr(@"W = \frac{1}{2}mv^{2}"))
                    + "<w:sectPr><w:titlePg/><w:headerReference w:type=\"first\" r:id=\"rFirst\"/><w:footerReference w:type=\"first\" r:id=\"rFirstFoot\"/><w:headerReference w:type=\"default\" r:id=\"rHead\"/><w:footerReference w:type=\"default\" r:id=\"rFoot\"/><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1417\" w:right=\"1701\" w:bottom=\"1417\" w:left=\"1701\"/></w:sectPr>";
                var wordParts = new Dictionary<string, string>
                {
                    ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"t\" Target=\"word/document.xml\"/></Relationships>",
                    ["word/document.xml"] = "<w:document " + ns + "><w:body>" + wordBody + "</w:body></w:document>",
                    ["word/_rels/document.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                        + "<Relationship Id=\"rS\" Type=\"t\" Target=\"styles.xml\"/><Relationship Id=\"rN\" Type=\"t\" Target=\"numbering.xml\"/><Relationship Id=\"rF\" Type=\"t\" Target=\"footnotes.xml\"/>"
                        + "<Relationship Id=\"rFoot\" Type=\"t\" Target=\"footer1.xml\"/><Relationship Id=\"rHead\" Type=\"t\" Target=\"header1.xml\"/><Relationship Id=\"rFirst\" Type=\"t\" Target=\"header2.xml\"/><Relationship Id=\"rFirstFoot\" Type=\"t\" Target=\"footer2.xml\"/><Relationship Id=\"rChart\" Type=\"t\" Target=\"charts/chart1.xml\"/><Relationship Id=\"rImg\" Type=\"t\" Target=\"media/image1.png\"/>"
                        + "<Relationship Id=\"rLink\" Type=\"t\" Target=\"https://example.org/page\" TargetMode=\"External\"/></Relationships>",
                    ["word/styles.xml"] = "<w:styles " + ns + "><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Georgia\"/><w:sz w:val=\"24\"/></w:rPr></w:rPrDefault></w:docDefaults>"
                        + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"160\"/></w:pPr></w:style>"
                        + "<w:style w:type=\"paragraph\" w:styleId=\"Ttulo1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:rPr><w:b/><w:sz w:val=\"36\"/><w:color w:val=\"2F5496\"/></w:rPr></w:style>"
                        + "<w:style w:type=\"paragraph\" w:styleId=\"TDC1\"><w:name w:val=\"toc 1\"/></w:style></w:styles>",
                    ["word/charts/chart1.xml"] = "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><c:chart>"
                        + "<c:title><c:tx><c:rich><a:p><a:r><a:t>Visitors</a:t></a:r></a:p></c:rich></c:tx></c:title><c:plotArea><c:barChart><c:barDir val=\"col\"/><c:grouping val=\"clustered\"/>"
                        + string.Concat(new[] { ("Weekdays", "120|180.5|90"), ("Weekends", "210|340|") }.Select((sr, si) => "<c:ser><c:idx val=\"" + si + "\"/><c:tx><c:strRef><c:strCache><c:pt idx=\"0\"><c:v>" + sr.Item1
                            + "</c:v></c:pt></c:strCache></c:strRef></c:tx><c:cat><c:strRef><c:strCache><c:ptCount val=\"3\"/><c:pt idx=\"0\"><c:v>Spring</c:v></c:pt><c:pt idx=\"1\"><c:v>Summer</c:v></c:pt><c:pt idx=\"2\"><c:v>Autumn</c:v></c:pt>"
                            + "</c:strCache></c:strRef></c:cat><c:val><c:numRef><c:numCache><c:formatCode>General</c:formatCode>"
                            + string.Concat(sr.Item2.Split('|').Select((v, vi) => v.Length == 0 ? "" : "<c:pt idx=\"" + vi + "\"><c:v>" + v + "</c:v></c:pt>")) + "</c:numCache></c:numRef></c:val></c:ser>"))
                        + "</c:barChart><c:catAx><c:title><c:tx><c:rich><a:p><a:r><a:t>Season</a:t></a:r></a:p></c:rich></c:tx></c:title></c:catAx>"
                        + "<c:valAx><c:title><c:tx><c:rich><a:p><a:r><a:t>People</a:t></a:r></a:p></c:rich></c:tx></c:title></c:valAx></c:plotArea></c:chart></c:chartSpace>",
                    ["word/numbering.xml"] = "<w:numbering " + ns + "><w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl>"
                        + "<w:lvl w:ilvl=\"1\"><w:numFmt w:val=\"lowerLetter\"/><w:lvlText w:val=\"%2)\"/><w:pPr><w:ind w:left=\"1800\" w:hanging=\"360\"/></w:pPr></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>",
                    ["word/footnotes.xml"] = "<w:footnotes " + ns + "><w:footnote w:type=\"separator\" w:id=\"0\"><w:p/></w:footnote><w:footnote w:id=\"2\">" + Wp("<w:r><w:footnoteRef/></w:r>" + Wr(" The note.")) + "</w:footnote></w:footnotes>",
                    ["word/header1.xml"] = "<w:hdr " + ns + ">" + Wp(Wr("Report") + "<w:r><w:tab/></w:r><w:r><w:tab/></w:r>" + Pic(1905000, 1428750), "<w:tabs><w:tab w:val=\"center\" w:pos=\"4680\"/></w:tabs>") + "</w:hdr>",
                    ["word/header2.xml"] = "<w:hdr " + ns + ">" + Wp(Wr("Cover"), "<w:jc w:val=\"center\"/>") + "</w:hdr>",
                    ["word/footer2.xml"] = "<w:ftr " + ns + ">" + Wp(Wr("The left text of this footer reaches past the middle of it")) + Wp(Wr("Middle"), "<w:jc w:val=\"center\"/>") + "</w:ftr>",
                    ["word/_rels/header1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rImg\" Type=\"t\" Target=\"media/image1.png\"/></Relationships>",
                    ["word/footer1.xml"] = "<w:ftr " + ns + ">" + Wp(Wr("Page ") + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>"
                        + Wr("7") + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>", "<w:jc w:val=\"center\"/>") + "</w:ftr>",
                };
                string wordFile = Path.Combine(outDir, "from-word.docx");
                File.Delete(wordFile);
                using (var zip = System.IO.Compression.ZipFile.Open(wordFile, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var (name, xml) in wordParts)
                    {
                        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new System.Text.UTF8Encoding(false));
                        w.Write(xml);
                    }
                    using var img = zip.CreateEntry("word/media/image1.png").Open();
                    byte[] picture = MakePicture(64, 48, false);
                    img.Write(picture, 0, picture.Length);
                }
                var word = DocxImport.Read(wordFile);
                // A Word file that another program holds open for writing, as Word does, is read all the same.
                bool whileOpen;
                using (new FileStream(wordFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    try { whileOpen = DocxImport.Read(wordFile).Doc.Body.Count == word.Doc.Body.Count; }
                    catch (IOException) { whileOpen = false; }
                }
                Check("word: a .docx that is open in another program, as in Word, is read all the same", whileOpen);
                var wdoc = word.Doc;
                var wb = wdoc.Body;
                var weng = new LayoutEngine(wdoc);
                weng.Update();
                int room = wdoc.Page.ContentWidth;
                var h1 = wdoc.FindPara("Heading1");
                int headAt = wb.FindIndex(p => p.Style == "Heading1" && !p.Toc && p.Text == "First part");
                var mixed = headAt >= 0 && headAt + 1 < wb.Count ? wb[headAt + 1] : new Paragraph();
                var grid = wb.FirstOrDefault(p => p.Table != null)?.Table;
                var pics = wb.SelectMany(p => p.Objects).Select(o => o.Obj).OfType<Picture>().ToList();
                var eq = wb.SelectMany(p => p.Objects).Select(o => o.Obj).OfType<Equation>().FirstOrDefault();
                var wordChart = wb.SelectMany(p => p.Objects).Select(o => o.Obj).OfType<Chart>().FirstOrDefault();
                bool wChart = wordChart is { Kind: "bar", Title: "Visitors", XTitle: "Season", YTitle: "People", Width: 4572000, Height: 2743200 } && string.Join(",", wordChart.Cats) == "Spring,Summer,Autumn"
                    && wordChart.Series.Count == 2 && wordChart.Series[0].Name == "Weekdays" && string.Join(",", wordChart.Series[0].Values) == "120000,180500,90000" && string.Join(",", wordChart.Series[1].Values) == "210000,340000,0";
                bool wStyles = h1 is { } hs && hs.Char.Bold && hs.Char.Size == 36 && hs.Char.Color == 0x2F5496 && hs.Char.Font == "Georgia" && wdoc.FindPara("Normal") is { After: 160 * 635 } ns0 && ns0.Char.Size == 24;
                bool wText = mixed.Text.StartsWith("Plain, bold and a link") && mixed.Runs.Any(r => r.Props.Bold == true && r.Text == "bold") && mixed.Runs.Any(r => r.Props.Link == "https://example.org/page")
                    && mixed.Align == Align.Center && wdoc.AfterOf(wb[headAt]) > 0 && mixed.Before == Math.Max(0, 240 * 635 - wdoc.AfterOf(wb[headAt])) && mixed.Line == 1500 && mixed.IndentLeft == 720 * 635 && mixed.IndentFirst == -360 * 635
                    && wdoc.Notes.Count == 1 && wdoc.Notes.Values.First().Paras[0].Text == "The note.";
                bool wList = wb.Any(p => p.List == "alpha-paren" && p.ListLevel == 1 && p.Text == "One" && p.IndentLeft == 360 * 635);
                bool wTable = grid is { Columns: 3, Lines: "horizontal" } && grid.Rows.Count == 2 && grid.Rows[0][0] == grid.Rows[0][1] && grid.Rows[0][2] == grid.Rows[1][2] && grid.Rows[1][0] != grid.Rows[1][1]
                    && grid.Width == room && grid.Shades.Count == 1 && grid.Shades.Values.First() == 0xDDEBF7;
                var inCell = grid == null ? null : wdoc.Cells[grid.Rows[1][0]].Paras[0].Objects.Select(o => o.Obj).OfType<Picture>().FirstOrDefault();
                bool wPics = inCell is { Floating: false, Block: false } && inCell.Width <= grid!.Widths[0] - 2 * LayoutEngine.CellPadX && inCell.Width > room / 4 && pics.Count == 3 && pics[0] is { Floating: false, Block: false, Width: 381000 } && pics[1] is { Wrap: "square", Width: 1524000 } && pics[1].Dx == room - 1524000
                    && pics[2] is { Floating: false, Block: true } && pics[2].Width == room && wdoc.Media.Count == 1;
                var eqPara = wb.FirstOrDefault(p => p.Objects.Any(o => o.Obj is Equation));
                bool wMath = eq is { Block: true } && eq.Source.Replace(" ", "") == "(a)/(b^(2)(x)^(1/3))" && eqPara != null && eqPara.FormatOfChar(eqPara.Objects.First(o => o.Obj is Equation).Off).Props.Size == 40;
                var framedBox = wb.FirstOrDefault(p => p.Table is { Columns: 1 })?.Table;
                bool wBox = framedBox is { Lines: "all" } && framedBox.Widths[0] == 1800000 && framedBox.Shades.Values.FirstOrDefault() == 0xFFF2CC && wdoc.Cells[framedBox.Rows[0][0]].Paras[0].Text.StartsWith("In a frame.") && wdoc.Cells[framedBox.Rows[0][0]].Paras[0].Objects.Any(o => o.Obj is Picture { Floating: false, Width: 381000 })
                    && wb.First(p => p.Table == framedBox).Align == Align.Right && wb[wb.FindIndex(p => p.Table == framedBox) - 1].Text == "Before the frame.";
                var fitted = wb.LastOrDefault(p => p.Table is { Columns: 2 })?.Table;
                bool wFit = fitted != null && fitted.Width == 8500 * 635 && fitted.Widths[0] > 500 * 635 && wdoc.Cells[fitted.Rows[0][0]].Paras[0].Lines is { Count: 1 }
                    && wb.Any(p => p.Length == 0 && p.Mark?.Props.Size == 4);
                bool wTasks = wFit && wb.Any(p => p.Together && p.Text.StartsWith("2. A task")) && wb.Any(p => p.Together && p.Text.StartsWith("3) Another"));
                bool wFlow = wTasks && wBox && wb.Any(p => p.Text == "From a text box.") && wb.Any(p => p.Text == "Before the break.") && wb.Any(p => p.Text == "Behind it." && p.PageBreakBefore)
                    && wb.Count(p => p.Toc) == 3 && wb.First(p => p.Toc && p.Text.Contains('\t')).Text.StartsWith("First part\t") && !wb.Any(p => p.Text.Contains("\t9"));
                bool wLogo = wdoc.Header is { Left: "Report", Centre: "", Right: "", Picture: { Place: 2 } headLogo } && (headLogo.Width, headLogo.Height) == weng.FitBandPicture(false, 1905000, 1428750, true)
                    && headLogo.Height < 1428750 && weng.Pages.Count > 1 && weng.Pages[1].BandPics is { Count: 1 } onPage && onPage[0].X + headLogo.Width == wdoc.Page.Width - wdoc.Page.Right && onPage[0].Y + headLogo.Height < wdoc.Page.Top;
                bool wFirst = wdoc.BandsSkipFirst && wdoc.FirstHeader is { Left: "", Centre: "Cover", Right: "", Picture: null } && wdoc.FirstFooter is { Left: "The left text of this footer reaches past the middle of it", Centre: "\nMiddle", Right: "" }
                    && weng.Pages[0].BandPics.Count == 0 && weng.Pages[0].Bands is { Count: 3 } onFirst && onFirst[2].Baseline > onFirst[1].Baseline && onFirst[0].Text == "Cover";
                wMath &= wb.SelectMany(p => p.Objects).Select(o => o.Obj).OfType<Equation>().Any(q => q.Block && q.Source.Contains("(1)/(2)"));
                // A number before a fraction and a letter behind it stay outside of the fraction.
                wMath &= wb.SelectMany(p => p.Objects).Select(o => o.Obj).OfType<Equation>().Any(q => q.Source.Contains("k=2") && q.Tree.Items.Count == 5
                    && q.Tree.Items[3] is MFrac { Num.Items.Count: 1, Den.Items.Count: 1 });
                // The line Word draws below a paragraph: one point thick, four points from the text, in its colour.
                wMath &= wb.Any(p => p.Text == "A line below this one." && p.Borders is { Top: 0, Bottom: 12700, Left: 0, Right: 0, Gap: 50800, Color: 0x4F81BD });
                bool wPage = wLogo && wFirst && wdoc.Page.Width == 11906 * 635 && wdoc.Page.Left == 1701 * 635 && wdoc.Footer.Centre == "Page {page}" && wdoc.WidowControl;
                string wordSaved = Path.Combine(outDir, "from-word.docu");
                RksFile.Save(wdoc, weng, wordSaved);
                var wordOpen = RksFile.Open(wordSaved);
                bool wKept = wordOpen.Stored && Diff(weng, wordOpen.Engine) == null && Invariants(weng) == null && word.Notes.Count >= 4;
                Check("word: a .docx is read with its styles, text, list, table, pictures, footnote, link, equation, chart, text box, page break, page, the logo of its header and the header of its first page, and saved as a Reiks document",
                    wStyles && wText && wList && wTable && wPics && wMath && wChart && wFlow && wPage && wKept,
                    $"styles {wStyles} text {wText} list {wList} table {wTable} pictures {wPics} math {wMath} ({eq?.Source}) chart {wChart} flow {wFlow} page {wPage} logo {wLogo} first page {wFirst} kept {wKept}; {word.Notes.Count} notes");
                string refusedWord = "";
                try { DocxImport.Read(Path.Combine(outDir, "ending.docu")); }
                catch (InvalidDataException ex) { refusedWord = ex.Message; }
                Check("word: a file that is not a Word document is refused in plain words", refusedWord.Contains("not a Word document"), refusedWord);
            }

            var hd = Document.NewDefault();
            hd.Page.Left = hd.Page.Right = 2400000;
            hd.Hyphenation = "es";
            string spanishText = string.Join(" ", Enumerable.Repeat("La administración internacional presentó extraordinariamente importantes consideraciones constitucionales fundamentales relacionadas directamente con responsabilidades administrativas.", 6));
            hd.Body[0].Align = Align.Justify;
            hd.Body[0].Runs.Add(new Run(spanishText));
            hd.Body[0].Touch();
            var he = new LayoutEngine(hd);
            he.Update();
            var hyPara = hd.Body[0];
            var hyLines = hyPara.Lines!.Where(l => l.Hyphen).ToList();
            bool hyPlaces = hyLines.Count > 0 && hyLines.All(l => Array.IndexOf(hyPara.Hyphens!, l.End) >= 0 && char.IsLetter(hyPara.Text[l.End]) && char.IsLetter(hyPara.Text[l.End - 1])
                && l.XOffset(l.Length) <= hd.Page.ContentWidth);
            var plain = Document.NewDefault();
            plain.Page.Left = plain.Page.Right = 2400000;
            plain.Body[0].Align = Align.Justify;
            plain.Body[0].Runs.Add(new Run(spanishText));
            plain.Body[0].Touch();
            var plainEng = new LayoutEngine(plain);
            plainEng.Update();
            bool hyFewer = hyPara.Lines!.Count <= plain.Body[0].Lines!.Count && plain.Body[0].Lines!.All(l => !l.Hyphen);
            string hf = Path.Combine(outDir, "hyphens.docu");
            RksFile.Save(hd, he, hf);
            var ho = RksFile.Open(hf);
            bool hyKept = ho.Stored && Diff(he, ho.Engine) == null && ho.Doc.Hyphenation == "es" && ho.Doc.Body[0].ValidHyphens.SequenceEqual(hyPara.Hyphens!)
                && ho.Doc.Body[0].Lines!.Count(l => l.Hyphen) == hyLines.Count && Invariants(he) == null && Diff(he, Fresh(hd)) == null;
            PageRenderer.SavePng(PageRenderer.RenderPage(he, 0, 1.0), Path.Combine(outDir, "hyphens-p1.png"));
            PdfExport.Write(he, Path.Combine(outDir, "hyphens.pdf"));
            var hed = new Editor(hd, he);
            hed.SetTextFlow(true, "");
            bool hyOff = hd.Body[0].Lines!.All(l => !l.Hyphen) && hd.Body[0].Lines!.Count == plain.Body[0].Lines!.Count;
            hed.SetTextFlow(true, "es");
            bool hyOn = hd.Body[0].Lines!.Count(l => l.Hyphen) == hyLines.Count;
            Check("hyphens: lines end inside words with a hyphen that has room, kept in the file, switched off and on",
                hyPlaces && hyFewer && hyKept && hyOff && hyOn, $"{hyLines.Count} divided lines of {hyPara.Lines!.Count}; places {hyPlaces} fewer {hyFewer} kept {hyKept} off {hyOff} on {hyOn} / {ho.Message}");

            // Lines left alone: counted in a long document with the rule and without it.
            int Alone(Document doc) => doc.Body.Count(bp => bp.Table == null && bp.Lines is { Count: >= 2 } bl && (bl[0].Page != bl[1].Page || bl[^1].Page != bl[^2].Page));
            var wd = Bench.Make(300);
            wd.WidowControl = false;
            // The rule alone: the room between paragraphs, giving, would bring some of those lines back by itself.
            wd.AdaptSpacing = false;
            var we = new LayoutEngine(wd);
            we.Update();
            int aloneWithout = Alone(wd), pagesWithout = we.Pages.Count;
            wd.WidowControl = true;
            we.InvalidateAll();
            we.Update();
            int aloneWith = Alone(wd);
            string wf = Path.Combine(outDir, "kept-lines.docu");
            RksFile.Save(wd, we, wf);
            var wo = RksFile.Open(wf);
            Check("pages: no line of a paragraph is left alone at the bottom or the top of a page, and the file says so",
                aloneWithout > 3 && aloneWith == 0 && we.Pages.Count >= pagesWithout && Invariants(we) == null && Diff(we, Fresh(wd)) == null
                && wo.Stored && wo.Doc.WidowControl && Diff(we, wo.Engine) == null,
                $"alone without the rule {aloneWithout}, with it {aloneWith}; pages {pagesWithout} and {we.Pages.Count}; {wo.Message}");

            var kd = Document.NewDefault();
            kd.Body[0].Runs.Add(new Run("hello "));
            kd.Body[0].Runs.Add(new Run("wORLD. second", new CharProps(Bold: true)));
            kd.Body[0].Runs.Add(new Run(" one"));
            kd.Body[0].Touch();
            var ke = new LayoutEngine(kd);
            ke.Update();
            var ked = new Editor(kd, ke);
            string Case(string mode)
            {
                ked.Select(new Pos(0, 0), new Pos(0, kd.Body[0].Length));
                ked.ChangeCase(mode);
                return kd.Body[0].Text;
            }
            string k1 = Case("sentence"), k2 = Case("words"), k3 = Case("upper"), k4 = Case("toggle");
            bool boldKept = kd.Body[0].Runs.Any(r => r.Props.Bold == true && r.Text == "world. second");
            ked.Undo();
            Check("case: sentence, words, upper and toggle; formats kept; one step to undo",
                k1 == "Hello world. Second one" && k2 == "Hello World. Second One" && k3 == "HELLO WORLD. SECOND ONE" && k4 == "hello world. second one"
                && boldKept && kd.Body[0].Text == k3, k1 + " / " + k2 + " / " + k3 + " / " + k4 + " / " + boldKept);
        }

        // The caret is as tall as the text, whatever the line spacing.
        ed.MoveTo(new Pos(2, 10), false);
        double caretBefore = win.View.CaretBox().Height;
        ed.SetLine(3000);
        Idle();
        var wide = ed.Layout.LineAt(0, ed.Caret, false, out _);
        double caretAfter = win.View.CaretBox().Height;
        Check("window: the caret keeps the height of the text when the line spacing grows",
            Math.Abs(caretAfter - caretBefore) < 0.01 && caretAfter > 8 && caretAfter < U.Dip(wide.Height) / 2, $"{caretBefore:0.##} {caretAfter:0.##} line {U.Dip(wide.Height):0.##}");
        ed.Undo();
        ed.Dirty = false;
        win.Close();
    }

    /// <summary>Opens a box off screen and saves a picture of what it draws.</summary>
    static void ShootDialog(Window dlg, string file, Action? wait = null)
    {
        dlg.WindowStartupLocation = WindowStartupLocation.Manual;
        dlg.Left = dlg.Top = -30000;
        dlg.ShowActivated = false;
        dlg.Show();
        Idle();
        wait?.Invoke();
        Idle();
        var content = (FrameworkElement)dlg.Content;
        var area = new Rect(0, 0, (int)content.ActualWidth, (int)content.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(dlg.Background, null, area);
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, area);
        }
        var shot = new RenderTargetBitmap((int)area.Width, (int)area.Height, 96, 96, PixelFormats.Pbgra32);
        shot.Render(dv);
        PageRenderer.SavePng(shot, file);
        dlg.Close();
    }

    static int Brightness(BitmapSource b, int x, int y)
    {
        var px = new byte[4];
        b.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return px[0] + px[1] + px[2];
    }

    public static int Run(string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            log.Add($"Reiks Document self-test, format {RksFile.FormatVersion}, layout rules {LayoutEngine.RulesVersion}");
            Check("latex mode: unless something else is chosen, an equation made from LaTeX stands in the line", Editor.LatexForm == 1);
            // The checks of the LaTeX mode say themselves where the equations are to stand; all others have it as it comes.
            Editor.LatexForm = 0;
            // Which keys raise and lower text on the keyboard in use (Ctrl with the + sign, Ctrl with the = sign).
            foreach (char sign in "+-=")
            {
                var keys = new List<string>();
                foreach (System.Windows.Input.Key key in Enum.GetValues(typeof(System.Windows.Input.Key)))
                    foreach (bool shift in new[] { false, true })
                        if (PageView.KeyChar(key, shift) == sign && !keys.Contains((shift ? "Shift+" : "") + key)) keys.Add((shift ? "Shift+" : "") + key);
                log.Add($"      keys: on this keyboard the sign {sign} is typed with {string.Join(", ", keys)}");
            }
            Sample("basic", Basic(), outDir, 2);
            Sample("long", Long(), outDir, 1);
            Sample("notes", Notes(), outDir, 2);
            Typing();
            Sample("math", MathDoc(), outDir, 2);
            Sample("charts", ChartDoc(), outDir, 9);
            Sample("format", FormatDoc(), outDir, 1);
            Sample("lists", ListDoc(), outDir, 1);
            Sample("bands", BandDoc(), outDir, 2);
            Sample("band-pictures", LogoDoc(), outDir, 2);
            Sample("band-first", FirstDoc(), outDir, 1);
            Sample("band-lines", LinesDoc(), outDir, 2);
            LatexChecks();
        PageNumberChecks();
        ReferenceChecks();
        foreach (string system in new[] { Citations.Harvard, Citations.Chicago, Citations.Mla, Citations.Ieee, Citations.Vancouver })
            Sample("refs-" + system, RefsDoc(system), outDir, 1);
        StyleChecks();
        CoverChecks();
        BorderChecks();
        SpacingChecks(outDir);
        WordRobustChecks(outDir);
        Sample("borders", BordersDoc(), outDir, 1);
        Sample("spacing", SpacingDoc(), outDir, 2);
        var spacingOpen = RksFile.Open(Path.Combine(outDir, "spacing.docu"));
        Check("spacing: the choice to let the room between paragraphs give is stored with the document", spacingOpen.Doc.AdaptSpacing && spacingOpen.Stored);
            Sample("latex", LatexDoc(), outDir, 1);
            Sample("tables", TableDoc(), outDir, 2);
            Tables();
            Bands(outDir);
            Sample("floats", FloatDoc(), outDir, 3);
            Formulas();
            Tools();
            Sample("sample", SampleDoc.Build(), outDir, 9);
            foreach (string name in new[] { "basic", "notes", "math", "charts", "format", "lists", "floats", "tables", "sample" })
            {
                var opened = RksFile.Open(Path.Combine(outDir, name + ".docu"));
                string file = Path.Combine(outDir, name + ".pdf");
                PdfExport.Write(opened.Engine, file);
                Check("pdf: " + name + " is written with every page", PdfExport.Verify(file) == opened.Engine.Pages.Count, PdfExport.Verify(file) + " pages, " + new FileInfo(file).Length + " bytes");
            }
            Edits(outDir);
            WindowCheck(outDir);
        }
        catch (Exception ex)
        {
            failures++;
            log.Add("FAIL  exception: " + ex);
        }
        log.Add(failures == 0 ? "RESULT: all checks passed" : $"RESULT: {failures} check(s) failed");
        try { File.WriteAllLines(Path.Combine(outDir, "selftest.log"), log); }
        catch (Exception) { return 2; }
        return failures == 0 ? 0 : 1;
    }
}
