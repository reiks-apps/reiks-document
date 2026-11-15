using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ReiksDocument;

static class DialogParts
{
    public static StackPanel Buttons(Window w, Func<bool>? accept = null)
    {
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(8, 3, 8, 3) };
        ok.Click += (_, _) => { if (accept == null || accept()) w.DialogResult = true; };
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        p.Children.Add(ok);
        p.Children.Add(cancel);
        return p;
    }

    /// <summary>Gives a box of the program its colours and the choice about screenshots.</summary>
    public static void Prepare(Window w)
    {
        w.Background = Theme.Window;
        ScreenCapture.Follow(w);
    }

    /// <summary>Lets a box take digits only, and no more than so many.</summary>
    public static void DigitsOnly(TextBox box, int digits)
    {
        box.MaxLength = digits;
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetData(typeof(string)) is not string pasted || !pasted.All(char.IsAsciiDigit)) e.CancelCommand();
        });
    }

    public static Border PreviewFrame(Image img, double height) => new()
    {
        Child = img, Height = height, Background = Brushes.White, BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true,
    };
}

/// <summary>Choices for the program itself, on tabs.</summary>
sealed class PreferencesDialog : Window
{
    readonly RadioButton cm = Choice("Centimetres"), inch = Choice("Inches");
    readonly RadioButton asWindows = Choice("Same as Windows"), light = Choice("Light"), dark = Choice("Dark"), ownColor = Choice("A colour of my own");
    readonly ColorChoice windowChoice, paperChoice;
    readonly ShortcutsPage keys;

    /// <summary>The shortcuts as the user left them.</summary>
    public Dictionary<string, string> Keys => keys.Custom;
    readonly CheckBox capture = new() { Content = "Allow screenshots and screen recordings of the program", Margin = new Thickness(0, 4, 0, 0) };

    public bool Metric => cm.IsChecked == true;
    readonly ComboBox spellBox = new() { Width = 330, HorizontalAlignment = HorizontalAlignment.Left };
    readonly List<string> spellTags = new() { "auto", "off" };
    readonly TextBlock spellLacking = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontWeight = FontWeights.SemiBold, Visibility = Visibility.Collapsed };

    /// <summary>The spelling check as chosen: "auto", "off" or a language.</summary>
    public string Spelling => spellTags[Math.Max(0, spellBox.SelectedIndex)];

    public string Colors => ownColor.IsChecked == true ? "custom" : light.IsChecked == true ? "light" : dark.IsChecked == true ? "dark" : "system";
    public int WindowColor => windowChoice.Value;
    public int PaperColor => paperChoice.Value;
    public bool AllowCapture => capture.IsChecked == true;
    public bool KeepRecovery => recovery.IsChecked == true;
    readonly CheckBox recovery = new() { Content = "Keep a recovery copy while I work", Margin = new Thickness(0, 16, 0, 0) };

    public PreferencesDialog(Window? owner, int tab = 0)
    {
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Preferences";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        cm.IsChecked = Settings.Metric;
        inch.IsChecked = !Settings.Metric;
        foreach (var choice in new[] { asWindows, light, dark, ownColor }) choice.GroupName = "colours";
        (Settings.Colors == "custom" ? ownColor : Settings.Colors == "light" ? light : Settings.Colors == "dark" ? dark : asWindows).IsChecked = true;
        windowChoice = new ColorChoice(this, new[] { ("Soft grey", 0xDCDCD8), ("Green", 0xD3E4CD), ("Blue", 0xD4E1EE), ("Khaki", 0xE4DCC4), ("Dark slate", 0x2C3138) }, Settings.WindowColor);
        paperChoice = new ColorChoice(this, new[]
        {
            ("White", 0xFFFFFF), ("Natural white", 0xFAF7F0), ("Natural paper", 0xF3EBD6), ("Soft grey", 0xE8E8E6), ("Green", 0xDCEBD5), ("Blue", 0xDEE9F3),
        }, Settings.PaperColor);
        windowChoice.Changed += () => ownColor.IsChecked = true;
        windowChoice.Row.Margin = new Thickness(28, 4, 0, 0);
        paperChoice.Row.Margin = new Thickness(0, 2, 0, 0);
        var sheetHead = Head("Colour of the sheet");
        sheetHead.Margin = new Thickness(0, 18, 0, 2);
        capture.IsChecked = Settings.AllowCapture;
        recovery.IsChecked = Settings.Recovery;

        var tabs = new TabControl { Width = 500, Height = 440, SelectedIndex = 0 };
        // Files ending in .docu: this program opens them, and they get the icon of a document. Done at once, for this user of Windows only.
        var filesHead = Head("Files ending in .docu");
        filesHead.Margin = new Thickness(0, 18, 0, 2);
        var filesNote = Hint("");
        filesNote.Margin = new Thickness(0, 6, 0, 0);
        void ShowFiles() => filesNote.Text = FileType.IsSet()
            ? "Windows opens .docu files with this program and shows them with the icon of a document."
            : "Windows does not open .docu files with this program yet. The button tells it to, for you only. It is done at once, not when OK is pressed.";
        Button FilesButton(string text, Action act)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) =>
            {
                try { act(); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    MsgBox.Show(this, "Windows did not take the change.\n\n" + ex.Message, "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                ShowFiles();
            };
            return b;
        }
        var filesRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        filesRow.Children.Add(FilesButton("Open .docu files with Reiks Document", FileType.Set));
        filesRow.Children.Add(FilesButton("Undo this", FileType.Remove));
        ShowFiles();
        tabs.Items.Add(Tab("General", Head("Lengths are given in"), cm, inch,
            Hint("With centimetres the indent buttons move a paragraph by 1 cm, with inches by half an inch."), recovery,
            Hint("A copy of the open document is written a few seconds after you stop typing, and at least every half minute. "
                + "If the program or the PC stops unexpectedly, the copy is offered when the program starts again. Your own file changes only when you save."), filesHead, filesRow, filesNote));
        tabs.Items.Add(Tab("Appearance", Head("Colours of the program"), asWindows, light, dark, ownColor, windowChoice.Row, sheetHead, paperChoice.Row,
            Hint("A softer sheet is easier on the eyes than bright white. It is only how the page looks on the screen: print and PDF stay white. "
                + "With a colour of your own for the program, the buttons turn light or dark to suit it. \"Same as Windows\" is read when the program starts.")));
        tabs.Items.Add(Tab("Privacy", Head("Screenshots"), capture,
            Hint("Unless this is ticked, Windows is asked to leave the program's windows out of screenshots and screen recordings.")));
        keys = new ShortcutsPage(this);
        tabs.Items.Add(new TabItem { Header = "Shortcuts", Content = keys.Panel });
        // Spelling: the languages Windows can check on this PC.
        void FillSpelling(string chosen)
        {
            spellBox.Items.Clear();
            spellTags.RemoveRange(2, spellTags.Count - 2);
            spellBox.Items.Add("The language of Windows, if it can be checked");
            spellBox.Items.Add("No spelling check");
            foreach (string tag in Spell.Languages())
            {
                string name = tag;
                try { name = CultureInfo.GetCultureInfo(tag).DisplayName + "  (" + tag + ")"; }
                catch (CultureNotFoundException) { }
                spellTags.Add(tag);
                spellBox.Items.Add(name);
            }
            spellBox.SelectedIndex = Math.Max(0, spellTags.FindIndex(t => t.Equals(chosen, StringComparison.OrdinalIgnoreCase)));
            // A language that is in the user's list of Windows but has no dictionary yet is named, with the way to get it.
            var lacking = Spell.WithoutDictionary().Select(tag =>
            {
                try { return CultureInfo.GetCultureInfo(tag).DisplayName; }
                catch (CultureNotFoundException) { return tag; }
            }).ToList();
            spellLacking.Text = lacking.Count == 0 ? "" :
                string.Join(", ", lacking) + (lacking.Count == 1 ? " is" : " are") + " in your list of languages in Windows, but the dictionary is not installed yet, so Windows cannot check it. "
                + "In the language settings of Windows, press the three dots beside the language, choose \"Language options\" and download \"Basic typing\". "
                + "When Windows has finished, press \"Look again\"; if the language is still missing, close the program and start it again.";
            spellLacking.Visibility = lacking.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        FillSpelling(Settings.Spelling);
        // More languages come from Windows: the button opens its language settings, where the user adds one.
        var addLanguage = new Button { Content = "Add a language in Windows…", ToolTip = "Opens the language settings of Windows. A language added there brings its dictionary, which Windows downloads" };
        addLanguage.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MsgBox.Show(this, "The settings of Windows could not be opened.\n\n" + ex.Message, "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        var lookAgain = new Button { Content = "Look again", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Looks again for the languages Windows can check, after one was added" };
        lookAgain.Click += (_, _) =>
        {
            string chosen = Spelling;
            Spell.LookAgain();
            FillSpelling(chosen);
        };
        var spellRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        spellRow.Children.Add(addLanguage);
        spellRow.Children.Add(lookAgain);
        tabs.Items.Add(Tab("Spelling", Head("Check the spelling in"), spellBox,
            Hint("Words the check does not know get a red wavy line on the screen; a right click on such a word offers other spellings. "
                + "The check is the one Windows has, with the dictionaries installed on this PC" + (Spell.Languages().Count == 0 ? " (none was found)" : "")
                + "; more languages are added in the language settings of Windows. One language is checked at a time. Nothing is sent anywhere, and print and PDF carry no marks."), spellLacking, spellRow,
            Hint("To check another language, add it in Windows: the button opens the right page, and there \"Add a language\" downloads its dictionary. "
                + "This program downloads nothing itself. Afterwards press \"Look again\". A word is judged when you have finished typing it. The check is for spelling only, not for grammar.")));
        tabs.SelectedIndex = tab;
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(tabs);
        panel.Children.Add(new TextBlock
        {
            Text = "These choices are kept on this PC and change nothing in a document.", Foreground = Theme.Dim, Margin = new Thickness(2, 8, 0, 0),
        });
        panel.Children.Add(DialogParts.Buttons(this));
        Content = panel;
    }

    static RadioButton Choice(string text) => new() { Content = text, Margin = new Thickness(0, 4, 0, 0) };

    static TextBlock Head(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };

    static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 12, 0, 0) };

    static TabItem Tab(string name, params UIElement[] parts)
    {
        var p = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        foreach (var e in parts) p.Children.Add(e);
        return new TabItem { Header = name, Content = p };
    }
}

/// <summary>A box of ready colours to choose from, with a button for any other colour.</summary>
sealed class ColorChoice
{
    public readonly StackPanel Row = new() { Orientation = Orientation.Horizontal };
    readonly ComboBox box = new() { Width = 230 };
    readonly (string Name, int Rgb)[] ready;
    int own = -1;

    /// <summary>The user chose another colour.</summary>
    public event Action? Changed;

    public int Value => box.SelectedIndex >= 0 && box.SelectedIndex < ready.Length ? ready[box.SelectedIndex].Rgb : own >= 0 ? own : ready[0].Rgb;

