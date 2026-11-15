using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ReiksDocument;

// ---- the structure of an equation ----

abstract class MathNode
{
    /// <summary>The stretch of the typed line this came from, when it was read from one; -1 otherwise. Not part of the structure.</summary>
    public int From = -1, To = -1;
}

/// <summary>Where a part of a laid-out equation stands: x from the left, y of its baseline (downward) from the baseline of the whole, and its box.</summary>
sealed record MathMark(MathNode Node, int X, int Y, int W, int H, int D);

/// <summary>Items side by side.</summary>
sealed class MRow : MathNode
{
    public readonly List<MathNode> Items;
    public MRow(List<MathNode> items) { Items = items; }
    public MRow() : this(new List<MathNode>()) { }
}

/// <summary>One character. Class: o ordinary, b binary operator, r relation, p punctuation, O large operator.</summary>
sealed class MSym : MathNode
{
    public readonly int Cp;
    public readonly char Class;
    public MSym(int cp, char cls) { Cp = cp; Class = cls; }
}

/// <summary>An upright name such as sin or lim.</summary>
sealed class MText : MathNode
{
    public readonly string Text;
    public MText(string text) { Text = text; }
}

sealed class MFrac : MathNode
{
    public readonly MRow Num, Den;
    public MFrac(MRow num, MRow den) { Num = num; Den = den; }
}

sealed class MSqrt : MathNode
{
    public readonly MRow Body;
    public MSqrt(MRow body) { Body = body; }
}

sealed class MScript : MathNode
{
    public readonly MathNode Base;
    public MRow? Sub, Sup;
    public MScript(MathNode b) { Base = b; }
}

/// <summary>Brackets that grow with what they hold. Close is 0 when the closing bracket is missing.</summary>
sealed class MDelim : MathNode
{
    public readonly int Open, Close;
    public readonly MRow Body;
    public MDelim(int open, int close, MRow body) { Open = open; Close = close; Body = body; }
}

// ---- from the typed line to the structure ----

sealed class MathParser
{
    static readonly Dictionary<string, int> Commands = new()
    {
        { "alpha", 0x3B1 }, { "beta", 0x3B2 }, { "gamma", 0x3B3 }, { "delta", 0x3B4 }, { "epsilon", 0x3B5 }, { "zeta", 0x3B6 },
        { "eta", 0x3B7 }, { "theta", 0x3B8 }, { "iota", 0x3B9 }, { "kappa", 0x3BA }, { "lambda", 0x3BB }, { "mu", 0x3BC },
        { "nu", 0x3BD }, { "xi", 0x3BE }, { "pi", 0x3C0 }, { "rho", 0x3C1 }, { "sigma", 0x3C3 }, { "tau", 0x3C4 },
        { "upsilon", 0x3C5 }, { "phi", 0x3C6 }, { "chi", 0x3C7 }, { "psi", 0x3C8 }, { "omega", 0x3C9 },
        { "Gamma", 0x393 }, { "Delta", 0x394 }, { "Theta", 0x398 }, { "Lambda", 0x39B }, { "Xi", 0x39E }, { "Pi", 0x3A0 },
        { "Sigma", 0x3A3 }, { "Phi", 0x3A6 }, { "Psi", 0x3A8 }, { "Omega", 0x3A9 },
        { "infty", 0x221E }, { "pm", 0xB1 }, { "mp", 0x2213 }, { "times", 0xD7 }, { "cdot", 0x22C5 }, { "div", 0xF7 },
        { "le", 0x2264 }, { "leq", 0x2264 }, { "ge", 0x2265 }, { "geq", 0x2265 }, { "ne", 0x2260 }, { "neq", 0x2260 },
        { "approx", 0x2248 }, { "equiv", 0x2261 }, { "sim", 0x223C }, { "propto", 0x221D }, { "in", 0x2208 }, { "notin", 0x2209 },
        { "subset", 0x2282 }, { "subseteq", 0x2286 }, { "cup", 0x222A }, { "cap", 0x2229 }, { "to", 0x2192 }, { "rightarrow", 0x2192 },
        { "leftarrow", 0x2190 }, { "leftrightarrow", 0x2194 }, { "Rightarrow", 0x21D2 }, { "Leftrightarrow", 0x21D4 },
        { "partial", 0x2202 }, { "nabla", 0x2207 }, { "forall", 0x2200 }, { "exists", 0x2203 }, { "emptyset", 0x2205 },
        { "sum", 0x2211 }, { "prod", 0x220F }, { "int", 0x222B }, { "iint", 0x222C }, { "iiint", 0x222D }, { "oint", 0x222E },
        { "ldots", 0x2026 }, { "cdots", 0x22EF }, { "degree", 0xB0 }, { "hbar", 0x210F }, { "ell", 0x2113 }, { "angle", 0x2220 },
        { "perp", 0x22A5 }, { "prime", 0x2032 },
    };

