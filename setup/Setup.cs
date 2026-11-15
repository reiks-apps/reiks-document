using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ReiksDocument;

/// <summary>Where the parts of an installed Reiks Document are. For a trial run everything lies in one folder,
/// and what would go into the registry of Windows is written to a file instead.</summary>
sealed class Places
{
    public string Folder = "", Menu = "", Desktop = "", Preferences = "";
    /// <summary>Where a copy installed under the program's earlier name lies, and where its preferences are.</summary>
    public string EarlierFolder = "", EarlierPreferences = "";
    public string? Record;

    public static Places Real()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new Places
        {
            Folder = Path.Combine(local, "Programs", Setup.Name),
            Menu = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Preferences = Path.Combine(local, "ReiksDocument"),
            EarlierFolder = Path.Combine(local, "Programs", Setup.EarlierName),
            EarlierPreferences = Path.Combine(local, "ReiksWriter"),
        };
    }

    public static Places Trial(string dir) => TrialIn(Path.GetFullPath(dir));

    static Places TrialIn(string dir) => new()
    {
        Folder = Path.Combine(dir, "program"), Menu = Path.Combine(dir, "menu"), Desktop = Path.Combine(dir, "desktop"),
        Preferences = Path.Combine(dir, "preferences"), Record = Path.Combine(dir, "registry.txt"),
        EarlierFolder = Path.Combine(dir, "program-earlier"), EarlierPreferences = Path.Combine(dir, "preferences-earlier"),
    };

    public string Program => Path.Combine(Folder, Setup.Exe);
    public string Icon => Path.Combine(Folder, "assets", "DocuFile.ico");
    public string Remover => Path.Combine(Folder, Setup.RemoverName);
    public string MenuLink => Path.Combine(Menu, Setup.Name + ".lnk");
    public string DesktopLink => Path.Combine(Desktop, Setup.Name + ".lnk");
}

/// <summary>Puts Reiks Document on this PC for the user who runs it, and takes it away again.
/// Nothing needs administrator rights: the program goes into the user's own folder for programs,
/// and what Windows has to know goes into the user's own part of the registry.</summary>
static class Setup
{
    public const string Name = "Reiks Document", Exe = "ReiksDocument.exe", RemoverName = "Uninstall.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ReiksDocument";

    public static string Version => typeof(Setup).Assembly.GetName().Version is { } v ? v.Major + "." + v.Minor + "." + v.Build : "";

    /// <summary>The program itself, which this file carries inside.</summary>
    static ZipArchive Payload() => new(typeof(Setup).Assembly.GetManifestResourceStream("payload.zip")
        ?? throw new IOException("This setup file is damaged: the program is not inside."), ZipArchiveMode.Read);