    public ColorChoice(Window owner, (string Name, int Rgb)[] colours, int current)
    {
        ready = colours;
        box.ItemTemplate = Look();
        if (Array.FindIndex(ready, c => c.Rgb == current) < 0) own = current;
        Fill(current);
        var other = new Button { Content = "Other colour…", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Any other colour, from the colour box of Windows" };
        other.Click += (_, _) =>
        {
            if (ColorPicker.Pick(owner, Value) is not { } c) return;
            if (Array.FindIndex(ready, x => x.Rgb == c) < 0) own = c;
            Fill(c);
            Changed?.Invoke();
        };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) Changed?.Invoke(); };
        Row.Children.Add(box);
        Row.Children.Add(other);
    }

    void Fill(int select)
    {
        box.Items.Clear();
        foreach (var (name, rgb) in ready) box.Items.Add(Item(name, rgb));
        if (own >= 0) box.Items.Add(Item("My own colour", own));
        int at = Array.FindIndex(ready, c => c.Rgb == select);
        box.SelectedIndex = at >= 0 ? at : box.Items.Count - 1;
    }

    /// <summary>One colour of the box: its name and the colour itself.</summary>
    sealed record Entry(string Name, Brush Brush);

    static Entry Item(string name, int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return new Entry(name, brush);
    }

    /// <summary>How a colour is shown, in the box and in its list: a small square of it and its name.</summary>
    static DataTemplate Look()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var square = new FrameworkElementFactory(typeof(Border));
        square.SetValue(FrameworkElement.WidthProperty, 18.0);
        square.SetValue(FrameworkElement.HeightProperty, 18.0);
        square.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        square.SetValue(Border.BorderBrushProperty, Brushes.Gray);
        square.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        square.SetBinding(Border.BackgroundProperty, new Binding("Brush"));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        name.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(square);
        row.AppendChild(name);
        return new DataTemplate { VisualTree = row };
    }
}

/// <summary>How many rows and columns a new table gets.</summary>
sealed class TableDialog : Window
{
    readonly TextBox rows = new() { Text = "3", Width = 96 }, columns = new() { Text = "3", Width = 96 };

    static int Number(TextBox box, int most) => int.TryParse(box.Text.Trim(), out int n) ? Math.Clamp(n, 1, most) : 3;
    public int Rows => Number(rows, 200);
    public int Columns => Number(columns, 12);

    public TableDialog(Window? owner)
    {
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Table";
        DialogParts.DigitsOnly(rows, 3);
        DialogParts.DigitsOnly(columns, 2);
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "Rows", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(rows);
        row.Children.Add(new TextBlock { Text = "Columns", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
        row.Children.Add(columns);
        var panel = new StackPanel { Margin = new Thickness(14), MinWidth = 300 };
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 320,
            Text = "Tab goes to the next cell and adds a row behind the last one. Drag the line between two columns to change their widths. Esc leaves the table.",
        });
        panel.Children.Add(DialogParts.Buttons(this));
        Content = panel;
        Loaded += (_, _) => { rows.Focus(); rows.SelectAll(); };
    }
}

/// <summary>The texts and pictures of the header and the footer: those of all pages, and those of the first page if it has its own.</summary>
sealed class BandDialog : Window
{
    // Two sets, each a header and a footer: 0 for all pages, 1 for the first page.
    readonly TextBox[] boxes = new TextBox[12];
    readonly BandPicture?[] pics = new BandPicture?[4];
    readonly ComboBox[] places = new ComboBox[4];
    readonly TextBox[] heights = new TextBox[4];
    readonly Button[] removes = new Button[4];
    readonly TextBlock[] says = new TextBlock[4];
    readonly CheckBox skip = new() { Content = "The first page has its own header and footer", Margin = new Thickness(0, 16, 0, 0) };
    readonly Document doc;
    readonly LayoutEngine eng;

    public Band Header => Band(0);
    public Band Footer => Band(1);
    public Band FirstHeader => Band(2);
    public Band FirstFooter => Band(3);
    public bool SkipFirst => skip.IsChecked == true;

    /// <summary>Band b: 0 header, 1 footer, 2 header of the first page, 3 footer of the first page.</summary>
    Band Band(int b) => new(Text(b * 3), Text(b * 3 + 1), Text(b * 3 + 2), Pic(b));

    string Text(int i) => RksFile.BandLines(boxes[i].Text, true);

    /// <summary>The picture as chosen, at the height typed, never larger than fits into the margin.</summary>
    BandPicture? Pic(int b)
    {
        if (pics[b] is not { } p) return null;
        int place = Math.Max(0, places[b].SelectedIndex);
        long h = Lengths.Read(heights[b]) is int typed && typed > 0 ? typed : p.Height;
        long w = Math.Max(1, (long)p.Width * h / p.Height);
        bool shares = Enumerable.Range(0, 3).Any(c => c != place && Text(b * 3 + c).Length > 0);
        int rows = new Band(Text(b * 3), Text(b * 3 + 1), Text(b * 3 + 2)).Rows;
        var (fw, fh) = eng.FitBandPicture(b % 2 == 1, w, h, shares, rows);
        return new BandPicture(p.Media, fw, fh, place);
    }

    void ShowPic(int b)
    {
        bool has = pics[b] != null;
        removes[b].IsEnabled = places[b].IsEnabled = heights[b].IsEnabled = has;
        heights[b].Text = has ? Lengths.Show(pics[b]!.Height) : "";
        says[b].Text = has ? Lengths.Unit + " high" : "none";
    }

    void Choose(int b)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dlg.ShowDialog(this) != true) return;
        byte[] data;
        System.Windows.Media.Imaging.BitmapSource? img = null;
        bool jpeg = false;
        try
        {
            if (new System.IO.FileInfo(dlg.FileName).Length > 64 << 20) throw new System.IO.IOException("The file is larger than 64 MB.");
            data = System.IO.File.ReadAllBytes(dlg.FileName);
            img = PageRenderer.Decode(data, out jpeg);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MsgBox.Show(this, "The image could not be read.\n\n" + ex.Message, "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (img == null)
        {
            MsgBox.Show(this, "This file is not a PNG or JPEG image.", "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string name = RksFile.MediaName(data, jpeg);
        doc.Media[name] = data;
        // As large as the margin lets it be, and no larger than the picture itself at one screen pixel per picture pixel.
        var (w, h) = eng.FitBandPicture(b % 2 == 1, img.PixelWidth * 9525L, img.PixelHeight * 9525L, false);
        pics[b] = new BandPicture(name, w, h, Math.Max(0, places[b].SelectedIndex));
        ShowPic(b);
    }

    static readonly string[] Heads = { "Left", "Middle", "Right" };

    /// <summary>The boxes of one set: three texts each for header and footer, and a row for the picture of each.</summary>
    StackPanel Part(int set, Band header, Band footer)
    {
        string[] now = { header.Left, header.Centre, header.Right, footer.Left, footer.Centre, footer.Right };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < 3; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < 3; c++)
        {
            var head = new TextBlock { Text = Heads[c], Foreground = Theme.Dim, Margin = new Thickness(4, 0, 0, 2) };
            Grid.SetColumn(head, c + 1);
            grid.Children.Add(head);
        }
        for (int r = 0; r < 2; r++)
        {
            var label = new TextBlock { Text = r == 0 ? "Header" : "Footer", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 8, 0) };
            Grid.SetRow(label, r + 1);
            grid.Children.Add(label);
            for (int c = 0; c < 3; c++)
            {
                var box = boxes[set * 6 + r * 3 + c] = new TextBox { Text = now[r * 3 + c], Margin = new Thickness(3), Padding = new Thickness(3), AcceptsReturn = true, MaxLines = RksFile.MaxBandLines, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetRow(box, r + 1);
                Grid.SetColumn(box, c + 1);
                grid.Children.Add(box);
            }
        }
        var part = new StackPanel();
        part.Children.Add(grid);
        for (int r = 0; r < 2; r++)
        {
            int b = set * 2 + r;
            pics[b] = (r == 0 ? header : footer).Picture;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, r == 0 ? 8 : 6, 0, 0) };
            row.Children.Add(new TextBlock { Text = r == 0 ? "Picture in the header" : "Picture in the footer", Width = 140, VerticalAlignment = VerticalAlignment.Center });
            var choose = new Button { Content = "Choose\u2026", Padding = new Thickness(8, 3, 8, 3), ToolTip = "A picture such as a logo" };
            choose.Click += (_, _) => Choose(b);
            removes[b] = new Button { Content = "Remove", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
            removes[b].Click += (_, _) => { pics[b] = null; ShowPic(b); };
            places[b] = new ComboBox { Width = 86, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Where the picture stands. A text in the same place stands beside it." };
            foreach (string place in Heads) places[b].Items.Add(place);
            places[b].SelectedIndex = pics[b]?.Place ?? 0;
            heights[b] = new TextBox { Width = 56, Margin = new Thickness(12, 0, 6, 0), Padding = new Thickness(3), ToolTip = "The height of the picture. It is never made larger than fits into the margin." };
            says[b] = new TextBlock { Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(choose);
            row.Children.Add(removes[b]);
            row.Children.Add(places[b]);
            row.Children.Add(heights[b]);
            row.Children.Add(says[b]);
            part.Children.Add(row);
            ShowPic(b);
        }
        return part;
    }

    public BandDialog(Window? owner, Document doc, LayoutEngine eng)
    {
        this.doc = doc;
        this.eng = eng;
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Header and footer";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        var number = new Button { Content = "Page number in the middle of the footer", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
        number.Click += (_, _) => boxes[4].Text = "{page}";
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(Part(0, doc.Header, doc.Footer));
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 10, 0, 0),
            Text = "Write {page} where the number of the page should stand and {pages} for the number of pages, for example: Page {page} of {pages}. Enter begins a new line.",
        });
        panel.Children.Add(number);
        var first = Part(1, doc.FirstHeader, doc.FirstFooter);
        first.Margin = new Thickness(0, 8, 0, 0);
        var hint = new TextBlock { Foreground = Theme.Dim, Margin = new Thickness(0, 8, 0, 0), Text = "Left empty, the first page has neither header nor footer." };
        skip.IsChecked = doc.BandsSkipFirst;
        void ShowFirst() => first.Visibility = hint.Visibility = skip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        skip.Checked += (_, _) => ShowFirst();
        skip.Unchecked += (_, _) => ShowFirst();
        ShowFirst();
        panel.Children.Add(skip);
        panel.Children.Add(first);
        panel.Children.Add(hint);
        panel.Children.Add(DialogParts.Buttons(this));
        Content = panel;
        Loaded += (_, _) => boxes[0].Focus();
    }
}

/// <summary>Typing an equation in one line, with a picture of the result.</summary>
sealed class EquationDialog : Window
{
    /// <summary>The empty box of a template: the place for a number or a letter.</summary>
    public const char Gap = (char)0x25A1;

    /// <summary>The shapes that can be put in with a button: what the button says, and what goes into the line.
    /// In the line a # stands for an empty box.</summary>
    public static readonly (string Tip, string Text)[] Templates =
    {
        ("Fraction", "(#)/(#)"),
        ("Power", "#^(#) "),
        ("Index", "#_(#) "),
        ("Square root", "\\sqrt(#)"),
        ("Sum from a start to an end", "\\sum_(#=#)^(#) #"),
        ("Product from a start to an end", "\\prod_(#=#)^(#) #"),
        ("Integral between two limits", "\\int_(#)^(#) # dx"),
        ("Brackets that grow", "(#)"),
        ("Absolute value", "|#|"),
    };

