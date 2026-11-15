using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReiksDocument;

/// <summary>The pages on screen: drawing, caret, mouse and keyboard.</summary>
sealed class PageView : FrameworkElement
{
    const double Gap = 14, Pad = 16;
    static readonly Pen PagePen = MakePen(Color.FromRgb(0xB8, 0xB8, 0xB8), false);
    static readonly Pen FramePen = MakePen(Color.FromRgb(0x2B, 0x6C, 0xC4), false);
    static readonly Pen DashPen = MakePen(Color.FromRgb(0x2B, 0x6C, 0xC4), true);

    public Editor Ed { get; private set; }
    public ScrollViewer? Scroller;
    public event Action? ZoomChanged;
    /// <summary>A double click on an equation or chart (it is selected).</summary>
    public event Action? ObjectActivated;
    /// <summary>A right click on an image, equation or chart (it is selected).</summary>
    public event Action? ObjectMenu;
    /// <summary>A right click inside a table, after the caret went to the cell.</summary>
    public event Action? TableMenu;
    /// <summary>A right click in the text.</summary>
    public event Action? TextMenu;

    readonly DrawingVisual caretVisual = new();
    readonly DispatcherTimer blink;
    double zoom = 1;
    bool caretOn = true, dragging, hadSelection, resizing;
    Rect resizeFrom, resizeNow;
    int resizeW, resizeH;
    bool floatDrag, floatMoved, colDrag;
    int colPara, colIndex;
    double colFromX, colNowX;
    Point floatFrom;
    Rect floatRect, floatNow;
    double resizeScale = 1;
    int shownFirst = -1, shownLast = -1;
    Pos dragAnchor;

    static Pen MakePen(Color c, bool dashed)
    {
        var p = new Pen(new SolidColorBrush(c), 1);
        if (dashed) p.DashStyle = DashStyles.Dash;
        p.Freeze();
        return p;
    }

    public PageView(Editor ed)
    {
        Ed = ed;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        AddVisualChild(caretVisual);
        blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        blink.Tick += (_, _) => { caretOn = !caretOn; UpdateCaret(); };
        GotKeyboardFocus += (_, _) => { ShowCaret(); blink.Start(); };
        LostKeyboardFocus += (_, _) => { blink.Stop(); UpdateCaret(); };
        SetEditor(ed);
    }

    public void SetEditor(Editor ed)
    {
        Ed.Changed -= OnChanged;
        Ed.SelectionChanged -= OnSelectionChanged;
        Ed = ed;
        Ed.Changed += OnChanged;
        Ed.SelectionChanged += OnSelectionChanged;
        hadSelection = false;
        InvalidateMeasure();
        InvalidateVisual();
        Scroller?.ScrollToVerticalOffset(0);
        ShowCaret();
    }