    // Longer names first, so that sinh is not read as sin and h.
    static readonly string[] Functions =
    {
        "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "sin", "cos", "tan", "cot", "sec", "csc",
        "log", "ln", "exp", "lim", "max", "min", "det", "gcd", "abs",
    };

    public static IEnumerable<string> CommandNames => Commands.Keys;
    public static IEnumerable<string> FunctionNames => Functions;

    readonly string src;
    int pos;
    bool spaced;

    MathParser(string s) { src = s; }

    public static MRow Parse(string source) => new(new MathParser(source).Row(false));

    public static char ClassOf(int cp) => cp switch
    {
        '+' or 0x2212 or 0xB1 or 0x2213 or 0xD7 or 0xF7 or 0x22C5 or 0x2217 or 0x222A or 0x2229 or 0x2218 or 0x2295 or 0x2297 or 0x2227 or 0x2228 => 'b',
        '=' or '<' or '>' or ':' or 0x2264 or 0x2265 or 0x2260 or 0x2248 or 0x2261 or 0x223C or 0x2208 or 0x2209 or 0x2282 or 0x2286
            or 0x2192 or 0x2190 or 0x2194 or 0x21D2 or 0x21D4 or 0x221D or 0x226A or 0x226B => 'r',
        ',' or ';' => 'p',
        0x2211 or 0x220F or 0x222B or 0x222C or 0x222D or 0x222E or 0x22C3 or 0x22C2 => 'O',
        _ => 'o',
    };

    /// <summary>The character that is drawn for a typed one: italic letters, a real minus sign.</summary>
    public static int Map(int cp)
    {
        if (cp == '-') return 0x2212;
        if (cp == '*') return 0x2217;
        if (cp == 'h') return 0x210E;
        if (cp >= 'a' && cp <= 'z') return 0x1D44E + (cp - 'a');
        if (cp >= 'A' && cp <= 'Z') return 0x1D434 + (cp - 'A');
        if (cp >= 0x3B1 && cp <= 0x3C9) return 0x1D6FC + (cp - 0x3B1);
        return cp;
    }