    /// <summary>The signs offered in the equation box, in groups. Each can have a shortcut.</summary>
    public static readonly (string Name, string Signs)[] SignGroups =
    {
        ("Greek", "αβγδεζηθικλμνξοπρστυφχψω"),
        ("Capitals", "ΓΔΘΛΞΠΣΦΨΩ"),
        ("Operators", "±∓×÷·∗∘√∑∏∫∮∂∇∆∞"),
        ("Relations", "≤≥≠≈≡∼∝≪≫→←↔⇒⇔"),
        ("Sets and logic", "∈∉⊂⊆∪∩∅∀∃¬∧∨ℝℕℤℚℂ"),
        ("More", "°′″…⋯⋮ℏℓ∠⊥∥"),
    };

    readonly TextBox box;
    readonly EquationPad pad;
    readonly TabControl tabs = new();
    readonly Image preview = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Span span;

    /// <summary>True while the equation is typed into; false while it is typed as a line.</summary>
    bool InPlace => tabs.SelectedIndex == 0;

    public string Source => InPlace ? pad.Source : box.Text.Replace(Gap.ToString(), "").Trim();

    public EquationDialog(Window? owner, string source, Span sp)
    {
        span = sp;
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Equation";
        Width = 700;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        pad = new EquationPad(sp);
        box = new TextBox { Text = source, FontFamily = new FontFamily("Consolas"), FontSize = 15 };
        box.TextChanged += (_, _) => Update();
        box.PreviewKeyDown += (_, e) =>
        {
            // Tab goes to the next empty box of the line, Shift+Tab to the one before.
            if (e.Key == Key.Tab && NextGap((Keyboard.Modifiers & ModifierKeys.Shift) != 0)) e.Handled = true;
        };

        // The shapes, each drawn as it will look, with its empty boxes.
        var shapes = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (tip, text) in Templates)
        {
            var face = new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 4, 7, 4), MinWidth = 40, MinHeight = 40,
                Child = new Image
                {
                    Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Source = PageRenderer.Preview(MathLayout.Layout(MathParser.Parse(Fill(text)), 20 * U.PerHalfPoint, true), sp),
                },
            };
            var b = new Button { Content = face, ToolTip = tip, Padding = new Thickness(3), Margin = new Thickness(0, 0, 6, 6), Focusable = false };
            b.Click += (_, _) => Put(text);
            shapes.Children.Add(b);
        }
        // The signs, in groups; only those the font of equations has.
        var signs = new TabControl { Margin = new Thickness(0, 0, 0, 8), Height = 126 };
        foreach (var (group, chars) in SignGroups)
        {
            var keys = new WrapPanel { Margin = new Thickness(6, 6, 6, 0) };
            foreach (char c in chars)
            {
                if (MathLayout.Font.Face.Lookup(c).Item1 == 0) continue;
                var b = new Button
                {
                    Content = c.ToString(), Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 3), FontSize = 15, Focusable = false,
                    ToolTip = SignNames.Of(c),
                };
                b.Click += (_, _) => Type(c.ToString());
                keys.Children.Add(b);
            }
            signs.Items.Add(new TabItem { Header = group, Content = keys });
        }

        TextBlock Hint(string text) => new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 8, 0, 0), Text = text };
        var inPlace = new StackPanel { Margin = new Thickness(10) };
        inPlace.Children.Add(new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(1), ClipToBounds = true, Child = pad });
        inPlace.Children.Add(Hint("Type into the equation itself. Press a shape, or type / for a fraction, ^ for a power, _ for an index, ( for brackets, and fill in its parts. "
            + "Tab goes to the next part that is still empty, the arrow keys and the mouse move the caret, and Space or → leaves a part. "
            + "Alt and a letter types the Greek letter (Alt+A α, Alt+B β, Alt+D δ); rest the mouse on a sign to see its name and keys. The keys can be changed under Preferences."));
        var inLine = new StackPanel { Margin = new Thickness(10) };
        inLine.Children.Add(box);
        inLine.Children.Add(DialogParts.PreviewFrame(preview, 130));
        inLine.Children.Add(Hint("Type the equation as one line:  x^2   a_i   (a+b)/c   \\sqrt(x+1)   \\sum_(i=1)^n i^2   sin x. A shape goes into the line with a □ for each part to fill in; Tab goes to the next one. "
            + "A space ends an index or exponent; round brackets around a whole numerator, denominator, index or exponent are not shown. Alt and a letter types the Greek letter."));
        tabs.Items.Add(new TabItem { Header = "In the equation", Content = inPlace });
        tabs.Items.Add(new TabItem { Header = "In a line", Content = inLine });
        pad.Source = source;
        tabs.SelectedIndex = Settings.EquationInLine ? 1 : 0;
        tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, tabs)) return;
            // Both ways hold the same equation: what was written in one is there in the other.
            if (tabs.SelectedIndex == 1)
            {
                box.Text = pad.Source;
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }
            else
            {
                pad.Source = box.Text;
                pad.Focus();
            }
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(shapes);
        panel.Children.Add(signs);
        panel.Children.Add(tabs);
        panel.Children.Add(DialogParts.Buttons(this, () =>
        {
            // The way of writing that was used is the one the box opens with next time.
            Settings.EquationInLine = tabs.SelectedIndex == 1;
            Settings.Save();
            return true;
        }));
        Content = panel;
        // The shortcuts of the signs, in either way of writing. (AltGr is Ctrl with Alt, and has no shortcut unless the user gives it one.)
        PreviewKeyDown += (_, e) =>
        {
            if (Shortcuts.Find(Shortcuts.Gesture(e), true) is not { } id) return;
            Type(id[4..]);
            e.Handled = true;
        };
        Loaded += (_, _) =>
        {
            if (tabs.SelectedIndex == 1)
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }
            else pad.Focus();
        };
        Update();
    }

    static string Fill(string template) => template.Replace('#', Gap);

    /// <summary>The line and the selection after a shape was put in: selected text goes into its first box,
    /// and the next empty box is selected, ready to be typed over.</summary>
    public static (string Text, int Start, int Length) Insert(string line, int start, int length, string template)
    {
        string piece = Fill(template), chosen = line.Substring(start, length);
        int first = piece.IndexOf(Gap);
        if (chosen.Length > 0 && first >= 0) piece = piece.Remove(first, 1).Insert(first, chosen);
        string text = line.Remove(start, length).Insert(start, piece);
        int gap = piece.IndexOf(Gap);
        return gap >= 0 ? (text, start + gap, 1) : (text, start + piece.Length, 0);
    }

    void Put(string template)
    {
        if (InPlace)
        {
            pad.InsertTemplate(template);
            pad.Focus();
            return;
        }
        var (text, start, length) = Insert(box.Text, box.SelectionStart, box.SelectionLength, template);
        box.Text = text;
        box.Focus();
        box.Select(start, length);
    }

    void Type(string sign)
    {
        if (InPlace)
        {
            pad.TypeText(sign);
            pad.Focus();
            return;
        }
        int at = box.SelectionStart;
        box.Text = box.Text.Remove(at, box.SelectionLength).Insert(at, sign);
        box.Focus();
        box.Select(at + sign.Length, 0);
    }

    /// <summary>Selects the next empty box after the caret (or the one before it); false if the line has none.</summary>
    bool NextGap(bool back)
    {
        string t = box.Text;
        int from = back ? box.SelectionStart - 1 : box.SelectionStart + box.SelectionLength;
        int at = back ? (from >= 0 ? t.LastIndexOf(Gap, from) : -1) : (from <= t.Length ? t.IndexOf(Gap, Math.Min(from, t.Length)) : -1);
        if (at < 0) at = back ? t.LastIndexOf(Gap) : t.IndexOf(Gap);
        if (at < 0) return false;
        box.Select(at, 1);
        return true;
    }

    void Update() => preview.Source = PageRenderer.Preview(MathLayout.Layout(MathParser.Parse(box.Text.Replace(Gap.ToString(), "")), 36 * U.PerHalfPoint, true), span);
}

/// <summary>A table of symbols to put into the text: signs for money, Greek letters, signs of mathematics, arrows and others.</summary>
sealed class SymbolDialog : Window
{
    public static readonly (string Name, string Signs)[] Groups =
    {
        ("Maths", "±∓×÷·∙∗∘√∞≈≠≡∼≤≥≪≫<>∑∏∫∮∂∆∇∈∉∩∪⊂⊃⊆⊇∅∝∠⊥∥°′″µ‰%¼½¾¹²³⁰⁴⁵⁶⁷⁸⁹ⁿ₀₁₂₃₄¬∧∨∀∃ℝℕℤℚℂℏℓ⋯"),
        ("Greek", "αβγδεζηθικλμνξοπρστυφχψωΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩ"),
        ("Arrows", "←→↑↓↔↕⇐⇒⇑⇓⇔↖↗↘↙↵"),
        ("Money", "$€£¥¢₹₽₩₺₴₪฿₫₱₿¤"),
        ("Signs", "©®™§¶†‡•…–—«»‹›“”‘’„‚¿¡№℮✓✗★☆♠♣♥♦♪♫☺☼"),
        ("Letters", "áàâäãåæçéèêëíìîïñóòôöõøœßúùûüýÿÁÀÂÄÃÅÆÇÉÈÊËÍÌÎÏÑÓÒÔÖÕØŒÚÙÛÜÝŸ"),
    };

    readonly Func<int, bool> has;
    readonly Action<string> insert;
    readonly WrapPanel recent = new();
    readonly TabControl tabs = new() { Height = 400 };

    /// <summary>has: whether the font of the text has a character; insert: puts a symbol into the text.</summary>
    public SymbolDialog(Window? owner, Func<int, bool> has, Action<string> insert)
    {
        this.has = has;
        this.insert = insert;
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Symbols";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        tabs.Items.Add(new TabItem { Header = "Used last", Content = Scroll(recent) });
        foreach (var (name, signs) in Groups)
        {
            var keys = new WrapPanel();
            foreach (char c in signs)
                if (has(c)) keys.Children.Add(Key(c.ToString()));
            tabs.Items.Add(new TabItem { Header = name, Content = Scroll(keys) });
        }
        ShowRecent();
        tabs.SelectedIndex = recent.Children.Count > 0 ? 0 : 1;

        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(tabs);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 8, 0, 0),
            Text = "Click a symbol to put it into the text where the caret is. A symbol that the font of the text does not have is put in with a font that has it.",
        });
        panel.Children.Add(close);
        Content = panel;
        Closed += (_, _) => Settings.Save();
    }

    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) };

    Button Key(string sign)
    {
        var b = new Button
        {
            Content = sign, Width = 40, Height = 40, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4), FontSize = 18,
            ToolTip = SignNames.Of(sign[0]) ?? "U+" + ((int)sign[0]).ToString("X4", CultureInfo.InvariantCulture),
        };
        b.Click += (_, _) =>
        {
            insert(sign);
            Settings.RecentSymbols = Recent(Settings.RecentSymbols, sign);
            ShowRecent();
        };
        return b;
    }

    /// <summary>The symbols used last with one more put in front, each once, 24 at most.</summary>
    public static string Recent(string before, string sign)
    {
        string s = sign + before.Replace(sign, "");
        return s.Length > 24 ? s[..24] : s;
    }

    void ShowRecent()
    {
        recent.Children.Clear();
        foreach (char c in Settings.RecentSymbols)
            if (has(c)) recent.Children.Add(Key(c.ToString()));
    }
}

