using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ReiksDocument;

/// <summary>Measures how long the things take that happen while somebody types in a long document.
/// ReiksDocument.exe --bench out.txt [paragraphs] writes the times to a file.</summary>
static class Bench
{
    static readonly string[] Words =
    {
        "the", "river", "runs", "past", "old", "houses", "and", "quiet", "gardens", "where", "people", "meet", "in", "summer", "evenings", "to", "talk",
        "about", "weather", "harvest", "music", "their", "children", "long", "journeys", "small", "boats", "letters", "from", "far", "away",
    };

    /// <summary>A long document of the usual things: text, headings, lists, footnotes and a table now and then.</summary>
    public static Document Make(int paragraphs)
    {
        var d = Document.NewDefault();
        d.Body.Clear();
        int seed = 12345;
        int Next(int n) { seed = unchecked(seed * 1103515245 + 12345); return (int)((uint)seed >> 8) % n; }
        string Text(int words)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < words; k++) sb.Append(k > 0 ? " " : "").Append(Words[Next(Words.Length)]);
            return sb.ToString();
        }
        for (int i = 0; i < paragraphs; i++)
        {
            var p = new Paragraph { Style = "Normal" };
            if (i % 12 == 0)
            {
                p.Style = i % 60 == 0 ? "Heading1" : "Heading2";
                p.Runs.Add(new Run(Text(4)));
            }
            else if (i % 25 == 3 || i % 25 == 4)
            {
                p.List = "number";
                p.Runs.Add(new Run(Text(12)));
            }
            else
            {
                p.Runs.Add(new Run(Text(40) + " "));
                p.Runs.Add(new Run(Text(6), new CharProps(Bold: true)));
                p.Runs.Add(new Run(" " + Text(40) + "."));
                if (i % 40 == 7)
                {
                    var n = d.NewNote();
                    n.Paras[0].Runs.Add(new Run(Text(15)));
                    n.Paras[0].Touch();
                    p.Runs.Add(ReiksDocument.Run.Object(new NoteRef(n.Id), new Run("")));
                }
            }
            d.Body.Add(p);
            if (i % 60 == 30)
            {
                var grid = new TableGrid { Widths = new[] { 1900000, 1900000, 1931200 } };
                for (int r = 0; r < 4; r++)
                {
                    var ids = new int[3];
                    for (int c = 0; c < 3; c++)
                    {
                        var cell = d.NewCell();
                        cell.Paras[0].Runs.Add(new Run(Text(3)));
                        ids[c] = cell.Id;
                    }
                    grid.Rows.Add(ids);
                }
                d.Body.Add(new Paragraph { Style = "Normal", Table = grid });
            }
        }
        if (d.Body.Count == 0) d.Body.Add(new Paragraph { Style = "Normal" });
        return d;
    }

    public static int Run(string file, int paragraphs)
    {
        var sb = new StringBuilder();
        var sw = new Stopwatch();
        void Time(string what, int times, Action act)
        {
            act();
            sw.Restart();
            for (int k = 0; k < times; k++) act();
            sw.Stop();
            sb.Append((sw.Elapsed.TotalMilliseconds / times).ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9)).Append(" ms  ").Append(what).Append('\n');
        }

        var doc = Make(paragraphs);
        var eng = new LayoutEngine(doc);
        sw.Restart();
        eng.Update();
        sw.Stop();
        sb.Append($"{doc.Body.Count} paragraphs, {eng.Pages.Count} pages, {doc.Body.Sum(p => (long)p.Length)} characters; first layout {sw.Elapsed.TotalMilliseconds:0} ms\n\n");
        var ed = new Editor(doc, eng);
        int mid = doc.Body.Count / 2;
        while (doc.Body[mid].Table != null || doc.Body[mid].Length < 20) mid++;
        ed.MoveTo(new Pos(mid, 10), false);
        sb.Append("Without a window\n");
        Time("one typed letter in the middle (all of it)", 40, () => ed.TypeText("x"));
        Time("  of that: numbering footnotes, lists and citations", 40, () => eng.SyncNumbers(true));
        Time("  of that: layout when nothing changed (numbering and pages)", 40, eng.Update);
        Time("a new paragraph and taking it away again", 20, () => { ed.Enter(); ed.Backspace(false); });
        Time("undo and redo of a letter", 20, () => { ed.Undo(); ed.Redo(); });
        int page = doc.Body[mid].Lines![0].Page;
        Time("drawing one page", 10, () => PageRenderer.RenderPage(eng, page, 1.0));
        string tmp = Path.Combine(Path.GetTempPath(), "reiks-bench-" + Environment.ProcessId + ".docu");
        Time("saving the file", 3, () => RksFile.Save(doc, eng, tmp));
        Time("the part of a recovery copy that holds up the typing", 3, () => RksFile.Pack(doc, eng));
        var packed = RksFile.Pack(doc, eng);
        Time("the rest of a recovery copy, done beside the typing", 3, () => RksFile.Write(packed, tmp + ".copy", true));
        File.Delete(tmp + ".copy");
        Time("  of that: writing down the content", 3, () => RksFile.ContentJson(doc));
        Time("  of that: writing down the layout", 3, () => RksFile.LayoutJson(doc, eng));
        Time("opening the file", 3, () => RksFile.Open(tmp));
        Time("counting the words", 5, () => ed.WordCount());
        string pdf = tmp + ".pdf";
        Time("making the PDF", 1, () => PdfExport.Write(eng, pdf));
        File.Delete(pdf);

        // With the window: what the program does around a keystroke, beyond the document itself.
        string folder = Path.Combine(Path.GetTempPath(), "reiks-bench-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        Recovery.Folder = folder;
        Settings.FilePath = Path.Combine(folder, "settings.json");
        if (Application.Current == null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply();
        var win = new MainWindow(tmp)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Width = 1120, Height = 720,
        };
        win.Show();
        void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Idle();
        var wed = win.Editor;
        wed.MoveTo(new Pos(mid, 10), false);
        Idle();
        sb.Append("\nIn the window\n");
        Time("one typed letter, until the window has drawn it", 30, () => { wed.TypeText("x"); Idle(); });
        Time("  of that: before the drawing", 30, () => wed.TypeText("x"));
        Time("moving the caret one letter", 30, () => { wed.MoveTo(new Pos(mid, wed.Caret.Off > 12 ? 10 : wed.Caret.Off + 1), false); Idle(); });
        wed.Dirty = false;
        win.Close();
        Idle();
        try
        {
            File.Delete(tmp);
            Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        File.WriteAllText(file, sb.ToString());
        return 0;
    }
}
