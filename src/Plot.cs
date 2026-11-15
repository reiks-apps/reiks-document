using System;
using System.Collections.Generic;

namespace ReiksDocument;

/// <summary>Graphs of formulas. The formula is worked out once, when the graph is made or changed, and the points are
/// kept in the document as whole thousandths; the layout only ever uses those stored numbers.</summary>
static class Plot
{
    /// <summary>The stretch from..to is cut into this many equal steps; a point is worked out at each end of a step.</summary>
    public const int Steps = 200;

    /// <summary>A graph of one or more formulas in x, separated by semicolons, between from and to (thousandths).</summary>
    public static Chart Make(Chart basis, string formula, long from, long to)
    {
        var series = new List<Series>();
        foreach (string part in formula.Split(';'))
        {
            string src = part.Trim();
            if (src.Length == 0) continue;
            // A formula can be given a name of its own, as in "Height = 20x - 5x^2"; without one its name is the formula.
            string name = src;
            int eq = src.IndexOf('=');
            if (eq >= 0)
            {
                name = src[..eq].Trim();
                src = src[(eq + 1)..].Trim();
                if (src.Length == 0) continue;
                if (name.Length == 0) name = src;
            }
            var tree = MathParser.Parse(src);
            var values = new long[Steps + 1];
            for (int k = 0; k <= Steps; k++)
            {
                double x = (from + (double)(to - from) * k / Steps) / 1000.0;
                double y = Eval(tree, x);
                values[k] = double.IsFinite(y) && Math.Abs(y) <= 1e9 ? (long)Math.Round(y * 1000, MidpointRounding.AwayFromZero) : ChartLayout.Missing;
            }
            series.Add(new Series(name, values));
        }
        return basis with { Kind = "plot", Formula = formula.Trim(), From = from, To = to, Cats = Array.Empty<string>(), Series = series };
    }

    sealed class Cursor
    {
        public List<MathNode> Items = null!;
        public int I;
        public double X;
        public MathNode? Now => I < Items.Count ? Items[I] : null;
    }

    static bool Is(MathNode? n, params int[] chars) => n is MSym s && Array.IndexOf(chars, s.Cp) >= 0;

    /// <summary>The value of a typed formula at x; not a number where it has none or is not understood.</summary>
    public static double Eval(MathNode n, double x)
    {
        switch (n)
        {
            case MRow r:
                return Number(r) ?? Sum(new Cursor { Items = r.Items, X = x });
            case MSym s:
                if (s.Cp >= '0' && s.Cp <= '9') return s.Cp - '0';
                return s.Cp switch { 0x1D465 => x, 0x1D452 => Math.E, 0x1D70B or 0x3C0 => Math.PI, _ => double.NaN };
            case MFrac f:
                return Eval(f.Num, x) / Eval(f.Den, x);
            case MSqrt q:
                return Math.Sqrt(Eval(q.Body, x));
            case MDelim d:
                return Eval(d.Body, x);
            case MScript sc:
                return sc.Sup != null && sc.Sub == null ? Math.Pow(Eval(sc.Base, x), Eval(sc.Sup, x)) : double.NaN;
            default:
                return double.NaN;
        }
    }

    /// <summary>A row that is nothing but digits and one decimal point or comma.</summary>
    static double? Number(MRow r)
    {
        if (r.Items.Count < 2) return null;
        var chars = new char[r.Items.Count];
        for (int i = 0; i < chars.Length; i++)
        {
            if (r.Items[i] is not MSym s) return null;
            if (s.Cp >= '0' && s.Cp <= '9') chars[i] = (char)s.Cp;
            else if (s.Cp == '.' || s.Cp == ',') chars[i] = '.';
            else return null;
        }
        return double.TryParse(new string(chars), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }

    static double Sum(Cursor c)
    {
        if (c.Items.Count == 0) return double.NaN;
        double v = Term(c);
        while (c.Now != null)
        {
            if (Is(c.Now, '+')) { c.I++; v += Term(c); }
            else if (Is(c.Now, 0x2212)) { c.I++; v -= Term(c); }
            else return double.NaN;
        }
        return v;
    }

    /// <summary>Factors side by side are multiplied, as in 2x or x sin x.</summary>
    static double Term(Cursor c)
    {
        double v = Factor(c);
        while (c.Now is { } n)
        {
            if (Is(n, '+', 0x2212) || n is MSym { Class: 'r' or 'p' }) break;
            if (Is(n, 0xD7, 0x22C5, 0x2217)) { c.I++; v *= Factor(c); }
            else if (Is(n, 0xF7)) { c.I++; v /= Factor(c); }
            else v *= Factor(c);
        }
        return v;
    }

    static double Factor(Cursor c)
    {
        var n = c.Now;
        if (n == null) return double.NaN;
        c.I++;
        if (Is(n, 0x2212)) return -Factor(c);
        if (Is(n, '+')) return Factor(c);
        if (n is MText t) return Apply(t.Text, Factor(c));
        if (n is MScript { Base: MText ft } sc)
            return sc.Sup != null && sc.Sub == null ? Math.Pow(Apply(ft.Text, Factor(c)), Eval(sc.Sup, c.X)) : double.NaN;
        return Eval(n, c.X);
    }

    static double Apply(string name, double a) => name switch
    {
        "sin" => Math.Sin(a), "cos" => Math.Cos(a), "tan" => Math.Tan(a),
        "cot" => 1 / Math.Tan(a), "sec" => 1 / Math.Cos(a), "csc" => 1 / Math.Sin(a),
        "arcsin" => Math.Asin(a), "arccos" => Math.Acos(a), "arctan" => Math.Atan(a),
        "sinh" => Math.Sinh(a), "cosh" => Math.Cosh(a), "tanh" => Math.Tanh(a),
        "log" => Math.Log10(a), "ln" => Math.Log(a), "exp" => Math.Exp(a), "abs" => Math.Abs(a),
        _ => double.NaN,
    };
}