    public double Zoom
    {
        get => zoom;
        set
        {
            double z = Math.Clamp(Math.Round(value, 2), 0.3, 4.0);
            if (z == zoom) return;
            zoom = z;
            InvalidateMeasure();
            InvalidateVisual();
            UpdateCaret();
            ZoomChanged?.Invoke();
        }
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => caretVisual;

    // ---- geometry ----

    double PageW => U.Dip(Ed.Doc.Page.Width) * zoom;
    double PageH => U.Dip(Ed.Doc.Page.Height) * zoom;
    double PageX => Math.Round(Math.Max(Pad, (ActualWidth - PageW) / 2));
    double PageY(int k) => Math.Round(Pad + k * (PageH + Gap));

    protected override Size MeasureOverride(Size availableSize)
    {
        int n = Ed.Layout.Pages.Count;
        return new Size(PageW + 2 * Pad, 2 * Pad + n * PageH + (n - 1) * Gap);
    }

    (int First, int Last) VisiblePages()
    {
        int n = Ed.Layout.Pages.Count;
        double y0 = 0, y1 = ActualHeight;
        if (Scroller != null) { y0 = Scroller.VerticalOffset; y1 = y0 + Scroller.ViewportHeight; }
        double step = PageH + Gap;
        int first = Math.Clamp((int)Math.Floor((y0 - Pad) / step), 0, n - 1);
        int last = Math.Clamp((int)Math.Floor((y1 - Pad) / step), 0, n - 1);
        return (first, last);
    }

    public void OnScrolled()
    {
        var (f, l) = VisiblePages();
        if (f != shownFirst || l != shownLast) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var (first, last) = VisiblePages();
        shownFirst = first;
        shownLast = last;
        float ppd = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Pos? s = Ed.HasSelection ? Ed.SelStart : null, e = Ed.HasSelection ? Ed.SelEnd : null;
        double px = PageX;
        for (int k = first; k <= last; k++)
        {
            double py = PageY(k);
            dc.DrawRectangle(Theme.Paper, PagePen, new Rect(px - 0.5, py - 0.5, PageW + 1, PageH + 1));
            dc.PushTransform(new TranslateTransform(px, py));
            dc.PushTransform(new ScaleTransform(zoom, zoom));
            PageDrawer.Spelling = SpellMarks;
            try { PageRenderer.DrawPage(dc, Ed.Layout, k, Ed.Story, s, e, ppd, Theme.SheetIsDark); }
            finally { PageDrawer.Spelling = null; }
            dc.Pop();
            dc.Pop();
        }
        if (colDrag)
        {
            double y0 = Scroller?.VerticalOffset ?? 0;
            dc.DrawLine(DashPen, new Point(colNowX, y0), new Point(colNowX, y0 + (Scroller?.ViewportHeight ?? ActualHeight)));
        }
        if (resizing) dc.DrawRectangle(null, DashPen, resizeNow);
        else if (floatDrag && floatMoved) dc.DrawRectangle(null, DashPen, floatNow);
        else if (SelectedBoxRect() is { } pr)
        {
            dc.DrawRectangle(null, FramePen, pr);
            if (CanResize) dc.DrawRectangle(Brushes.White, FramePen, new Rect(pr.Right - 4, pr.Bottom - 4, 8, 8));
        }
    }

    internal Rect CaretBox() => CaretRect();

    Rect CaretRect()
    {
        var line = Ed.Layout.LineAt(Ed.Story, Ed.Caret, Ed.CaretAtLineEnd, out _);
        int x = Ed.Layout.CaretX(Ed.Story, Ed.Caret, Ed.CaretAtLineEnd);
        // As tall as the text it stands in, on the baseline, however much room the line spacing gives the line.
        var p = Ed.Layout.Paras(Ed.Story)[Ed.Caret.Para];
        var sp = Ed.Layout.SpanAt(p, Math.Max(0, Ed.Caret.Off - 1)) ?? Ed.Layout.SpanAt(p, Ed.Caret.Off) ?? Ed.Layout.StyleSpan(Ed.Doc.StyleOf(p));
        var face = sp.Font.Face;
        int asc = (int)((long)face.Ascender * sp.SizeEmu / face.UnitsPerEm);
        int top = Math.Max(0, line.Baseline - asc), bottom = Math.Min(line.Height, line.Baseline + Math.Abs(sp.Desc));
        if (bottom <= top) { top = 0; bottom = line.Height; }
        return new Rect(PageX + U.Dip(x) * zoom, PageY(line.Page) + U.Dip(line.Top + top) * zoom, 1, U.Dip(bottom - top) * zoom);
    }

    void UpdateCaret()
    {
        using var dc = caretVisual.RenderOpen();
        if (!caretOn || !IsKeyboardFocused) return;
        var r = CaretRect();
        // The caret is dark on a light sheet and light on a dark one.
        dc.DrawRectangle(Theme.SheetIsDark ? Brushes.White : Brushes.Black, null, new Rect(Math.Round(r.X), r.Y, Math.Max(1, Math.Round(zoom)), r.Height));
    }

    void ShowCaret()
    {
        caretOn = true;
        if (blink.IsEnabled) { blink.Stop(); blink.Start(); }
        UpdateCaret();
    }

    public int CaretPage => Ed.Layout.LineAt(Ed.Story, Ed.Caret, Ed.CaretAtLineEnd, out _).Page;

    void ScrollCaretIntoView()
    {
        if (Scroller == null) return;
        Scroller.UpdateLayout();
        var r = CaretRect();
        double top = Scroller.VerticalOffset, vh = Scroller.ViewportHeight;
        if (r.Top < top + 8) Scroller.ScrollToVerticalOffset(Math.Max(0, r.Top - 24));
        else if (r.Bottom > top + vh - 8) Scroller.ScrollToVerticalOffset(r.Bottom - vh + 24);
    }

    (int Page, int X, int Y) PagePoint(Point pt)
    {
        int n = Ed.Layout.Pages.Count;
        int k = Math.Clamp((int)Math.Floor((pt.Y - Pad + Gap / 2) / (PageH + Gap)), 0, n - 1);
        int x = (int)Math.Clamp((pt.X - PageX) / zoom * U.PerDip, -1e8, 1e8);
        int y = (int)Math.Clamp((pt.Y - PageY(k)) / zoom * U.PerDip, -1e8, 1e8);
        return (k, x, y);
    }

    /// <summary>The text position at a point; with only set, the nearest one in that story.</summary>
    (int Story, Pos Pos) PosAt(Point pt, out bool atEnd, int? only = null)
    {
        var (k, x, y) = PagePoint(pt);
        return Ed.Layout.HitTest(k, x, y, out atEnd, only);
    }

    /// <summary>Pictures and charts have a size of their own; an equation follows its font size.</summary>
    bool CanResize => Ed.SelectedBox() is { Obj: Picture or Chart };

    Rect? SelectedBoxRect()
    {
        if (Ed.SelectedBox() is not { } sel || Ed.Layout.ObjectRect(sel.Pos, Ed.Story) is not { } r) return null;
        return new Rect(PageX + U.Dip(r.X) * zoom, PageY(r.Page) + U.Dip(r.Y) * zoom, U.Dip(r.W) * zoom, U.Dip(r.H) * zoom);
    }

    /// <summary>How near the mouse must be to the line between two columns to take hold of it: four screen pixels.</summary>
    int EdgeReach => (int)(4 / zoom * U.PerDip);

    static bool OnHandle(Rect r, Point pt) => Math.Abs(pt.X - r.Right) <= 7 && Math.Abs(pt.Y - r.Bottom) <= 7;

    // ---- editor events ----

    void OnChanged()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    void OnSelectionChanged()
    {
        if (Ed.HasSelection || hadSelection) InvalidateVisual();
        hadSelection = Ed.HasSelection;
        ShowCaret();
        ScrollCaretIntoView();
        UpdateCaret();
    }

    // ---- mouse ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        TypingOver();
        Focus();
        e.Handled = true;
        var pt = e.GetPosition(this);
        var (edgePage, edgeX, edgeY) = PagePoint(pt);
        if (Ed.Layout.ColumnEdgeAt(edgePage, edgeX, edgeY, EdgeReach) is { } edge)
        {
            // The line between two columns of a table is dragged to make them wider or narrower.
            colDrag = true;
            colPara = edge.Para;
            colIndex = edge.Col;
            colFromX = colNowX = pt.X;
            CaptureMouse();
            return;
        }
        if (CanResize && Ed.SelectedBox() is { } sel && SelectedBoxRect() is { } pr && OnHandle(pr, pt))
        {
            resizing = true;
            resizeFrom = resizeNow = pr;
            (resizeW, resizeH) = sel.Obj is Picture p ? (p.Width, p.Height) : sel.Obj is Chart c ? (c.Width, c.Height) : (1, 1);
            resizeScale = 1;
            CaptureMouse();
            return;
        }
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var (page, px, py) = PagePoint(pt);
        if (!shift && Ed.Layout.ObjectAtAny(page, px, py) is { } hit)
        {
            var obj = hit.Pos;
            Ed.MoveTo(obj, false, story: hit.Story);
            Ed.Select(obj, new Pos(obj.Para, obj.Off + 1));
            if (e.ClickCount == 2) ObjectActivated?.Invoke();
            else if (hit.Story == 0 && Ed.Layout.FloatAt(obj) != null && SelectedBoxRect() is { } fr)
            {
                // A floating picture can be dragged to another place.
                floatDrag = true;
                floatMoved = false;
                floatFrom = pt;
                floatRect = floatNow = fr;
                CaptureMouse();
            }
            return;
        }
        var (story, pos) = PosAt(pt, out bool atEnd, shift ? Ed.Story : null);
        if (e.ClickCount == 1 && !shift && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Ctrl and a click follow a link, or an entry of the table of contents.
            Ed.MoveTo(pos, false, story: story);
            if (Follow?.Invoke() == true)
            {
                e.Handled = true;
                return;
            }
        }
        if (e.ClickCount == 2)
        {
            Ed.MoveTo(pos, false, story: story);
            Ed.SelectWord(pos);
            UseBrush();
        }
        else if (e.ClickCount >= 3)
        {
            Ed.MoveTo(pos, false, story: story);
            Ed.SelectParagraph(pos);
            UseBrush();
        }
        else
        {
            Ed.MoveTo(pos, shift, atEnd, story: story);
            dragAnchor = Ed.Anchor;
            dragging = true;
            CaptureMouse();
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        TypingOver();
        Focus();
        var (page, x, y) = PagePoint(e.GetPosition(this));
        if (Ed.Layout.ObjectAt(page, x, y) is not { } obj)
        {
            // A click outside the selection moves the caret there; inside it the selection stays, for cut and copy.
            var (story, pos) = PosAt(e.GetPosition(this), out bool atEnd);
            bool forward = Ed.Caret.CompareTo(Ed.Anchor) >= 0;
            Pos first = forward ? Ed.Anchor : Ed.Caret, last = forward ? Ed.Caret : Ed.Anchor;
            bool inside = story == Ed.Story && Ed.HasSelection && pos.CompareTo(first) >= 0 && pos.CompareTo(last) <= 0;
            if (!inside) Ed.MoveTo(pos, false, atEnd, story: story);
            e.Handled = true;
            if (Ed.Story < 0) TableMenu?.Invoke();
            else TextMenu?.Invoke();
            return;
        }
        Ed.MoveTo(obj, false, story: 0);
        Ed.Select(obj, new Pos(obj.Para, obj.Off + 1));
        e.Handled = true;
        ObjectMenu?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pt = e.GetPosition(this);
        if (colDrag)
        {
            colNowX = pt.X;
            InvalidateVisual();
            return;
        }
        if (resizing)
        {
            // The corner follows the mouse; the object keeps its proportions and stays inside the text area.
            var page = Ed.Doc.Page;
            double s = Math.Max((pt.X - resizeFrom.Left) / resizeFrom.Width, (pt.Y - resizeFrom.Top) / resizeFrom.Height);
            double max = Math.Min((double)page.ContentWidth / resizeW, (double)(page.Height - page.Top - page.Bottom) / resizeH);
            double min = Math.Min(max, 360000.0 / Math.Min(resizeW, resizeH));
            resizeScale = Math.Clamp(s, min, max);
            resizeNow = new Rect(resizeFrom.Left, resizeFrom.Top, resizeFrom.Width * resizeScale, resizeFrom.Height * resizeScale);
            InvalidateVisual();
            return;
        }
        if (floatDrag)
        {
            var by = pt - floatFrom;
            if (!floatMoved && Math.Abs(by.X) < 4 && Math.Abs(by.Y) < 4) return;
            floatMoved = true;
            floatNow = new Rect(floatRect.X + by.X, floatRect.Y + by.Y, floatRect.Width, floatRect.Height);
            InvalidateVisual();
            return;
        }
        if (!dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            var (hp, hx, hy) = PagePoint(pt);
            Cursor = Ed.BrushArmed ? Cursors.Pen
                : Ed.Layout.ColumnEdgeAt(hp, hx, hy, EdgeReach) != null ? Cursors.SizeWE
                : CanResize && SelectedBoxRect() is { } r && OnHandle(r, pt) ? Cursors.SizeNWSE
                : Ed.Layout.ObjectAt(hp, hx, hy) is { } over && Ed.Layout.FloatAt(over) != null ? Cursors.SizeAll : Cursors.IBeam;
            return;
        }
        var (_, pos) = PosAt(pt, out bool atEnd, Ed.Story);
        if (pos == Ed.Caret) return;
        Ed.Anchor = dragAnchor;
        Ed.MoveTo(pos, true, atEnd);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (colDrag)
        {
            colDrag = false;
            ReleaseMouseCapture();
            Ed.MoveColumnEdge(colPara, colIndex, (int)((e.GetPosition(this).X - colFromX) / zoom * U.PerDip));
            InvalidateVisual();
            return;
        }
        if (resizing)
        {
            resizing = false;
            ReleaseMouseCapture();
            int w = Math.Max(1, (int)Math.Round(resizeW * resizeScale)), h = Math.Max(1, (int)Math.Round(resizeH * resizeScale));
            if (Math.Abs(resizeScale - 1) > 0.001)
                Ed.ChangeObject(o => o switch
                {
                    Picture p => p with { Width = w, Height = h },
                    Chart c => c with { Width = w, Height = h },
                    _ => o,
                });
            InvalidateVisual();
            return;
        }
        if (floatDrag)
        {
            floatDrag = false;
            ReleaseMouseCapture();
            if (floatMoved && Ed.SelectedBox() is { Obj: Picture pic } sel) DropFloat(sel.Pos, pic, floatNow.TopLeft);
            InvalidateVisual();
            return;
        }
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        UseBrush();
    }

    /// <summary>Puts a floating picture where it was dropped: its item goes to the start of the line at that height,
    /// and its offsets are counted from the left edge of the text area and from the top of that line.</summary>
    void DropFloat(Pos from, Picture pic, Point at)
    {
        var (page, x, y) = PagePoint(at);
        var doc = Ed.Doc.Page;
        x = Math.Clamp(x, 0, Math.Max(0, doc.Width - pic.Width));
        y = Math.Clamp(y, 0, Math.Max(0, doc.Height - pic.Height));
        var lines = Ed.Layout.Pages[page].Lines;
        var target = lines[0];
        foreach (var l in lines)
            if (l.Line.Top <= y) target = l;
        Ed.MoveFloat(from, new Pos(target.Para, target.Line.Start), x - doc.Left, y - target.Line.Top);
    }

    /// <summary>A copied format waiting for text goes onto what was just selected (or the word clicked).</summary>
    void UseBrush()
    {
        if (!Ed.BrushArmed) return;
        if (!Ed.HasSelection) Ed.SelectWord(Ed.Caret);
        Ed.PasteFormat();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        Zoom = zoom + (e.Delta > 0 ? 0.1 : -0.1);
        e.Handled = true;
    }

    // ---- keyboard ----

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        Ed.TypeText(e.Text);
        typingWord = true;
        e.Handled = true;
    }

