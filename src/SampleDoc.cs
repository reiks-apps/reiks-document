using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReiksDocument;

/// <summary>The sample document: one of everything the program can do. ReiksDocument.exe --sample file.docu writes it.</summary>
static class SampleDoc
{
    static Run T(string text, CharProps props = default, string? style = null) => new(text, props, style);

    static Paragraph P(Document d, string style, Align? align, params Run[] runs)
    {
        var p = new Paragraph { Style = style, Align = align };
        p.Runs.AddRange(runs);
        d.Body.Add(p);
        return p;
    }

    static Run Obj(Inline o, CharProps props = default) => Run.Object(o, new Run("", props));

    static Run Eq(string source, bool block = false) => Obj(Equation.From(source, block));

    static Run Note(Document d, params Run[] runs)
    {
        var n = d.NewNote();
        n.Paras[0].Runs.AddRange(runs);
        n.Paras[0].Touch();
        return Obj(new NoteRef(n.Id));
    }

    static Run Picture(Document d, byte[] data, bool jpeg, int widthPx, int heightPx, bool block)
    {
        string name = RksFile.MediaName(data, jpeg);
        d.Media[name] = data;
        return Obj(new Picture(name, widthPx * 9525, heightPx * 9525, block));
    }

    static Run Floating(Document d, byte[] data, bool jpeg, int widthPx, int heightPx, string wrap, int dx, int dy)
    {
        string name = RksFile.MediaName(data, jpeg);
        d.Media[name] = data;
        return Obj(new Picture(name, widthPx * 9525, heightPx * 9525, false, wrap, dx, dy));
    }

    /// <summary>A table with one run per cell.</summary>
    static void Table(Document d, int[] widths, Align? align, params Run[][] rows)
    {
        var grid = new TableGrid { Widths = widths };
        foreach (var row in rows)
        {
            var ids = new int[widths.Length];
            for (int c = 0; c < ids.Length; c++)
            {
                var cell = d.NewCell();
                if (c < row.Length && row[c].Text.Length > 0) cell.Paras[0].Runs.Add(row[c]);
                ids[c] = cell.Id;
            }
            grid.Rows.Add(ids);
        }
        d.Body.Add(new Paragraph { Style = "Normal", Align = align, Table = grid });
    }