    /// <summary>True while a Reiks Document from the folder is open; its files cannot be replaced then.</summary>
    public static bool Running(Places pl)
    {
        foreach (string name in new[] { "ReiksDocument", "ReiksWriter" })
            foreach (var p in Process.GetProcessesByName(name))
                try
                {
                    if (p.MainModule?.FileName is { } f && (f.StartsWith(pl.Folder, StringComparison.OrdinalIgnoreCase)
                        || f.StartsWith(pl.EarlierFolder, StringComparison.OrdinalIgnoreCase))) return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return false;
    }

    // The program was called Reiks Writer before. A copy installed under that name is taken away when this one is installed:
    // its files, its shortcuts and its entry in the list of installed apps. Preferences are taken over by the program itself.
    public const string EarlierName = "Reiks Writer";
    const string EarlierKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ReiksWriter";
    static readonly string[] EarlierFiles =
    {
        "ReiksWriter.exe", "ReiksWriter.dll", "ReiksWriter.deps.json", "ReiksWriter.runtimeconfig.json", RemoverName,
        @"assets\UdocFile.ico", @"assets\DocuFile.ico", @"hyph\NOTICE.txt", @"hyph\languages.txt",
        @"hyph\hyph-en-us.pat.txt", @"hyph\hyph-en-us.hyp.txt", @"hyph\hyph-en-us.tex", @"hyph\hyph-sv.pat.txt", @"hyph\hyph-sv.tex",
    };

    public static bool EarlierInstalled(Places pl) => File.Exists(Path.Combine(pl.EarlierFolder, "ReiksWriter.exe"));

    static void RemoveEarlier(Places pl)
    {
        if (Directory.Exists(pl.EarlierFolder))
        {
            foreach (string f in EarlierFiles) Delete(Path.Combine(pl.EarlierFolder, f));
            foreach (string d in new[] { Path.Combine(pl.EarlierFolder, "hyph"), Path.Combine(pl.EarlierFolder, "assets"), pl.EarlierFolder })
                try
                {
                    if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Delete(Path.Combine(pl.Menu, EarlierName + ".lnk"));
        Delete(Path.Combine(pl.Desktop, EarlierName + ".lnk"));
        if (pl.Record == null) Registry.CurrentUser.DeleteSubKeyTree(EarlierKey, false);
    }

    public static bool Installed(Places pl) => File.Exists(pl.Program);

    /// <summary>What the list of installed apps of Windows shows, and how it removes the program.</summary>
    static List<(string Name, object Value)> AppEntry(Places pl, long bytes) => new()
    {
        ("DisplayName", Name), ("DisplayVersion", Version), ("DisplayIcon", pl.Program), ("InstallLocation", pl.Folder),
        ("UninstallString", "\"" + pl.Remover + "\" --uninstall"), ("NoModify", 1), ("NoRepair", 1), ("EstimatedSize", (int)Math.Max(1, bytes / 1024)),
    };

    public static void Install(Places pl, bool fileType, bool desktop)
    {
        // Who had the earlier program on the desktop gets this one there too.
        desktop |= File.Exists(Path.Combine(pl.Desktop, EarlierName + ".lnk"));
        RemoveEarlier(pl);
        Directory.CreateDirectory(pl.Folder);
        long bytes = 0;
        string root = Path.GetFullPath(pl.Folder) + Path.DirectorySeparatorChar;
        using (var zip = Payload())
            foreach (var e in zip.Entries)
            {
                if (e.Name.Length == 0) continue;
                string to = Path.GetFullPath(Path.Combine(pl.Folder, e.FullName));
                if (!to.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("This setup file is damaged.");
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                e.ExtractToFile(to, true);
                bytes += e.Length;
            }
        // A copy of this file stays in the folder: it is what removes the program later.
        if (Environment.ProcessPath is { } me && !Same(me, pl.Remover))
        {
            File.Copy(me, pl.Remover, true);
            bytes += new FileInfo(me).Length;
        }
        Directory.CreateDirectory(pl.Menu);
        Shortcut(pl.MenuLink, pl.Program, pl.Folder);
        if (desktop)
        {
            Directory.CreateDirectory(pl.Desktop);
            Shortcut(pl.DesktopLink, pl.Program, pl.Folder);
        }
        var app = AppEntry(pl, bytes);
        if (pl.Record != null)
        {
            var sb = new StringBuilder();
            foreach (var (name, value) in app) sb.Append(UninstallKey).Append(" | ").Append(name).Append(" = ").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');
            if (fileType)
                foreach (var (key, value) in FileType.Entries(pl.Program, pl.Icon)) sb.Append(@"Software\Classes\").Append(key).Append(" = ").Append(value).Append('\n');
            File.WriteAllText(pl.Record, sb.ToString());
            return;
        }
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            foreach (var (name, value) in app) k.SetValue(name, value);
        if (fileType) FileType.SetFor(pl.Program, pl.Icon);
    }

    /// <summary>Takes away what Install put there, and nothing else: a file somebody added to the folder stays, and so does the folder then.</summary>
    public static void Uninstall(Places pl, bool preferences)
    {
        Delete(pl.MenuLink);
        Delete(pl.DesktopLink);
        if (pl.Record != null) Delete(pl.Record);
        else
        {
            if (FileType.IsSetFor(pl.Program)) FileType.Remove();
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        }
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var zip = Payload())
            foreach (var e in zip.Entries)
            {
                if (e.Name.Length == 0) continue;
                string file = Path.Combine(pl.Folder, e.FullName);
                Delete(file);
                for (string? d = Path.GetDirectoryName(file); d != null && d.Length > pl.Folder.Length; d = Path.GetDirectoryName(d)) folders.Add(d);
            }
        Delete(pl.Remover);
        // The deepest folders first; each goes only if it is empty.
        var deep = new List<string>(folders);
        deep.Sort((a, b) => b.Length.CompareTo(a.Length));
        deep.Add(pl.Folder);
        foreach (string d in deep)
            try
            {
                if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (preferences && Directory.Exists(pl.Preferences)) Directory.Delete(pl.Preferences, true);
        if (preferences && Directory.Exists(pl.EarlierPreferences)) Directory.Delete(pl.EarlierPreferences, true);
    }

    public static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Deletes a file; one that is still held, as by a program that is just closing, is tried again for a few seconds.</summary>
    static void Delete(string file)
    {
        for (int round = 0; ; round++)
            try
            {
                if (File.Exists(file)) File.Delete(file);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && round < 30)
            {
                System.Threading.Thread.Sleep(150);
            }
    }

    // ---- a shortcut file ----

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr data, int flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    static void Shortcut(string link, string program, string folder)
    {
        var s = (IShellLinkW)new ShellLink();
        s.SetPath(program);
        s.SetWorkingDirectory(folder);
        s.SetIconLocation(program, 0);
        s.SetDescription("Write documents");
        ((IPersistFile)s).Save(link, false);
        Marshal.ReleaseComObject(s);
    }

    // ---- start ----

    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        try
        {
            // Trial runs without a window, for checking the setup file: everything stays inside the folder given.
            if (mode == "--trial-install" && args.Length > 1)
            {
                Install(Places.Trial(args[1]), true, true);
                return 0;
            }
            if (mode == "--trial-uninstall" && args.Length > 1)
            {
                Uninstall(Places.Trial(args[1]), true);
                return 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var pl = Places.Real();
        bool remove = mode == "--uninstall";
        string? me = Environment.ProcessPath;
        if (remove && me != null && Same(me, pl.Remover))
        {
            // A file cannot delete itself while it runs: a copy outside the folder does the work.
            string copy = Path.Combine(Path.GetTempPath(), "ReiksDocumentRemove.exe");
            File.Copy(me, copy, true);
            Process.Start(new ProcessStartInfo(copy) { Arguments = "--uninstall", UseShellExecute = false });
            return 0;
        }
        // The window is light or dark as the apps of Windows are set to be.
        bool dark = false;
        try
        {
            dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        var app = new Application { ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light };
        var win = new SetupWindow(pl, remove)
        {
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
            Foreground = dark ? Brushes.White : Brushes.Black,
        };
        if (mode == "--shot" && args.Length > 2) win.Shoot(args[1], args[2]);
        app.Run(win);
        if (remove && me != null && Same(me, Path.Combine(Path.GetTempPath(), "ReiksDocumentRemove.exe")))
        {
            // The copy that did the removing takes itself away a moment after it has closed.
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = "/c waitfor /t 2 ReiksDocumentRemoved >nul 2>&1 & del /q \"" + me + "\"", UseShellExecute = false, CreateNoWindow = true,
            });
        }
        return 0;
    }
}

/// <summary>The one window of the setup: what will be done, then that it was done. The same for removing.</summary>
sealed class SetupWindow : Window
{
    readonly Places pl;
    readonly StackPanel page = new();
    readonly Border frame = new() { Padding = new Thickness(28, 22, 28, 22) };

    public SetupWindow(Places places, bool remove)
    {
        pl = places;
        Title = Setup.Name + (remove ? ": remove" : ": setup");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        frame.Child = page;
        Content = frame;
        if (remove) RemovePage();
        else InstallPage();
    }

    TextBlock Text(string text, double size = 14, bool dim = false, double above = 0) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Opacity = dim ? 0.7 : 1, Margin = new Thickness(0, above, 0, 0),
    };

    void Head(string title, string below)
    {
        page.Children.Clear();
        page.Children.Add(Text(title, 24));
        page.Children.Add(Text(below, 13, true, 2));
    }

    void Buttons(string main, Action act, string? other)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var go = new Button { Content = main, MinWidth = 104, Padding = new Thickness(14, 5, 14, 5), IsDefault = true };
        go.Click += (_, _) => act();
        row.Children.Add(go);
        if (other != null)
        {
            var no = new Button { Content = other, MinWidth = 104, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
            no.Click += (_, _) => Close();
            row.Children.Add(no);
        }
        page.Children.Add(row);
    }

    /// <summary>Runs a step; what goes wrong is told in words, and the window stays.</summary>
    bool Try(Action step, string what)
    {
        try
        {
            step();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidDataException or System.Security.SecurityException)
        {
            MessageBox.Show(this, what + "\n\n" + ex.Message, Setup.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    bool Closed()
    {
        if (!Setup.Running(pl)) return true;
        MessageBox.Show(this, "Reiks Document is open. Save your work, close it, and press the button again.", Setup.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    void InstallPage()
    {
        bool again = Setup.Installed(pl), earlier = Setup.EarlierInstalled(pl);
        Head(Setup.Name, "Version " + Setup.Version);
        page.Children.Add(Text("This puts Reiks Document on this PC, for you only. It needs no administrator rights, and nothing goes online.", above: 16));
        page.Children.Add(Text("Folder", 13, true, 16));
        page.Children.Add(Text(pl.Folder, 13));
        var type = new CheckBox { Content = "Open .docu documents with Reiks Document, and give them their icon", IsChecked = true, Margin = new Thickness(0, 18, 0, 0) };
        var desk = new CheckBox { Content = "Put a shortcut on the desktop", Margin = new Thickness(0, 6, 0, 0) };
        page.Children.Add(type);
        page.Children.Add(desk);
        page.Children.Add(Text("Reiks Document gets an entry in the Start menu." + (again ? " The copy that is in this folder now is replaced; your documents and preferences stay." : "")
            + (earlier ? " The program was called " + Setup.EarlierName + " before: the copy installed under that name is taken away, and your documents and preferences stay." : ""), 13, true, 14));
        Buttons(again ? "Update" : "Install", () =>
        {
            if (Closed() && Try(() => Setup.Install(pl, type.IsChecked == true, desk.IsChecked == true), "Reiks Document could not be installed.")) DonePage();
        }, "Cancel");
    }

    void DonePage()
    {
        Head("Reiks Document is installed", "Version " + Setup.Version);
        page.Children.Add(Text("You find it in the Start menu. To remove it later, open the Settings of Windows, then Apps, then Installed apps.", above: 16));
        page.Children.Add(Text("Spelling", 13, true, 18));
        page.Children.Add(Text("Reiks Document checks spelling with the dictionaries of Windows. A language is checked once Windows has it with \"Basic typing\"; "
            + "choose the language in Reiks Document under Preferences, Spelling.", 13, false, 2));
        var lang = new Button { Content = "Language settings of Windows…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 10, 0, 0) };
        lang.Click += (_, _) => Try(() => Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }), "The settings could not be opened.");
        page.Children.Add(lang);
        var start = new CheckBox { Content = "Start Reiks Document now", IsChecked = true, Margin = new Thickness(0, 20, 0, 0) };
        page.Children.Add(start);
        Buttons("Close", () =>
        {
            if (start.IsChecked == true) Try(() => Process.Start(new ProcessStartInfo(pl.Program) { UseShellExecute = true, WorkingDirectory = pl.Folder }), "Reiks Document could not be started.");
            Close();
        }, null);
    }

    void RemovePage()
    {
        Head("Remove Reiks Document", "Version " + Setup.Version);
        page.Children.Add(Text("This takes Reiks Document off this PC: the program, its entry in the Start menu, and that .docu documents open with it. Your documents stay where they are.", above: 16));
        var prefs = new CheckBox { Content = "Also remove my preferences and the recovery copies", Margin = new Thickness(0, 18, 0, 0) };
        page.Children.Add(prefs);
        Buttons("Remove", () =>
        {
            if (!Closed() || !Try(() => Setup.Uninstall(pl, prefs.IsChecked == true), "Reiks Document could not be removed completely.")) return;
            Head("Reiks Document was removed", "");
            page.Children.Add(Text("Your documents are still there. A .docu file is a Reiks document; Reiks Document opens it again whenever you install it.", above: 16));
            Buttons("Close", Close, null);
        }, "Cancel");
    }

    /// <summary>For looking at the window without anybody at the screen: a page of it as a picture, then the window closes.</summary>
    public void Shoot(string file, string which)
    {
        ContentRendered += (_, _) =>
        {
            if (which == "done") DonePage();
            else if (which == "remove") RemovePage();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateLayout();
                var size = new Size(frame.ActualWidth, frame.ActualHeight);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Background ?? Brushes.White, null, new Rect(size));
                    dc.DrawRectangle(new VisualBrush(frame) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
                }
                var bmp = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(visual);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(file)) enc.Save(fs);
                Close();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }
}