    /// <summary>True from a typed character until the caret is moved another way: the word at the caret is then still being written.</summary>
    bool typingWord;

    void TypingOver()
    {
        if (!typingWord) return;
        typingWord = false;
        InvalidateVisual();
    }

    /// <summary>The unknown words of a paragraph that get a mark. The word that is still being typed gets none:
    /// it is judged when the caret has left it.</summary>
    System.Collections.Generic.IReadOnlyList<(int Start, int Length)>? SpellMarks(Paragraph p)
    {
        var all = Spell.Of(p);
        if (all == null || all.Count == 0 || !typingWord) return all;
        var paras = Ed.Layout.Paras(Ed.Story);
        if (Ed.Caret.Para >= paras.Count || !ReferenceEquals(paras[Ed.Caret.Para], p)) return all;
        int at = Ed.Caret.Off;
        var marked = new System.Collections.Generic.List<(int Start, int Length)>();
        foreach (var w in all)
            if (at < w.Start || at > w.Start + w.Length) marked.Add(w);
        return marked;
    }

    void PageMove(int dir, bool extend)
    {
        if (Scroller == null) return;
        var r = CaretRect();
        var pt = new Point(r.X, r.Y + r.Height / 2 + dir * Math.Max(40, Scroller.ViewportHeight - 40));
        pt.Y = Math.Clamp(pt.Y, 0, Math.Max(0, ActualHeight - 1));
        var (_, pos) = PosAt(pt, out bool atEnd, Ed.Story);
        Ed.MoveTo(pos, extend, atEnd);
    }

