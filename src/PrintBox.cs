using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Printing;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReiksDocument;

/// <summary>The program's own box for printing: a picture of every page, and the choices of printer, pages,
/// copies, sides of the sheet and paper. Nothing is sent to a printer before Print is pressed.</summary>
sealed class PrintBox : Window
{
    readonly LayoutEngine eng;
    readonly ComboBox printer = new(), sides = new(), paper = new();
    readonly RadioButton all = new() { GroupName = "which", Margin = new Thickness(0, 4, 0, 0) }, some = new() { GroupName = "which", Content = "Pages", Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox range = new() { Width = 170 }, copies = new() { Text = "1", Width = 96, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock pageText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), MinWidth = 150, TextAlignment = TextAlignment.Center };
    readonly Image preview = new() { Stretch = Stretch.Uniform };
    readonly Button print = new() { Content = "Print", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) };
    readonly List<string> names = new();
    readonly List<PageMediaSize> papers = new();
    readonly List<Duplexing> sideValues = new();
    int shown;

    public PrintBox(Window? owner, LayoutEngine engine, string? preferPrinter = null)
    {
        eng = engine;
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Print";
        DialogParts.DigitsOnly(copies, 3);
        Width = 900;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        var left = new StackPanel { Width = 320 };
        void Part(string head, UIElement body)
        {
            left.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, left.Children.Count == 0 ? 0 : 14, 0, 4) });
            left.Children.Add(body);
        }
        Part("Printer", printer);
        all.Content = eng.Pages.Count == 1 ? "The one page" : "All " + eng.Pages.Count.ToString(CultureInfo.InvariantCulture) + " pages";
        all.IsChecked = true;
        var someRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        someRow.Children.Add(some);
        someRow.Children.Add(range);
        var pagesPart = new StackPanel();
        pagesPart.Children.Add(all);
        pagesPart.Children.Add(someRow);
        pagesPart.Children.Add(new TextBlock { Text = "For example:  1-3, 5", Foreground = Theme.Dim, Margin = new Thickness(0, 3, 0, 0) });
        Part("Which pages", pagesPart);
        Part("Copies", copies);
        Part("Sides of the sheet", sides);
        Part("Paper", paper);
        left.Children.Add(note);

        var back = new Button { Content = ((char)0x25C0).ToString(), MinWidth = 40, ToolTip = "The page before" };
        var next = new Button { Content = ((char)0x25B6).ToString(), MinWidth = 40, ToolTip = "The next page" };
        back.Click += (_, _) => Show(shown - 1);
        next.Click += (_, _) => Show(shown + 1);
        var turn = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        turn.Children.Add(back);
        turn.Children.Add(pageText);
        turn.Children.Add(next);
        var right = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        right.Children.Add(new Border
        {
            Background = Theme.Desk, Height = 470, Padding = new Thickness(12),
            Child = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = preview },
        });
        right.Children.Add(turn);

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3) };
        print.Click += (_, _) => { if (Valid()) DialogResult = true; };
        var ends = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        ends.Children.Add(print);
        ends.Children.Add(cancel);

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(right);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(body);
        panel.Children.Add(ends);
        Content = panel;

        // The box is there at once; the printers are looked for and asked in the background.
        FindPrinters(preferPrinter);

        range.TextChanged += (_, _) => { some.IsChecked = true; Show(0); };
        all.Checked += (_, _) => Show(0);
        some.Checked += (_, _) => Show(0);
        Show(0);
    }

    static readonly EnumeratedPrintQueueTypes[] Kinds = { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections };

    /// <summary>True when the printers are found and the chosen one has said what it can do.</summary>
    public bool Ready { get; private set; }

    /// <summary>Counts the questions put to printers, so that a late answer for a printer no longer chosen is dropped.</summary>
    int asked;

    /// <summary>Does slow work with printers away from the window, and afterwards runs what it gives back in the window.</summary>
    void Background(Func<Action> work)
    {
        var thread = new Thread(() =>
        {
            Action done;
            try { done = work(); }
            catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException) { done = () => Ready = true; }
            Dispatcher.BeginInvoke(done);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Looks for the printers Windows knows. The one Windows has as its own choice is chosen, unless another is asked for.</summary>
    void FindPrinters(string? preferPrinter)
    {
        printer.IsEnabled = false;
        print.IsEnabled = false;
        note.Text = "Looking for the printers…";
        ShowChoices(new(), new(), null, true);
        printer.SelectionChanged += (_, _) => AskPrinter();
        Background(() =>
        {
            var found = new List<string>();
            string? first = preferPrinter, problem = null;
            try
            {
                using var server = new LocalPrintServer();
                if (first == null)
                {
                    try { first = LocalPrintServer.GetDefaultPrintQueue()?.FullName; }
                    catch (PrintSystemException) { }
                }
                foreach (var q in server.GetPrintQueues(Kinds)) found.Add(q.FullName);
            }
            catch (PrintSystemException ex) { problem = ex.Message; }
            return () =>
            {
                names.AddRange(found);
                foreach (string n in found) printer.Items.Add(n);
                printer.IsEnabled = true;
                int at = names.IndexOf(first ?? "");
                if (at < 0 && preferPrinter == null && names.Count > 0) at = 0;
                if (at >= 0) printer.SelectedIndex = at;
                else
                {
                    note.Text = problem != null ? "Windows did not give its printers. " + problem : names.Count == 0 ? "Windows knows no printer." : "Choose a printer.";
                    Ready = true;
                }
            };
        });
    }

    /// <summary>Asks the chosen printer, in the background, which papers it takes and whether it prints on both sides.</summary>
    void AskPrinter()
    {
        if (printer.SelectedIndex < 0) return;
        string name = names[printer.SelectedIndex];
        int mine = ++asked;
        Ready = false;
        print.IsEnabled = false;
        note.Text = "Asking the printer what it can do…";
        Background(() =>
        {
            var sizes = new List<(PageMediaSizeName? Name, double W, double H)>();
            var twoSided = new List<Duplexing>();
            PageMediaSizeName? usual = null;
            bool answered = false;
            try
            {
                using var server = new LocalPrintServer();
                var q = server.GetPrintQueues(Kinds).FirstOrDefault(x => x.FullName == name);
                if (q != null)
                {
                    var caps = q.GetPrintCapabilities();
                    foreach (var m in caps.PageMediaSizeCapability)
                        if (m.Width is > 0 && m.Height is > 0) sizes.Add((m.PageMediaSizeName, m.Width.Value, m.Height.Value));
                    twoSided.AddRange(caps.DuplexingCapability);
                    usual = q.UserPrintTicket?.PageMediaSize?.PageMediaSizeName;
                    answered = true;
                }
            }
            catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException) { }
            return () =>
            {
                if (mine != asked) return;
                ShowChoices(sizes, twoSided, usual, answered);
                print.IsEnabled = true;
                Ready = true;
            };
        });
    }

    /// <summary>Shows what a printer offers: its papers, and one side or both sides of the sheet.</summary>
    void ShowChoices(List<(PageMediaSizeName? Name, double W, double H)> sizes, List<Duplexing> twoSided, PageMediaSizeName? usual, bool answered)
    {
        papers.Clear();
        sideValues.Clear();
        paper.Items.Clear();
        sides.Items.Clear();
        string said = answered ? "" : "The printer did not say which paper it takes, so A4 on one side is offered.";
        foreach (var (name, w, h) in sizes) papers.Add(name is { } known ? new PageMediaSize(known, w, h) : new PageMediaSize(w, h));
        if (papers.Count == 0) papers.Add(new PageMediaSize(PageMediaSizeName.ISOA4, 210 * 96 / 25.4, 297 * 96 / 25.4));
        foreach (var m in papers) paper.Items.Add(PaperName(m));
        // The paper of the document first, if the printer has it; else A4; else the paper the printer usually takes.
        double pw = U.Dip(eng.Doc.Page.Width), ph = U.Dip(eng.Doc.Page.Height);
        int a4 = papers.FindIndex(m => Math.Abs(m.Width!.Value - Math.Min(pw, ph)) < 4 && Math.Abs(m.Height!.Value - Math.Max(pw, ph)) < 4);
        if (a4 < 0) a4 = papers.FindIndex(m => m.PageMediaSizeName == PageMediaSizeName.ISOA4);
        if (a4 < 0 && usual != null) a4 = papers.FindIndex(m => m.PageMediaSizeName == usual);
        paper.SelectedIndex = Math.Max(0, a4);

        sideValues.Add(Duplexing.OneSided);
        sides.Items.Add("One side");
        if (twoSided.Contains(Duplexing.TwoSidedLongEdge))
        {
            sideValues.Add(Duplexing.TwoSidedLongEdge);
            sides.Items.Add("Both sides, flip on long edge (like a book)");
        }
        if (twoSided.Contains(Duplexing.TwoSidedShortEdge))
        {
            sideValues.Add(Duplexing.TwoSidedShortEdge);
            sides.Items.Add("Both sides, flip on short edge (like a notepad)");
        }
        sides.SelectedIndex = 0;
        if (printer.SelectedIndex < 0) return;
        if (sideValues.Count == 1 && said.Length == 0) said = "This printer prints on one side of the sheet only.";
        if (said.Length > 0) said += "\n";
        if (sideValues.Count > 1) said += "Both sides: for upright pages like these, the long edge is the usual choice.\n";
        note.Text = said + "On paper of another size than the page, the page stands in the middle and is made smaller if it does not fit.";
    }

    /// <summary>"A4 (210 × 297 mm)" from the name and size Windows gives for a paper.</summary>
    static string PaperName(PageMediaSize m)
    {
        string size = Math.Round(m.Width!.Value * 25.4 / 96).ToString(CultureInfo.InvariantCulture) + " " + (char)0xD7 + " "
            + Math.Round(m.Height!.Value * 25.4 / 96).ToString(CultureInfo.InvariantCulture) + " mm";
        if (m.PageMediaSizeName is not { } name || name == PageMediaSizeName.Unknown) return size;
        string s = name.ToString();
        foreach (string lead in new[] { "ISO", "NorthAmerica", "JIS", "Japan", "PRC", "Other", "Metric" })
            if (s.StartsWith(lead, StringComparison.Ordinal) && s.Length > lead.Length) { s = s[lead.Length..]; break; }
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb + " (" + size + ")";
    }

    /// <summary>The pages a typed text names, as "1-3, 5": numbers from 0, in the order typed, each once.
    /// Null if the text cannot be read; pages the document does not have are left out.</summary>
    public static List<int>? ParsePages(string text, int count)
    {
        var list = new List<int>();
        foreach (string part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] ends = part.Split('-');
            if (ends.Length > 2 || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out int a)) return null;
            int b = a;
            if (ends.Length == 2 && !int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out b)) return null;
            if (a < 1 || b < a) return null;
            for (int p = a; p <= Math.Min(b, count); p++)
                if (!list.Contains(p - 1)) list.Add(p - 1);
        }
        return list;
    }

    /// <summary>The pages that will be printed.</summary>
    public List<int> Pages
    {
        get
        {
            if (some.IsChecked == true && ParsePages(range.Text, eng.Pages.Count) is { Count: > 0 } chosen) return chosen;
            return Enumerable.Range(0, eng.Pages.Count).ToList();
        }
    }

    int Copies => int.TryParse(copies.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

    void Show(int index)
    {
        var pages = Pages;
        shown = Math.Clamp(index, 0, pages.Count - 1);
        preview.Source = PageRenderer.RenderPage(eng, pages[shown], 0.6);
        pageText.Text = "Page " + (pages[shown] + 1).ToString(CultureInfo.InvariantCulture) + " of " + eng.Pages.Count.ToString(CultureInfo.InvariantCulture)
            + (pages.Count < eng.Pages.Count ? "   (" + pages.Count.ToString(CultureInfo.InvariantCulture) + " to print)" : "");
    }

    bool Valid()
    {
        string? wrong = null;
        if (printer.SelectedIndex < 0) wrong = "Choose a printer.";
        else if (some.IsChecked == true && ParsePages(range.Text, eng.Pages.Count) is not { Count: > 0 })
            wrong = "The pages could not be read. Type them like  1-3, 5  with numbers from 1 to " + eng.Pages.Count.ToString(CultureInfo.InvariantCulture) + ".";
        else if (Copies < 1 || Copies > 999) wrong = "The number of copies is a number from 1 to 999.";
        if (wrong == null) return true;
        MsgBox.Show(this, wrong, "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    /// <summary>Sends the chosen pages to the chosen printer. Called after the box was closed with Print.</summary>
    public void Send(string title)
    {
        string name = names[printer.SelectedIndex];
        using var server = new LocalPrintServer();
        var q = server.GetPrintQueues(Kinds).First(x => x.FullName == name);
        var size = papers[paper.SelectedIndex];
        bool wide = eng.Doc.Page.Width > eng.Doc.Page.Height;
        var wish = new PrintTicket
        {
            CopyCount = Copies, PageOrientation = wide ? PageOrientation.Landscape : PageOrientation.Portrait, Duplexing = sideValues[sides.SelectedIndex], PageMediaSize = size,
        };
        var ticket = q.MergeAndValidatePrintTicket(q.UserPrintTicket ?? q.DefaultPrintTicket, wish).ValidatedPrintTicket;
        var dlg = new PrintDialog { PrintQueue = q, PrintTicket = ticket };
        // A page that lies on its side is printed on paper that lies on its side.
        double pw = size.Width!.Value, ph = size.Height!.Value;
        dlg.PrintDocument(new PagePaginator(eng, Pages, wide ? new Size(Math.Max(pw, ph), Math.Min(pw, ph)) : new Size(pw, ph)), title);
    }
}