    static byte[] Encode(DrawingVisual dv, int w, int h, bool jpeg)
    {
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        BitmapEncoder enc = jpeg ? new JpegBitmapEncoder { QualityLevel = 90 } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static Brush Fill(int rgb) => new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    /// <summary>A small drawn landscape, so the sample needs no picture file.</summary>
    static byte[] Landscape(int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x7F, 0xBD, 0xF2), Color.FromRgb(0xEA, 0xF5, 0xFD), 90), null, new Rect(0, 0, w, h));
            dc.DrawEllipse(Fill(0xF9D54A), null, new Point(0.76 * w, 0.30 * h), 0.13 * h, 0.13 * h);
            dc.DrawEllipse(Fill(0x6DB164), null, new Point(0.28 * w, 1.08 * h), 0.58 * w, 0.52 * h);
            dc.DrawEllipse(Fill(0x3F8A4B), null, new Point(0.82 * w, 1.14 * h), 0.52 * w, 0.48 * h);
        }
        return Encode(dv, w, h, true);
    }

    static byte[] Badge(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var c = new Point(size / 2.0, size / 2.0);
            dc.DrawEllipse(Fill(0x0072B2), null, c, size * 0.48, size * 0.48);
            dc.DrawEllipse(null, new Pen(Brushes.White, size * 0.09), c, size * 0.27, size * 0.27);
        }
        return Encode(dv, size, size, false);
    }

    public static Document Build()
    {
        var d = Document.NewDefault();
        d.Body.Clear();
        string br = Document.LineBreak.ToString();

        d.Header = new Band("", "", "A sample Reiks document");
        d.Footer = new Band("", "Page {page} of {pages}", "");
        d.BandsSkipFirst = true;
        P(d, "Title", null, T("A sample Reiks document"));
        P(d, "Normal", null,
            T("This document uses everything Reiks Document can do. It is stored as a Reiks document, a file ending in .docu. " +
              "Such a file holds the text and, next to it, the finished layout: the place of every line and every object on every page, in whole units."));

        P(d, "Heading1", null, T("Text"));
        P(d, "Normal", null,
            T("Text can be "), T("bold", new(Bold: true)), T(", "), T("italic", new(Italic: true)), T(", "), T("underlined", new(Underline: true)),
            T(" or "), T("all three at once", new(Bold: true, Italic: true, Underline: true)), T(". It can change its font, as in "),
            T("Times New Roman", new(Font: "Times New Roman")), T(", "), T("Georgia", new(Font: "Georgia")), T(" or "),
            T("Consolas", new(Font: "Consolas")), T(", and its size, from "), T("small", new(Size: 16)), T(" to "), T("large", new(Size: 36)),
            T(". It can be "), T("coloured", new(Color: 0xC00000)), T(", "), T("highlighted", new(Highlight: 0xFFFF00)),
            T(" or "), T("struck through", new(Strike: true)), T(", and raised or lowered, as in x"), T("2", new(Vert: 1)), T(" and H"), T("2", new(Vert: 2)), T("O."));
        P(d, "Heading2", null, T("Alignment"));
        P(d, "Normal", Align.Justify,
            T("This paragraph is justified: every line but the last is stretched until it reaches both edges of the text area. " +
              "The extra room is shared among the spaces of the line by a fixed rule, so that the same text always ends up at the same places. " +
              "Words are never split; a line ends at a space or after a hyphen, as in well-known or state-of-the-art."));
        P(d, "Normal", Align.Center, T("This paragraph is centred."));
        P(d, "Normal", Align.Right, T("And this one is set to the right."));
        var wide = P(d, "Normal", null,
            T("A paragraph can stand further in from the left edge and have more room between its lines than its style gives it, as this one does: " +
              "it is moved in by one centimetre and its lines are one and a half times as far apart."));
        wide.IndentLeft = 360000;
        wide.Line = 1500;
        P(d, "Normal", null, T("Text can be a link, as in "),
            T("example.org", new CharProps(Link: "https://example.org", Color: Editor.LinkColour, Underline: true)),
            T(". In a PDF the link works, the headings are bookmarks, and the entries of the table of contents lead to their pages."));
        var own = P(d, "Normal", Align.Justify, T("A single paragraph can have room of its own above and below, and its own indents. This one stands in from both sides, has more room around it, and its first line stands out to the left of the others."));
        (own.Before, own.After, own.IndentLeft, own.IndentRight, own.IndentFirst) = (6 * 12700, 14 * 12700, 540000, 540000, -270000);
        P(d, "Heading2", null, T("Tabs and line breaks"));
        P(d, "Normal", null,
            T("Paper\tWidth\t\tHeight" + br, new(Bold: true)),
            T("A4\t210 mm\t297 mm" + br + "A5\t148 mm\t210 mm" + br + "Letter\t216 mm\t279 mm"));

        P(d, "Heading2", null, T("Lists"));
        P(d, "Normal", null, T("A list has a bullet or a number in front of each of its paragraphs.")).List = "bullet";
        P(d, "Normal", null, T("The text of a list paragraph is set in from the left, also on its second line and the ones after it, as this longer paragraph shows once it reaches the right edge of the text area.")).List = "bullet";
        P(d, "Normal", null, T("Enter in an empty paragraph of a list ends the list.")).List = "bullet";
        P(d, "Normal", null, T("Numbers count on from the paragraph before.")).List = "number";
        P(d, "Normal", null, T("A paragraph that is not in the list makes the count start again.")).List = "number";
        P(d, "Normal", null, T("Formats work as in any other text: "), T("bold", new(Bold: true)), T(", "), T("italic", new(Italic: true)), T(" and the rest.")).List = "number";

        P(d, "Heading1", null, T("Images"));
        P(d, "Normal", null,
            T("An image can stand in the line like a letter "), Picture(d, Badge(96), false, 18, 18, false),
            T(" or on a line of its own, like the one below. Images are PNG or JPEG files, kept inside the document under a fingerprint of their content."));
        P(d, "Normal", Align.Center, Picture(d, Landscape(960, 540), true, 400, 225, true));
        P(d, "Normal", Align.Center, T("A drawn landscape, on its own line and centred", new(Italic: true, Size: 18)));
        P(d, "Normal", Align.Justify, Floating(d, Landscape(640, 400), true, 200, 125, "square", d.Page.ContentWidth - 200 * 9525, 0),
            T("An image can also float. Then it is no longer part of a line: it can be dragged to any place on the page, and the text keeps clear of it. " +
              "The image beside this paragraph lets the text pass on its wider side, and the lines next to it are simply shorter. " +
              "A floating image can also take the whole width for itself, with text only above and below it, or it can lie behind the text or in front of it, in which case the text takes no notice of it at all. " +
              "Where a floating image stands is stored in the document like everything else: its place on the page, and the place of every line that had to make room for it."));

        P(d, "Heading1", null, T("Footnotes"));
        P(d, "Normal", null,
            T("Footnotes are numbered by themselves, in the order of their marks"),
            Note(d, T("This is the first footnote. Its number comes from its place in the text, not from when it was written.")),
            T(". They stand at the foot of the page that holds their mark"),
            Note(d, T("A line stays on a page only if its footnotes fit there too. Otherwise the line moves to the next page and takes them along.")),
            T(". The text of a footnote can be formatted like any other text"),
            Note(d, T("For example with "), T("italic", new(Italic: true)), T(" or "), T("bold", new(Bold: true)), T(" words.")),
            T("."));

        P(d, "Heading1", null, T("Equations"));
        P(d, "Normal", null,
            T("An equation is typed in one line and can stand inside the text: the area of a circle is "), Eq("A = \\pi r^2"),
            T(", and in a right triangle "), Eq("a^2 + b^2 = c^2"), T(". Fractions such as "), Eq("(a+b)/(c+d)"), T(" and roots such as "),
            Eq("\\sqrt(x^2 + 1)"), T(" make their line as tall and as deep as they need."));
        P(d, "Normal", null, T("On a line of its own an equation is set larger:"));
        P(d, "Normal", Align.Center, Eq("x = (-b \\pm \\sqrt(b^2 - 4ac))/(2a)", true));
        P(d, "Normal", Align.Center, Eq("\\sum_(k=1)^n k = n(n+1)/2", true));
        P(d, "Normal", Align.Center, Eq("\\int_0^1 x^2 dx = 1/3", true));
        P(d, "Normal", Align.Center, Eq("lim_(n\\to\\infty) (1 + 1/n)^n = e", true));

        P(d, "Heading1", null, T("Charts")).PageBreakBefore = true;
        P(d, "Normal", null,
            T("A chart is drawn from a small table of numbers. The table is part of the document, so the chart can be changed at any time. " +
              "This heading starts a new page because a page break was put before it."));
        P(d, "Normal", Align.Center, Obj(new Chart("bar", "Visitors by season", new[] { "Spring", "Summer", "Autumn", "Winter" }, new[]
        {
            new Series("Last year", new long[] { 1200000, 2150000, 1480000, 640000 }),
            new Series("This year", new long[] { 1350000, 2400000, 1610000, 720000 }),
        }, 5040000, 2880000, true) { XTitle = "Season", YTitle = "Visitors" }));
        P(d, "Normal", Align.Center, Obj(new Chart("line", "Temperature over a week", new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }, new[]
        {
            new Series("Day", new long[] { 18500, 21000, 19200, 23700, 22000, 17500, 16000 }),
            new Series("Night", new long[] { 9000, 11500, 8200, 12000, 10700, 7000, -1500 }),
        }, 5040000, 2700000, true) { XTitle = "Day of the week", YTitle = "Degrees", AxisNames = "ends" }));
        P(d, "Normal", null,
            T("A chart can also stand in the line. "),
            Obj(new Chart("pie", "A day", new[] { "Sleep", "Work", "Meals", "Travel", "Free" }, new[]
            {
                new Series("Hours", new long[] { 8000, 8000, 2000, 1500, 4500 }),
            }, 2520000, 2160000, false), new(Size: 18)),
            T(" A pie shows the first series of its table: here the hours of a day."));

        P(d, "Normal", null,
            T("A graph can also be drawn from a formula. The formula is worked out when the graph is made, and the points are kept in the document as whole numbers, so that every program draws exactly the same curve."));
        P(d, "Normal", Align.Center, Obj(Plot.Make(new Chart("plot", "Two formulas", System.Array.Empty<string>(), System.Array.Empty<Series>(), 5040000, 2700000, true),
            "sin(x)/x; x^2/50 - 1/2", -15000, 15000)));

        P(d, "Heading1", null, T("Tables"));
        P(d, "Normal", null,
            T("A table has rows and columns of cells. Each cell holds text of its own, formatted like any other text; a row is as tall as its tallest cell, and a long table goes on over the pages row by row."));
        var bold = new CharProps(Bold: true);
        Table(d, new[] { 1300000, 1300000, 1300000, 1831200 }, Align.Center,
            new[] { T("Paper", bold), T("Width", bold), T("Height", bold), T("Used for", bold) },
            new[] { T("A4"), T("210 mm"), T("297 mm"), T("Letters, reports and most other documents in Europe") },
            new[] { T("A5"), T("148 mm"), T("210 mm"), T("Booklets and notepads") },
            new[] { T("Letter"), T("216 mm"), T("279 mm"), T("The usual size in North America") });
        P(d, "Normal", null);
        P(d, "Normal", null,
            T("Cells can be joined across columns and down rows, and a cell can be shaded. A table can have all its lines, only those above and below its rows, only those around it, or none, and the lines can be thin, medium or thick."));
        Table(d, new[] { 1500000, 1400000, 1400000, 1431200 }, Align.Center,
            new[] { T("Season", bold), T("Visitors", bold), T(""), T("Note", bold) },
            new[] { T(""), T("Weekdays", bold), T("Weekends", bold), T("") },
            new[] { T("Spring"), T("120"), T("210"), T("Counted at the gate. The summer figures include the two weeks of the festival.") },
            new[] { T("Summer"), T("180"), T("340"), T("") });
        var styled = d.Body[^1].Table!;
        void Join(int r0, int c0, int r1, int c1)
        {
            int id = styled.Rows[r0][c0];
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++) styled.Rows[r][c] = id;
        }
        Join(0, 1, 0, 2);
        Join(0, 0, 1, 0);
        Join(0, 3, 1, 3);
        Join(2, 3, 3, 3);
        foreach (int id in new[] { styled.Rows[0][0], styled.Rows[0][1], styled.Rows[0][3], styled.Rows[1][1], styled.Rows[1][2] }) styled.Shades[id] = 0xDDEBF7;
        styled.Lines = "horizontal";
        styled.LineWidth = 19050;
        P(d, "Normal", null);

        P(d, "Heading1", null, T("Pages"));
        P(d, "Normal", null,
            T("Every page but the first has a header and a footer here: the name of the document at the top right, and the number of the page with the number of pages at the bottom. " +
              "The document can be saved as a PDF, in which the text can still be selected and searched, and it can be printed."));

        P(d, "Heading1", null, T("The file"));
        P(d, "Normal", Align.Justify,
            T("When this document is opened, the program compares fingerprints: of the content, of the stored layout, and of every font file the layout was made with. " +
              "If all match, the pages are shown exactly as they were stored. If a font is different on this computer, the program says which one, lays the document out again by the rules of the format, and shows that. " +
              "The rules are written down in FORMAT.md, in whole numbers, so that another program can arrive at the same positions."));
        d.Sources.Add(new Source("s1", new[] { "Okafor, Ngozi", "Lindqvist, Sven" }, "2021", "Layouts that stay put", "Journal of Document Engineering 14(2)", ""));
        d.Sources.Add(new Source("s2", new[] { "Tanaka, Hiro" }, "2019", "Fonts and their fingerprints", "Northlight Press", ""));
        d.Body.Add(new Paragraph { Style = "Heading1", Runs = { new Run("Citations") } });
        d.Body.Add(new Paragraph
        {
            Runs =
            {
                new Run("A stored layout shows the same pages everywhere "), Run.Object(new Cite("s1"), new Run("")),
                new Run(", as long as the fonts are the same "), Run.Object(new Cite("s2"), new Run("")),
                new Run(". The sources are kept in the document, and the list below is written from them. An underline can be "),
                new Run("double", new CharProps(Underline: true, Under: "double")), new Run(", "),
                new Run("thick", new CharProps(Underline: true, Under: "thick")), new Run(", "),
                new Run("dotted", new CharProps(Underline: true, Under: "dotted")), new Run(", "),
                new Run("dashed", new CharProps(Underline: true, Under: "dashed")), new Run(" or "),
                new Run("wavy", new CharProps(Underline: true, Under: "wave")), new Run("."),
            },
        });
        d.Body.AddRange(Citations.BuildRefs(d));
        Contents.Put(d, 1);
        return d;
    }

    public static void Write(string path)
    {
        var doc = Build();
        var eng = new LayoutEngine(doc);
        eng.Update();
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        RksFile.Save(doc, eng, path);
    }
}