    static bool IsOpen(int c) => c == '(' || c == '[' || c == '{';
    static bool IsClose(int c) => c == ')' || c == ']' || c == '}';
    static bool IsLetter(int c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    int Peek()
    {
        if (pos >= src.Length) return -1;
        char c = src[pos];
        if (c < ' ') return ' ';
        return char.IsHighSurrogate(c) && pos + 1 < src.Length && char.IsLowSurrogate(src[pos + 1]) ? char.ConvertToUtf32(c, src[pos + 1]) : c;
    }

    void Next() { pos += Peek() > 0xFFFF ? 2 : 1; }

    /// <summary>Notes the stretch of the line a node came from: from there to where the reading stands now.</summary>
    T At<T>(T n, int from) where T : MathNode
    {
        if (n.From < 0) { n.From = from; n.To = pos; }
        return n;
    }

    void SkipSpaces() { while (Peek() == ' ') pos++; }

    char CommandClass()
    {
        int p = pos + 1;
        while (p < src.Length && IsLetter(src[p])) p++;
        return Commands.TryGetValue(src[(pos + 1)..p], out int cp) ? ClassOf(cp) : 'o';
    }

    bool StartsElement(int c)
    {
        if (c < 0 || c == ' ' || c == '/' || IsClose(c)) return false;
        if (c == '^' || c == '_') return true;
        if (c == '\\') return "brp".IndexOf(CommandClass()) < 0;
        return "brp".IndexOf(ClassOf(Map(c))) < 0;
    }

    static MathNode Wrap(List<MathNode> run) => run.Count == 1 ? run[0] : new MRow(run);

    /// <summary>As a row; round brackets around the whole thing are dropped.</summary>
    static MRow Strip(MathNode n) =>
        n is MDelim { Open: '(', Close: ')' } d ? d.Body : n as MRow ?? new MRow(new List<MathNode> { n });

    List<MathNode> Row(bool inGroup)
    {
        var items = new List<MathNode>();
        while (pos < src.Length)
        {
            int c = Peek();
            if (c == ' ') { Next(); continue; }
            if (IsClose(c))
            {
                if (inGroup) break;
                int closeAt = pos;
                Next();
                items.Add(At(new MSym(c, 'o'), closeAt));
                continue;
            }
            if (c == '/' || StartsElement(c))
            {
                int from = pos;
                var run = c == '/' ? new List<MathNode>() : Operand();
                SkipSpaces();
                if (Peek() != '/') { items.AddRange(run); continue; }
                MathNode node = Wrap(run);
                while (Peek() == '/')
                {
                    Next();
                    SkipSpaces();
                    var den = StartsElement(Peek()) ? Operand() : new List<MathNode>();
                    node = At(new MFrac(Strip(node), Strip(Wrap(den))), from);
                    SkipSpaces();
                }
                items.Add(node);
                continue;
            }
            if (c == '\\') { items.Add(Command()); continue; }
            int m = Map(c), symAt = pos;
            Next();
            items.Add(At(new MSym(m, ClassOf(m)), symAt));
        }
        return items;
    }

    /// <summary>Elements with nothing between them: the operand of a fraction.</summary>
    List<MathNode> Operand()
    {
        var run = new List<MathNode>();
        while (pos < src.Length && StartsElement(Peek())) run.Add(Element(true));
        return run;
    }

    MRow ScriptArg()
    {
        var items = new List<MathNode>();
        int c = Peek();
        if (c == '-' || c == '+' || c == 0x2212)
        {
            int signAt = pos;
            Next();
            items.Add(At(new MSym(Map(c), 'o'), signAt));
        }
        var run = new List<MathNode>();
        while (pos < src.Length && StartsElement(Peek()) && Peek() != '^' && Peek() != '_')
        {
            run.Add(Element(false));
            if (spaced) break;
        }
        if (run.Count == 1 && run[0] is MDelim { Open: '(', Close: ')' } d) items.AddRange(d.Body.Items);
        else items.AddRange(run);
        return new MRow(items);
    }

    MathNode Element(bool scripts)
    {
        spaced = false;
        int from = pos;
        MathNode b;
        int c = Peek();
        if (c == '^' || c == '_') b = new MRow();
        else if (c >= '0' && c <= '9')
        {
            var digits = new List<MathNode>();
            while (pos < src.Length)
            {
                char ch = src[pos];
                bool digit = ch >= '0' && ch <= '9';
                bool point = (ch == '.' || ch == ',') && pos + 1 < src.Length && src[pos + 1] >= '0' && src[pos + 1] <= '9';
                if (!digit && !point) break;
                pos++;
                digits.Add(At(new MSym(ch, 'o'), pos - 1));
            }
            b = Wrap(digits);
        }
        else if (c == '\\') b = Command();
        else if (c == 0x221A)
        {
            Next();
            b = new MSqrt(SqrtArg());
        }
        else if (IsOpen(c))
        {
            Next();
            var body = Row(true);
            int close = 0;
            if (pos < src.Length && IsClose(Peek())) { close = Peek(); Next(); }
            b = new MDelim(c, close, new MRow(body));
            spaced = false;
        }
        else if (c == '"' && src.IndexOf('"', pos + 1) is var quote && quote > pos)
        {
            // Text between two quotation marks is written upright as it stands.
            b = new MText(src[(pos + 1)..quote]);
            pos = quote + 1;
        }
        else if (IsLetter(c) && FunctionAt() is { } fn)
        {
            pos += fn.Length;
            b = new MText(fn);
        }
        else
        {
            int m = Map(c);
            b = new MSym(m, ClassOf(m));
            Next();
        }
        At(b, from);
        while (scripts && !spaced && (Peek() == '^' || Peek() == '_'))
        {
            bool sup = Peek() == '^';
            Next();
            var arg = ScriptArg();
            if (b is not MScript sc || (sup ? sc.Sup : sc.Sub) != null) b = sc = new MScript(b);
            if (sup) sc.Sup = arg; else sc.Sub = arg;
            sc.From = from;
            sc.To = pos;
        }
        return b;
    }

    string? FunctionAt()
    {
        if (pos > 0 && IsLetter(src[pos - 1])) return null;
        foreach (string f in Functions)
        {
            if (string.CompareOrdinal(src, pos, f, 0, f.Length) != 0) continue;
            int after = pos + f.Length;
            if (after < src.Length && IsLetter(src[after])) continue;
            return f;
        }
        return null;
    }

    MRow SqrtArg()
    {
        spaced = false;
        int c = Peek();
        return StartsElement(c) && c != '^' && c != '_' ? Strip(Element(false)) : new MRow();
    }

    MathNode Command()
    {
        pos++;
        int start = pos;
        while (pos < src.Length && IsLetter(src[pos])) pos++;
        string name = src[start..pos];
        if (name.Length == 0) return At(new MSym('\\', 'o'), start - 1);
        bool space = pos < src.Length && src[pos] == ' ';
        if (space) pos++;
        if (name == "sqrt") return At(new MSqrt(SqrtArg()), start - 1);
        spaced = space;
        if (Commands.TryGetValue(name, out int cp))
        {
            int m = Map(cp);
            return At(new MSym(m, ClassOf(m)), start - 1);
        }
        return At(new MText(name), start - 1);
    }
}

// ---- the structure in content.json ----

static class MathJson
{
    static string ClassName(char c) => c switch { 'b' => "bin", 'r' => "rel", 'p' => "punct", 'O' => "op", _ => "ord" };
    static char ClassOf(string? s) => s switch { "bin" => 'b', "rel" => 'r', "punct" => 'p', "op" => 'O', _ => 'o' };