/// <summary>The kind, title and data table of a chart, with a picture of the result.</summary>
sealed class ChartDialog : Window
{
    const int SeriesColumns = 6;
    static readonly string[] Kinds = { "bar", "line", "pie", "plot" };

    readonly Chart original;
    readonly Span span;
    readonly ComboBox kind = new(), place = new() { ToolTip = "Where the names of the two axes stand" };
    readonly TextBox title = new();
    readonly TextBox xTitle = new() { ToolTip = "A name for the horizontal axis, written below it" };
    readonly TextBox yTitle = new() { ToolTip = "A name for the vertical axis, written along it or above it" };
    readonly List<UIElement> axisParts = new();
    readonly DataTable table = new();
    readonly DataGrid grid;
    readonly TextBox[] names = new TextBox[SeriesColumns];
    readonly TextBlock tableHint = new() { Foreground = Theme.Dim, Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap };
    readonly TextBox formula = new()
    {
        Width = 440, Height = 84, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top, FontFamily = new FontFamily("Consolas"), FontSize = 14,
    };
    readonly TextBox fromBox = new() { Width = 104 }, toBox = new() { Width = 104 };
    readonly StackPanel tablePanel = new(), plotPanel = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly Image preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(6) };

    public Chart Result { get; private set; }

    public ChartDialog(Window? owner, Chart chart, Span sp)
    {
        original = Result = chart;
        span = sp;
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Chart";
        Width = 780;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        foreach (string k in new[] { "Bars", "Lines", "Pie", "Graph of formulas" }) kind.Items.Add(k);
        kind.SelectedIndex = Math.Max(0, Array.IndexOf(Kinds, chart.Kind));
        title.Text = chart.Title;
        xTitle.Text = chart.XTitle;
        yTitle.Text = chart.YTitle;
        place.Items.Add("Along the axes, in the middle");
        place.Items.Add("At the ends of the axes");
        place.SelectedIndex = chart.AxisNames == "ends" ? 1 : 0;

        // The table: one row for each category, one column for each series. The names of the series are typed
        // into the boxes at the top of the columns. A graph of formulas has no table; it starts with a small example.
        var data = chart.Kind == "plot" ? Chart.Default() : chart;
        for (int j = 0; j <= SeriesColumns; j++) table.Columns.Add("c" + j);
        for (int i = 0; i < data.Cats.Count; i++)
        {
            var row = table.NewRow();
            row[0] = data.Cats[i];
            for (int j = 0; j < Math.Min(SeriesColumns, data.Series.Count); j++)
                row[j + 1] = i < data.Series[j].Values.Count ? ChartLayout.Fmt(data.Series[j].Values[i], true) : "";
            table.Rows.Add(row);
        }
        grid = new DataGrid
        {
            ItemsSource = table.DefaultView, AutoGenerateColumns = false, Height = 230, CanUserSortColumns = false, CanUserReorderColumns = false,
            CanUserAddRows = true, CanUserDeleteRows = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionUnit = DataGridSelectionUnit.Cell, GridLinesVisibility = DataGridGridLinesVisibility.All,
            HorizontalGridLinesBrush = Theme.Line, VerticalGridLinesBrush = Theme.Line, ColumnWidth = 104,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Category", Binding = new Binding("c0") });
        for (int j = 0; j < SeriesColumns; j++)
        {
            names[j] = new TextBox
            {
                Text = j < data.Series.Count ? data.Series[j].Name : "", Width = 88,
                ToolTip = "Name of this series. A chart with two or more series shows the names below it",
            };
            names[j].TextChanged += (_, _) => Update();
            grid.Columns.Add(new DataGridTextColumn { Header = names[j], Binding = new Binding("c" + (j + 1)) });
        }

        // Kind and title, then the names of the two axes and where they stand: labels and boxes in columns.
        var form = new Grid();
        foreach (double wide in new[] { 0.0, 230, 0, 280 })
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = wide == 0 ? GridLength.Auto : new GridLength(wide) });
        for (int r = 0; r < 3; r++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(FrameworkElement e, int row, int col, bool axis)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            e.Margin = new Thickness(col == 2 ? 20 : 0, row == 0 ? 0 : 8, col % 2 == 0 ? 8 : 0, 0);
            form.Children.Add(e);
            if (axis) axisParts.Add(e);
        }
        Put(Label("Kind", 0), 0, 0, false);
        Put(kind, 0, 1, false);
        Put(Label("Title", 0), 0, 2, false);
        Put(title, 0, 3, false);
        Put(Label("Horizontal axis", 0), 1, 0, true);
        Put(xTitle, 1, 1, true);
        Put(Label("Vertical axis", 0), 1, 2, true);
        Put(yTitle, 1, 3, true);
        Put(Label("Names of the axes", 0), 2, 0, true);
        Put(place, 2, 1, true);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(form);
        tablePanel.Children.Add(tableHint);
        tablePanel.Children.Add(grid);
        panel.Children.Add(tablePanel);

        bool plot = chart.Kind == "plot";
        formula.Text = plot ? string.Join(Environment.NewLine, Formulas(chart.Formula)) : "sin(x)";
        fromBox.Text = ChartLayout.Fmt(plot ? chart.From : -10000, true);
        toBox.Text = ChartLayout.Fmt(plot ? chart.To : 10000, true);
        plotPanel.Children.Add(new TextBlock { Text = "Formulas, one in each line", Margin = new Thickness(0, 0, 0, 4) });
        var plotRow = new StackPanel { Orientation = Orientation.Horizontal };
        plotRow.Children.Add(formula);
        var stretch = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        stretch.Children.Add(Label("x from", 14));
        stretch.Children.Add(fromBox);
        stretch.Children.Add(Label("to", 8));
        stretch.Children.Add(toBox);
        plotRow.Children.Add(stretch);
        plotPanel.Children.Add(plotRow);
        plotPanel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 8, 0, 0),
            Text = "Every line gives one graph. Typed like an equation, with x:\n" +
                   "x^2 - 3x + 1     sin(x)/x     \\sqrt(x)     e^(-x^2)     2 cos x     abs(x)     ln x\n" +
                   "A line can begin with a name for its graph:     Height = 20x - 5x^2",
        });
        panel.Children.Add(plotPanel);
        Switch();
        panel.Children.Add(DialogParts.PreviewFrame(preview, 230));
        panel.Children.Add(DialogParts.Buttons(this, () =>
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            Result = Build();
            return true;
        }));
        Content = panel;

        kind.SelectionChanged += (_, _) => { Switch(); Update(); };
        foreach (var box in new[] { formula, fromBox, toBox, title, xTitle, yTitle }) box.TextChanged += (_, _) => Update();
        place.SelectionChanged += (_, _) => Update();
        table.RowChanged += (_, _) => Update();
        table.RowDeleted += (_, _) => Update();
        table.ColumnChanged += (_, _) => Update();
        Update();
    }

    static TextBlock Label(string text, double left) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0) };

    /// <summary>The formulas of a typed text: one in each line, or separated by semicolons.</summary>
    static List<string> Formulas(string text) => text.Split(';', '\n', '\r').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    static string Cell(DataRow r, int col) => r.RowState == DataRowState.Deleted || r.RowState == DataRowState.Detached ? "" : (r[col] as string ?? "").Trim();

    static long Number(string s) =>
        decimal.TryParse(s.Replace(',', '.').Replace(((char)0x2212).ToString(), "-"), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) ? RksFile.Thousandths(d) : 0;

    string KindNow => Kinds[Math.Max(0, kind.SelectedIndex)];

    /// <summary>Shows what the chosen kind needs: a pie has slices with one value each and no axes,
    /// a graph of formulas has formulas instead of a table.</summary>
    void Switch()
    {
        bool plot = KindNow == "plot", pie = KindNow == "pie";
        tablePanel.Visibility = plot ? Visibility.Collapsed : Visibility.Visible;
        plotPanel.Visibility = plot ? Visibility.Visible : Visibility.Collapsed;
        foreach (var e in axisParts) e.Visibility = pie ? Visibility.Collapsed : Visibility.Visible;
        grid.Columns[0].Header = pie ? "Slice" : "Category";
        grid.Columns[1].Header = pie ? "Value" : names[0];
        for (int j = 2; j <= SeriesColumns; j++) grid.Columns[j].Visibility = pie ? Visibility.Collapsed : Visibility.Visible;
        tableHint.Text = pie
            ? "One row for each slice: its name and its value. A new row starts in the empty line at the bottom."
            : "One row for each category, one column for each series. The boxes at the top hold the names of the series. A new row starts in the empty line at the bottom.";
    }

    /// <summary>The chart the box describes: empty rows and empty columns are left out.</summary>
    Chart Build()
    {
        bool pie = KindNow == "pie";
        var basis = original with { Title = title.Text.Trim(), XTitle = pie ? "" : xTitle.Text.Trim(), YTitle = pie ? "" : yTitle.Text.Trim(), AxisNames = place.SelectedIndex == 1 ? "ends" : "middle" };
        if (KindNow == "plot")
        {
            long from = Number(fromBox.Text), to = Number(toBox.Text);
            if (to <= from) to = from + 1000;
            return Plot.Make(basis, string.Join("; ", Formulas(formula.Text)), from, to);
        }
        var rows = table.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Deleted && r.RowState != DataRowState.Detached)
            .Where(r => Enumerable.Range(0, SeriesColumns + 1).Any(c => Cell(r, c).Length > 0)).ToList();
        var series = new List<Series>();
        for (int j = 1; j <= SeriesColumns; j++)
        {
            string name = names[j - 1].Text.Trim();
            bool filled = rows.Any(r => Cell(r, j).Length > 0);
            if (!filled && (name.Length == 0 || rows.Count == 0)) continue;
            series.Add(new Series(name.Length > 0 ? name : "Series " + j, rows.Select(r => Number(Cell(r, j))).ToList()));
        }
        return basis with { Kind = KindNow, Cats = rows.Select(r => Cell(r, 0)).ToList(), Series = series };
    }

    void Update() => preview.Source = PageRenderer.Preview(ChartLayout.Layout(Build(), span), span);
}