    public void DoCopy()
    {
        if (Ed.Copy() is { } t) SetClipboard(t);
    }

    public void DoCut()
    {
        if (Ed.Cut() is { } t) SetClipboard(t);
    }

    /// <summary>Follows what is at the caret, if it leads somewhere; set by the window.</summary>
    public System.Func<bool>? Follow;

    /// <summary>Takes the bytes of a PNG or JPEG picture and puts it at the caret; set by the window.</summary>
    public System.Func<byte[], bool>? PastePicture;

    /// <summary>Text if the clipboard has text; else a picture: one copied in another program, or a picture file copied in the Explorer.</summary>
    public void DoPaste()
    {
        try
        {
            if (Clipboard.ContainsText()) { Ed.Paste(Clipboard.GetText()); return; }
            if (PastePicture == null) return;
            if (Clipboard.GetData("PNG") is System.IO.MemoryStream png && png.Length > 0 && png.Length <= MaxPicture) PastePicture(png.ToArray());
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) PastePicture(PngOf(img));
            else if (Clipboard.ContainsFileDropList())
                foreach (string? f in Clipboard.GetFileDropList())
                {
                    if (f == null || !(f.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".jpeg", System.StringComparison.OrdinalIgnoreCase))) continue;
                    if (new System.IO.FileInfo(f).Length <= MaxPicture) PastePicture(System.IO.File.ReadAllBytes(f));
                    break;
                }
        }
        catch (System.Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or System.IO.IOException or System.UnauthorizedAccessException
            or System.NotSupportedException or System.ArgumentException or System.OutOfMemoryException) { }
    }

    const long MaxPicture = 64 << 20;

    /// <summary>A picture from the clipboard as a PNG file. Such pictures often claim a transparency they do not have,
    /// which would leave them empty, so they are stored without any.</summary>
    internal static byte[] PngOf(System.Windows.Media.Imaging.BitmapSource img)
    {
        var solid = new System.Windows.Media.Imaging.FormatConvertedBitmap(img, System.Windows.Media.PixelFormats.Bgr24, null, 0);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(solid));
        using var ms = new System.IO.MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static void SetClipboard(string t)
    {
        try { Clipboard.SetText(t); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, [System.Runtime.InteropServices.Out] System.Text.StringBuilder buffer, int size, uint flags, IntPtr layout);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint MapVirtualKeyW(uint code, uint mapType);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetKeyboardLayout(uint thread);

    /// <summary>The character a key types on the keyboard layout in use, with or without Shift; 0 if none.</summary>
    public static char KeyChar(Key key, bool shift)
    {
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return '\0';
        var state = new byte[256];
        if (shift) state[0x10] = 0x80;
        var sb = new System.Text.StringBuilder(8);
        // Flag 4: ask without disturbing a dead key (an accent) that is waiting for its letter.
        int n = ToUnicodeEx(vk, MapVirtualKeyW(vk, 0), state, sb, sb.Capacity, 4, GetKeyboardLayout(0));
        return n == 1 ? sb[0] : '\0';
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Enter or Key.Tab or Key.Escape) TypingOver();
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        if ((mods & ModifierKeys.Alt) != 0) return;
        bool handled = true;
        switch (e.Key)
        {
            case Key.Left: if (ctrl) Ed.MoveWord(-1, shift); else Ed.MoveChar(-1, shift); break;
            case Key.Right: if (ctrl) Ed.MoveWord(1, shift); else Ed.MoveChar(1, shift); break;
            case Key.Up: Ed.MoveLine(-1, shift); break;
            case Key.Down: Ed.MoveLine(1, shift); break;
            case Key.Home: Ed.MoveHome(shift, ctrl); break;
            case Key.End: Ed.MoveEnd(shift, ctrl); break;
            case Key.PageUp: PageMove(-1, shift); break;
            case Key.PageDown: PageMove(1, shift); break;
            case Key.Back: Ed.Backspace(ctrl); break;
            case Key.Delete: if (shift) DoCut(); else Ed.Delete(ctrl); break;
            case Key.Enter: if (ctrl) Ed.Enter(true); else if (shift) Ed.InsertLineBreak(); else Ed.Enter(); break;
            case Key.Tab: if (ctrl) handled = false; else if (Ed.Story < 0) Ed.NextCell(shift ? -1 : 1);
                else if (Ed.InList && (shift || Ed.HasSelection || Ed.Caret.Off == 0)) Ed.ChangeListLevel(shift ? -1 : 1);
                else Ed.TypeText("\t");
                break;
            case Key.Escape: if (Ed.BrushArmed) Ed.DropBrush(); else if (Ed.Story != 0) Ed.LeaveNote(); else handled = false; break;
            case Key.Insert: if (ctrl) DoCopy(); else if (shift) DoPaste(); else handled = false; break;
            default:
                if (!ctrl) { handled = false; break; }
                // Raised and lowered go by the sign the key types on this keyboard, not by where the key sits:
                // Ctrl with + raises; Ctrl with - or with = lowers; whichever keys (with or without Shift) give those signs.
                // Ctrl with a number types that number lowered, as the 2 in H2O; with Shift as well, raised, as in x squared.
                int digit = e.Key >= Key.D0 && e.Key <= Key.D9 ? e.Key - Key.D0 : e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
                if (digit >= 0) { Ed.TypeVert(digit.ToString(), shift ? 1 : 2); break; }
                char sign = KeyChar(e.Key, shift);
                if (sign == '+') { Ed.SetVert(1); break; }
                if (sign == '=' || sign == '-') { Ed.SetVert(2); break; }
                // With Shift these keys are other keys: only the four that have a meaning with Shift take it.
                switch (e.Key)
                {
                    case Key.C: if (shift) Ed.CopyFormat(false); else DoCopy(); break;
                    case Key.V: if (shift) Ed.PasteFormat(); else DoPaste(); break;
                    case Key.Z: if (shift) Ed.Redo(); else Ed.Undo(); break;
                    case Key.M: Ed.ChangeIndent(shift ? -Settings.IndentStep : Settings.IndentStep); break;
                    case Key.A when !shift: Ed.SelectAll(); break;
                    case Key.X when !shift: DoCut(); break;
                    case Key.Y when !shift: Ed.Redo(); break;
                    case Key.B when !shift: Ed.ToggleBold(); break;
                    case Key.I when !shift: Ed.ToggleItalic(); break;
                    case Key.U when !shift: Ed.ToggleUnderline(); break;
                    case Key.Space when !shift: Ed.ClearFormat(); break;
                    case Key.L when !shift: Ed.SetAlign(Align.Left); break;
                    case Key.E when !shift: Ed.SetAlign(Align.Center); break;
                    case Key.R when !shift: Ed.SetAlign(Align.Right); break;
                    case Key.J when !shift: Ed.SetAlign(Align.Justify); break;
                    default: handled = false; break;
                }
                break;
        }
        if (handled) e.Handled = true;
    }
}
