using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReiksDocument;

/// <summary>An equation that is typed in place: the caret stands inside the drawn equation, in a numerator,
/// an exponent, under a root. What it holds is the same typed line as the other way of writing an equation;
/// every key changes that line, and the line is drawn again. In the line a part that is still empty holds the
/// sign of an empty box, so that the caret has a place there; the box is not drawn and does not leave the pad.</summary>
sealed class EquationPad : FrameworkElement
{
    const char Gap = EquationDialog.Gap;
    const int Size = 40 * U.PerHalfPoint;

    /// <summary>A place the caret can stand: where in the line, where in the picture, and what is around it.</summary>
    sealed record Slot(int Index, int X, int Top, int Bottom, MathNode? Prev, MathNode? Next, MathNode? Owner, bool NeedsSpace, int Depth);

    readonly Span span;
    readonly DispatcherTimer blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    string src = Gap.ToString();
    int caret;
    bool caretOn = true;
    ObjBox box = new(), shown = new();
    List<Slot> slots = new();

    /// <summary>The line changed.</summary>
    public event Action? Changed;

    public EquationPad(Span sp)
    {
        span = sp;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        MinHeight = 130;
        blink.Tick += (_, _) => { caretOn = !caretOn; InvalidateVisual(); };
        GotKeyboardFocus += (_, _) => { caretOn = true; blink.Start(); InvalidateVisual(); };
        LostKeyboardFocus += (_, _) => { blink.Stop(); InvalidateVisual(); };
        Parse(0);
    }

    /// <summary>The typed line of the equation. An equation nothing was put into is empty.</summary>
    public string Source
    {
        get => src.Replace(Gap.ToString(), "").Trim();
        set
        {
            src = value.Replace("\r", "").Replace("\n", " ");
            Parse(src.Length);
            Changed?.Invoke();
        }
    }

    /// <summary>The place of the caret in the line.</summary>
    public int Caret => caret;

    // ---- from the line to the picture and the places of the caret ----