/// <summary>The sources of the document: to add, change and remove them, to choose how citations look,
/// and to put a citation or the list of references into the text.</summary>
sealed class SourcesDialog : Window
{
    readonly List<Source> sources;
    readonly ListBox list = new() { Height = 210, Background = Theme.Field, BorderBrush = Theme.Edge, BorderThickness = new Thickness(1) };
    readonly ComboBox system = new() { Width = 230, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock example = new() { Foreground = Theme.Dim, Margin = new Thickness(0, 6, 0, 0) };
    readonly Button cite;

    public List<Source> Sources => sources;
    public string Style => system.SelectedItem is ComboBoxItem { Tag: string id } ? id : Citations.ByName;
    /// <summary>"cite": put a citation of the chosen source at the caret; "list": put in the list of references; "": only keep the changes.</summary>
    public string Action { get; private set; } = "";
    public string? Chosen => list.SelectedIndex >= 0 ? sources[list.SelectedIndex].Id : null;

    public SourcesDialog(Window? owner, IEnumerable<Source> current, string style)
    {
        sources = new List<Source>(current);
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Sources and citations";
        Width = 680;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        foreach (var (id, name, _) in Citations.Systems) system.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        system.SelectionChanged += (_, _) => example.Text = "A citation looks like " + Citations.Systems.First(x => x.Id == Style).Example + "; the list of references is written as this system has it.";
        system.SelectedIndex = Math.Max(0, Array.FindIndex(Citations.Systems, x => x.Id == Citations.Known(style)));

        Button Side(string text, Action act)
        {
            var b = new Button { Content = text, MinWidth = 110, Margin = new Thickness(10, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) => act();
            return b;
        }
        var side = new StackPanel();
        side.Children.Add(Side("New source…", () => Edit(-1)));
        side.Children.Add(Side("Change…", () => Edit(list.SelectedIndex)));
        side.Children.Add(Side("Remove", Remove));
        var top = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        top.Children.Add(side);
        top.Children.Add(list);
        list.MouseDoubleClick += (_, _) => Edit(list.SelectedIndex);

        Button End(string text, string action, bool main)
        {
            var b = new Button { Content = text, IsDefault = main, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
            b.Click += (_, _) => { Action = action; DialogResult = true; };
            return b;
        }
        cite = End("Insert citation", "cite", true);
        var ends = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        ends.Children.Add(cite);
        ends.Children.Add(End("Insert list of references", "list", false));
        ends.Children.Add(End("OK", "", false));
        ends.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) });

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Sources of this document", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(top);
        panel.Children.Add(new TextBlock { Text = "Reference system", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
        panel.Children.Add(system);
        panel.Children.Add(example);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 10, 0, 0),
            Text = "Choose a source and press \"Insert citation\" to cite it where the caret is. The list of references holds every source that is cited; "
                 + "it is written again by itself whenever a citation or a source changes, so changes typed into it are not kept.",
        });
        panel.Children.Add(ends);
        Content = panel;
        list.SelectionChanged += (_, _) => cite.IsEnabled = list.SelectedIndex >= 0;
        Fill(sources.Count > 0 ? 0 : -1);
    }

    void Fill(int select)
    {
        list.Items.Clear();
        foreach (var s in sources)
            list.Items.Add(Citations.ShortAuthors(s) + " (" + (s.Year.Length > 0 ? s.Year : "n.d.") + ")" + (s.Authors.Count > 0 && s.Title.Length > 0 ? "   " + s.Title : ""));
        list.SelectedIndex = Math.Min(select, sources.Count - 1);
        cite.IsEnabled = list.SelectedIndex >= 0;
    }

    void Edit(int index)
    {
        var dlg = new SourceDialog(this, index >= 0 ? sources[index] : null);
        if (dlg.ShowDialog() != true) return;
        if (index >= 0) sources[index] = dlg.Result with { Id = sources[index].Id };
        else
        {
            int n = 1;
            while (sources.Exists(s => s.Id == "s" + n.ToString(CultureInfo.InvariantCulture))) n++;
            sources.Add(dlg.Result with { Id = "s" + n.ToString(CultureInfo.InvariantCulture) });
            index = sources.Count - 1;
        }
        Fill(index);
    }

    void Remove()
    {
        int i = list.SelectedIndex;
        if (i < 0) return;
        sources.RemoveAt(i);
        Fill(Math.Min(i, sources.Count - 1));
    }
}

/// <summary>One source: who wrote it, when, what it is called and where it appeared.</summary>
sealed class SourceDialog : Window
{
    readonly TextBox authors = new() { Height = 76, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    readonly TextBox year = new() { Width = 110, HorizontalAlignment = HorizontalAlignment.Left }, title = new(), where = new(), url = new();

    public Source Result { get; private set; } = new("", Array.Empty<string>(), "", "", "", "");

    public SourceDialog(Window? owner, Source? s)
    {
        Owner = owner;
        DialogParts.Prepare(this);
        Title = s == null ? "New source" : "Change a source";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        if (s != null)
        {
            authors.Text = string.Join(Environment.NewLine, s.Authors);
            year.Text = s.Year;
            title.Text = s.Title;
            where.Text = s.In;
            url.Text = s.Url;
        }
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, FrameworkElement box, string? hint)
        {
            int r = form.RowDefinitions.Count;
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Margin = new Thickness(0, r == 0 ? 6 : 14, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            var cell = new StackPanel { Margin = new Thickness(0, r == 0 ? 0 : 8, 0, 0) };
            cell.Children.Add(box);
            if (hint != null) cell.Children.Add(new TextBlock { Text = hint, Foreground = Theme.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            Grid.SetRow(l, r);
            Grid.SetRow(cell, r);
            Grid.SetColumn(cell, 1);
            form.Children.Add(l);
            form.Children.Add(cell);
        }
        Row("Authors", authors, "One author in each line, family name first:  Smith, Anna");
        Row("Year", year, null);
        Row("Title", title, null);
        Row("Published in or by", where, "A journal with its volume and pages, a publisher, a website");
        Row("Web address", url, null);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(form);
        panel.Children.Add(DialogParts.Buttons(this, () =>
        {
            var names = authors.Text.Split('\n', '\r').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            if (names.Count == 0 && title.Text.Trim().Length == 0)
            {
                MsgBox.Show(this, "A source needs at least an author or a title.", "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            Result = new Source("", names, year.Text.Trim(), title.Text.Trim(), where.Text.Trim(), url.Text.Trim());
            return true;
        }));
        Content = panel;
    }
}

/// <summary>The names of signs, shown when the mouse rests on their buttons, and the Greek letter each key gives together with Alt.</summary>
static class SignNames
{
    const string Keys = "abgdezhqiklmnxoprstufcyw";
    const string Small = "αβγδεζηθικλμνξοπρστυφχψω", Capital = "ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩ";

    static readonly string[] Letters =
    {
        "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", "lambda", "mu", "nu", "xi", "omicron",
        "pi", "rho", "sigma", "tau", "upsilon", "phi", "chi", "psi", "omega",
    };

    static readonly Dictionary<char, string> Others = new()
    {
        ['±'] = "plus or minus", ['×'] = "times", ['·'] = "dot (times)", ['÷'] = "divided by", ['≤'] = "less than or equal to", ['≥'] = "greater than or equal to",
        ['≠'] = "not equal to", ['≈'] = "about equal to", ['≡'] = "identical to", ['∞'] = "infinity", ['→'] = "arrow to the right (tends to)", ['∂'] = "partial derivative",
        ['∈'] = "element of", ['√'] = "square root", ['∑'] = "sum", ['∏'] = "product", ['∫'] = "integral", ['∆'] = "increment", ['°'] = "degree",
        ['µ'] = "micro", ['‰'] = "per thousand", ['←'] = "arrow to the left", ['↑'] = "arrow up", ['↓'] = "arrow down", ['↔'] = "arrow both ways",
        ['⇒'] = "implies", ['⇔'] = "if and only if", ['€'] = "euro", ['£'] = "pound", ['¥'] = "yen", ['¢'] = "cent", ['$'] = "dollar",
        ['©'] = "copyright", ['®'] = "registered", ['™'] = "trade mark", ['§'] = "section", ['¶'] = "paragraph", ['•'] = "bullet", ['…'] = "three dots",
        ['–'] = "short dash", ['—'] = "long dash", ['¼'] = "one quarter", ['½'] = "one half", ['¾'] = "three quarters",
        ['∓'] = "minus or plus", ['∗'] = "star", ['∘'] = "ring", ['∮'] = "integral round a path", ['∇'] = "nabla", ['∼'] = "similar to", ['∝'] = "proportional to",
        ['≪'] = "much less than", ['≫'] = "much greater than", ['∉'] = "not an element of", ['⊂'] = "subset of", ['⊆'] = "subset of or equal to", ['∪'] = "union",
        ['∩'] = "intersection", ['∅'] = "empty set", ['∀'] = "for all", ['∃'] = "there is", ['¬'] = "not", ['∧'] = "and", ['∨'] = "or",
        ['ℝ'] = "real numbers", ['ℕ'] = "natural numbers", ['ℤ'] = "whole numbers", ['ℚ'] = "rational numbers", ['ℂ'] = "complex numbers",
        ['′'] = "prime", ['″'] = "double prime", ['⋯'] = "dots in the middle", ['⋮'] = "dots downward", ['ℏ'] = "h-bar", ['ℓ'] = "script l",
        ['∠'] = "angle", ['⊥'] = "at right angles to", ['∥'] = "parallel to",
    };

    /// <summary>The Greek letter of a key (a to z), small or capital; null for a key that has none.</summary>
    public static char? ForKey(char key, bool capital)
    {
        int i = Keys.IndexOf(char.ToLowerInvariant(key));
        return i < 0 ? null : (capital ? Capital : Small)[i];
    }

    /// <summary>The standard keys of a Greek letter: Alt and its letter, with Shift for a capital; null for other signs.</summary>
    public static string? KeysOf(char c)
    {
        int i = Small.IndexOf(c);
        if (i >= 0) return "Alt+" + char.ToUpperInvariant(Keys[i]);
        i = Capital.IndexOf(c);
        return i >= 0 ? "Alt+Shift+" + char.ToUpperInvariant(Keys[i]) : null;
    }

    /// <summary>The name of a sign; null if it has none here.</summary>
    public static string? NameOf(char c)
    {
        int i = Small.IndexOf(c);
        if (i >= 0) return Letters[i];
        i = Capital.IndexOf(c);
        if (i >= 0) return "capital " + Letters[i];
        return Others.TryGetValue(c, out string? name) ? name : null;
    }

    /// <summary>What is shown when the mouse rests on a sign: its name, and its keys in the equation box if it has any.</summary>
    public static string? Of(char c)
    {
        string? name = NameOf(c);
        string keys = Shortcuts.Of("sym." + c);
        return keys.Length == 0 ? name : (name ?? c.ToString()) + "  (" + keys + ")";
    }
}

/// <summary>The page of Preferences where the shortcuts are looked up and changed. It works on a copy,
/// which becomes the shortcuts of the program when Preferences is closed with OK.</summary>
sealed class ShortcutsPage
{
    public readonly StackPanel Panel = new() { Margin = new Thickness(14, 12, 14, 12) };
    public readonly Dictionary<string, string> Custom = new(Shortcuts.Custom);
    readonly ComboBox scope = new() { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ListBox list = new() { Height = 232, Margin = new Thickness(0, 8, 0, 0), Background = Theme.Field, BorderBrush = Theme.Edge, BorderThickness = new Thickness(1) };
    readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 8, 0, 0) };
    readonly Window owner;

    public ShortcutsPage(Window owner)
    {
        this.owner = owner;
        scope.Items.Add("Commands of the program");
        scope.Items.Add("Symbols in the equation box");
        scope.SelectedIndex = 0;
        scope.SelectionChanged += (_, _) => Fill(null);
        Button B(string text, string tip, System.Action act)
        {
            var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 8, 8, 0) };
            b.Click += (_, _) => act();
            return b;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(B("Change…", "Give the chosen line other keys", Change));
        row.Children.Add(B("No keys", "Take the keys of the chosen line away", () => Give("")));
        row.Children.Add(B("Standard", "Give the chosen line its standard keys again", () => { if (Chosen is { } i) Give(i.Standard); }));
        row.Children.Add(B("All standard", "Give everything its standard keys again", () => { Custom.Clear(); note.Text = "Everything has its standard keys again."; Fill(null); }));
        Panel.Children.Add(scope);
        Panel.Children.Add(list);
        Panel.Children.Add(row);
        Panel.Children.Add(note);
        list.MouseDoubleClick += (_, _) => Change();
        note.Text = "Choose a line and press \"Change…\", or double-click it. A shortcut has Ctrl or Alt in it, or is one of F1 to F12.";
        Fill(null);
    }

    Shortcuts.Item? Chosen => (list.SelectedItem as ListBoxItem)?.Tag is string id ? Shortcuts.Items.Find(i => i.Id == id) : null;

    void Fill(string? keep)
    {
        list.Items.Clear();
        foreach (var item in Shortcuts.Items.Where(i => i.Symbol == (scope.SelectedIndex == 1)))
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            var keys = new TextBlock { Text = Shortcuts.Of(item.Id, Custom), FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(keys, 1);
            line.Children.Add(new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            line.Children.Add(keys);
            var entry = new ListBoxItem { Content = line, Tag = item.Id, Padding = new Thickness(8, 3, 8, 3), MinHeight = 0 };
            list.Items.Add(entry);
            if (item.Id == keep) { list.SelectedItem = entry; list.ScrollIntoView(entry); }
        }
    }

    void Change()
    {
        if (Chosen is not { } item) return;
        var dlg = new KeysDialog(owner, item.Name.Trim());
        if (dlg.ShowDialog() == true) Give(dlg.Keys);
    }

    void Give(string keys)
    {
        if (Chosen is not { } item) return;
        string? lost = Shortcuts.Set(Custom, item.Id, keys);
        note.Text = keys.Length == 0 ? item.Name.Trim() + " has no keys now."
            : item.Name.Trim() + " now has " + keys + "." + (lost != null ? " " + lost.Trim() + " had these keys and has none now." : "");
        Fill(item.Id);
    }
}

/// <summary>Asks for the keys of a shortcut: the user presses them.</summary>
sealed class KeysDialog : Window
{
    readonly TextBlock shown = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 6), Text = "…" };
    readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, MaxWidth = 340, MinHeight = 36 };

    public string Keys { get; private set; } = "";

    public KeysDialog(Window? owner, string what)
    {
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Shortcut";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(18), MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = "Press the keys for:  " + what, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
        panel.Children.Add(shown);
        panel.Children.Add(note);
        panel.Children.Add(DialogParts.Buttons(this, () => Keys.Length > 0));
        Content = panel;
        note.Text = "With Ctrl or Alt, or one of F1 to F12.";
        PreviewKeyDown += (_, e) =>
        {
            string keys = Shortcuts.Gesture(e);
            if (keys.Length == 0) return;
            e.Handled = true;
            if (Shortcuts.Reserved(keys))
            {
                note.Text = keys + " keeps its own meaning everywhere and cannot be used.";
                return;
            }
            Keys = keys;
            shown.Text = keys;
            note.Text = keys.StartsWith("Ctrl+Alt+", System.StringComparison.Ordinal)
                ? "On many keyboards Ctrl with Alt is the same as AltGr, which types signs such as € and @: those would no longer be typed with these keys."
                : "Press OK to use these keys.";
        };
    }
}

/// <summary>A message or a question in a small box of the program's own, in the colours the user chose.</summary>
sealed class MsgBox : Window
{
    MessageBoxResult result;