    public static void Write(Utf8JsonWriter w, MathNode n)
    {
        switch (n)
        {
            case MRow r:
                w.WriteStartArray();
                foreach (var i in r.Items) Write(w, i);
                w.WriteEndArray();
                return;
            case MSym s:
                w.WriteStartObject();
                w.WriteString("c", char.ConvertFromUtf32(s.Cp));
                if (s.Class != 'o') w.WriteString("k", ClassName(s.Class));
                break;
            case MText t:
                w.WriteStartObject();
                w.WriteString("fn", t.Text);
                break;
            case MFrac f:
                w.WriteStartObject();
                w.WriteStartArray("frac");
                Write(w, f.Num);
                Write(w, f.Den);
                w.WriteEndArray();
                break;
            case MSqrt q:
                w.WriteStartObject();
                w.WritePropertyName("sqrt");
                Write(w, q.Body);
                break;
            case MScript sc:
                w.WriteStartObject();
                w.WritePropertyName("base");
                Write(w, sc.Base);
                if (sc.Sub != null) { w.WritePropertyName("sub"); Write(w, sc.Sub); }
                if (sc.Sup != null) { w.WritePropertyName("sup"); Write(w, sc.Sup); }
                break;
            case MDelim d:
                w.WriteStartObject();
                w.WriteString("open", char.ConvertFromUtf32(d.Open));
                if (d.Close != 0) w.WriteString("close", char.ConvertFromUtf32(d.Close));
                w.WritePropertyName("body");
                Write(w, d.Body);
                break;
            default:
                w.WriteStartObject();
                break;
        }
        w.WriteEndObject();
    }

    static int FirstCp(JsonElement e, string name)
    {
        string? s = e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return string.IsNullOrEmpty(s) ? 0 : char.ConvertToUtf32(s, 0);
    }

    public static MRow ReadRow(JsonElement e) => Read(e) as MRow ?? new MRow(new List<MathNode> { Read(e) });

