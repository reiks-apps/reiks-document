using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ReiksDocument;

enum PrimKind { Glyph, Text, TextUp, Rect, Line, Disc, Slice }

/// <summary>One drawn piece of an equation or chart. X to the right from the object's left edge,
/// Y downward from its baseline (negative = above).</summary>
sealed class Prim
{
    public PrimKind Kind;
    /// <summary>Glyph, Text: origin on the baseline. TextUp: text turned a quarter turn, starting here and running upward. Rect: top left. Disc, Slice: centre.</summary>
    public int X, Y;
    /// <summary>Rect: size. Disc, Slice: W is the radius.</summary>
    public int W, H;
    public int Size, Cp, Thick;
    /// <summary>0xRRGGBB, or -1 for the colour of the text.</summary>
    public int Color = -1;
    public string? Text;
    /// <summary>Line: x0, y0, x1, y1, …</summary>
    public int[]? Pts;
    /// <summary>Slice: the part From…To of Total of a full turn, clockwise from 12 o'clock.</summary>
    public long From, To, Total;
}

/// <summary>The size of an object in a line (width, height above and depth below the baseline) and what it draws.</summary>
sealed class ObjBox
{
    public int W, H, D;
    public List<Prim> Prims = new();
}

/// <summary>An equation: what was typed, and the structure that is laid out. Block = on its own line, in display style.</summary>
sealed record Equation(string Source, MRow Tree, bool Block) : Inline(Block)
{
    public static Equation From(string source, bool block) => new(source, MathParser.Parse(source), block);
}

/// <summary>Values are in thousandths.</summary>
sealed record Series(string Name, IReadOnlyList<long> Values);

/// <summary>A chart: its kind (bar, line, pie), its data and its size.</summary>
/// A plot is the graph of formulas in x: Formula is what was typed, From and To the stretch of x (thousandths),
/// and each series holds the values at equal steps from From to To, with ChartLayout.Missing where there is none.
sealed record Chart(string Kind, string Title, IReadOnlyList<string> Cats, IReadOnlyList<Series> Series, int Width, int Height, bool Block,
    string Formula = "", long From = 0, long To = 0, string XTitle = "", string YTitle = "", string AxisNames = "middle") : Inline(Block)
{
    public static Chart Default() => new("bar", "", new[] { "A", "B", "C" }, new[] { new Series("Series 1", new long[] { 3000, 5000, 4000 }) }, 4320000, 2700000, false);
}

/// <summary>Sizes of objects, kept per object as long as it lives.</summary>
static class Objects
{
    sealed class Cached
    {
        public ResolvedFont Font = null!;
        public int Size;
        public ObjBox Box = null!;
    }

    static readonly ConditionalWeakTable<Inline, Cached> cache = new();

    /// <summary>The box of a picture, equation or chart standing in a run of the given format.</summary>
    public static ObjBox Measure(Inline o, Span sp)
    {
        if (cache.TryGetValue(o, out var c) && ReferenceEquals(c.Font, sp.Font) && c.Size == sp.SizeEmu) return c.Box;
        var box = o switch
        {
            Picture pic => new ObjBox { W = pic.Width, H = pic.Height },
            Equation eq => MathLayout.Layout(eq.Tree, sp.SizeEmu, eq.Block),
            Chart ch => ChartLayout.Layout(ch, sp),
            _ => new ObjBox(),
        };
        cache.AddOrUpdate(o, new Cached { Font = sp.Font, Size = sp.SizeEmu, Box = box });
        return box;
    }
}

/// <summary>The chart rules of FORMAT.md.</summary>
static class ChartLayout
{
    public static readonly int[] Palette = { 0x0072B2, 0xE69F00, 0x009E73, 0xD55E00, 0xCC79A7, 0x56B4E9, 0xF0E442, 0x999999 };
    const int P = 76200, Hair = 9525, Frame = 0xD0D0D0, Grid = 0xD9D9D9, Zero = 0x808080;
    /// <summary>A place in a series of a plot where the formula has no value.</summary>
    public const long Missing = long.MinValue;