    MsgBox(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        Owner = owner is { IsVisible: true } ? owner : null;
        DialogParts.Prepare(this);
        Title = caption;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        // Closing the box without an answer is the careful answer.
        result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;

        var body = new DockPanel();
        var (sign, colour) = icon switch
        {
            MessageBoxImage.Question => ("?", 0x2B6CC4),
            MessageBoxImage.Warning => ("!", 0xC98A00),
            MessageBoxImage.Error => ("×", 0xC0392B),
            MessageBoxImage.Information => ("i", 0x2B6CC4),
            _ => ("", 0),
        };
        if (sign.Length > 0)
        {
            var mark = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromRgb((byte)(colour >> 16), (byte)(colour >> 8), (byte)colour)),
                Child = new TextBlock { Text = sign, Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            DockPanel.SetDock(mark, Dock.Left);
            body.Children.Add(mark);
        }
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, MinWidth = 220, VerticalAlignment = VerticalAlignment.Center });

        var ends = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        void Add(string label, MessageBoxResult answer, bool main, bool cancel)
        {
            var b = new Button { Content = label, MinWidth = 86, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), IsDefault = main, IsCancel = cancel };
            b.Click += (_, _) => { result = answer; Close(); };
            ends.Children.Add(b);
        }
        switch (buttons)
        {
            case MessageBoxButton.YesNoCancel:
                Add("Yes", MessageBoxResult.Yes, true, false);
                Add("No", MessageBoxResult.No, false, false);
                Add("Cancel", MessageBoxResult.Cancel, false, true);
                break;
            case MessageBoxButton.YesNo:
                Add("Yes", MessageBoxResult.Yes, true, false);
                Add("No", MessageBoxResult.No, false, true);
                break;
            case MessageBoxButton.OKCancel:
                Add("OK", MessageBoxResult.OK, true, false);
                Add("Cancel", MessageBoxResult.Cancel, false, true);
                break;
            default:
                Add("OK", MessageBoxResult.OK, true, true);
                break;
        }
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(body);
        panel.Children.Add(ends);
        Content = panel;
    }

    /// <summary>The box itself, not shown yet.</summary>
    public static Window Make(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) => new MsgBox(owner, text, caption, buttons, icon);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        var box = new MsgBox(owner, text, caption, buttons, icon);
        box.ShowDialog();
        return box.result;
    }

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) =>
        Show(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive), text, caption, buttons, icon);
}

/// <summary>Lengths in the boxes of the program, in centimetres or inches as chosen under Preferences.</summary>
static class Lengths
{
    public static string Unit => Settings.Metric ? "cm" : "in";
    static double PerUnit => Settings.Metric ? 360000 : 914400;