    public static MathNode Read(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            var items = new List<MathNode>();
            foreach (var i in e.EnumerateArray()) items.Add(Read(i));
            return new MRow(items);
        }
        if (e.ValueKind != JsonValueKind.Object) return new MRow();
        if (e.TryGetProperty("c", out _))
            return new MSym(Math.Max(0x20, FirstCp(e, "c")), ClassOf(e.TryGetProperty("k", out var k) ? k.GetString() : null));
        if (e.TryGetProperty("fn", out var fn)) return new MText(fn.GetString() ?? "");
        if (e.TryGetProperty("frac", out var fr) && fr.ValueKind == JsonValueKind.Array && fr.GetArrayLength() == 2)
            return new MFrac(ReadRow(fr[0]), ReadRow(fr[1]));
        if (e.TryGetProperty("sqrt", out var sq)) return new MSqrt(ReadRow(sq));
        if (e.TryGetProperty("base", out var bs))
        {
            var sc = new MScript(Read(bs));
            if (e.TryGetProperty("sub", out var sub)) sc.Sub = ReadRow(sub);
            if (e.TryGetProperty("sup", out var sup)) sc.Sup = ReadRow(sup);
            return sc;
        }
        if (e.TryGetProperty("body", out var body))
            return new MDelim(Math.Max(0x20, FirstCp(e, "open")), FirstCp(e, "close"), ReadRow(body));
        return new MRow();
    }
}

// ---- from the structure to positions ----

/// <summary>The equation rules of FORMAT.md: boxes with a width, a height above and a depth below the baseline.</summary>
sealed class MathLayout
{
    public const string Family = "Cambria Math";
    public static ResolvedFont Font => FontCatalog.Resolve(Family, false, false);

    sealed class Box
    {
        public int W, H, D;
        public List<Prim> Prims = new();
        /// <summary>Boxes inside, with x to the right and y downward from this box's baseline.</summary>
        public List<(Box B, int X, int Y)> Kids = new();
        /// <summary>What this is the box of.</summary>
        public MathNode? Node;
    }

    readonly FontFace f;
    readonly int size, minusLo, minusHi;

    MathLayout(int s)
    {
        f = Font.Face;
        size = s;
        var (g, _) = f.Lookup(0x2212);
        if (g != 0) (minusLo, minusHi) = f.GlyphY(g);
    }

    /// <summary>Lays an equation out. With marks, the place of every part is noted there as well.</summary>
    public static ObjBox Layout(MRow tree, int sizeEmu, bool display, List<MathMark>? marks = null)
    {
        var top = new MathLayout(sizeEmu).Row(tree, 0, display);
        var o = new ObjBox { W = top.W, H = top.H, D = top.D };
        Emit(top, 0, 0, o.Prims, marks);
        return o;
    }

    static void Emit(Box b, int x, int y, List<Prim> to, List<MathMark>? marks)
    {
        if (marks != null && b.Node != null) marks.Add(new MathMark(b.Node, x, y, b.W, b.H, b.D));
        foreach (var p in b.Prims)
        {
            p.X += x;
            p.Y += y;
            if (p.Pts != null)
                for (int i = 0; i < p.Pts.Length; i += 2) { p.Pts[i] += x; p.Pts[i + 1] += y; }
            to.Add(p);
        }
        foreach (var (k, kx, ky) in b.Kids) Emit(k, x + kx, y + ky, to, marks);
    }

    static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    int SizeAt(int level) => level == 0 ? size : level == 1 ? (int)((long)size * 7 / 10) : size / 2;

    /// <summary>Height of the axis (the middle of the minus sign) and thickness of rules (the thickness of the minus sign).</summary>
    int Axis(int s) => minusHi > minusLo ? (int)U.RoundDiv((long)(minusLo + minusHi) * s, 2L * f.UnitsPerEm) : s / 4;
    int Thick(int s) => minusHi > minusLo ? (int)Math.Max(1, U.RoundDiv((long)(minusHi - minusLo) * s, f.UnitsPerEm)) : Math.Max(1, s / 16);

    (int Adv, int H, int D) Glyph(int cp, int s)
    {
        var (g, adv) = f.Lookup(cp);
        var (lo, hi) = f.GlyphY(g);
        return ((int)U.RoundDiv((long)adv * s, f.UnitsPerEm),
            hi > 0 ? (int)U.RoundDiv((long)hi * s, f.UnitsPerEm) : 0,
            lo < 0 ? (int)U.RoundDiv((long)-lo * s, f.UnitsPerEm) : 0);
    }

    static Prim GlyphPrim(int cp, int x, int y, int s) => new() { Kind = PrimKind.Glyph, Cp = cp, X = x, Y = y, Size = s };

    static char Class(MathNode n) => n switch { MSym s => s.Class, MText => 'O', MScript sc => Class(sc.Base), _ => 'o' };

    static bool TakesLimits(MathNode n) =>
        n is MSym { Cp: 0x2211 or 0x220F or 0x22C3 or 0x22C2 } || n is MText { Text: "lim" or "max" or "min" };