    /// <summary>The step of an axis: the first of 1, 2, 5, 10, 20, 50, … that cuts lo..hi, widened to its multiples, into at most max parts.</summary>
    static (long Step, long Lo, long Hi) Axis(long lo, long hi, int max)
    {
        for (long pow = 1; ; pow *= 10)
            foreach (int mul in new[] { 1, 2, 5 })
            {
                long step = mul * pow, aLo = FloorTo(lo, step), aHi = -FloorTo(-hi, step);
                if ((aHi - aLo) / step <= max) return (step, aLo, aHi);
            }
    }

    /// <summary>A value in thousandths as text: no trailing zeros, a real minus sign.</summary>
    public static string Fmt(long v, bool plainMinus = false)
    {
        long a = Math.Abs(v);
        string s = (a / 1000).ToString(CultureInfo.InvariantCulture);
        if (a % 1000 != 0) s += "." + (a % 1000).ToString("000", CultureInfo.InvariantCulture).TrimEnd('0');
        return v < 0 ? (plainMinus ? "-" : ((char)0x2212).ToString()) + s : s;
    }

    public static int TextWidth(FontFace f, string s, int size)
    {
        int w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) cp = char.ConvertToUtf32(s[i], s[++i]);
            w += f.Advance(cp, size);
        }
        return w;
    }

    static long Half(long v) => v >= 0 ? v / 2 : -((-v + 1) / 2);

    static long FloorTo(long v, long step) => (v >= 0 ? v / step : -((-v + step - 1) / step)) * step;

    public static ObjBox Layout(Chart c, Span sp)
    {
        int W = c.Width, H = c.Height;
        var box = new ObjBox { W = W, H = H };
        var prims = box.Prims;
        var f = sp.Font.Face;
        int S = sp.SizeEmu, Sc = (int)((long)S * 4 / 5);
        int Asc(int size) => (int)U.RoundDiv((long)(f.Ascender + f.LineGap) * size, f.UnitsPerEm);
        int Desc(int size) => (int)U.RoundDiv((long)f.Descender * size, f.UnitsPerEm);
        int ac = Asc(Sc), dc = Desc(Sc), lc = ac + dc;

        // Chart coordinates start at the top left corner of the chart; the chart stands on the baseline.
        void R(long x, long y, long w, long h, int color)
        {
            if (w > 0 && h > 0) prims.Add(new Prim { Kind = PrimKind.Rect, X = (int)x, Y = (int)y - H, W = (int)w, H = (int)h, Color = color });
        }
        void T(long x, long baseline, string text, int size)
        {
            if (text.Length > 0) prims.Add(new Prim { Kind = PrimKind.Text, X = (int)x, Y = (int)baseline - H, Size = size, Text = text });
        }

        void Up(long x, long y, string text, int size)
        {
            if (text.Length > 0) prims.Add(new Prim { Kind = PrimKind.TextUp, X = (int)x, Y = (int)y - H, Size = size, Text = text });
        }

        R(0, 0, W, Hair, Frame);
        R(0, H - Hair, W, Hair, Frame);
        R(0, 0, Hair, H, Frame);
        R(W - Hair, 0, Hair, H, Frame);

        int y0 = P;
        if (c.Title.Length > 0)
        {
            int tw = TextWidth(f, c.Title, S);
            T(Math.Max(0, (W - tw) / 2), P + Asc(S), c.Title, S);
            y0 = P + Asc(S) + Desc(S) + P / 2;
        }

        bool pie = c.Kind == "pie";
        var legend = pie ? c.Cats : c.Series.Count >= 2 ? c.Series.Select(s => s.Name).ToList() : (IReadOnlyList<string>)Array.Empty<string>();
        int y1 = H - P;
        if (legend.Count > 0)
        {
            int top = H - P - lc, q = (int)((long)Sc * 7 / 10), gap = Sc / 3;
            y1 = top - P / 2;
            long total = (long)(legend.Count - 1) * Sc;
            foreach (string name in legend) total += q + gap + TextWidth(f, name, Sc);
            long x = Math.Max(P, (W - total) / 2);
            for (int i = 0; i < legend.Count; i++)
            {
                R(x, top + ac - q, q, q, Palette[i % Palette.Length]);
                T(x + q + gap, top + ac, legend[i], Sc);
                x += q + gap + TextWidth(f, legend[i], Sc) + Sc;
            }
        }

        // The names of the axes, for every kind but the pie. "middle": the horizontal one centred below the plot,
        // the vertical one turned along its axis. "ends": the horizontal one at the right end, the vertical one above its axis.
        bool ends = c.AxisNames == "ends";
        int xNameBase = 0, yCol = 0;
        if (!pie)
        {
            if (c.XTitle.Length > 0) { xNameBase = y1 - dc; y1 -= lc + Sc / 4; }
            if (c.YTitle.Length > 0)
            {
                if (ends) { T(P, y0 + ac, c.YTitle, Sc); y0 += lc + Sc / 4; }
                else yCol = lc + Sc / 4;
            }
        }
        void NameAxes(int from, int width, int top, int height)
        {
            if (c.XTitle.Length > 0)
            {
                int tw = TextWidth(f, c.XTitle, Sc);
                T(ends ? from + width - tw : from + Half(width - tw), xNameBase, c.XTitle, Sc);
            }
            if (yCol > 0) Up(P + ac, top + height - Half(height - TextWidth(f, c.YTitle, Sc)), c.YTitle, Sc);
        }

        if (c.Kind == "plot")
        {
            long vLo = long.MaxValue, vHi = long.MinValue;
            foreach (var se in c.Series)
                foreach (long v in se.Values)
                    if (v != Missing) { vLo = Math.Min(vLo, v); vHi = Math.Max(vHi, v); }
            if (vLo > vHi || c.To <= c.From) return box;
            if (vHi == vLo) vHi = vLo + 1000;
            var (yStep, yLo, yHi) = Axis(vLo, vHi, 6);
            long xStep = 1;
            for (long pow = 1, found = 0; found == 0; pow *= 10)
                foreach (int mul in new[] { 1, 2, 5 })
                {
                    xStep = mul * pow;
                    if ((c.To - c.From) / xStep <= 8) { found = 1; break; }
                }
            int yLabelW = 0;
            for (long v = yLo; v <= yHi; v += yStep) yLabelW = Math.Max(yLabelW, TextWidth(f, Fmt(v), Sc));
            int pLeft = P + yCol + yLabelW + Sc / 3, pTop = y0 + lc / 2, pBottom = y1 - lc - Sc / 4;
            int pW = W - P - pLeft, pH = pBottom - pTop;
            if (pW <= 0 || pH <= 0) return box;
            NameAxes(pLeft, pW, pTop, pH);
            long PY(long v) => pBottom - (v - yLo) * pH / (yHi - yLo);
            long PX(long v) => pLeft + (v - c.From) * pW / (c.To - c.From);
            for (long v = yLo; v <= yHi; v += yStep)
            {
                long y = PY(v);
                R(pLeft, y - Hair / 2, pW, Hair, v == 0 ? Zero : Grid);
                string label = Fmt(v);
                T(pLeft - Sc / 3 - TextWidth(f, label, Sc), y + Half(ac - dc), label, Sc);
            }
            for (long v = -FloorTo(-c.From, xStep); v <= c.To; v += xStep)
            {
                long x = PX(v);
                R(x - Hair / 2, pTop, Hair, pH, v == 0 ? Zero : Grid);
                string label = Fmt(v);
                T(x - Half(TextWidth(f, label, Sc)), pBottom + Sc / 4 + ac, label, Sc);
            }
            for (int j = 0; j < c.Series.Count; j++)
            {
                var values = c.Series[j].Values;
                int last = values.Count - 1;
                var run = new List<int>();
                for (int k = 0; k <= values.Count; k++)
                {
                    if (k < values.Count && values[k] != Missing && last > 0)
                    {
                        run.Add((int)(pLeft + (long)k * pW / last));
                        run.Add((int)PY(values[k]) - H);
                        continue;
                    }
                    if (run.Count >= 4) prims.Add(new Prim { Kind = PrimKind.Line, Pts = run.ToArray(), Thick = 19050, Color = Palette[j % Palette.Length] });
                    run.Clear();
                }
            }
            return box;
        }

        int n = c.Cats.Count, m = c.Series.Count;
        if (n == 0 || m == 0) return box;
        long Value(int j, int i) => i < c.Series[j].Values.Count && c.Series[j].Values[i] != Missing ? c.Series[j].Values[i] : 0;

        if (pie)
        {
            long total = 0;
            for (int i = 0; i < n; i++) total += Math.Max(0, Value(0, i));
            int r = Math.Min(W - 2 * P, y1 - y0) / 2;
            if (total <= 0 || r <= 0) return box;
            long from = 0;
            for (int i = 0; i < n; i++)
            {
                long v = Math.Max(0, Value(0, i));
                if (v > 0)
                    prims.Add(new Prim
                    {
                        Kind = PrimKind.Slice, X = W / 2, Y = (y0 + y1) / 2 - H, W = r, From = from, To = from + v, Total = total,
                        Color = Palette[i % Palette.Length],
                    });
                from += v;
            }
            return box;
        }

        // The value axis: from the multiple of the step at or below the smallest value (or 0)
        // to the multiple at or above the largest (or 0), in at most 6 steps.
        long lo = 0, hi = 0;
        for (int j = 0; j < m; j++)
            for (int i = 0; i < n; i++) { lo = Math.Min(lo, Value(j, i)); hi = Math.Max(hi, Value(j, i)); }
        if (hi == lo) hi = lo + 1000;
        var (step, aLo, aHi) = Axis(lo, hi, 6);

        int labelW = 0;
        for (long v = aLo; v <= aHi; v += step) labelW = Math.Max(labelW, TextWidth(f, Fmt(v), Sc));
        int left = P + yCol + labelW + Sc / 3, right = W - P, plotTop = y0 + lc / 2, bottom = y1 - lc - Sc / 4;
        int pw = right - left, ph = bottom - plotTop;
        if (pw <= 0 || ph <= 0) return box;
        NameAxes(left, pw, plotTop, ph);
        long Y(long v) => bottom - (v - aLo) * ph / (aHi - aLo);

        for (long v = aLo; v <= aHi; v += step)
        {
            long y = Y(v);
            R(left, y - Hair / 2, pw, Hair, v == 0 ? Zero : Grid);
            string label = Fmt(v);
            T(left - Sc / 3 - TextWidth(f, label, Sc), y + Half(ac - dc), label, Sc);
        }

        int bw = pw / n;
        for (int i = 0; i < n; i++)
            T(left + (long)i * bw + Half(bw - TextWidth(f, c.Cats[i], Sc)), bottom + Sc / 4 + ac, c.Cats[i], Sc);

        if (c.Kind == "line")
        {
            for (int j = 0; j < m; j++)
            {
                int color = Palette[j % Palette.Length];
                var pts = new int[2 * n];
                for (int i = 0; i < n; i++)
                {
                    pts[2 * i] = left + i * bw + bw / 2;
                    pts[2 * i + 1] = (int)Y(Value(j, i)) - H;
                }
                if (n > 1) prims.Add(new Prim { Kind = PrimKind.Line, Pts = pts, Thick = 19050, Color = color });
                for (int i = 0; i < n; i++)
                    prims.Add(new Prim { Kind = PrimKind.Disc, X = pts[2 * i], Y = pts[2 * i + 1], W = 28575, Color = color });
            }
        }
        else
        {
            int mg = bw / 8, sw = (bw - 2 * mg) / m;
            long zero = Y(0);
            for (int j = 0; j < m; j++)
                for (int i = 0; i < n; i++)
                {
                    long y = Y(Value(j, i));
                    R(left + (long)i * bw + mg + (long)j * sw, Math.Min(y, zero), sw, Math.Abs(y - zero), Palette[j % Palette.Length]);
                }
        }
        return box;
    }
}