    public static string Show(int emu) => (emu / PerUnit).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The length typed into a box, in the units of layout; null if it is not a number.</summary>
    public static int? Read(TextBox box) =>
        double.TryParse(box.Text.Trim().Replace(',', '.').Replace(((char)0x2212).ToString(), "-"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && Math.Abs(v) < 1000
            ? (int)Math.Round(v * PerUnit) : null;

    public static TextBox Box(int emu) => new() { Text = Show(emu), Width = 96 };
}

/// <summary>The page: its paper, upright or on its side, and its margins.</summary>
sealed class PageDialog : Window
{
    static readonly (string Name, int W, int H)[] Papers =
    {
        ("A4  (21 × 29.7 cm)", 7560000, 10692000), ("A5  (14.8 × 21 cm)", 5328000, 7560000), ("A3  (29.7 × 42 cm)", 10692000, 15120000),
        ("Letter  (8.5 × 11 in)", 7772400, 10058400), ("Legal  (8.5 × 14 in)", 7772400, 12801600),
    };

    readonly ComboBox paper = new() { Width = 230, HorizontalAlignment = HorizontalAlignment.Left };
    readonly RadioButton upright = new() { Content = "Upright", GroupName = "turn", Margin = new Thickness(0, 4, 18, 0) }, sideways = new() { Content = "On its side (landscape)", GroupName = "turn", Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBox top, bottom, left, right;
    readonly int otherW, otherH;

    public PageSetup Result { get; private set; }

    readonly CheckBox keepLines = new() { Content = "No line of a paragraph alone at the top or the bottom of a page" };
    readonly ComboBox hyphenBox = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };

    readonly CheckBox adaptSpacing = new()
    {
        Content = "Adapt the spacing between paragraphs at page ends", Margin = new Thickness(0, 6, 0, 0),
        ToolTip = "The room between paragraphs gives a little, so that what the end of a page cuts comes back or begins the next page whole",
    };

    public bool KeepLines => keepLines.IsChecked == true;
    public bool Adapt => adaptSpacing.IsChecked == true;
    public string Hyphenation => hyphenBox.SelectedIndex > 0 && hyphenBox.SelectedIndex <= Hyphenator.Languages.Length ? Hyphenator.Languages[hyphenBox.SelectedIndex - 1].Id : "";

    readonly ComboBox numberPlace = new() { Width = 200 }, numberLook = new() { Width = 130, Margin = new Thickness(8, 0, 0, 0) };
    /// <summary>Where the page numbers stand (a place of PageNumbers.Places, 0 for none) and how they look.</summary>
    public int NumberPlace => Math.Max(0, numberPlace.SelectedIndex);
    public int NumberLook => Math.Max(0, numberLook.SelectedIndex);

    public PageDialog(Window? owner, PageSetup page, bool keep = false, string hyphenation = "", int numbersAt = 0, int numbersAs = 0, bool adapt = false)
    {
        foreach (string place in PageNumbers.Places) numberPlace.Items.Add(place);
        foreach (var (name, _) in PageNumbers.Looks) numberLook.Items.Add(name);
        numberPlace.SelectedIndex = numbersAt;
        numberLook.SelectedIndex = numbersAs;
        numberPlace.SelectionChanged += (_, _) => numberLook.IsEnabled = numberPlace.SelectedIndex > 0;
        numberLook.IsEnabled = numbersAt > 0;
        Result = page;
        keepLines.IsChecked = keep;
        adaptSpacing.IsChecked = adapt;
        hyphenBox.Items.Add("Off");
        foreach (var (_, name) in Hyphenator.Languages) hyphenBox.Items.Add(name);
        hyphenBox.SelectedIndex = 1 + Array.FindIndex(Hyphenator.Languages, l => l.Id == hyphenation);
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Page";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        bool wide = page.Width > page.Height;
        int w = Math.Min(page.Width, page.Height), h = Math.Max(page.Width, page.Height);
        foreach (var p in Papers) paper.Items.Add(p.Name);
        int known = Array.FindIndex(Papers, p => Math.Abs(p.W - w) < 20000 && Math.Abs(p.H - h) < 20000);
        otherW = w;
        otherH = h;
        if (known < 0) paper.Items.Add("As it is  (" + Lengths.Show(w) + " × " + Lengths.Show(h) + " " + Lengths.Unit + ")");
        paper.SelectedIndex = known >= 0 ? known : Papers.Length;
        (wide ? sideways : upright).IsChecked = true;
        top = Lengths.Box(page.Top);
        bottom = Lengths.Box(page.Bottom);
        left = Lengths.Box(page.Left);
        right = Lengths.Box(page.Right);

        TextBlock Head(string text, double above) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, above, 0, 4) };
        var turn = new StackPanel { Orientation = Orientation.Horizontal };
        turn.Children.Add(upright);
        turn.Children.Add(sideways);
        var margins = new Grid();
        for (int c = 0; c < 4; c++) margins.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        margins.RowDefinitions.Add(new RowDefinition());
        margins.RowDefinitions.Add(new RowDefinition());
        void Put(string label, TextBox box, int row, int col)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(col == 0 ? 0 : 20, row == 0 ? 0 : 8, 8, 0) };
            box.Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 0);
            Grid.SetRow(l, row);
            Grid.SetColumn(l, col);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col + 1);
            margins.Children.Add(l);
            margins.Children.Add(box);
        }
        Put("Top", top, 0, 0);
        Put("Bottom", bottom, 0, 2);
        Put("Left", left, 1, 0);
        Put("Right", right, 1, 2);

        var panel = new StackPanel { Margin = new Thickness(16), MinWidth = 380 };
        panel.Children.Add(Head("Paper", 0));
        panel.Children.Add(paper);
        panel.Children.Add(Head("The page stands", 14));
        panel.Children.Add(turn);
        panel.Children.Add(Head("Margins, in " + (Settings.Metric ? "centimetres" : "inches"), 14));
        panel.Children.Add(margins);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 12, 0, 0), MaxWidth = 380,
            Text = "The whole document is laid out again for the new page. A margin can be a third of the page at most.",
        });
        panel.Children.Add(Head("Page numbers", 14));
        var numberRow = new StackPanel { Orientation = Orientation.Horizontal };
        numberRow.Children.Add(numberPlace);
        numberRow.Children.Add(numberLook);
        panel.Children.Add(numberRow);
        panel.Children.Add(Head("Text on the pages", 14));
        panel.Children.Add(keepLines);
        panel.Children.Add(adaptSpacing);
        var hyphenRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        hyphenRow.Children.Add(new TextBlock { Text = "Divide words at the ends of lines", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        hyphenRow.Children.Add(hyphenBox);
        panel.Children.Add(hyphenRow);
        panel.Children.Add(DialogParts.Buttons(this, Accept));
        Content = panel;
    }

    bool Accept()
    {
        var (w, h) = paper.SelectedIndex < Papers.Length ? (Papers[paper.SelectedIndex].W, Papers[paper.SelectedIndex].H) : (otherW, otherH);
        if (sideways.IsChecked == true) (w, h) = (h, w);
        int?[] m = { Lengths.Read(top), Lengths.Read(bottom), Lengths.Read(left), Lengths.Read(right) };
        string? wrong = null;
        if (m.Any(v => v == null || v < 0)) wrong = "A margin is a number that is not below 0, such as 2.5.";
        else if (m[0] > h / 3 || m[1] > h / 3 || m[2] > w / 3 || m[3] > w / 3) wrong = "A margin can be a third of the page at most.";
        if (wrong != null)
        {
            MsgBox.Show(this, wrong, "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        Result = new PageSetup { Width = w, Height = h, Top = m[0]!.Value, Bottom = m[1]!.Value, Left = m[2]!.Value, Right = m[3]!.Value };
        return true;
    }
}

/// <summary>The styles of the document: what the text of each looks like and how its paragraphs stand.
/// A change reaches every paragraph of the style. Styles can be added, and those that were added taken away.</summary>
sealed class StylesDialog : Window
{
    static readonly string[] Fixed = { "Normal", "NoSpacing", "Title", "Heading1", "Heading2", "Heading3", "Footnote", "Header", "Footer" };
    static readonly int[] Lines = { 1000, 1080, 1150, 1500, 2000, 2500, 3000 };
    static readonly (string Name, int Rgb)[] Inks =
    {
        ("Black", 0x000000), ("Dark grey", 0x595959), ("Blue", 0x2F5496), ("Dark blue", 0x1F3763), ("Red", 0xC00000), ("Green", 0x2E7D32),
    };

    readonly List<ParaStyle> styles;
    readonly ListBox list = new() { Width = 170, Height = 330, Background = Theme.Field, BorderBrush = Theme.Edge, BorderThickness = new Thickness(1) };
    readonly ComboBox font = new() { Width = 200 }, align = new() { Width = 130 }, line = new() { Width = 96 };
    readonly TextBox size = new() { Width = 70 }, before = new() { Width = 70 }, after = new() { Width = 70 };
    readonly TextBox left = new() { Width = 96 }, first = new() { Width = 96 }, right = new() { Width = 96 };
    readonly CheckBox bold = new() { Content = "Bold", Margin = new Thickness(0, 0, 14, 0) }, italic = new() { Content = "Italic", Margin = new Thickness(0, 0, 14, 0) }, under = new() { Content = "Underlined" };
    readonly StackPanel inkHost = new();
    ColorChoice ink;
    readonly Button remove = new() { Content = "Remove", Margin = new Thickness(8, 8, 0, 0) };
    int shown = -1;

    public List<ParaStyle> Styles => styles;

    public StylesDialog(Window? owner, IEnumerable<ParaStyle> current, string start)
    {
        styles = current.Select(Copy).ToList();
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Styles";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        font.ItemsSource = FontCatalog.Families;
        foreach (string a in new[] { "Left", "Centre", "Right", "Justified" }) align.Items.Add(a);
        foreach (int l in Lines) line.Items.Add((l / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture));
        ink = new ColorChoice(this, Inks, 0);
        inkHost.Children.Add(ink.Row);

        var form = new Grid { Margin = new Thickness(18, 0, 0, 0) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Row(string label, UIElement field, bool head = false)
        {
            int r = form.RowDefinitions.Count;
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, r == 0 ? 0 : head ? 16 : 8, 12, 0), FontWeight = head ? FontWeights.SemiBold : FontWeights.Normal };
            Grid.SetRow(l, r);
            form.Children.Add(l);
            if (field is FrameworkElement fe)
            {
                fe.Margin = new Thickness(0, r == 0 ? 0 : head ? 16 : 8, 0, 0);
                fe.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetRow(fe, r);
                Grid.SetColumn(fe, 1);
                form.Children.Add(fe);
            }
        }
        StackPanel Side(params UIElement[] parts)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var e in parts) p.Children.Add(e);
            return p;
        }
        TextBlock Unit(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 16, 0), Foreground = Theme.Dim };
        Row("The text", new TextBlock(), true);
        Row("Font", font);
        Row("Size", Side(size, Unit("points")));
        Row("Look", Side(bold, italic, under));
        Row("Colour", inkHost);
        Row("The paragraph", new TextBlock(), true);
        Row("Alignment", align);
        Row("Line spacing", line);
        Row("Space before", Side(before, Unit("points"), new TextBlock { Text = "after", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, after, Unit("points")));
        Row("Indent on the left", Side(left, Unit(Lengths.Unit)));
        Row("First line, further in", Side(first, Unit(Lengths.Unit + "  (below 0: further out)")));
        Row("Indent on the right", Side(right, Unit(Lengths.Unit)));

        var add = new Button { Content = "New style…", Margin = new Thickness(0, 8, 0, 0), ToolTip = "A new style that starts as a copy of the chosen one" };
        add.Click += (_, _) => Add();
        remove.Click += (_, _) => Remove();
        remove.ToolTip = "Takes a style away that was added. Its paragraphs become Normal";
        var under1 = new StackPanel { Orientation = Orientation.Horizontal };
        under1.Children.Add(add);
        under1.Children.Add(remove);
        var leftSide = new StackPanel();
        leftSide.Children.Add(list);
        leftSide.Children.Add(under1);
        var body = new DockPanel();
        DockPanel.SetDock(leftSide, Dock.Left);
        body.Children.Add(leftSide);
        body.Children.Add(form);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(body);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Dim, Margin = new Thickness(0, 12, 0, 0), MaxWidth = 640,
            Text = "A change holds for every paragraph of the style in this document. Text that was given its own font, size or colour keeps it.",
        });
        panel.Children.Add(DialogParts.Buttons(this, () => Store()));
        Content = panel;

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex == shown) return;
            if (shown >= 0 && shown < styles.Count && !Store()) { list.SelectedIndex = shown; return; }
            Load(list.SelectedIndex);
        };
        Fill(Math.Max(0, styles.FindIndex(s => s.Id == start)));
    }

    public static ParaStyle Copy(ParaStyle s) => new()
    {
        Id = s.Id, Name = s.Name, Char = s.Char, Align = s.Align, Before = s.Before, After = s.After, Line = s.Line,
        IndentLeft = s.IndentLeft, IndentRight = s.IndentRight, IndentFirst = s.IndentFirst, KeepNext = s.KeepNext, Next = s.Next, Borders = s.Borders,
    };

    void Fill(int select)
    {
        shown = -1;
        list.Items.Clear();
        foreach (var s in styles) list.Items.Add(s.Name);
        list.SelectedIndex = select;
        if (shown != select) Load(select);
    }

    void Load(int index)
    {
        var s = styles[index];
        shown = index;
        font.SelectedItem = FontCatalog.Families.FirstOrDefault(n => n.Equals(s.Char.Font, StringComparison.OrdinalIgnoreCase));
        if (font.SelectedItem == null) font.Text = s.Char.Font;
        size.Text = (s.Char.Size / 2.0).ToString("0.#", CultureInfo.InvariantCulture);
        bold.IsChecked = s.Char.Bold;
        italic.IsChecked = s.Char.Italic;
        under.IsChecked = s.Char.Underline;
        ink = new ColorChoice(this, Inks, s.Char.Color);
        inkHost.Children.Clear();
        inkHost.Children.Add(ink.Row);
        align.SelectedIndex = (int)s.Align;
        int l = Array.IndexOf(Lines, s.Line);
        if (l < 0) { line.Items.Add((s.Line / 1000.0).ToString("0.0##", CultureInfo.InvariantCulture)); l = line.Items.Count - 1; }
        line.SelectedIndex = l;
        before.Text = (s.Before / (double)U.PerPoint).ToString("0.#", CultureInfo.InvariantCulture);
        after.Text = (s.After / (double)U.PerPoint).ToString("0.#", CultureInfo.InvariantCulture);
        left.Text = Lengths.Show(s.IndentLeft);
        first.Text = Lengths.Show(s.IndentFirst);
        right.Text = Lengths.Show(s.IndentRight);
        remove.IsEnabled = Array.IndexOf(Fixed, s.Id) < 0;
    }

    static double? Number(TextBox box) =>
        double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary>Takes what the boxes say into the style that is shown; false, with a message, if a box cannot be read.</summary>
    bool Store()
    {
        if (shown < 0 || shown >= styles.Count) return true;
        var s = styles[shown];
        double? pt = Number(size), b = Number(before), a = Number(after);
        int? l = Lengths.Read(left), f = Lengths.Read(first), r = Lengths.Read(right);
        string? wrong = null;
        if (pt is not (>= 1 and <= 500)) wrong = "The size is a number from 1 to 500.";
        else if (b is not (>= 0 and <= 500) || a is not (>= 0 and <= 500)) wrong = "The space before and after is a number of points from 0 to 500.";
        else if (l is not >= 0 || r is not >= 0 || f == null) wrong = "The indents are numbers such as 1.25; those on the left and on the right are not below 0.";
        else if (l + Math.Min(0, f.Value) < 0) wrong = "The first line cannot stand further out than the left edge of the text.";
        if (wrong != null)
        {
            MsgBox.Show(this, wrong, "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        string family = font.SelectedItem as string ?? s.Char.Font;
        s.Char = s.Char with { Font = family, Size = (int)Math.Round(pt!.Value * 2), Bold = bold.IsChecked == true, Italic = italic.IsChecked == true, Underline = under.IsChecked == true, Color = ink.Value };
        s.Align = (Align)Math.Max(0, align.SelectedIndex);
        if (line.SelectedIndex >= 0 && line.SelectedIndex < Lines.Length) s.Line = Lines[line.SelectedIndex];
        s.Before = (int)Math.Round(b!.Value * U.PerPoint);
        s.After = (int)Math.Round(a!.Value * U.PerPoint);
        s.IndentLeft = l!.Value;
        s.IndentFirst = f.Value;
        s.IndentRight = r!.Value;
        return true;
    }

    void Add()
    {
        if (!Store()) return;
        var ask = new NameDialog(this, "New style", "Name of the new style", "");
        if (ask.ShowDialog() != true) return;
        string name = ask.Value;
        if (styles.Exists(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MsgBox.Show(this, "There is a style of that name already.", "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int n = 1;
        while (styles.Exists(s => s.Id == "Own" + n.ToString(CultureInfo.InvariantCulture))) n++;
        var made = Copy(styles[Math.Max(0, shown)]);
        made.Id = "Own" + n.ToString(CultureInfo.InvariantCulture);
        made.Name = name;
        made.Next = made.Id;
        made.KeepNext = false;
        styles.Add(made);
        Fill(styles.Count - 1);
    }

    void Remove()
    {
        if (shown < 0 || Array.IndexOf(Fixed, styles[shown].Id) >= 0) return;
        styles.RemoveAt(shown);
        Fill(0);
    }
}

/// <summary>Asks for one short text, such as a name.</summary>
sealed class NameDialog : Window
{
    readonly TextBox box = new() { Width = 300 };

    public string Value => box.Text.Trim();

    public NameDialog(Window? owner, string title, string label, string text)
    {
        Owner = owner;
        DialogParts.Prepare(this);
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        box.Text = text;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(box);
        panel.Children.Add(DialogParts.Buttons(this, () => Value.Length > 0));
        Content = panel;
        Loaded += (_, _) => box.Focus();
    }
}

/// <summary>The box for a link: the address, and for a new link the text that is shown.</summary>
sealed class LinkDialog : Window
{
    readonly TextBox shown = new() { Width = 340 }, address = new() { Width = 340 };

    public string Shown => shown.Text.Trim();
    public string Address => Tidy(address.Text);
    public bool Remove { get; private set; }

    /// <summary>An address as it is kept: without spaces around it; what has no kind of its own becomes a web address,
    /// or an e-mail address if it looks like one.</summary>
    public static string Tidy(string text)
    {
        string a = text.Trim();
        if (a.Length == 0 || a.Contains("://") || a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return a;
        return a.Contains('@') && !a.Contains('/') ? "mailto:" + a : "https://" + a;
    }

    static bool Valid(string a)
    {
        if (a.Length == 0 || a.Length > 2000) return false;
        foreach (char c in a)
            if (c <= ' ' || c == 127) return false;
        return true;
    }

    /// <summary>True for the addresses the program hands to Windows: web pages and e-mail addresses.</summary>
    public static bool CanOpen(string a) =>
        Valid(a) && Uri.TryCreate(a, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto);

    public LinkDialog(Window? owner, bool fresh, string now)
    {
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Link";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        address.Text = now;
        var panel = new StackPanel { Margin = new Thickness(16) };
        if (fresh)
        {
            panel.Children.Add(new TextBlock { Text = "Text to show (empty: the address itself)", Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(shown);
        }
        panel.Children.Add(new TextBlock { Text = "Address of a web page, or an e-mail address", Margin = new Thickness(0, fresh ? 12 : 0, 0, 6) });
        panel.Children.Add(address);
        panel.Children.Add(new TextBlock
        {
            Text = "In the text, hold Ctrl and click a link to open it. In a PDF the link works by itself.",
            Foreground = Theme.Dim, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 340, TextWrapping = TextWrapping.Wrap,
        });
        if (now.Length > 0)
        {
            var remove = new Button { Content = "Remove the link", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
            remove.Click += (_, _) => { Remove = true; DialogResult = true; };
            panel.Children.Add(remove);
        }
        panel.Children.Add(DialogParts.Buttons(this, () => Valid(Address)));
        Content = panel;
        Loaded += (_, _) => { if (fresh) shown.Focus(); else { address.Focus(); address.SelectAll(); } };
    }
}

/// <summary>The box for the spacing and the indents of paragraphs. What is left as the style says goes on following the style.</summary>
sealed class ParagraphDialog : Window
{
    static readonly int[] Lines = { 1000, 1080, 1150, 1500, 2000, 2500, 3000 };
    readonly TextBox before = new() { Width = 96 }, after = new() { Width = 96 }, left = new() { Width = 96 }, first = new() { Width = 96 }, right = new() { Width = 96 };
    readonly ComboBox line = new() { Width = 96 };
    readonly CheckBox together = new();
    /// <summary>Where the paragraph has lines: none, below, above, above and below, around; a sixth entry stands for another set that it came with.</summary>
    readonly ComboBox edges = new() { Width = 150 };
    static readonly string[] EdgeNames = { "None", "Below", "Above", "Above and below", "All around" };
    ParaBorders shownEdges;
    readonly int room;
    /// <summary>A list can stand a little left of the text; what is shown is not called wrong.</summary>
    readonly int leastLeft;
    int lineValue;

    public ParaLook Value { get; private set; }

    public ParagraphDialog(Window? owner, ParaLook now, ParaLook style, int textWidth)
    {
        Owner = owner;
        DialogParts.Prepare(this);
        Title = "Paragraph";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        room = textWidth;
        leastLeft = Math.Min(0, now.Left);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Head(string text)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, grid.RowDefinitions.Count > 1 ? 14 : 0, 0, 6) };
            Grid.SetRow(t, grid.RowDefinitions.Count - 1);
            Grid.SetColumnSpan(t, 3);
            grid.Children.Add(t);
        }
        void Row(string label, Control box, string unit)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            int r = grid.RowDefinitions.Count - 1;
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var u = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Theme.Dim };
            box.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(l, r);
            Grid.SetRow(box, r);
            Grid.SetColumn(box, 1);
            Grid.SetRow(u, r);
            Grid.SetColumn(u, 2);
            grid.Children.Add(l);
            grid.Children.Add(box);
            grid.Children.Add(u);
        }
        Head("Spacing");
        Row("Above the paragraph", before, "pt");
        Row("Below the paragraph", after, "pt");
        Row("Between its lines", line, "lines");
        Head("Indents");
        Row("On the left", left, Lengths.Unit);
        Row("First line (a number below 0 lets it stand out)", first, Lengths.Unit);
        Row("On the right", right, Lengths.Unit);
        Head("Lines");
        Row("At the edges of the paragraph", edges, "");
        Grid.SetColumnSpan(edges, 2);
        edges.HorizontalAlignment = HorizontalAlignment.Left;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(grid);
        var back = new Button { Content = "As the style says", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        back.Click += (_, _) => Show(style);
        panel.Children.Add(back);
        var keepHead = new TextBlock { Text = "On the pages", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };
        panel.Children.Add(keepHead);
        together.Content = new TextBlock
        {
            Text = "Begins a block, such as a task with its parts. The block goes on to the next paragraph marked like this. If less than half of it fits at the bottom of a page, it starts on the next page.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
        };
        panel.Children.Add(together);
        panel.Children.Add(DialogParts.Buttons(this, Store));
        Content = panel;
        Show(now);
        Loaded += (_, _) => { before.Focus(); before.SelectAll(); };
    }

    void Show(ParaLook v)
    {
        before.Text = (v.Before / (double)U.PerPoint).ToString("0.#", CultureInfo.InvariantCulture);
        after.Text = (v.After / (double)U.PerPoint).ToString("0.#", CultureInfo.InvariantCulture);
        left.Text = Lengths.Show(v.Left);
        first.Text = Lengths.Show(v.First);
        right.Text = Lengths.Show(v.Right);
        line.Items.Clear();
        foreach (int l in Lines) line.Items.Add((l / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture));
        int at = Array.IndexOf(Lines, v.Line);
        if (at < 0)
        {
            line.Items.Add((v.Line / 1000.0).ToString("0.0##", CultureInfo.InvariantCulture));
            at = line.Items.Count - 1;
        }
        lineValue = v.Line;
        line.SelectedIndex = at;
        together.IsChecked = v.Together;
        shownEdges = v.Borders;
        edges.Items.Clear();
        foreach (string name in EdgeNames) edges.Items.Add(name);
        int kind = KindOf(v.Borders);
        if (kind < 0)
        {
            edges.Items.Add("As they are");
            kind = EdgeNames.Length;
        }
        edges.SelectedIndex = kind;
    }

    /// <summary>Which of the five named sets these lines are; -1 for another one.</summary>
    static int KindOf(ParaBorders b)
    {
        bool t = b.Top > 0, m = b.Bottom > 0, l = b.Left > 0, r = b.Right > 0;
        if (l != r) return -1;
        if (l) return t && m ? 4 : -1;
        return t ? (m ? 3 : 2) : m ? 1 : 0;
    }

    /// <summary>The lines for the chosen set, with the thickness, room and colour the paragraph had, or the usual ones.</summary>
    ParaBorders Chosen()
    {
        int kind = edges.SelectedIndex;
        if (kind < 0 || kind >= EdgeNames.Length || kind == KindOf(shownEdges)) return shownEdges;
        if (kind == 0) return default;
        var had = shownEdges;
        int w = new[] { had.Top, had.Bottom, had.Left, had.Right }.FirstOrDefault(x => x > 0);
        if (w == 0) (w, had) = (ParaBorders.UsualWidth, new ParaBorders(Gap: ParaBorders.UsualGap, SideGap: ParaBorders.UsualSideGap));
        bool top = kind is 2 or 3 or 4, bottom = kind is 1 or 3 or 4, sides = kind == 4;
        return new ParaBorders(top ? w : 0, bottom ? w : 0, sides ? w : 0, sides ? w : 0, had.Gap, sides && had.SideGap == 0 ? ParaBorders.UsualSideGap : had.SideGap, had.Color);
    }

    static double? Number(TextBox box) =>
        double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    bool Store()
    {
        double? b = Number(before), a = Number(after);
        int? l = Lengths.Read(left), f = Lengths.Read(first), r = Lengths.Read(right);
        string? wrong = null;
        if (b is not (>= 0 and <= 500) || a is not (>= 0 and <= 500)) wrong = "The room above and below is a number of points from 0 to 500.";
        else if (l == null || l < leastLeft || r is not >= 0 || f == null) wrong = "The indents are numbers such as 1.25; those on the left and on the right are not below 0.";
        else if (l + Math.Min(0, f.Value) < leastLeft) wrong = "The first line cannot stand further out than the left edge of the text.";
        else if ((long)l.Value + r.Value + Math.Max(0, f.Value) > room - 720000) wrong = "These indents leave too little room for the text.";
        if (wrong != null)
        {
            MsgBox.Show(this, wrong, "Reiks Document", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        int lv = line.SelectedIndex >= 0 && line.SelectedIndex < Lines.Length ? Lines[line.SelectedIndex] : lineValue;
        Value = new ParaLook((int)Math.Round(b!.Value * U.PerPoint), (int)Math.Round(a!.Value * U.PerPoint), lv, l!.Value, f.Value, r!.Value, together.IsChecked == true, Chosen());
        return true;
    }
}