    /// <summary>Space between two neighbours, in eighteenths of the size.</summary>
    static int Space(char l, char r)
    {
        if (l == 'b' || r == 'b') return 4;
        if (l == 'r' || r == 'r') return l == r ? 0 : l == 'p' ? 3 : r == 'p' ? 0 : 5;
        if (l == 'p') return 3;
        if (l == 'O' && (r == 'o' || r == 'O')) return 3;
        if (r == 'O' && l == 'o') return 3;
        return 0;
    }

    Box Lay(MathNode n, int level, bool disp)
    {
        var b = LayCore(n, level, disp);
        b.Node ??= n;
        return b;
    }

    Box LayCore(MathNode n, int level, bool disp) => n switch
    {
        MRow r => Row(r, level, disp),
        MSym s => Sym(s, level, disp),
        MText t => Text(t, level),
        MFrac fr => Frac(fr, level, disp),
        MSqrt q => Sqrt(q, level, disp),
        MScript sc => Script(sc, level, disp),
        MDelim d => Delim(d, level, disp),
        _ => new Box(),
    };

    Box Row(MRow r, int level, bool disp)
    {
        var b = new Box { Node = r };
        int e = SizeAt(level) / 18, x = 0;
        char prev = '\0';
        var items = r.Items;
        for (int i = 0; i < items.Count; i++)
        {
            char k = Class(items[i]);
            if (k == 'b')
            {
                char next = i + 1 < items.Count ? Class(items[i + 1]) : '\0';
                if (prev == '\0' || prev == 'b' || prev == 'r' || prev == 'p' || prev == 'O' || next == '\0' || next == 'r' || next == 'p') k = 'o';
            }
            if (level == 0 && prev != '\0') x += Space(prev, k) * e;
            var c = Lay(items[i], level, disp);
            b.Kids.Add((c, x, 0));
            x += c.W;
            b.H = Math.Max(b.H, c.H);
            b.D = Math.Max(b.D, c.D);
            prev = k;
        }
        b.W = x;
        return b;
    }

    Box Sym(MSym m, int level, bool disp)
    {
        int s = SizeAt(level);
        var b = new Box();
        if (m.Class == 'O')
        {
            // Large operators are centred on the axis, and half as large again in a displayed equation.
            int sz = disp ? (int)((long)s * 3 / 2) : s;
            var (adv, h, d) = Glyph(m.Cp, sz);
            int shift = Axis(s) - FloorDiv(h - d, 2);
            b.Prims.Add(GlyphPrim(m.Cp, 0, -shift, sz));
            b.W = adv;
            b.H = Math.Max(0, h + shift);
            b.D = Math.Max(0, d - shift);
        }
        else
        {
            var (adv, h, d) = Glyph(m.Cp, s);
            b.Prims.Add(GlyphPrim(m.Cp, 0, 0, s));
            b.W = adv;
            b.H = h;
            b.D = d;
        }
        return b;
    }

    Box Text(MText t, int level)
    {
        int s = SizeAt(level);
        var b = new Box();
        foreach (char ch in t.Text)
        {
            var (adv, h, d) = Glyph(ch, s);
            b.Prims.Add(GlyphPrim(ch, b.W, 0, s));
            b.W += adv;
            b.H = Math.Max(b.H, h);
            b.D = Math.Max(b.D, d);
        }
        return b;
    }

    Box Frac(MFrac fr, int level, bool disp)
    {
        int s = SizeAt(level), e = s / 18, t = Thick(s), a = Axis(s);
        int part = disp ? level : Math.Min(level + 1, 2);
        Box n = Row(fr.Num, part, false), dn = Row(fr.Den, part, false);
        int gap = disp ? 3 * t : t + t / 2;
        int inner = Math.Max(n.W, dn.W) + 2 * e;
        int barBottom = a - t / 2, barTop = barBottom + t;
        int yn = barTop + gap + n.D;      // baseline of the numerator, above the baseline
        int yd = barBottom - gap - dn.H;  // baseline of the denominator, above the baseline (negative)
        var b = new Box { W = inner + 2 * e, H = yn + n.H, D = Math.Max(0, dn.D - yd) };
        b.Kids.Add((n, e + (inner - n.W) / 2, -yn));
        b.Kids.Add((dn, e + (inner - dn.W) / 2, -yd));
        b.Prims.Add(new Prim { Kind = PrimKind.Rect, X = e, Y = -barTop, W = inner, H = t });
        return b;
    }

