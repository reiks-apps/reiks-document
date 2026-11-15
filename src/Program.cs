using System;
using System.Windows;

namespace ReiksDocument;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
            return SelfTest.Run(args.Length > 1 ? args[1] : "selftest-out");
        if (args.Length > 2 && args[0] == "--printtest")
        {
            // Writes the pages of a document to an XPS file through the printing path, without a printer.
            Printing.WriteXps(RksFile.Open(args[1]).Engine, args[2]);
            return 0;
        }
        if (args.Length > 2 && args[0] == "--pdf")
        {
            PdfExport.Write(RksFile.Open(args[1]).Engine, args[2]);
            return 0;
        }
        if (args.Length > 2 && args[0] == "--import")
        {
            // Reads a Word document and writes it as a Reiks document; what was simplified or left out goes to a third file, if one is named.
            try
            {
                var read = DocxImport.Read(args[1]);
                var laid = new LayoutEngine(read.Doc);
                laid.Update();
                RksFile.Save(read.Doc, laid, args[2]);
                if (args.Length > 3) System.IO.File.WriteAllLines(args[3], read.Notes);
                return 0;
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // A file that cannot be read is said so, in the third file if one is named.
                if (args.Length > 3) System.IO.File.WriteAllText(args[3], ex.Message);
                return 1;
            }
        }
        if (args.Length > 2 && args[0] == "--resave")
        {
            // Opens a document, lays it out by the rules of this version and writes it to another file.
            var opened = RksFile.Open(args[1]);
            opened.Engine.InvalidateAll();
            opened.Engine.Update();
            RksFile.Save(opened.Doc, opened.Engine, args[2]);
            return 0;
        }
        if (args.Length > 1 && args[0] == "--bench")
            return Bench.Run(args[1], args.Length > 2 && int.TryParse(args[2], out int count) ? Math.Clamp(count, 10, 200000) : 6000);
        if (args.Length > 1 && args[0] == "--sample")
        {
            SampleDoc.Write(args[1]);
            return 0;
        }

        Settings.Load();
        var app = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
        Theme.Apply();
        app.DispatcherUnhandledException += (_, e) =>
        {
            MsgBox.Show(e.Exception.Message, "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        return app.Run(new MainWindow(args.Length > 0 ? args[0] : null));
    }
}