    void Parse(int want)
    {
        src = src.Replace("()", "(" + Gap + ")").Replace("[]", "[" + Gap + "]").Replace("{}", "{" + Gap + "}");
        if (src.Trim().Length == 0) src = Gap.ToString();
        var tree = MathParser.Parse(src);
        var marks = new List<MathMark>();
        box = MathLayout.Layout(tree, Size, true, marks);
        // A part that is still empty keeps its room but is not drawn: the caret shows where one is.
        shown = new ObjBox { W = box.W, H = box.H, D = box.D };
        shown.Prims.AddRange(box.Prims.Where(p => !(p.Kind == PrimKind.Glyph && p.Cp == Gap)));
        var at = new Dictionary<MathNode, MathMark>(ReferenceEqualityComparer.Instance);
        foreach (var m in marks) at[m.Node] = m;
        var found = new Dictionary<int, Slot>();
        Walk(tree, null, 0, at, found);
        slots = found.Values.OrderBy(s => s.Index).ToList();
        caret = Nearest(want);
        caretOn = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    int Nearest(int index) => slots.Count == 0 ? 0 : slots.OrderBy(s => Math.Abs(s.Index - index)).ThenBy(s => s.Index).First().Index;

    Slot? Here => slots.Find(s => s.Index == caret);

    void Walk(MathNode n, MathNode? owner, int depth, Dictionary<MathNode, MathMark> at, Dictionary<int, Slot> found)
    {
        switch (n)
        {
            case MRow r:
                Row(r, owner, depth, at, found);
                foreach (var item in r.Items) Walk(item, null, depth + 1, at, found);
                break;
            case MFrac f:
                Walk(f.Num, f, depth + 1, at, found);
                Walk(f.Den, f, depth + 1, at, found);
                break;
            case MSqrt q:
                Walk(q.Body, q, depth + 1, at, found);
                break;
            case MScript s:
                Walk(s.Base, null, depth + 1, at, found);
                if (s.Sub != null) Walk(s.Sub, s, depth + 1, at, found);
                if (s.Sup != null) Walk(s.Sup, s, depth + 1, at, found);
                break;
            case MDelim d:
                Walk(d.Body, d, depth + 1, at, found);
                break;
        }
    }

    /// <summary>The places of a row: before each of its items, and after the last one.</summary>
    void Row(MRow r, MathNode? owner, int depth, Dictionary<MathNode, MathMark> at, Dictionary<int, Slot> found)
    {
        var items = r.Items.Where(i => i.From >= 0 && at.ContainsKey(i)).ToList();
        if (items.Count == 0) return;
        at.TryGetValue(r, out var whole);
        for (int i = 0; i <= items.Count; i++)
        {
            var mark = at[items[Math.Min(i, items.Count - 1)]];
            int index, x = i < items.Count ? mark.X : mark.X + mark.W;
            bool needsSpace = false;
            if (i < items.Count) index = items[i].From;
            else
            {
                // An index or exponent ends at a space: the place after it lies behind that space.
                index = items[^1].To;
                if (items[^1] is MScript)
                {
                    if (index < src.Length && src[index] == ' ') index++;
                    else needsSpace = true;
                }
            }
            var tall = whole ?? mark;
            int pad = Size / 14, top = tall.Y - Math.Max(tall.H, Size / 2) - pad, bottom = tall.Y + Math.Max(tall.D, Size / 10) + pad;
            var slot = new Slot(index, x, top, bottom, i > 0 ? items[i - 1] : null, i < items.Count ? items[i] : null, owner, needsSpace, depth);
            if (!found.TryGetValue(index, out var old) || depth >= old.Depth) found[index] = slot;
        }
    }

    // ---- changing the line ----

    void Set(string line, int want)
    {
        src = line;
        Parse(want);
        Changed?.Invoke();
    }

    bool IsGap(int index) => index >= 0 && index < src.Length && src[index] == Gap;

    /// <summary>Puts text where the caret is; an empty box under the caret is filled.</summary>
    void Put(string text, int caretInText)
    {
        var s = Here;
        int at = caret, cut = 0;
        if (IsGap(at)) cut = 1;
        else if (IsGap(at - 1) && s?.Prev is MSym) { at--; cut = 1; }
        else if (s is { NeedsSpace: true }) { text = " " + text; caretInText++; }
        Set(src.Remove(at, cut).Insert(at, text), at + caretInText);
    }

    /// <summary>Puts in a shape; a # in it is an empty box. The caret goes to its first empty box.</summary>
    public void InsertTemplate(string template)
    {
        var s = Here;
        // A power or an index belongs to what stands before it; only without that does it bring a box of its own.
        if ((template.StartsWith("#^", StringComparison.Ordinal) || template.StartsWith("#_", StringComparison.Ordinal)) && s?.Prev != null && !IsGap(caret) && !IsGap(caret - 1))
            template = template[1..];
        string text = template.Replace('#', Gap);
        int gap = text.IndexOf(Gap);
        Put(text, gap >= 0 ? gap : text.Length);
    }

    /// <summary>A typed character. Some keys make a shape: / a fraction, ^ a power, _ an index, an opening bracket a pair of brackets.</summary>
    public void TypeChar(char c)
    {
        switch (c)
        {
            case ' ': case ')': case ']': case '}': Move(1); return;
            case '\\': return;
            case '^': InsertTemplate("#^(#) "); return;
            case '_': InsertTemplate("#_(#) "); return;
            case '(': InsertTemplate("(#)"); return;
            case '[': InsertTemplate("[#]"); return;
            case '{': InsertTemplate("{#}"); return;
            case '/':
                // What stands before the caret becomes the numerator.
                if (Here is { Prev: { } before } s && !IsGap(before.From) && !(before is MSym { Class: 'b' or 'r' or 'p' }))
                {
                    string num = src[before.From..s.Index].Trim();
                    string frac = "(" + num + ")/(" + Gap + ")";
                    Set(src.Remove(before.From, s.Index - before.From).Insert(before.From, frac), before.From + frac.Length - 2);
                }
                else InsertTemplate("(#)/(#)");
                return;
        }
        if (c < ' ') return;
        Put(c.ToString(), 1);
    }

    public void TypeText(string text)
    {
        foreach (char c in text) TypeChar(c);
    }

    /// <summary>To the next place (1) or the one before (-1).</summary>
    public void Move(int dir)
    {
        int i = slots.FindIndex(s => s.Index == caret) + dir;
        if (i < 0 || i >= slots.Count) return;
        caret = slots[i].Index;
        caretOn = true;
        InvalidateVisual();
    }

    static readonly Regex Name = new(@"\\[a-zA-Z]+");

    /// <summary>True when a stretch of the line holds nothing but empty boxes and the signs of its shape.</summary>
    bool Hollow(int from, int to) => Name.Replace(src[from..to], "").All(c => c == Gap || "()[]{}/^_ ".Contains(c));

    public void Back()
    {
        if (Here is not { } s) return;
        var p = s.Prev;
        if (p != null && !IsGap(p.From))
        {
            if (p is MSym) { Set(src.Remove(p.From, s.Index - p.From), p.From); return; }
            if (p is MText or MRow)
            {
                // A name or a number loses its last character; a name typed with a backslash goes whole.
                int from = p is MText && src[p.From] == '\\' ? p.From : p.To - 1;
                Set(src.Remove(from, s.Index - from), from);
                return;
            }
            Move(-1);
            return;
        }
        // At the start of a box: a shape that is still empty goes away, any other is left by the caret.
        if (s.Owner is { } o)
        {
            int from = o is MScript sc ? sc.Base.To : o.From, to = o.To;
            if (o is MScript && to < src.Length && src[to] == ' ') to++;
            if (from >= 0 && to >= from && Hollow(from, to)) { Set(src.Remove(from, to - from), from); return; }
        }
        Move(-1);
    }

    public void Delete()
    {
        if (Here is not { } s || s.Next is not { } n || IsGap(n.From)) { Move(1); return; }
        if (n is MSym) { Set(src.Remove(n.From, n.To - n.From), n.From); return; }
        if (n is MRow) { Set(src.Remove(n.From, 1), n.From); return; }
        Move(1);
    }

    /// <summary>To the next empty box (or the one before); false if there is none.</summary>
    public bool NextGap(bool back)
    {
        var gaps = slots.Where(s => IsGap(s.Index)).Select(s => s.Index).ToList();
        if (gaps.Count == 0) return false;
        int to = back ? gaps.LastOrDefault(g => g < caret, gaps[^1]) : gaps.FirstOrDefault(g => g > caret, gaps[0]);
        caret = to;
        caretOn = true;
        InvalidateVisual();
        return true;
    }

    // ---- keys and mouse ----

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (e.Text.Length > 0 && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            TypeText(e.Text);
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Left: Move(-1); e.Handled = true; break;
            case Key.Right: Move(1); e.Handled = true; break;
            case Key.Up: Step(-1); e.Handled = true; break;
            case Key.Down: Step(1); e.Handled = true; break;
            case Key.Home: if (slots.Count > 0) { caret = slots[0].Index; InvalidateVisual(); } e.Handled = true; break;
            case Key.End: if (slots.Count > 0) { caret = slots[^1].Index; InvalidateVisual(); } e.Handled = true; break;
            case Key.Back: Back(); e.Handled = true; break;
            case Key.Delete: Delete(); e.Handled = true; break;
            case Key.Tab: if (NextGap(shift)) e.Handled = true; break;
            case Key.Space: Move(1); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    /// <summary>To the nearest place above (-1) or below (1) the caret.</summary>
    void Step(int dir)
    {
        if (Here is not { } s) return;
        int mid = (s.Top + s.Bottom) / 2;
        var best = slots.Where(o => dir * ((o.Top + o.Bottom) / 2 - mid) > Size / 8)
            .OrderBy(o => Math.Abs(o.X - s.X) + 3L * Math.Abs((o.Top + o.Bottom) / 2 - mid)).FirstOrDefault();
        if (best == null) return;
        caret = best.Index;
        caretOn = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var (x0, y0, scale) = Place();
        var pt = e.GetPosition(this);
        double x = (pt.X - x0) / scale, y = (pt.Y - y0) / scale;
        Slot? best = null;
        double least = double.MaxValue;
        foreach (var s in slots)
        {
            double top = U.Dip(s.Top), bottom = U.Dip(s.Bottom), dy = y < top ? top - y : y > bottom ? y - bottom : 0, dx = Math.Abs(U.Dip(s.X) - x);
            double d = dx + 3 * dy - s.Depth * 0.01;
            if (d < least) { least = d; best = s; }
        }
        if (best != null) caret = best.Index;
        caretOn = true;
        InvalidateVisual();
        e.Handled = true;
    }

    // ---- drawing ----

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 400 : available.Width, Math.Max(MinHeight, U.Dip(box.H + box.D) + 44));

    /// <summary>Where the equation stands in the pad: the left end of its baseline, and how much it is shrunk to fit.</summary>
    (double X, double Y, double Scale) Place()
    {
        double w = U.Dip(box.W), h = U.Dip(box.H + box.D);
        double scale = w > 0 ? Math.Min(1, Math.Max(0.2, (ActualWidth - 28) / w)) : 1;
        return ((ActualWidth - w * scale) / 2, (ActualHeight - h * scale) / 2 + U.Dip(box.H) * scale, scale);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        var (x0, y0, scale) = Place();
        dc.PushTransform(new TranslateTransform(x0, y0));
        dc.PushTransform(new ScaleTransform(scale, scale));
        PageRenderer.DrawBox(dc, shown, span);
        if (IsKeyboardFocused && caretOn && Here is { } s)
            dc.DrawRectangle(Brushes.Black, null, new Rect(U.Dip(s.X) - 0.6, U.Dip(s.Top), 1.3, U.Dip(s.Bottom - s.Top)));
        dc.Pop();
        dc.Pop();
    }
}