    Box Sqrt(MSqrt q, int level, bool disp)
    {
        int s = SizeAt(level), e = s / 18, t = Thick(s);
        var body = Row(q.Body, level, disp);
        int gap = disp ? 2 * t : t + t / 2;
        int yb = -body.D, yc = body.H + gap + t / 2, hs = yc - yb;
        var b = new Box { W = 11 * e + body.W, H = body.H + gap + t, D = body.D };
        b.Kids.Add((body, 10 * e, 0));
        b.Prims.Add(new Prim
        {
            Kind = PrimKind.Line, Thick = t,
            Pts = new[] { e, -(yb + hs * 2 / 5), 3 * e, -(yb + hs / 2), 6 * e, -yb, 9 * e, -yc, b.W, -yc },
        });
        return b;
    }

    Box Script(MScript sc, int level, bool disp)
    {
        int s = SizeAt(level), e = s / 18, t = Thick(s), sl = Math.Min(level + 1, 2);
        var bs = Lay(sc.Base, level, disp);
        var sup = sc.Sup != null ? Row(sc.Sup, sl, false) : null;
        var sub = sc.Sub != null ? Row(sc.Sub, sl, false) : null;
        var b = new Box { H = bs.H, D = bs.D };
        if (disp && TakesLimits(sc.Base))
        {
            int gl = 3 * e;
            b.W = Math.Max(bs.W, Math.Max(sup?.W ?? 0, sub?.W ?? 0));
            b.Kids.Add((bs, (b.W - bs.W) / 2, 0));
            if (sup != null)
            {
                int y = bs.H + gl + sup.D;
                b.Kids.Add((sup, (b.W - sup.W) / 2, -y));
                b.H = y + sup.H;
            }
            if (sub != null)
            {
                int y = bs.D + gl + sub.H;
                b.Kids.Add((sub, (b.W - sub.W) / 2, y));
                b.D = y + sub.D;
            }
            return b;
        }
        int up = sup != null ? Math.Max(s * 2 / 5, bs.H - s * 3 / 10) : 0;
        int down = sub != null ? Math.Max(s / 5, bs.D + s / 10) : 0;
        if (sup != null && sub != null)
        {
            int clear = up - sup.D - (sub.H - down);
            if (clear < 3 * t) down += 3 * t - clear;
        }
        b.Kids.Add((bs, 0, 0));
        int w = 0;
        if (sup != null)
        {
            b.Kids.Add((sup, bs.W, -up));
            b.H = Math.Max(b.H, up + sup.H);
            w = sup.W;
        }
        if (sub != null)
        {
            b.Kids.Add((sub, bs.W, down));
            b.D = Math.Max(b.D, down + sub.D);
            w = Math.Max(w, sub.W);
        }
        b.W = bs.W + w + e;
        return b;
    }

    Box Delim(MDelim dl, int level, bool disp)
    {
        int s = SizeAt(level), a = Axis(s);
        var body = Row(dl.Body, level, disp);
        var b = new Box { H = body.H, D = body.D };
        void Fence(int cp)
        {
            var (adv, h, d) = Glyph(cp, s);
            int sz = s, shift = 0;
            if (body.H > h || body.D > d)
            {
                // Too small: the bracket is drawn larger, as tall as twice the larger reach from the axis.
                long need = 2L * Math.Max(body.H - a, body.D + a), have = Math.Max(1, h + d);
                sz = (int)Math.Clamp((s * need + have - 1) / have, s, 6L * s);
                (adv, h, d) = Glyph(cp, sz);
                shift = a - FloorDiv(h - d, 2);
            }
            var fb = new Box { W = adv, H = Math.Max(0, h + shift), D = Math.Max(0, d - shift) };
            fb.Prims.Add(GlyphPrim(cp, 0, -shift, sz));
            b.Kids.Add((fb, b.W, 0));
            b.W += adv;
            b.H = Math.Max(b.H, fb.H);
            b.D = Math.Max(b.D, fb.D);
        }
        Fence(dl.Open);
        b.Kids.Add((body, b.W, 0));
        b.W += body.W;
        if (dl.Close != 0) Fence(dl.Close);
        return b;
    }
}
