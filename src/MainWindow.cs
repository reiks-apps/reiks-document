using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace ReiksDocument;

sealed class MainWindow : Window
{
    const string AppName = "Reiks Document";
    const string Filter = "Reiks document (*.docu)|*.docu";
    const string OpenFilter = "Documents (*.docu, *.docx)|*.docu;*.docx|Reiks documents (*.docu)|*.docu|Word documents (*.docx)|*.docx";

    Editor ed;
    readonly PageView view;
    readonly ScrollViewer scroller;
    readonly ComboBox styleBox = new() { Width = 110, ToolTip = "Paragraph style. Normal leaves a gap after every paragraph; No spacing leaves none" };
    readonly ComboBox fontBox = new() { Width = 156, ToolTip = "Font" };
    readonly ComboBox sizeBox = new() { Width = 96, IsEditable = true, ToolTip = "Font size in points, from 1 to 500" };
    readonly ToggleButton boldBtn, italicBtn, underBtn, codeBtn, latexBtn, strikeBtn, superBtn, subBtn, brushBtn, listBtn, darkPageBtn;
    readonly Border listFace = new() { Width = 16, Height = 14 };
    string? shownList = "?";
    readonly Separator tableSep = new();
    readonly ComboBox lineBox = new() { Width = 88, ToolTip = "Line spacing: the room between the lines inside a paragraph" };
    static readonly int[] LineValues = { 1000, 1080, 1150, 1500, 2000, 2500, 3000 };
    static readonly int[] TextColors = { 0x000000, 0x595959, 0xC00000, 0xED7D31, 0xBF9000, 0x548235, 0x2F5496, 0x0070C0, 0x7030A0, 0xFFFFFF };
    static readonly int[] MarkColors = { 0xFFFF00, 0x00FF00, 0x00FFFF, 0xFF66FF, 0xFFD966, 0xBDD7EE, 0xC6E0B4, 0xF8CBAD, 0xD9D9D9, 0xFF0000 };
    readonly TextBox findBox = new() { Width = 190, Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox replaceBox = new() { Width = 190, Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox caseBox = new() { Content = "Match case", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0), Focusable = false };
    readonly Border findBar;
    readonly TextBlock wordsText = new();
    /// <summary>The spelling button at the foot of the window: the language that is checked and how many words are to be
    /// looked at. The words are counted a few paragraphs at a time, so that typing is never held up.</summary>
    readonly Button spellBtn, spellBack, spellOn;
    readonly TextBlock spellText = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly System.Windows.Threading.DispatcherTimer spellBeat = new() { Interval = TimeSpan.FromMilliseconds(15) };
    /// <summary>Runs out a moment after the last change to the text: then the words to check are counted anew.</summary>
    readonly System.Windows.Threading.DispatcherTimer spellPause = new() { Interval = TimeSpan.FromMilliseconds(400) };
    List<Paragraph>? spellQueue;
    int spellAt, spellSum, spellCount = -1;
    Button[] tableBtns = Array.Empty<Button>();
    Button? indentLess, indentMore;
    int words;
    bool wordsStale = true;
    string stylesShown = "";
    readonly ToggleButton[] alignBtns = new ToggleButton[4];
    readonly Button undoBtn, redoBtn, underMore, latexMore, caseBtn;
    readonly TextBlock statusText = new() { Margin = new Thickness(6, 0, 6, 0) }, pageText = new(), zoomText = new() { Width = 44, TextAlignment = TextAlignment.Center };
    string? path;
    /// <summary>For a document read from a Word file, which has no file of its own yet: the name it is offered to be saved under.</summary>
    string? offered;
    string note = "";
    bool syncing;

    internal Editor Editor => ed;

    // The recovery copy: written a moment after the last change, and at least every half minute while changes go on.
    readonly string recoveryId = Guid.NewGuid().ToString("N");
    readonly System.Windows.Threading.DispatcherTimer recoveryPause = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly System.Windows.Threading.DispatcherTimer recoveryBeat = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly System.Windows.Threading.DispatcherTimer wordsPause = new() { Interval = TimeSpan.FromMilliseconds(400) };
    /// <summary>From this many paragraphs on, what takes time with the length of the document is kept out of the way of the typing.</summary>
    const int BigDocument = 400;
    bool recoveryStale;
    static bool recoveryOffered;
    internal PageView View => view;

    public MainWindow(string? openPath)
    {
        Title = AppName;
        Width = 1120;
        Height = 860;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var doc = Document.NewDefault();
        var eng = new LayoutEngine(doc);
        eng.Update();
        ed = new Editor(doc, eng);
        view = new PageView(ed);
        view.PastePicture = AddPicture;
        view.Follow = FollowAtCaret;
        scroller = new ScrollViewer
        {
            Content = view, Background = Theme.Desk,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
        };
        view.Scroller = scroller;
        scroller.ScrollChanged += (_, _) => view.OnScrolled();
        scroller.SizeChanged += (_, _) => view.InvalidateVisual();
        view.ZoomChanged += () => zoomText.Text = Math.Round(view.Zoom * 100) + "%";

        var bar = new ToolBar();
        bar.Items.Add(Btn("New", "A new, empty document in a window of its own (Ctrl+N)", NewDoc));
        bar.Items.Add(Btn("Open", "Open a document (Ctrl+O). It opens in a window of its own, unless this window is still empty", () => OpenDoc(null)));
        bar.Items.Add(Btn("Save", "Save (Ctrl+S)", () => Save(false)));
        bar.Items.Add(Btn("Save as", "Save under a new name (Ctrl+Shift+S)", () => Save(true)));
        bar.Items.Add(Btn("PDF", "Save the document as a PDF (Ctrl+Shift+P)", ExportPdf));
        bar.Items.Add(Btn("Print", "Print, or make a PDF with the printer \"Microsoft Print to PDF\" (Ctrl+P)", Print));
        bar.Items.Add(Btn("Page", "The page: its paper, upright or on its side, and its margins", EditPage));
        // The third row: everything that is put into the document.
        var ins = new ToolBar { Band = 2 };
        ins.Items.Add(Btn("Image", "Insert a PNG or JPEG image", InsertPicture));
        ins.Items.Add(Btn("New page", "Start a new page where the caret is (Ctrl+Enter). What stands after the caret moves to the new page; at the end of the document it is an empty page to write on",
            () => { if (ed.Story == 0) ed.Enter(true); else Say("A new page starts in the text of the document itself, not in a footnote or a table."); }));
        ins.Items.Add(Btn("Footnote", "Insert a footnote (Ctrl+Alt+F); Esc returns to the text", InsertFootnote));
        ins.Items.Add(Btn("Symbol", "A table of symbols to put into the text: signs for money, Greek letters, signs of mathematics, arrows and others", InsertSymbol));
        ins.Items.Add(Btn("Equation", "Insert an equation typed in a line, such as x^2 + a/b", InsertEquation));
        ins.Items.Add(Btn("Chart", "Insert a bar, line or pie chart from a small table", InsertChart));
        ins.Items.Add(Btn("Link", "Link to a web page or an e-mail address (Ctrl+K)", EditLink));
        ins.Items.Add(Btn("Citation", "Sources, citations and the list of references", EditSources));
        ins.Items.Add(Btn("Contents", "Table of contents from the headings; press again to write it anew", InsertContents));
        ins.Items.Add(Btn("Header", "Header, footer and page numbers", EditBands));
        ins.Items.Add(new Separator());
        ins.Items.Add(Btn("Table", "Insert a table", InsertTable));
        ins.Items.Add(Btn("Box", "A box with a line around it, for text or a picture. A right click in it changes its lines and its shading; drag its right edge to make it narrower", InsertBox));
        // Undo and redo, the clipboard and the search stand in this row too, after the things that are put in.
        ins.Items.Add(new Separator());
        ins.Items.Add(undoBtn = Btn(Icon("M3.5,6.5 H10 A3.75,3.75 0 0 1 10,14 H6.5", "M6.5,3.5 L3.5,6.5 L6.5,9.5"), "Undo (Ctrl+Z)", () => ed.Undo()));
        ins.Items.Add(redoBtn = Btn(Icon("M12.5,6.5 H6 A3.75,3.75 0 0 0 6,14 H9.5", "M9.5,3.5 L12.5,6.5 L9.5,9.5"), "Redo (Ctrl+Y)", () => ed.Redo()));
        ins.Items.Add(new Separator());
        ins.Items.Add(Btn(Icon("M2.5,12 A2,2 0 1 1 6.5,12 A2,2 0 1 1 2.5,12 Z", "M9.5,12 A2,2 0 1 1 13.5,12 A2,2 0 1 1 9.5,12 Z", "M5.7,10.4 L11.5,1.8 M10.3,10.4 L4.5,1.8"),
            "Cut (Ctrl+X)", () => view.DoCut()));
        ins.Items.Add(Btn(Icon("M5.5,5.5 H13.5 V14.5 H5.5 Z", "M10.5,5.5 V1.5 H2.5 V10.5 H5.5"), "Copy (Ctrl+C)", () => view.DoCopy()));
        ins.Items.Add(Btn(Icon("M3,3 H13 V14.5 H3 Z", "M6,1.5 H10 V4.5 H6 Z", "M5.5,8 H10.5 M5.5,11 H10.5"), "Paste (Ctrl+V)", () => view.DoPaste()));
        ins.Items.Add(new Separator());
        ins.Items.Add(FindBtn(Icon("M11,6.75 A4.25,4.25 0 1 1 2.5,6.75 A4.25,4.25 0 1 1 11,6.75 Z", "M10,10 L14.2,14.2"), "Find and replace (Ctrl+F)", ShowFind));
        tableBtns = new[]
        {
            Btn("Row +", "A new row below this one (Tab in the last cell does the same)", () => ed.InsertRow()),
            Btn("Column +", "A new column to the right of this one", () => ed.InsertColumn()),
            Btn("Row -", "Take this row away", () => ed.DeleteRow()),
            Btn("Column -", "Take this column away", () => ed.DeleteColumn()),
        };
        // The buttons for rows and columns are there only while the caret is in a table.
        ins.Items.Add(tableSep);
        foreach (var tb in tableBtns) ins.Items.Add(tb);
        bar.Items.Add(new Separator());
        bar.Items.Add(Btn("Preferences", "Choices for the program itself: units, colours, screenshots", EditPreferences));
        view.ObjectActivated += EditObject;
        view.ObjectMenu += ShowObjectMenu;
        view.TableMenu += ShowTableMenu;
        view.TextMenu += ShowTextMenu;

        var fmt = new ToolBar { Band = 1 };
        fmt.Items.Add(styleBox);
        // A pencil, in a colour of its own so that it is found: it changes the style itself, for everything that has it.
        var stylesFace = Letter("✎", new FontFamily("Segoe UI Symbol"), minWidth: 0);
        stylesFace.FontSize = 15;
        stylesFace.Foreground = Theme.Accent;
        fmt.Items.Add(Btn(stylesFace, "Change the styles: the font, size, colour, spacing and indents of Normal, of the headings and of the other styles, for all text that has the style; or add a style", EditStyles));
        fmt.Items.Add(fontBox);
        fmt.Items.Add(sizeBox);
        var caseFace = new StackPanel { Orientation = Orientation.Horizontal };
        caseFace.Children.Add(Letter("Aa", minWidth: 0));
        caseFace.Children.Add(Chevron(4, 7));
        fmt.Items.Add(caseBtn = MenuBtn(caseFace, "Change case: capitals and small letters of the selected text, or of the word the caret is in", ShowCaseMenu));
        fmt.Items.Add(new Separator());
        fmt.Items.Add(boldBtn = Toggle(Letter("B", weight: FontWeights.Bold), "Bold (Ctrl+B)", () => ed.ToggleBold()));
        fmt.Items.Add(italicBtn = Toggle(Letter("I", style: FontStyles.Italic), "Italic (Ctrl+I)", () => ed.ToggleItalic()));
        fmt.Items.Add(underBtn = Toggle(Letter("U", lines: TextDecorations.Underline), "Underline (Ctrl+U)", () => ed.ToggleUnderline()));
        fmt.Items.Add(underMore = MenuBtn(Chevron(0, 1), "Kinds of underline", ShowUnderMenu));
        fmt.Items.Add(strikeBtn = Toggle(Letter("S", lines: TextDecorations.Strikethrough), "Strike through", () => ed.ToggleStrike()));
        fmt.Items.Add(codeBtn = Toggle(Letter("code", new FontFamily("Consolas"), minWidth: 0), "Code: letters of equal width, as for program text", () => ed.ToggleCode()));
        fmt.Items.Add(latexBtn = Toggle(Letter("LaTeX", minWidth: 0),
            "LaTeX (Ctrl+Shift+E): while this is on, what you type is LaTeX, and Enter or switching it off makes an equation of it. With text selected: makes an equation of the selected LaTeX.",
            () => ed.ToggleLatex()));
        fmt.Items.Add(latexMore = MenuBtn(Chevron(0, 1), "Where equations made from LaTeX stand: in the line, or on a line of their own", ShowLatexMenu));
        fmt.Items.Add(superBtn = Toggle(ScriptFace(true), "Raised, as in x squared. Ctrl+Shift and a number types that number raised; Ctrl and + switches raising on and off", () => ed.SetVert(1)));
        fmt.Items.Add(subBtn = Toggle(ScriptFace(false), "Lowered, as in the 2 of H2O. Ctrl and a number types that number lowered; Ctrl and - switches lowering on and off", () => ed.SetVert(2)));
        fmt.Items.Add(new Separator());
        // The letter stands on the line of the other letters; the bar of colour lies below that line.
        var colorFace = new Grid();
        colorFace.Children.Add(Letter("A", weight: FontWeights.Bold));
        colorFace.Children.Add(new Rectangle { Width = 14, Height = 3, Fill = Fill(0xC00000), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -3) });
        fmt.Items.Add(ColorButton(colorFace, "Text colour", "Colour of the style", TextColors, c => ed.SetProps(p => p with { Color = c })));
        fmt.Items.Add(ColorButton(Marked(Letter("ab", minWidth: 0)), "Highlight", "No highlight", MarkColors,
            c => ed.SetProps(p => p with { Highlight = c })));
        fmt.Items.Add(brushBtn = Toggle(BrushIcon(), "Copy format: takes the look of the text at the caret; the next text you select with the mouse gets it. Esc gives up. (Ctrl+Shift+C copies, Ctrl+Shift+V applies)",
            () => { if (ed.BrushArmed) ed.DropBrush(); else ed.CopyFormat(true); }));
        fmt.Items.Add(Btn(EraserIcon(), "Erase format: back to the plain look of the style, for the selected text or without a selection the whole paragraph (Ctrl+Space)", () => ed.ClearFormat()));
        fmt.Items.Add(new Separator());
        string[] alignTips = { "Align left (Ctrl+L)", "Centre (Ctrl+E)", "Align right (Ctrl+R)", "Justify (Ctrl+J)" };
        for (int i = 0; i < 4; i++)
        {
            var a = (Align)i;
            fmt.Items.Add(alignBtns[i] = Toggle(AlignIcon(a), alignTips[i], () => ed.SetAlign(a)));
        }
        fmt.Items.Add(new Separator());
        foreach (string label in new[] { "1.0", "1.08", "1.15", "1.5", "2.0", "2.5", "3.0" }) lineBox.Items.Add(label);
        lineBox.SelectionChanged += (_, _) =>
        {
            if (syncing || lineBox.SelectedIndex < 0) return;
            ed.SetLine(LineValues[lineBox.SelectedIndex]);
            view.Focus();
        };
        fmt.Items.Add(lineBox);
        fmt.Items.Add(Btn(Icon("M3.5,2.5 V13.5", "M1.5,4.5 L3.5,2.5 L5.5,4.5", "M1.5,11.5 L3.5,13.5 L5.5,11.5", "M8,4 H15", "M8,8 H15", "M8,12 H15"),
            "Paragraph: the room above and below, the line spacing and the indents of the selected paragraphs", EditParagraph));
        fmt.Items.Add(indentLess = Btn("←", "", () => { if (ed.InList) ed.ChangeListLevel(-1); else ed.ChangeIndent(-Settings.IndentStep); }));
        fmt.Items.Add(indentMore = Btn("→", "", () => { if (ed.InList) ed.ChangeListLevel(1); else ed.ChangeIndent(Settings.IndentStep); }));
        ShowUnits();
        // Lists: a button with the sign of the list, which opens the kinds to choose from.
        var listContent = new StackPanel { Orientation = Orientation.Horizontal };
        listContent.Children.Add(listFace);
        var chevron = Stroke("M0.5,0.5 L3.5,3.5 L6.5,0.5", 1.1);
        chevron.Width = 7;
        chevron.Height = 4;
        chevron.Margin = new Thickness(5, 6, 0, 0);
        listContent.Children.Add(chevron);
        listBtn = new ToggleButton { Content = listContent, ToolTip = "List: bullets or numbers in front of the paragraphs", Focusable = false };
        listBtn.Click += (_, _) => { Sync(); ShowListMenu(); };
        fmt.Items.Add(listBtn);
        Even(bar);
        Even(fmt);
        underMore.MinWidth = 0;
        underMore.Padding = new Thickness(3, 0, 4, 0);
        latexMore.MinWidth = 0;
        latexMore.Padding = new Thickness(3, 0, 4, 0);
        Even(ins);
        var tray = new ToolBarTray { IsLocked = true, Background = Theme.Window };
        tray.ToolBars.Add(bar);
        tray.ToolBars.Add(fmt);
        tray.ToolBars.Add(ins);

        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 5, 8, 5) };
        findRow.Children.Add(new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        findRow.Children.Add(findBox);
        findRow.Children.Add(FindBtn("Next", "Go to the next place (Enter)", FindNext));
        findRow.Children.Add(new TextBlock { Text = "Replace with", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 6, 0) });
        findRow.Children.Add(replaceBox);
        findRow.Children.Add(FindBtn("Replace", "Replace this place and go to the next (Enter)", ReplaceOne));
        findRow.Children.Add(FindBtn("Replace all", "Replace every place, in the text or in the footnote the caret is in", ReplaceEvery));
        findRow.Children.Add(caseBox);
        findRow.Children.Add(FindBtn("Close", "Close (Esc)", HideFind));
        findBar = new Border
        {
            Child = findRow, Background = Theme.Bar, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        findBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FindNext(); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
        };
        replaceBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ReplaceOne(); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
        };

        foreach (var s in new[] { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36", "48", "72" }) sizeBox.Items.Add(s);
        fontBox.ItemsSource = FontCatalog.Families;
        styleBox.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; ShowStyleMenu(); };
        styleBox.SelectionChanged += (_, _) =>
        {
            if (syncing || styleBox.SelectedItem is not ComboBoxItem it) return;
            ed.SetParaStyle((string)it.Tag);
            view.Focus();
        };
        fontBox.SelectionChanged += (_, _) =>
        {
            if (syncing || fontBox.SelectedItem is not string f) return;
            ed.SetProps(p => p with { Font = f });
            view.Focus();
        };
        sizeBox.SelectionChanged += (_, _) =>
        {
            if (syncing || sizeBox.SelectedItem is not string s) return;
            ApplySize(s);
        };
        // Only a size can be typed: up to three digits, and one more after a point or comma.
        sizeBox.PreviewTextInput += (_, e) =>
        {
            var inner = sizeBox.Template?.FindName("PART_EditableTextBox", sizeBox) as TextBox;
            string now = inner?.Text ?? sizeBox.Text;
            int at = Math.Min(inner?.SelectionStart ?? now.Length, now.Length), len = Math.Min(inner?.SelectionLength ?? 0, now.Length - at);
            e.Handled = !SizeText.IsMatch(now.Remove(at, len).Insert(at, e.Text));
        };
        DataObject.AddPastingHandler(sizeBox, (_, e) =>
        {
            if (e.DataObject.GetData(typeof(string)) is not string pasted || !SizeText.IsMatch(pasted.Trim())) e.CancelCommand();
        });
        sizeBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ApplySize(sizeBox.Text); e.Handled = true; }
            else if (e.Key == Key.Escape) { Sync(); view.Focus(); e.Handled = true; }
        };

        var status = new DockPanel { Background = Theme.Bar, LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 2) };
        wordsText.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(wordsText);
        right.Children.Add(pageText);
        right.Children.Add(new TextBlock { Text = "   " });
        // The dark page: a small half-dark disc that stays pressed while it is on.
        darkPageBtn = Toggle(Letter("◐", minWidth: 0), "Dark page: a dark sheet with light text, on the screen only. Print and PDF stay as the document is.", () =>
        {
            Settings.DarkPage = !Settings.DarkPage;
            Settings.Save();
            Theme.Apply();
            foreach (Window w in Application.Current.Windows) (w as MainWindow)?.view.InvalidateVisual();
        });
        darkPageBtn.Margin = new Thickness(0, 0, 8, 0);
        right.Children.Add(darkPageBtn);
        right.Children.Add(Btn("−", "Zoom out (Ctrl+wheel)", () => view.Zoom -= 0.1));
        right.Children.Add(zoomText);
        right.Children.Add(Btn("+", "Zoom in (Ctrl+wheel)", () => view.Zoom += 0.1));
        zoomText.VerticalAlignment = pageText.VerticalAlignment = statusText.VerticalAlignment = VerticalAlignment.Center;
        zoomText.Text = "100%";
        DockPanel.SetDock(right, Dock.Right);
        status.Children.Add(right);
        var spellFace = new StackPanel { Orientation = Orientation.Horizontal };
        spellFace.Children.Add(spellText);
        spellFace.Children.Add(Chevron(5, 1));
        spellBtn = MenuBtn(spellFace, "Spelling: the language that is checked, and how many words the check does not know. Click to choose another language, to switch the check off, or to go to the next such word.", ShowSpellMenu);
        spellBtn.Margin = new Thickness(6, 2, 0, 2);
        spellBtn.Padding = new Thickness(6, 1, 6, 1);
        DockPanel.SetDock(spellBtn, Dock.Left);
        status.Children.Add(spellBtn);
        // Two small arrows: to the word to check before the caret, and to the next one. They are there while there is any.
        FrameworkElement Arrow(bool back)
        {
            var a = Stroke(back ? "M4.5,0.5 L0.5,4.5 L4.5,8.5" : "M0.5,0.5 L4.5,4.5 L0.5,8.5", 1.2);
            a.Width = 5;
            a.Height = 9;
            return a;
        }
        spellBack = Btn(Arrow(true), "To the word to check before this place", () => GoToUnknown(-1));
        spellOn = Btn(Arrow(false), "To the next word to check", () => GoToUnknown(1));
        foreach (var arrow in new[] { spellBack, spellOn })
        {
            arrow.MinWidth = 0;
            arrow.Padding = new Thickness(6, 3, 6, 3);
            arrow.Margin = new Thickness(arrow == spellBack ? 4 : 0, 2, 0, 2);
            arrow.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(arrow, Dock.Left);
            status.Children.Add(arrow);
        }
        spellBeat.Tick += (_, _) => CountSomeSpelling();
        spellPause.Tick += (_, _) =>
        {
            spellPause.Stop();
            CountSpelling();
        };
        statusText.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Children.Add(statusText);

        var root = new DockPanel { Background = Theme.Window };
        DockPanel.SetDock(tray, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(findBar, Dock.Bottom);
        root.Children.Add(tray);
        root.Children.Add(status);
        root.Children.Add(findBar);
        root.Children.Add(new Border { Background = Theme.Desk, Child = scroller });
        Content = root;

        SetDocument(doc, eng, null, "");
        Loaded += (_, _) =>
        {
            view.Focus();
            if (openPath != null) OpenDoc(openPath, true);
            // Only the first window of the program offers what an earlier session left behind.
            if (!recoveryOffered)
            {
                recoveryOffered = true;
                OfferRecovery();
            }
        };
        recoveryPause.Tick += (_, _) => { recoveryPause.Stop(); WriteRecovery(); };
        recoveryBeat.Tick += (_, _) => WriteRecovery();
        wordsPause.Tick += (_, _) =>
        {
            wordsPause.Stop();
            wordsStale = true;
            Sync();
        };
        recoveryBeat.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Out of screenshots and screen recordings, unless the user allows them.
        ScreenCapture.Apply(this);
    }

    const double ItemHeight = 28;

    /// <summary>Gives every button and box of a toolbar the same height and the same flat look, with its content in the middle.</summary>
    static void Even(ToolBar bar)
    {
        bar.Background = Theme.Window;
        foreach (object item in bar.Items)
        {
            if (item is not Control c || item is Separator) continue;
            c.Height = ItemHeight;
            c.MinHeight = 0;
            c.VerticalAlignment = VerticalAlignment.Center;
            c.VerticalContentAlignment = VerticalAlignment.Center;
            if (c is ButtonBase)
            {
                c.MinWidth = 26;
                c.Padding = new Thickness(6, 0, 6, 0);
                c.Template = c is ToggleButton ? ToggleFace : ButtonFace;
            }
            else
            {
                // A box to choose from looks like a field to fill in, not like a button.
                c.Padding = new Thickness(10, 0, 0, 0);
                c.Background = Theme.Field;
                c.BorderBrush = Theme.Edge;
                c.BorderThickness = new Thickness(1);
                c.Margin = new Thickness(1, 0, 2, 0);
            }
        }
    }

    static readonly ControlTemplate ButtonFace = Face(typeof(Button)), ToggleFace = Face(typeof(ToggleButton));

    /// <summary>The look of a toolbar button: flat, its content in the middle, a fill while the mouse is on it,
    /// while it is pressed and, for a switch, while it is on. The same for buttons and switches, in light and dark.</summary>
    static ControlTemplate Face(Type type)
    {
        var face = new FrameworkElementFactory(typeof(Border), "face");
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        face.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        face.SetValue(SnapsToDevicePixelsProperty, true);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(MarginProperty, new TemplateBindingExtension(PaddingProperty));
        face.AppendChild(content);
        var t = new ControlTemplate(type) { VisualTree = face };
        void When(DependencyProperty p, string fill)
        {
            var tr = new Trigger { Property = p, Value = true };
            tr.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(fill), "face"));
            t.Triggers.Add(tr);
        }
        When(IsMouseOverProperty, Theme.HoverKey);
        if (type == typeof(ToggleButton)) When(ToggleButton.IsCheckedProperty, Theme.OnKey);
        When(ButtonBase.IsPressedProperty, Theme.PressKey);
        var off = new Trigger { Property = IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(OpacityProperty, 0.4, "face"));
        t.Triggers.Add(off);
        return t;
    }

    Button Btn(object face, string tip, Action a)
    {
        var b = new Button { Content = face, ToolTip = tip, Focusable = false, Padding = new Thickness(6, 2, 6, 2), MinWidth = 22 };
        b.Click += (_, _) => { a(); view.Focus(); };
        return b;
    }

    ToggleButton Toggle(object content, string tip, Action a)
    {
        var b = new ToggleButton { Content = content, ToolTip = tip, Focusable = false, Padding = new Thickness(5, 3, 5, 3) };
        b.Click += (_, _) => { a(); Sync(); view.Focus(); };
        return b;
    }

    /// <summary>One bar of an icon, in the colour the button gives its text. Every icon made of lines is built from
    /// these, four rows of them, so that their lines agree.</summary>
    static Rectangle IconBar(double left, int row, double width, double height = 1.5)
    {
        var r = new Rectangle { Width = width, Height = height, RadiusX = height > 1.5 ? height / 2 : 0, RadiusY = height > 1.5 ? height / 2 : 0 };
        r.SetBinding(Rectangle.FillProperty, new System.Windows.Data.Binding
        {
            Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty), RelativeSource = System.Windows.Data.RelativeSource.Self,
        });
        Canvas.SetLeft(r, left);
        Canvas.SetTop(r, 1 + row * 3.5 - (height - 1.5) / 2);
        return r;
    }

    static UIElement AlignIcon(Align a)
    {
        var c = new Canvas { Width = 16, Height = 14 };
        double[] w = a == Align.Justify ? new double[] { 16, 16, 16, 16 } : new double[] { 16, 10, 16, 7 };
        for (int i = 0; i < 4; i++) c.Children.Add(IconBar(a == Align.Center ? (16 - w[i]) / 2 : a == Align.Right ? 16 - w[i] : 0, i, w[i]));
        return c;
    }

    /// <summary>What may stand in the size box while typing.</summary>
    static readonly System.Text.RegularExpressions.Regex SizeText = new("^[0-9]{0,3}([.,][0-9]?)?$");

    /// <summary>The largest size: a line of it still fits on a page.</summary>
    const int MaxSize = 500;

    void ApplySize(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pt))
        {
            int half = (int)Math.Round(Math.Clamp(pt, 1, MaxSize) * 2);
            ed.SetProps(p => p with { Size = half });
        }
        Sync();
        view.Focus();
    }

    // ---- document ----

    void SetDocument(Document doc, LayoutEngine eng, string? file, string message)
    {
        ed = new Editor(doc, eng);
        path = file;
        offered = null;
        note = message;
        wordsStale = true;
        spellCount = -1;
        ed.Changed += () =>
        {
            note = "";
            // Counting the words of a long document takes a moment: it waits until the typing pauses.
            if (ed.Doc.Body.Count <= BigDocument) wordsStale = true;
            else
            {
                wordsPause.Stop();
                wordsPause.Start();
            }
        };
        ed.Changed += RecoveryDue;
        // The words to check are counted anew when the typing pauses, in a document of any length.
        ed.Changed += () =>
        {
            spellPause.Stop();
            spellPause.Start();
        };
        Recovery.Remove(recoveryId);
        recoveryStale = false;
        ed.Changed += Sync;
        ed.SelectionChanged += Sync;
        view.SetEditor(ed);
        syncing = true;
        stylesShown = "";
        syncing = false;
        Sync();
        CountSpelling();
    }

    void Sync()
    {
        syncing = true;
        var f = ed.CurrentFormat();
        var run = ed.CurrentRun();
        boldBtn.IsChecked = f.Bold;
        italicBtn.IsChecked = f.Italic;
        underBtn.IsChecked = f.Underline;
        strikeBtn.IsChecked = f.Strike;
        codeBtn.IsChecked = run.CharStyle == "Code";
        latexBtn.IsChecked = ed.LatexMode;
        darkPageBtn.IsChecked = Settings.DarkPage;
        superBtn.IsChecked = f.Vert == 1;
        subBtn.IsChecked = f.Vert == 2;
        brushBtn.IsChecked = ed.BrushArmed;
        lineBox.SelectedIndex = Array.IndexOf(LineValues, ed.CurrentLine);
        if (wordsStale)
        {
            words = ed.WordCount();
            wordsStale = false;
        }
        wordsText.Text = words.ToString("N0", CultureInfo.InvariantCulture) + (words == 1 ? " word" : " words") + "   ·   ";
        fontBox.SelectedItem = FontCatalog.Families.FirstOrDefault(n => n.Equals(f.Font, StringComparison.OrdinalIgnoreCase));
        sizeBox.Text = (f.Size / 2.0).ToString("0.#", CultureInfo.InvariantCulture);
        // The list of styles shows every style in its own look: it is written anew whenever a style, or the window, changed.
        string looks = string.Join("|", ed.Doc.ParaStyles.Select(s => $"{s.Id}/{s.Name}/{s.Char.Font}/{s.Char.Size}/{s.Char.Bold}/{s.Char.Italic}/{s.Char.Underline}/{s.Char.Color}")) + Theme.Dark;
        if (looks != stylesShown)
        {
            stylesShown = looks;
            styleBox.Items.Clear();
            foreach (var s in ed.Doc.ParaStyles) styleBox.Items.Add(StyleItem(s));
        }
        string style = ed.CurrentParagraph.Style;
        styleBox.SelectedItem = styleBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == style);
        var al = ed.CurrentAlign;
        for (int i = 0; i < 4; i++) alignBtns[i].IsChecked = (int)al == i;
        undoBtn.IsEnabled = ed.CanUndo;
        redoBtn.IsEnabled = ed.CanRedo;
        tableSep.Visibility = ed.Story < 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tb in tableBtns) tb.Visibility = tableSep.Visibility;
        string? list = ed.CurrentParagraph.List == null ? null : LayoutEngine.Bullet(ed.CurrentParagraph.List) != null ? "bullet" : "number";
        listBtn.IsChecked = list != null;
        if (list != shownList)
        {
            shownList = list;
            listFace.Child = ListIcon(list ?? "bullet");
        }
        string where = ed.Story != 0 && ed.Layout.NoteNumbers.TryGetValue(ed.Story, out int num) ? $"Footnote {num}  ·  " : "";
        if (ed.Story < 0) where = "Table cell  ·  ";
        pageText.Text = $"{where}Page {view.CaretPage + 1} of {ed.Layout.Pages.Count}";
        statusText.Text = note;
        Title = (path != null ? Path.GetFileName(path) : offered != null ? Path.GetFileNameWithoutExtension(offered) + " (from Word)" : "Untitled") + (ed.Dirty ? "*" : "") + " – " + AppName;
        syncing = false;
    }

    // ---- colours ----

    static Brush Fill(int rgb) => new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    /// <summary>A button that opens a small sheet of colours.</summary>
    Button ColorButton(UIElement face, string tip, string noneText, int[] colors, Action<int?> pick)
    {
        var btn = new Button { Content = face, ToolTip = tip, Focusable = false, Padding = new Thickness(4, 2, 4, 2) };
        var grid = new UniformGrid { Columns = 5, Margin = new Thickness(4) };
        var none = new Button { Content = noneText, Focusable = false, Margin = new Thickness(6, 6, 6, 0), Padding = new Thickness(6, 2, 6, 2) };
        var panel = new StackPanel();
        panel.Children.Add(none);
        panel.Children.Add(grid);
        var popup = new Popup
        {
            PlacementTarget = btn, Placement = PlacementMode.Bottom, StaysOpen = false,
            Child = new Border { Background = Theme.Sheet, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Child = panel },
        };
        void Choose(int? c)
        {
            popup.IsOpen = false;
            pick(c);
            view.Focus();
        }
        none.Click += (_, _) => Choose(null);
        foreach (int c in colors)
        {
            var swatch = new Button
            {
                Width = 22, Height = 22, Margin = new Thickness(2), Focusable = false, ToolTip = c.ToString("X6", CultureInfo.InvariantCulture),
                Content = new Rectangle { Width = 16, Height = 16, Fill = Fill(c), Stroke = Brushes.Gray, StrokeThickness = 0.5 }, Padding = new Thickness(0),
            };
            swatch.Click += (_, _) => Choose(c);
            grid.Children.Add(swatch);
        }
        btn.Click += (_, _) => popup.IsOpen = true;
        return btn;
    }

    /// <summary>An x with a small 2 above or below; as high as plain text, so that the x stands in line with the other buttons.</summary>
    static UIElement ScriptFace(bool raised)
    {
        // The x stands on the line of the other letters; only the 2 is raised or lowered. The x gives the face its height.
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(Letter("x", minWidth: 0));
        p.Children.Add(new TextBlock
        {
            Text = "2", FontSize = 8.5, VerticalAlignment = raised ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Margin = raised ? new Thickness(1, -1, 0, 0) : new Thickness(1, 0, 0, -2),
        });
        return p;
    }

    /// <summary>A letter or a word as the face of a button. All such faces stand on one line: each is a line of the window's own
    /// font and of one height, and a face in another font or weight has it on its text alone, so that it does not move the line.</summary>
    static TextBlock Letter(string text, FontFamily? family = null, FontWeight? weight = null, FontStyle? style = null, TextDecorationCollection? lines = null, double minWidth = 14)
    {
        var t = new TextBlock
        {
            TextAlignment = TextAlignment.Center, MinWidth = minWidth, VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        var run = new System.Windows.Documents.Run(text);
        if (family != null) run.FontFamily = family;
        if (weight != null) run.FontWeight = weight.Value;
        if (style != null) run.FontStyle = style.Value;
        if (lines != null) run.TextDecorations = lines;
        t.Inlines.Add(run);
        return t;
    }

    /// <summary>The face of the highlight button: its letters on yellow.</summary>
    static TextBlock Marked(TextBlock t)
    {
        t.Foreground = Brushes.Black;
        t.Background = Fill(0xFFFF00);
        t.Padding = new Thickness(3, 0, 3, 0);
        return t;
    }

    // ---- find and replace ----

    Button FindBtn(object face, string tip, Action a)
    {
        var b = new Button { Content = face, ToolTip = tip, Focusable = false, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        b.Click += (_, _) => a();
        return b;
    }

    void ShowFind()
    {
        findBar.Visibility = Visibility.Visible;
        Pos s = ed.SelStart, e = ed.SelEnd;
        if (ed.HasSelection && s.Para == e.Para && e.Off - s.Off <= 80)
        {
            string sel = ed.CurrentParagraph.Text[s.Off..e.Off];
            if (sel.IndexOf(ReiksDocument.Run.ObjectChar) < 0) findBox.Text = sel;
        }
        findBox.Focus();
        findBox.SelectAll();
    }

    void HideFind()
    {
        findBar.Visibility = Visibility.Collapsed;
        view.Focus();
    }

    void Say(string text)
    {
        note = text;
        Sync();
    }

    void FindNext()
    {
        if (findBox.Text.Length == 0) return;
        Say(ed.Find(findBox.Text, caseBox.IsChecked == true) ? "" : "Not found: " + findBox.Text);
    }

    void ReplaceOne()
    {
        if (findBox.Text.Length == 0) return;
        bool hit = ed.Replace(findBox.Text, replaceBox.Text, caseBox.IsChecked == true);
        Say(hit || ed.HasSelection ? "" : "Not found: " + findBox.Text);
    }

    void ReplaceEvery()
    {
        if (findBox.Text.Length == 0) return;
        int n = ed.ReplaceAll(findBox.Text, replaceBox.Text, caseBox.IsChecked == true);
        Say(n == 0 ? "Not found: " + findBox.Text : n == 1 ? "1 place replaced." : n + " places replaced.");
    }

    Span DialogSpan => ed.Layout.StyleSpan(ed.Doc.ParaStyles[0]);

    /// <summary>The room for a picture, equation or chart where the caret is: the width of the text, or of the table cell.</summary>
    long RoomAtCaret()
    {
        if (ed.CurrentCell is { } at && ed.CurrentTable is { } grid)
            return Math.Max(90000, grid.SpanWidth(at.Col, grid.Span(at.Row, at.Col).Cols) - 2L * LayoutEngine.CellPadX);
        return ed.Doc.Page.ContentWidth;
    }

    bool InBodyOrCell()
    {
        if (ed.Story <= 0) return true;
        note = "This goes into the text or into a table cell, not into a footnote.";
        Sync();
        return false;
    }

    /// <summary>A box with a line around it, for text or a picture: a table of one cell.</summary>
    void InsertBox()
    {
        if (!InBody()) return;
        ed.InsertTable(1, 1);
        view.Focus();
    }

    bool InBody()
    {
        if (ed.Story == 0) return true;
        note = "This goes into the text, not into a footnote or a table cell.";
        Sync();
        return false;
    }

    /// <summary>Opens the table of symbols; a click on one types it where the caret is.</summary>
    void InsertSymbol()
    {
        var p = ed.Layout.Paras(ed.Story)[ed.Caret.Para];
        var sp = ed.Layout.SpanAt(p, Math.Max(0, ed.Caret.Off - 1)) ?? ed.Layout.SpanAt(p, ed.Caret.Off) ?? ed.Layout.StyleSpan(ed.Doc.StyleOf(p));
        var face = sp.Font.Face;
        // A symbol the font of the text does not have is put in with a font that has it.
        string? Other(int cp) => new[] { MathLayout.Family, "Segoe UI Symbol" }.FirstOrDefault(f => FontCatalog.Resolve(f, false, false).Face.Lookup(cp).Item1 != 0);
        new SymbolDialog(this, cp => face.Lookup(cp).Item1 != 0 || Other(cp) != null, sign =>
        {
            if (face.Lookup(sign[0]).Item1 != 0 || Other(sign[0]) is not { } family) { ed.TypeText(sign); return; }
            string before = ed.CurrentFormat().Font;
            ed.SetProps(p => p with { Font = family });
            ed.TypeText(sign);
            ed.SetProps(p => p with { Font = before });
        }).ShowDialog();
        view.Focus();
    }

    void InsertEquation()
    {
        if (!InBodyOrCell()) return;
        var dlg = new EquationDialog(this, "", DialogSpan);
        if (dlg.ShowDialog() != true || dlg.Source.Length == 0) return;
        // In an empty paragraph the equation is displayed: on its own line, centred.
        bool block = !ed.HasSelection && ed.CurrentParagraph.Length == 0;
        ed.InsertObject(Equation.From(dlg.Source, block));
        if (block) ed.SetAlign(Align.Center);
    }

    void InsertChart()
    {
        if (!InBodyOrCell()) return;
        var dlg = new ChartDialog(this, Chart.Default(), DialogSpan);
        if (dlg.ShowDialog() != true) return;
        ed.InsertObject(dlg.Result);
        var c = ed.Caret;
        ed.Select(new Pos(c.Para, c.Off - 1), c);
    }

    void EditObject()
    {
        if (ed.SelectedBox() is not { } sel) return;
        if (sel.Obj is Equation eq)
        {
            var dlg = new EquationDialog(this, eq.Source, DialogSpan);
            if (dlg.ShowDialog() == true && dlg.Source.Length > 0 && dlg.Source != eq.Source)
                ed.ChangeObject(_ => Equation.From(dlg.Source, eq.Block));
        }
        else if (sel.Obj is Chart ch)
        {
            var dlg = new ChartDialog(this, ch, DialogSpan);
            if (dlg.ShowDialog() == true) ed.ChangeObject(_ => dlg.Result);
        }
        view.Focus();
    }

    /// <summary>Puts the selected image, chart or equation in the line, on a line of its own, or (an image only) lets it
    /// float in one of four ways. An image that starts to float stays where it is.</summary>
    void SetPlace(string wrap, bool ownLine)
    {
        if (ed.SelectedBox() is not { } sel) return;
        if (sel.Obj is not Picture pic)
        {
            if (sel.Obj.Block != ownLine) ed.ChangeObject(o => o with { Block = ownLine });
            return;
        }
        if (pic.Wrap == wrap && pic.Block == ownLine) return;
        int dx = 0, dy = 0;
        if (wrap != "inline")
        {
            if (pic.Floating) { dx = pic.Dx; dy = pic.Dy; }
            else if (ed.Layout.ObjectRect(sel.Pos, ed.Story) is { } r) dx = Math.Max(0, r.X - ed.Doc.Page.Left);
        }
        ed.ChangeObject(o => o is Picture p ? p with { Wrap = wrap, Block = wrap == "inline" && ownLine, Dx = dx, Dy = dy } : o);
    }

    /// <summary>The menu of a right click on an image, a chart or an equation: where it stands, and for a chart its data.</summary>
    /// <summary>A button that only opens a menu below itself.</summary>
    static Button MenuBtn(object face, string tip, Action open)
    {
        var b = new Button { Content = face, ToolTip = tip, Focusable = false };
        b.Click += (_, _) => open();
        return b;
    }

    /// <summary>The small arrow that says a button opens a menu.</summary>
    static FrameworkElement Chevron(double left, double top)
    {
        var c = Stroke("M0.5,0.5 L3.5,3.5 L6.5,0.5", 1.1);
        c.Width = 7;
        c.Height = 4;
        c.Margin = new Thickness(left, top, 0, 0);
        return c;
    }

    void ShowCaseMenu()
    {
        var menu = new ContextMenu { PlacementTarget = caseBtn, Placement = PlacementMode.Bottom };
        void Item(string text, string mode)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) =>
            {
                if (!ed.HasSelection) ed.SelectWord(ed.Caret);
                ed.ChangeCase(mode);
                view.Focus();
            };
            menu.Items.Add(item);
        }
        Item("Sentence case.", "sentence");
        Item("lowercase", "lower");
        Item("UPPERCASE", "upper");
        Item("Capitalize Each Word", "words");
        Item("tOGGLE cASE", "toggle");
        menu.IsOpen = true;
    }

    /// <summary>The menu beside the LaTeX button: where the equations made from LaTeX stand. An equation that is selected
    /// takes the place chosen at once.</summary>
    void ShowLatexMenu()
    {
        var menu = new ContextMenu { PlacementTarget = latexMore, Placement = PlacementMode.Bottom };
        void Item(string text, int form)
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = Editor.LatexForm == form };
            item.Click += (_, _) =>
            {
                Editor.LatexForm = form;
                if (form != 0 && ed.SelectedBox() is { Obj: Equation }) SetPlace("inline", form == 2);
                Sync();
                view.Focus();
            };
            menu.Items.Add(item);
        }
        Item("In the line", 1);
        Item("On a line of its own", 2);
        Item("As it comes: LaTeX alone on a line gets a line of its own", 0);
        menu.IsOpen = true;
    }

    // ---- the spelling button ----

    /// <summary>What the spelling button says.</summary>
    void ShowSpelling()
    {
        string language = Spell.Language;
        spellText.Text = Settings.Spelling == "off" ? "Spelling: off"
            : language.Length == 0 ? "Spelling: no dictionary"
            // With nothing to look at, the language alone: that nothing is said means that nothing is wrong.
            : Spell.NameOf(language) + (spellCount > 0 ? "  \u00B7  Spelling: " + spellCount.ToString("N0", CultureInfo.InvariantCulture) : "");
        spellBack.Visibility = spellOn.Visibility = spellCount > 0 && language.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Begins to count anew the words the check does not know. The number shown stays until the new one is there.</summary>
    void CountSpelling()
    {
        spellBeat.Stop();
        spellQueue = null;
        if (Spell.Language.Length == 0)
        {
            spellCount = 0;
            ShowSpelling();
            return;
        }
        spellQueue = Spell.Everywhere(ed.Doc).ToList();
        spellAt = spellSum = 0;
        ShowSpelling();
        spellBeat.Start();
    }

    /// <summary>Counts for a few thousandths of a second, then lets the window go on; the last round shows the number.</summary>
    void CountSomeSpelling()
    {
        if (spellQueue is not { } queue)
        {
            spellBeat.Stop();
            return;
        }
        var until = DateTime.UtcNow.AddMilliseconds(8);
        while (spellAt < queue.Count && DateTime.UtcNow < until) spellSum += Spell.Of(queue[spellAt++])?.Count ?? 0;
        if (spellAt < queue.Count) return;
        spellBeat.Stop();
        spellQueue = null;
        spellCount = spellSum;
        ShowSpelling();
    }

    /// <summary>Another language for the check, "auto" for that of Windows, or "off". It holds for every window and is kept.</summary>
    void SetSpelling(string value)
    {
        Settings.Spelling = value;
        Settings.Save();
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow m)
            {
                m.view.InvalidateVisual();
                m.spellCount = -1;
                m.CountSpelling();
            }
        view.Focus();
    }

    void ShowSpellMenu()
    {
        var menu = new ContextMenu { PlacementTarget = spellBtn, Placement = PlacementMode.Top };
        void Item(string text, string value)
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = Settings.Spelling.Equals(value, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => SetSpelling(value);
            menu.Items.Add(item);
        }
        string auto = Spell.Automatic;
        Item("As Windows" + (auto.Length > 0 ? ": " + Spell.NameOf(auto) : ""), "auto");
        foreach (string tag in Spell.Languages()) Item(Spell.NameOf(tag), tag);
        menu.Items.Add(new Separator());
        Item("Off", "off");
        menu.IsOpen = true;
    }

    /// <summary>Selects the next word the check does not know (way 1) or the one before (way -1), in the text of the
    /// document, from the selection on and round to the other end again.</summary>
    void GoToUnknown(int way)
    {
        if (Spell.Language.Length == 0) return;
        if (Spell.Step(ed.Doc.Body, ed.Story != 0 ? new Pos(0, 0) : ed.SelStart, ed.Story != 0 ? new Pos(0, 0) : ed.Caret > ed.Anchor ? ed.Caret : ed.Anchor, way) is { } found)
        {
            ed.MoveTo(new Pos(found.Para, found.Start), false, story: 0);
            ed.MoveTo(new Pos(found.Para, found.Start + found.Length), true, story: 0);
        }
        else Say(spellCount > 0 ? "The words to check are in table cells or footnotes; they are marked there." : "No word to check was found.");
        view.Focus();
    }

    void ShowUnderMenu()
    {
        var menu = new ContextMenu { PlacementTarget = underMore, Placement = PlacementMode.Bottom };
        void Item(string text, string? kind, string shape, double thick, double[]? dash)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var sample = Stroke(shape, thick);
            sample.Width = 84;
            sample.Height = 12;
            sample.StrokeStartLineCap = sample.StrokeEndLineCap = PenLineCap.Flat;
            if (dash != null) sample.StrokeDashArray = new DoubleCollection(dash);
            row.Children.Add(sample);
            row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(12, 0, 0, 0) });
            var item = new MenuItem { Header = row };
            item.Click += (_, _) => { ed.SetUnder(kind); view.Focus(); };
            menu.Items.Add(item);
        }
        string wave = "M0,6";
        for (int i = 0; i < 14; i++) wave += i % 2 == 0 ? " q1.5,-3.6 3,0" : " q1.5,3.6 3,0";
        Item("Single", "single", "M0,6 H84", 1, null);
        Item("Double", "double", "M0,4.5 H84 M0,7.5 H84", 1, null);
        Item("Thick", "thick", "M0,6 H84", 2.4, null);
        Item("Dotted", "dotted", "M0,6 H84", 1.2, new[] { 1.0, 1.5 });
        Item("Dashed", "dashed", "M0,6 H84", 1.2, new[] { 5.0, 2.5 });
        Item("Wavy", "wave", wave, 1, null);
        menu.Items.Add(new Separator());
        Item("None", null, "M0,6 h0", 0, null);
        menu.IsOpen = true;
    }

    void EditPage()
    {
        var (place, look) = PageNumbers.Of(ed.Doc.Header, ed.Doc.Footer);
        var dlg = new PageDialog(this, ed.Doc.Page, ed.Doc.WidowControl, ed.Doc.Hyphenation, place, look, ed.Doc.AdaptSpacing);
        if (dlg.ShowDialog() == true)
        {
            ed.SetPage(dlg.Result);
            ed.SetTextFlow(dlg.KeepLines, dlg.Hyphenation, dlg.Adapt);
            if (dlg.NumberPlace != place || dlg.NumberPlace > 0 && dlg.NumberLook != look) SetPageNumbers(dlg.NumberPlace, dlg.NumberLook);
        }
        view.Focus();
    }

    /// <summary>Puts the page numbers at a place in a look (place 0: none). They are a line of the header or footer; a margin
    /// that the header or footer would now reach into is made as large as they need.</summary>
    void SetPageNumbers(int place, int look)
    {
        var (header, footer) = PageNumbers.Put(ed.Doc.Header, ed.Doc.Footer, place, look);
        ed.SetBands(header, footer, ed.Doc.BandsSkipFirst, ed.Doc.FirstHeader, ed.Doc.FirstFooter);
        var pg = ed.Doc.Page;
        int top = Math.Min(pg.Height / 4, ed.Layout.BandRoom(false)), bottom = Math.Min(pg.Height / 4, ed.Layout.BandRoom(true));
        if (pg.Top < top || pg.Bottom < bottom)
            ed.SetPage(new PageSetup { Width = pg.Width, Height = pg.Height, Left = pg.Left, Right = pg.Right, Top = Math.Max(pg.Top, top), Bottom = Math.Max(pg.Bottom, bottom) });
    }

    /// <summary>An entry of the list of styles, written in the look of its style: its font, weight, slant, underline and
    /// colour, and a size that follows the style's within what the list has room for. On a dark window a dark colour is
    /// shown light, so that it can be read.</summary>
    static ComboBoxItem StyleItem(ParaStyle st)
    {
        var c = st.Char;
        var name = new TextBlock
        {
            Text = st.Name, FontFamily = new FontFamily(FontCatalog.Resolve(c.Font, c.Bold, c.Italic).Face.Info.Family),
            FontWeight = c.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = c.Italic ? FontStyles.Italic : FontStyles.Normal,
            FontSize = Math.Clamp(8 + c.Size / 2.0 * 0.4, 11.5, 16), VerticalAlignment = VerticalAlignment.Center,
        };
        if (c.Underline) name.TextDecorations = TextDecorations.Underline;
        int ink = c.Color & 0xFFFFFF;
        // Black, the usual colour, is left to the window, which shows it dark or light as it needs.
        if (ink != 0) name.Foreground = Fill(Theme.Dark ? Theme.Turned(ink, 0x2B2B2B) : ink);
        return new ComboBoxItem { Content = name, Tag = st.Id };
    }

    void EditStyles()
    {
        var dlg = new StylesDialog(this, ed.Doc.ParaStyles, ed.CurrentParagraph.Style);
        if (dlg.ShowDialog() == true)
        {
            ed.SetStyles(dlg.Styles);
            syncing = true;
            stylesShown = "";
            syncing = false;
            Sync();
        }
        view.Focus();
    }

    /// <summary>For a right click on a word the spelling check does not know: other spellings, and two ways to keep the word.</summary>
    void SpellItems(ContextMenu menu)
    {
        var p = ed.Layout.Paras(ed.Story)[ed.Caret.Para];
        if (Spell.Of(p) is not { } unknown) return;
        int para = ed.Caret.Para, off = ed.Caret.Off;
        foreach (var (start, length) in unknown)
        {
            if (off < start || off > start + length || start + length > p.Text.Length) continue;
            string word = p.Text.Substring(start, length);
            void Item(string text, Action act, bool strong = false)
            {
                var item = new MenuItem { Header = text, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal };
                item.Click += (_, _) => { act(); view.InvalidateVisual(); view.Focus(); };
                menu.Items.Add(item);
            }
            var others = Spell.Suggest(word);
            foreach (string other in others.Take(6))
                Item(other, () => { ed.Select(new Pos(para, start), new Pos(para, start + length)); ed.TypeText(other); }, true);
            if (others.Count == 0) menu.Items.Add(new MenuItem { Header = "No other spelling found", IsEnabled = false });
            menu.Items.Add(new Separator());
            Item("Leave \u201c" + word + "\u201d as it is, this time", () => { Spell.Ignore(word); CountSpelling(); });
            Item("Add \u201c" + word + "\u201d to my dictionary", () => { Spell.Add(word); CountSpelling(); });
            menu.Items.Add(new Separator());
            return;
        }
    }

    /// <summary>Sources, citations and the list of references.</summary>
    void EditSources()
    {
        var dlg = new SourcesDialog(this, ed.Doc.Sources, ed.Doc.CiteStyle);
        if (dlg.ShowDialog() == true)
        {
            ed.SetSources(dlg.Sources, dlg.Style);
            if (dlg.Action == "cite" && dlg.Chosen != null && !ed.InsertCite(dlg.Chosen))
                Say("A citation stands in the text of the document itself, not in a footnote or a table.");
            else if (dlg.Action == "list" && !ed.UpdateRefs(true))
                Say("The list of references stands in the text of the document itself, not in a footnote or a table.");
        }
        view.Focus();
    }

    /// <summary>Cut, copy and paste, at the top of every right-click menu.</summary>
    void ClipItems(ContextMenu menu, bool paste)
    {
        void Item(string text, string keys, bool on, Action act)
        {
            var item = new MenuItem { Header = text, InputGestureText = keys, IsEnabled = on };
            item.Click += (_, _) => { act(); view.Focus(); };
            menu.Items.Add(item);
        }
        bool text = false;
        try { text = Clipboard.ContainsText() || Clipboard.ContainsImage() || Clipboard.ContainsFileDropList(); }
        catch (ExternalException) { }
        Item("Cut", "Ctrl+X", ed.HasSelection, view.DoCut);
        Item("Copy", "Ctrl+C", ed.HasSelection, view.DoCopy);
        if (paste) Item("Paste", "Ctrl+V", text, view.DoPaste);
    }

    /// <summary>The shadings offered for table cells: soft colours that black text stays readable on.</summary>
    static readonly (string Name, int? Rgb)[] TableShades =
    {
        ("No shading", null), ("Light grey", 0xF2F2F2), ("Grey", 0xD9D9D9), ("Blue", 0xDDEBF7), ("Green", 0xE2EFDA), ("Yellow", 0xFFF2CC), ("Orange", 0xFCE4D6),
    };

    /// <summary>The menu of a right click in the text.</summary>
    void ShowTextMenu()
    {
        var menu = new ContextMenu { PlacementTarget = view, Placement = PlacementMode.MousePoint };
        SpellItems(menu);
        ClipItems(menu, true);
        void Item(string text, string keys, Action act)
        {
            var item = new MenuItem { Header = text, InputGestureText = keys };
            item.Click += (_, _) => { act(); view.Focus(); };
            menu.Items.Add(item);
        }
        if (!ed.HasSelection && ed.LinkAround() is { } link)
        {
            menu.Items.Add(new Separator());
            Item("Open the link", "Ctrl+click", () => OpenLink(link.Address));
            Item("Change the link…", Shortcuts.Of("ins.link"), EditLink);
            Item("Remove the link", "", () => ed.SetLink(null));
        }
        else if (ed.HasSelection)
        {
            menu.Items.Add(new Separator());
            Item("Link…", Shortcuts.Of("ins.link"), EditLink);
        }
        menu.Items.Add(new Separator());
        Item("Paragraph…", Shortcuts.Of("fmt.paragraph"), EditParagraph);
        menu.Items.Add(MatchItem());
        menu.IsOpen = true;
    }

    /// <summary>The entry that gives the style of the text at the caret the look this text has now; greyed when it already has it.</summary>
    MenuItem MatchItem()
    {
        var (style, _, differs) = ed.MatchTarget();
        var item = new MenuItem { Header = $"Update “{style.Name.Replace("_", "__")}” to match", IsEnabled = differs,
            ToolTip = "Give the style the look of this text; all text of the style follows" };
        item.Click += (_, _) => { ed.MatchStyle(); view.Focus(); };
        return item;
    }

    /// <summary>The menu of a right click on the list of styles.</summary>
    void ShowStyleMenu()
    {
        var menu = new ContextMenu { PlacementTarget = styleBox, Placement = PlacementMode.Bottom };
        menu.Items.Add(MatchItem());
        var edit = new MenuItem { Header = "Styles…" };
        edit.Click += (_, _) => EditStyles();
        menu.Items.Add(edit);
        menu.IsOpen = true;
    }

    /// <summary>The kinds of list, shown below the list button.</summary>
    void ShowListMenu()
    {
        var menu = new ContextMenu { PlacementTarget = listBtn, Placement = PlacementMode.Bottom };
        string? now = ed.CurrentParagraph.List;
        void Head(string text) => menu.Items.Add(new MenuItem { Header = text, IsEnabled = false, FontSize = 11 });
        void Item(string text, string? kind)
        {
            var item = new MenuItem { Header = text, FontWeight = kind == now ? FontWeights.Bold : FontWeights.Normal };
            if (kind == null) item.Icon = ListIcon(null);
            item.Click += (_, _) => { ed.SetList(kind); view.Focus(); };
            menu.Items.Add(item);
        }
        Head("Bullets");
        Item("•    round", "bullet");
        Item("◦    hollow", "circle");
        Item("▪    square", "square");
        Item("–    dash", "dash");
        menu.Items.Add(new Separator());
        Head("Numbers");
        Item("1.   2.   3.", "number");
        Item("1)   2)   3)", "number-paren");
        Item("I.   II.   III.", "roman-upper");
        Item("A.   B.   C.", "alpha-upper");
        Item("a)   b)   c)", "alpha-paren");
        Item("a.   b.   c.", "alpha");
        Item("i.   ii.   iii.", "roman");
        menu.Items.Add(new Separator());
        Head("Several levels: Tab moves a paragraph one level in, Shift+Tab out");
        Item("1.     1.1.     1.1.1.", "decimal");
        Item("1.     a.     i.", "outline");
        Item("1)     a)     i)", "outline-paren");
        menu.Items.Add(new Separator());
        Item("No list", null);
        menu.IsOpen = true;
    }

    /// <summary>The sign of a list: three lines with bullets or numbers in front, or plain lines for no list.</summary>
    static UIElement ListIcon(string? kind)
    {
        var c = new Canvas { Width = 16, Height = 14 };
        for (int i = 0; i < 4; i++)
        {
            c.Children.Add(kind == null ? IconBar(0, i, 16) : IconBar(5, i, 11));
            if (kind == "bullet") c.Children.Add(IconBar(0.4, i, 2.4, 2.4));
            if (kind == "number")
            {
                var t = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 4.8, FontWeight = FontWeights.Bold, LineHeight = 5, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
                Canvas.SetLeft(t, 0.3);
                Canvas.SetTop(t, 1 + i * 3.5 - 1.9);
                c.Children.Add(t);
            }
        }
        return c;
    }

    /// <summary>A sign drawn from lines, 16 by 16, in the colour of the button's text.</summary>
    static UIElement Icon(params string[] shapes)
    {
        var c = new Canvas { Width = 16, Height = 16 };
        foreach (string shape in shapes) c.Children.Add(Stroke(shape, 1.25));
        return c;
    }

    /// <summary>The menu of a right click inside a table.</summary>
    void ShowTableMenu()
    {
        if (ed.Story >= 0) return;
        var menu = new ContextMenu { PlacementTarget = view, Placement = PlacementMode.MousePoint };
        void Item(string text, Action act)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => { act(); view.Focus(); };
            menu.Items.Add(item);
        }
        SpellItems(menu);
        ClipItems(menu, true);
        menu.Items.Add(new Separator());
        Item("Add a row above", () => ed.InsertRow(true));
        Item("Add a row below", () => ed.InsertRow());
        Item("Add a column to the right", () => ed.InsertColumn());
        menu.Items.Add(new Separator());
        Item("Remove this row", () => ed.DeleteRow());
        Item("Remove this column", () => ed.DeleteColumn());
        menu.Items.Add(new Separator());
        Item("Join with the cell to the right", () => ed.JoinCell(false));
        ((MenuItem)menu.Items[^1]).IsEnabled = ed.CanJoin(false);
        Item("Join with the cell below", () => ed.JoinCell(true));
        ((MenuItem)menu.Items[^1]).IsEnabled = ed.CanJoin(true);
        Item("Split the joined cell", () => ed.SplitCell());
        ((MenuItem)menu.Items[^1]).IsEnabled = ed.CellJoined;
        menu.Items.Add(new Separator());
        var table = ed.CurrentTable;
        var shade = new MenuItem { Header = "Shading" };
        foreach (var (scopeName, scope) in new[] { ("This cell", "cell"), ("This row", "row"), ("This column", "column"), ("The whole table", "table") })
        {
            var of = new MenuItem { Header = scopeName };
            foreach (var (name, rgb) in TableShades)
            {
                var item = new MenuItem
                {
                    Header = name,
                    Icon = new Rectangle { Width = 14, Height = 14, Stroke = Theme.Edge, StrokeThickness = 1, Fill = rgb is { } v ? new SolidColorBrush(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)) : Brushes.Transparent },
                };
                item.Click += (_, _) => { ed.ShadeCells(scope, rgb); view.Focus(); };
                of.Items.Add(item);
            }
            shade.Items.Add(of);
        }
        menu.Items.Add(shade);
        var lines = new MenuItem { Header = "Lines" };
        void Choice(string text, bool on, Action act)
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = on };
            item.Click += (_, _) => { act(); view.Focus(); };
            lines.Items.Add(item);
        }
        foreach (var (name, kind) in new[] { ("All lines", "all"), ("Above and below the rows only", "horizontal"), ("Around the table only", "outer"), ("No lines", "none") })
            Choice(name, table?.Lines == kind, () => ed.SetTableLines(kind, null));
        lines.Items.Add(new Separator());
        foreach (var (name, width) in new[] { ("Thin", TableGrid.DefaultLine), ("Medium", 19050), ("Thick", 28575) })
            Choice(name, table?.LineWidth == width, () => ed.SetTableLines(null, width));
        menu.Items.Add(lines);
        menu.Items.Add(new Separator());
        Item("Remove the table", () => ed.DeleteTable());
        menu.IsOpen = true;
    }

    /// <summary>A drawn line in the colour the button gives its text.</summary>
    static System.Windows.Shapes.Path Stroke(string data, double thick)
    {
        var p = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data), StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        p.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding
        {
            Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty), RelativeSource = System.Windows.Data.RelativeSource.Self,
        });
        return p;
    }

    /// <summary>A paintbrush: the sign for copying a format.</summary>
    static UIElement BrushIcon()
    {
        var c = new Canvas { Width = 16, Height = 16 };
        c.Children.Add(Stroke("M2.5,1.5 H13.5 V6.5 H2.5 Z", 1.2));
        c.Children.Add(Stroke("M5.5,1.5 V3.8 M8,1.5 V4.4 M10.5,1.5 V3.8", 1));
        c.Children.Add(Stroke("M6,6.5 V9 H10 V6.5", 1.2));
        c.Children.Add(Stroke("M8,9.6 V14.4", 2.4));
        return c;
    }

    /// <summary>An eraser on a line: the sign for erasing a format.</summary>
    static UIElement EraserIcon()
    {
        var c = new Canvas { Width = 16, Height = 16 };
        c.Children.Add(Stroke("M14.2,5.9 L9.6,1.3 L1.8,9.1 L6.4,13.7 Z", 1.2));
        c.Children.Add(Stroke("M4.85,6.05 L9.45,10.65", 1.2));
        c.Children.Add(Stroke("M6.4,14.6 H15", 1.2));
        return c;
    }

    void ShowObjectMenu()
    {
        if (ed.SelectedBox() is not { } sel) return;
        var menu = new ContextMenu { PlacementTarget = view, Placement = PlacementMode.MousePoint };
        void Item(string text, bool check, Action act)
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = check };
            item.Click += (_, _) => { act(); view.Focus(); };
            menu.Items.Add(item);
        }
        ClipItems(menu, false);
        menu.Items.Add(new Separator());
        var pic = sel.Obj as Picture;
        string wrap = pic?.Wrap ?? "inline";
        if (sel.Obj is Chart)
        {
            var edit = new MenuItem { Header = "Change the chart…" };
            edit.Click += (_, _) => EditObject();
            menu.Items.Add(edit);
            menu.Items.Add(new Separator());
        }
        Item("In the line", wrap == "inline" && !sel.Obj.Block, () => SetPlace("inline", false));
        Item("On a line of its own", wrap == "inline" && sel.Obj.Block, () => SetPlace("inline", true));
        if (pic != null)
        {
            menu.Items.Add(new Separator());
            Item("Text above and below", wrap == "topbottom", () => SetPlace("topbottom", false));
            Item("Text around it", wrap == "square", () => SetPlace("square", false));
            Item("Behind the text", wrap == "behind", () => SetPlace("behind", false));
            Item("In front of the text", wrap == "front", () => SetPlace("front", false));
        }
        menu.IsOpen = true;
    }

    /// <summary>Writes the chosen unit into the texts that name a length.</summary>
    void ShowUnits()
    {
        if (indentLess != null) indentLess.ToolTip = "Move the paragraph " + Settings.IndentText + " to the left (Ctrl+Shift+M); in a list, one level out (Shift+Tab)";
        if (indentMore != null) indentMore.ToolTip = "Move the paragraph " + Settings.IndentText + " to the right (Ctrl+M); in a list, one level in (Tab)";
    }

    void EditPreferences()
    {
        var dlg = new PreferencesDialog(this);
        if (dlg.ShowDialog() == true)
        {
            Settings.Metric = dlg.Metric;
            Settings.Colors = dlg.Colors;
            Settings.Spelling = dlg.Spelling;
            Shortcuts.Custom = dlg.Keys;
            Settings.WindowColor = dlg.WindowColor;
            Settings.PaperColor = dlg.PaperColor;
            Settings.AllowCapture = dlg.AllowCapture;
            Settings.Recovery = dlg.KeepRecovery;
            if (!Settings.Recovery) Recovery.Remove(recoveryId);
            else if (ed.Dirty) RecoveryDue();
            Settings.Save();
            ShowUnits();
            Theme.Apply();
            // The choices hold for every window of the program.
            foreach (Window w in Application.Current.Windows)
            {
                ScreenCapture.Apply(w);
                (w as MainWindow)?.ShowUnits();
                (w as MainWindow)?.view.InvalidateVisual();
                (w as MainWindow)?.CountSpelling();
            }
        }
        view.Focus();
    }

    void InsertTable()
    {
        if (!InBody()) return;
        var dlg = new TableDialog(this);
        if (dlg.ShowDialog() == true) ed.InsertTable(dlg.Rows, dlg.Columns);
        view.Focus();
    }

    void EditBands()
    {
        var dlg = new BandDialog(this, ed.Doc, ed.Layout);
        if (dlg.ShowDialog() != true) return;
        ed.SetBands(dlg.Header, dlg.Footer, dlg.SkipFirst, dlg.FirstHeader, dlg.FirstFooter);
        // A header or footer of several lines can reach into the text: the margin is made larger if that is wanted.
        var pg = ed.Doc.Page;
        int top = Math.Min(pg.Height / 4, ed.Layout.BandRoom(false)), bottom = Math.Min(pg.Height / 4, ed.Layout.BandRoom(true));
        if ((top > pg.Top || bottom > pg.Bottom)
            && MsgBox.Show(this, "The header or footer now reaches into the text.\n\nMake the margin of the page larger, so that it does not?", AppName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            ed.SetPage(new PageSetup { Width = pg.Width, Height = pg.Height, Left = pg.Left, Right = pg.Right, Top = Math.Max(pg.Top, top), Bottom = Math.Max(pg.Bottom, bottom) });
        view.Focus();
    }

    void ExportPdf()
    {
        ed.UpdateToc(false);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf", DefaultExt = ".pdf",
            FileName = path == null ? "Document.pdf" : Path.ChangeExtension(Path.GetFileName(path), ".pdf"),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            PdfExport.Write(ed.Layout, dlg.FileName);
            Say("Saved as PDF: " + Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MsgBox.Show(this, "The PDF could not be written.\n\n" + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        view.Focus();
    }

    void Print()
    {
        ed.UpdateToc(false);
        try
        {
            var box = new PrintBox(this, ed.Layout);
            if (box.ShowDialog() == true)
            {
                box.Send(path == null ? "Untitled" : Path.GetFileNameWithoutExtension(path));
                Say("Sent to the printer.");
            }
        }
        catch (Exception ex) when (ex is System.Printing.PrintSystemException or InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
        {
            MsgBox.Show(this, "The document could not be printed.\n\n" + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        view.Focus();
    }

    void InsertFootnote()
    {
        if (ed.InsertFootnote()) return;
        note = "The caret is already in a footnote.";
        Sync();
    }

    /// <summary>The spacing and the indents of the selected paragraphs.</summary>
    void EditParagraph()
    {
        if (ed.CurrentParagraph.Table != null) return;
        var dlg = new ParagraphDialog(this, ed.CurrentLook(), ed.CurrentLook(true), ed.Doc.Page.ContentWidth);
        if (dlg.ShowDialog() == true) ed.SetLook(dlg.Value);
        view.Focus();
    }

    /// <summary>Makes the selected text a link, changes the link at the caret, or puts in a new one.</summary>
    void EditLink()
    {
        var around = ed.HasSelection ? null : ed.LinkAround();
        string now = ed.HasSelection ? ed.CurrentFormat().Link : around?.Address ?? "";
        bool fresh = !ed.HasSelection && around == null;
        var dlg = new LinkDialog(this, fresh, now);
        if (dlg.ShowDialog() != true) return;
        if (dlg.Remove) ed.SetLink(null);
        else if (fresh) ed.InsertLink(dlg.Shown.Length > 0 ? dlg.Shown : dlg.Address, dlg.Address);
        else ed.SetLink(dlg.Address);
        view.Focus();
    }

    /// <summary>Ctrl and a click: opens the link at the caret, or goes from an entry of the table of contents to its heading.</summary>
    bool FollowAtCaret()
    {
        if (ed.HasSelection) return false;
        if (ed.LinkAround() is { } link)
        {
            OpenLink(link.Address);
            return true;
        }
        if (ed.Story == 0 && ed.CurrentParagraph.Toc && Contents.Targets(ed.Doc).TryGetValue(ed.CurrentParagraph, out var head))
        {
            ed.MoveTo(new Pos(ed.Doc.Body.IndexOf(head), 0), false);
            return true;
        }
        return false;
    }

    /// <summary>Hands a web or e-mail address to the program Windows opens such addresses with. Nothing else is opened.</summary>
    void OpenLink(string address)
    {
        if (!LinkDialog.CanOpen(address))
        {
            MsgBox.Show(this, "Only links to web pages (http, https) and to e-mail addresses are opened.\n\n" + address, AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            MsgBox.Show(this, "The link could not be opened.\n\n" + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Puts in the table of contents, or writes it again if the document has one.</summary>
    void InsertContents()
    {
        bool has = ed.Doc.Body.Exists(p => p.Toc);
        if (!has && !InBody()) return;
        if (!has && !Contents.Any(ed.Doc))
        {
            MsgBox.Show(this, "There are no headings yet.\n\nGive the titles of your sections the style Heading 1, Heading 2 or Heading 3. "
                + "The table of contents lists them with their pages.", AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ed.UpdateToc(!has);
        view.Focus();
    }

    void InsertPicture()
    {
        if (!InBodyOrCell()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dlg.ShowDialog(this) != true) return;
        byte[] data;
        try
        {
            if (new FileInfo(dlg.FileName).Length > 64 << 20) throw new IOException("The file is larger than 64 MB.");
            data = File.ReadAllBytes(dlg.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MsgBox.Show(this, "The image could not be read.\n\n" + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!AddPicture(data))
            MsgBox.Show(this, "This file is not a PNG or JPEG image.", AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Puts a picture at the caret, at one screen pixel per picture pixel, made smaller if it would not fit the text area.</summary>
    internal bool AddPicture(byte[] data)
    {
        var img = PageRenderer.Decode(data, out bool jpeg);
        if (img == null || ed.Story > 0) return false;
        string name = RksFile.MediaName(data, jpeg);
        ed.Doc.Media[name] = data;
        var pg = ed.Doc.Page;
        long w = img.PixelWidth * 9525L, h = img.PixelHeight * 9525L;
        long maxW = RoomAtCaret(), maxH = (pg.Height - pg.Top - pg.Bottom) * 3L / 4;
        if (w > maxW) { h = Math.Max(1, U.RoundDiv(h * maxW, w)); w = maxW; }
        if (h > maxH) { w = Math.Max(1, U.RoundDiv(w * maxH, h)); h = maxH; }
        ed.InsertObject(new Picture(name, (int)w, (int)h, false));
        var c = ed.Caret;
        ed.Select(new Pos(c.Para, c.Off - 1), c);
        return true;
    }

    bool ConfirmDiscard()
    {
        if (!ed.Dirty) return true;
        var r = MsgBox.Show(this, "Save the changes to " + (path == null ? "this document" : Path.GetFileName(path)) + "?", AppName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        return r == MessageBoxResult.No || Save(false);
    }

    /// <summary>A new, empty document in a window of its own; this window and its document stay as they are.</summary>
    void NewDoc() => Another(null);


    /// <summary>Opens a further window, a little down and to the right of this one, empty or with a file.</summary>
    internal MainWindow Another(string? file, bool show = true)
    {
        var w = new MainWindow(file) { WindowStartupLocation = WindowStartupLocation.Manual, Width = ActualWidth, Height = ActualHeight };
        var area = SystemParameters.WorkArea;
        double left = WindowState == WindowState.Normal ? Left + 32 : area.Left + 48, top = WindowState == WindowState.Normal ? Top + 32 : area.Top + 48;
        if (WindowState != WindowState.Normal) { w.Width = Math.Min(1120, area.Width - 96); w.Height = Math.Min(860, area.Height - 96); }
        w.Left = Math.Max(area.Left, Math.Min(left, area.Right - w.Width));
        w.Top = Math.Max(area.Top, Math.Min(top, area.Bottom - w.Height));
        // A window that fills the screen is followed by one that fills the screen.
        if (WindowState == WindowState.Maximized) w.WindowState = WindowState.Maximized;
        if (show)
        {
            w.Show();
            // Once this window is done with the click or the key, the new window comes to the front and takes the typing.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                w.Activate();
                w.view.Focus();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        return w;
    }

    /// <summary>True while this window holds a new document that nothing was done to: such a window can take an opened file.</summary>
    bool Untouched => path == null && !ed.Dirty && ed.Doc.Body.Count == 1 && ed.Doc.Body[0].Length == 0 && ed.Doc.Body[0].Table == null;

    void OpenDoc(string? file, bool here = false)
    {
        if (file == null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = OpenFilter };
            if (dlg.ShowDialog(this) != true) return;
            file = dlg.FileName;
        }
        // A document that is being worked on stays where it is: the file opens in a window of its own.
        if (!here && !Untouched)
        {
            Another(file);
            return;
        }
        if (!ConfirmDiscard()) return;
        try
        {
            if (file.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                // A Word document is read into a new document; the Word file itself is never written to.
                var read = DocxImport.Read(file);
                var laid = new LayoutEngine(read.Doc);
                laid.Update();
                SetDocument(read.Doc, laid, null, "Read from " + Path.GetFileName(file) + " and laid out anew");
                offered = Path.ChangeExtension(Path.GetFullPath(file), ".docu");
                ed.Dirty = true;
                Sync();
                Dispatcher.BeginInvoke(new Action(() => MsgBox.Show(this, string.Join("\n\n", read.Notes), AppName, MessageBoxButton.OK, MessageBoxImage.Information)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                return;
            }
            var r = RksFile.Open(file);
            SetDocument(r.Doc, r.Engine, Path.GetFullPath(file), r.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Xml.XmlException)
        {
            MsgBox.Show(this, "The file could not be opened.\n\n" + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    bool Save(bool saveAs)
    {
        ed.UpdateToc(false);
        string? file = path;
        if (saveAs || file == null || !file.EndsWith(".docu", StringComparison.OrdinalIgnoreCase))
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = Filter, DefaultExt = ".docu", FileName = path != null ? Path.ChangeExtension(Path.GetFileName(path), ".docu") : offered != null ? Path.GetFileName(offered) : "Document.docu" };
            if (path == null && offered != null && Path.GetDirectoryName(offered) is { Length: > 0 } beside && Directory.Exists(beside)) dlg.InitialDirectory = beside;
            if (dlg.ShowDialog(this) != true) return false;
            file = dlg.FileName;
        }
        try
        {
            RksFile.Save(ed.Doc, ed.Layout, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MsgBox.Show(this, "The file could not be saved.\n\n" + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        path = file;
        ed.Dirty = false;
        Recovery.Remove(recoveryId);
        recoveryStale = false;
        note = "Saved with its layout.";
        Sync();
        return true;
    }

    /// <summary>What each command with a shortcut does, and whether it also works while the caret is not in the page
    /// (in the find bar or in a box of the toolbar).</summary>
    Dictionary<string, (Action Run, bool Anywhere)>? commands;

    Dictionary<string, (Action Run, bool Anywhere)> BuildCommands() => new()
    {
        ["file.new"] = (NewDoc, true), ["file.open"] = (() => OpenDoc(null), true), ["file.save"] = (() => Save(false), true), ["file.saveas"] = (() => Save(true), true),
        ["file.pdf"] = (ExportPdf, true), ["file.print"] = (Print, true), ["file.page"] = (EditPage, true), ["fmt.styles"] = (EditStyles, false), ["edit.find"] = (ShowFind, true),
        ["edit.undo"] = (() => ed.Undo(), false), ["edit.redo"] = (() => ed.Redo(), false),
        ["fmt.bold"] = (() => ed.ToggleBold(), false), ["fmt.italic"] = (() => ed.ToggleItalic(), false), ["fmt.underline"] = (() => ed.ToggleUnderline(), false),
        ["fmt.latex"] = (() => ed.ToggleLatex(), false),
        ["fmt.copy"] = (() => ed.CopyFormat(false), false), ["fmt.paste"] = (() => ed.PasteFormat(), false), ["fmt.erase"] = (() => ed.ClearFormat(), false),
        ["para.left"] = (() => ed.SetAlign(Align.Left), false), ["para.centre"] = (() => ed.SetAlign(Align.Center), false),
        ["para.right"] = (() => ed.SetAlign(Align.Right), false), ["para.justify"] = (() => ed.SetAlign(Align.Justify), false),
        ["para.in"] = (() => { if (ed.InList) ed.ChangeListLevel(1); else ed.ChangeIndent(Settings.IndentStep); }, false),
        ["para.out"] = (() => { if (ed.InList) ed.ChangeListLevel(-1); else ed.ChangeIndent(-Settings.IndentStep); }, false),
        ["ins.page"] = (() => { if (ed.Story == 0) ed.Enter(true); }, false), ["ins.footnote"] = (InsertFootnote, false), ["ins.image"] = (InsertPicture, false),
        ["ins.symbol"] = (InsertSymbol, false), ["ins.equation"] = (InsertEquation, false), ["ins.chart"] = (InsertChart, false), ["ins.table"] = (InsertTable, false),
        ["ins.citation"] = (EditSources, false), ["ins.toc"] = (InsertContents, false), ["ins.box"] = (InsertBox, false), ["fmt.paragraph"] = (EditParagraph, false), ["ins.link"] = (EditLink, false), ["ins.header"] = (EditBands, false), ["app.preferences"] = (EditPreferences, true),
    };

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // The shortcuts of the commands, as the user has them. Keys that were the standard of a command
        // which now has other keys do nothing, rather than what they did before.
        string keys = Shortcuts.Gesture(e);
        if (keys.Length > 0)
        {
            bool inPage = view.IsKeyboardFocusWithin;
            commands ??= BuildCommands();
            if (Shortcuts.Find(keys, false) is { } id && commands.TryGetValue(id, out var command) && (command.Anywhere || inPage))
            {
                command.Run();
                e.Handled = true;
            }
            else if (inPage && Shortcuts.Freed(keys, false)) e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closed in the normal way: the user saved or chose not to, so the recovery copy is no longer needed.
        recoveryPause.Stop();
        recoveryBeat.Stop();
        wordsPause.Stop();
        spellPause.Stop();
        spellBeat.Stop();
        Recovery.Remove(recoveryId);
        base.OnClosed(e);
    }

    void RecoveryDue()
    {
        if (!Settings.Recovery) return;
        recoveryStale = true;
        recoveryPause.Stop();
        recoveryPause.Start();
    }

    void WriteRecovery()
    {
        if (!recoveryStale || !Settings.Recovery || !ed.Dirty) return;
        try
        {
            if (ed.Doc.Body.Count <= BigDocument) Recovery.Write(recoveryId, ed.Doc, ed.Layout, path);
            else if (!Recovery.WriteBehind(recoveryId, ed.Doc, ed.Layout, path)) return;
            recoveryStale = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>At the start: a copy left by a session that ended unexpectedly is offered once.</summary>
    void OfferRecovery()
    {
        var found = Recovery.Find();
        if (found.Count == 0) return;
        var f = found[0];
        string name = f.Original != null ? Path.GetFileName(f.Original) : "a document that had no name yet";
        var answer = MsgBox.Show(this,
            "Reiks Document was not closed in the normal way last time.\n\nThere is a recovery copy of " + name + ", written on "
            + f.When.ToString("d MMMM yyyy 'at' HH:mm", CultureInfo.CurrentCulture) + ".\n\n"
            + "Yes: open the recovery copy.\nNo: throw it away.\nCancel: keep it and ask again next time.",
            AppName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.No) Recovery.Remove(f.Id);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var r = RksFile.Open(f.File);
            SetDocument(r.Doc, r.Engine, f.Original, "Recovery copy opened. Save it to keep it.");
            ed.Dirty = true;
            Sync();
            // It stays protected under this window's own copy before the old one goes.
            recoveryStale = true;
            WriteRecovery();
            if (!recoveryStale) Recovery.Remove(f.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MsgBox.Show(this, "The recovery copy could not be opened.\n\n" + ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
