using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReiksDocument;

/// <summary>Documents made by chance, to test the layout against tools\check_format.py: headings, lists, tables, footnotes,
/// pictures in the line and floating, equations, empty paragraphs, page breaks, blocks, lines at the edges, tall lines,
/// paper sizes and margins, mostly with the room between paragraphs giving at the ends of pages.
/// ReiksDocument.exe --random folder count seed writes random-0.docu … and random.log.</summary>
static class RandomDocs
{
    static readonly string[] Words =
    {
        "the", "room", "page", "line", "of", "a", "paragraph", "gives", "text", "section", "and", "letter", "school",
        "number", "table", "picture", "is", "to", "in", "every", "short", "longer", "word", "end", "begins", "with",
    };

    static string Text(Random r, int words)
    {
        var w = new List<string>(words);
        for (int i = 0; i < words; i++) w.Add(Words[r.Next(Words.Length)]);
        string s = string.Join(" ", w);
        return char.ToUpperInvariant(s[0]) + s[1..] + ".";
    }

    static byte[] Image(int w, int h, Color c)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(0, 0, w, h));
            dc.DrawEllipse(Brushes.White, null, new Point(w / 2.0, h / 2.0), w / 4.0, h / 4.0);
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static Run Pic(Document d, byte[] data, int w, int h, bool block, string wrap = "inline", int dx = 0, int dy = 0)
    {
        string name = RksFile.MediaName(data, false);
        d.Media[name] = data;
        return Run.Object(new Picture(name, w, h, block, wrap, dx, dy), new Run(""));
    }

    static Run Note(Document d, Random r)
    {
        var n = d.NewNote();
        n.Paras[0].Runs.Add(new Run(Text(r, 3 + r.Next(25))));
        n.Paras[0].Touch();
        return Run.Object(new NoteRef(n.Id), new Run(""));
    }

    static readonly string[] Sources = { @"\frac{a}{b} + x^{2}", @"\sqrt{x^{2} + 1}", @"\sum_{k=1}^{n} k", @"x_{1} + x_{2} = 7" };

    public static Document Make(int seed)
    {
        var r = new Random(seed);
        var d = Document.NewDefault();
        d.Body.Clear();
        d.AdaptSpacing = r.Next(10) < 9;
        d.WidowControl = r.Next(10) < 7;
        // Paper: A4 or Letter, now and then on its side; margins from half an inch to an inch and a half.
        bool a4 = r.Next(2) == 0, side = r.Next(8) == 0;
        int w = a4 ? 7560000 : 7772400, h = a4 ? 10692000 : 10058400;
        if (side) (w, h) = (h, w);
        int M() => 457200 + r.Next(914400);
        d.Page = new PageSetup { Width = w, Height = h, Top = M(), Bottom = M(), Left = M(), Right = M() };
        if (r.Next(3) == 0) d.Footer = new Band("", "Page {page} of {pages}", "");
        if (r.Next(4) == 0) d.Header = new Band(Text(r, 3), "", "Notes");

        var colours = new[] { Color.FromRgb(0x2B, 0x6C, 0xC4), Color.FromRgb(0xC0, 0x50, 0x40), Color.FromRgb(0x40, 0x90, 0x50) };
        var images = colours.Select(c => Image(60, 40, c)).ToArray();
        int width = d.Page.ContentWidth;
        int count = 25 + r.Next(130);
        string[] kinds = LayoutEngine.BulletKinds.Concat(LayoutEngine.NumberKinds).ToArray();
        for (int i = 0; i < count; i++)
        {
            int roll = r.Next(100);
            Paragraph p;
            if (roll < 4) p = Add(d, "Title", null, new Run(Text(r, 2 + r.Next(5))));
            else if (roll < 16) p = Add(d, "Heading" + (1 + r.Next(3)), null, new Run(Text(r, 1 + r.Next(6))));
            else if (roll < 25) p = Add(d, r.Next(4) == 0 ? "NoSpacing" : "Normal", null);
            else if (roll < 29)
            {
                // A table of short and longer cells.
                int cols = 2 + r.Next(3), rows = 1 + r.Next(6);
                var grid = new TableGrid { Widths = Enumerable.Repeat(width / cols, cols).ToArray() };
                for (int row = 0; row < rows; row++)
                {
                    var ids = new int[cols];
                    for (int c = 0; c < cols; c++)
                    {
                        var cell = d.NewCell();
                        if (r.Next(5) > 0) cell.Paras[0].Runs.Add(new Run(Text(r, 1 + r.Next(r.Next(3) == 0 ? 30 : 6))));
                        cell.Paras[0].Touch();
                        ids[c] = cell.Id;
                    }
                    grid.Rows.Add(ids);
                }
                p = new Paragraph { Style = "Normal", Table = grid };
                d.Body.Add(p);
            }
            else if (roll < 32) p = Add(d, "Normal", Align.Center, Pic(d, images[r.Next(3)], 914400 + r.Next(2743200), 457200 + r.Next(2286000), true));
            else
            {
                // Text, with now and then a list, a footnote, a floating picture, a picture or an equation in the line,
                // a line broken by hand, or a large word.
                var runs = new List<Run>();
                int words = r.Next(4) == 0 ? 1 + r.Next(6) : 5 + r.Next(110);
                runs.Add(new Run(Text(r, words)));
                if (r.Next(12) == 0) runs.Add(Note(d, r));
                if (r.Next(14) == 0)
                {
                    string[] wraps = { "square", "square", "topbottom", "behind", "front" };
                    int pw = 457200 + r.Next(1828800), ph = 457200 + r.Next(1828800);
                    runs.Insert(0, Pic(d, images[r.Next(3)], pw, ph, false, wraps[r.Next(wraps.Length)], r.Next(Math.Max(1, width - pw)), r.Next(5) == 0 ? -r.Next(254000) : r.Next(762000)));
                }
                if (r.Next(25) == 0) runs.Add(Pic(d, images[r.Next(3)], 304800 + r.Next(914400), 228600 + r.Next(914400), false));
                if (r.Next(25) == 0) runs.Add(Run.Object(Equation.From(Sources[r.Next(Sources.Length)], false), new Run("")));
                if (r.Next(20) == 0) runs.Add(new Run(Document.LineBreak + Text(r, 2 + r.Next(10))));
                if (r.Next(30) == 0) runs.Add(new Run(" " + Words[r.Next(Words.Length)], new CharProps(Size: 48 + r.Next(60))));
                runs.Add(new Run(" " + Text(r, 1 + r.Next(5))));
                p = Add(d, r.Next(6) == 0 ? "NoSpacing" : "Normal", (Align)r.Next(4), runs.ToArray());
                if (r.Next(9) == 0)
                {
                    p.List = kinds[r.Next(kinds.Length)];
                    p.ListLevel = r.Next(3);
                }
            }
            if (p.Table == null)
            {
                if (r.Next(10) == 0) p.Before = r.Next(457200);
                if (r.Next(10) == 0) p.After = r.Next(457200);
                if (r.Next(30) == 0) p.Borders = new ParaBorders(9525, 9525, r.Next(2) == 0 ? 9525 : 0, r.Next(2) == 0 ? 9525 : 0, 25400, 50800, 0);
            }
            if (r.Next(35) == 0) p.PageBreakBefore = true;
            if (r.Next(30) == 0 && p.Table == null) p.Together = true;
        }
        // Often the document ends with empty paragraphs, as Word documents do.
        int tail = r.Next(4) == 0 ? 1 + r.Next(3) : 0;
        for (int i = 0; i < tail; i++) Add(d, "Normal", null);
        foreach (var p in d.Body) p.Touch();
        return d;
    }

    static Paragraph Add(Document d, string style, Align? align, params Run[] runs)
    {
        var p = new Paragraph { Style = style, Align = align };
        p.Runs.AddRange(runs.Where(x => x.Text.Length > 0));
        d.Body.Add(p);
        return p;
    }

    /// <summary>Writes count documents into dir, each laid out, checked for its own rules (well formed, laid out the same twice,
    /// the same after saving and opening), and saved.</summary>
    public static int Write(string dir, int count, int seed)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        int bad = 0;
        for (int k = 0; k < count; k++)
        {
            string file = Path.Combine(dir, $"random-{k}.docu");
            try
            {
                var d = Make(seed + k);
                var eng = new LayoutEngine(d);
                eng.Update();
                string? fault = SelfTest.Invariants(eng) ?? SelfTest.Diff(eng, SelfTest.Fresh(d));
                RksFile.Save(d, eng, file);
                if (fault == null)
                {
                    var opened = RksFile.Open(file);
                    fault = !opened.Stored ? "stored layout not used: " + opened.Message : SelfTest.Diff(eng, opened.Engine);
                }
                log.Add($"{(fault == null ? "PASS" : "FAIL")}  random-{k} (seed {seed + k}): {d.Body.Count} paragraphs, {eng.Pages.Count} pages, adapt {d.AdaptSpacing}{(fault == null ? "" : "  -- " + fault)}");
                if (fault != null) bad++;
            }
            catch (Exception ex)
            {
                log.Add($"FAIL  random-{k} (seed {seed + k}): {ex.GetType().Name}: {ex.Message}");
                bad++;
            }
        }
        log.Add($"RESULT: {count - bad} of {count} pass");
        File.WriteAllLines(Path.Combine(dir, "random.log"), log);
        return bad == 0 ? 0 : 1;
    }
}
