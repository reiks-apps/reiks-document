using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReiksDocument;

/// <summary>Mathematics written as LaTeX: read as the line this program's equations are typed in, and found in a text.</summary>
static class Latex
{
    /// <summary>Names for one character that the typed line has no name for.</summary>
    static readonly Dictionary<string, string> Chars = new()
    {
        { "geqslant", "≥" }, { "leqslant", "≤" }, { "ast", "*" }, { "star", "⋆" }, { "circ", "∘" }, { "bullet", "∙" },
        { "wedge", "∧" }, { "land", "∧" }, { "vee", "∨" }, { "lor", "∨" }, { "oplus", "⊕" }, { "otimes", "⊗" },
        { "ll", "≪" }, { "gg", "≫" }, { "mid", "|" }, { "vert", "|" }, { "lvert", "|" }, { "rvert", "|" },
        { "Vert", "‖" }, { "lVert", "‖" }, { "rVert", "‖" }, { "parallel", "∥" }, { "langle", "⟨" }, { "rangle", "⟩" },
        { "dots", "…" }, { "dotsc", "…" }, { "dotsb", "⋯" }, { "vdots", "⋮" }, { "ddots", "⋱" },
        { "implies", "⇒" }, { "iff", "⇔" }, { "Leftarrow", "⇐" }, { "mapsto", "↦" }, { "longrightarrow", "→" },
        { "Longrightarrow", "⇒" }, { "gets", "←" }, { "uparrow", "↑" }, { "downarrow", "↓" },
        { "varepsilon", "ε" }, { "varphi", "φ" }, { "vartheta", "ϑ" }, { "varrho", "ρ" }, { "varsigma", "ς" }, { "Upsilon", "Υ" },
        { "lbrace", "{" }, { "rbrace", "}" }, { "lbrack", "[" }, { "rbrack", "]" }, { "colon", ":" }, { "setminus", "∖" },
        { "neg", "¬" }, { "lnot", "¬" }, { "cong", "≅" }, { "simeq", "≃" }, { "supset", "⊃" }, { "supseteq", "⊇" }, { "ni", "∋" },
        { "lfloor", "⌊" }, { "rfloor", "⌋" }, { "lceil", "⌈" }, { "rceil", "⌉" }, { "aleph", "ℵ" }, { "triangle", "△" },
        { "therefore", "∴" }, { "because", "∵" }, { "bigcup", "⋃" }, { "bigcap", "⋂" }, { "lt", "<" }, { "gt", ">" },
    };

    /// <summary>Their argument is text, written upright as it stands.</summary>
    static readonly HashSet<string> TextLike = new()
    {
        "text", "mathrm", "textrm", "operatorname", "mbox", "hbox", "textnormal", "textit", "textbf", "mathsf", "texttt", "textsf", "textup", "mathtt", "intertext",
    };

    /// <summary>They change the look of their argument or put a mark on it: the argument alone is kept.</summary>
    static readonly HashSet<string> Through = new()
    {
        "mathbf", "mathit", "boldsymbol", "bm", "mathbb", "mathcal", "mathfrak", "mathscr", "vec", "hat", "bar", "overline", "underline", "tilde",
        "dot", "ddot", "widehat", "widetilde", "overrightarrow", "overleftarrow", "boxed", "underbrace", "overbrace", "cancel",
    };

    static readonly HashSet<string> Nothing = new()
    {
        "displaystyle", "textstyle", "scriptstyle", "scriptscriptstyle", "limits", "nolimits", "big", "Big", "bigg", "Bigg", "bigl", "bigr", "Bigl", "Bigr",
        "biggl", "biggr", "Biggl", "Biggr", "bigm", "Bigm", "left", "right", "middle", "nonumber", "notag", "hfill", "hline", "centering", "noindent",
        "mathstrut", "strut", "allowbreak", "nobreak",
    };

    /// <summary>Left out together with their argument.</summary>
    static readonly HashSet<string> Dropped = new() { "label", "tag", "hspace", "vspace", "phantom", "hphantom", "vphantom", "color" };

    static readonly HashSet<string> Spaces = new() { "enspace", "enskip", "thinspace", "medspace", "thickspace", "space", "nobreakspace" };

    /// <summary>Names the typed line does not know and writes upright, as they should be.</summary>
    static readonly HashSet<string> Upright = new() { "arg", "deg", "dim", "hom", "ker", "lg", "sup", "inf", "Pr", "liminf", "limsup", "coth" };

    static readonly HashSet<string> Fn = new(MathParser.FunctionNames);
    static readonly HashSet<string> Known = new(MathParser.CommandNames);

    static bool Letter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    sealed class Reader
    {
        readonly string s;
        int i, depth, matrix;
        public bool Ok = true;

        public Reader(string tex) { s = tex; }

        void SkipSpaces() { while (i < s.Length && s[i] == ' ') i++; }

        bool ScriptNext()
        {
            int j = i;
            while (j < s.Length && s[j] == ' ') j++;
            return j < s.Length && (s[j] == '^' || s[j] == '_');
        }

        /// <summary>Adds a piece. A letter behind a name would become part of the name, and the first space behind a name only ends it;
        /// a name of a function has to stand free of other letters.</summary>
        static void Add(StringBuilder sb, string t)
        {
            if (t.Length == 0) return;
            int k = sb.Length;
            while (k > 0 && Letter(sb[k - 1])) k--;
            if (k < sb.Length)
            {
                bool name = k > 0 && sb[k - 1] == '\\';
                if (name && (Letter(t[0]) || t[0] == ' ')) sb.Append(' ');
                else if (!name && Letter(t[0]) && (Fn.Contains(sb.ToString(k, sb.Length - k)) || Fn.Contains(t))) sb.Append(' ');
            }
            sb.Append(t);
        }

        /// <summary>What can carry an exponent as one element: as it is, or in brackets.</summary>
        static string One(string inner)
        {
            string t = inner.Trim();
            if (t.Length <= 1) return t;
            if (t.All(c => c is >= '0' and <= '9' or '.')) return t;
            if (t[0] == '\\' && t.Skip(1).All(Letter)) return t;
            if (t[0] == '"' && t.IndexOf('"', 1) == t.Length - 1) return t;
            if (t[0] is '(' or '[')
            {
                int level = 0;
                for (int k = 0; k < t.Length; k++)
                {
                    if (t[k] is '(' or '[' or '{') level++;
                    else if (t[k] is ')' or ']' or '}') { level--; if (level == 0) { if (k == t.Length - 1) return t; break; } }
                }
            }
            return "(" + t + ")";
        }

        string Grouped(string inner) => ScriptNext() ? One(inner) : inner;

        void Close()
        {
            if (i < s.Length && s[i] == '}') i++;
            else Ok = false;
        }

        public string Row(char until)
        {
            var sb = new StringBuilder();
            depth++;
            while (i < s.Length && Ok)
            {
                char c = s[i];
                if (c == '}')
                {
                    if (until != '}') Ok = false;
                    break;
                }
                if (c == ']' && until == ']') break;
                if (c == '^' || c == '_')
                {
                    i++;
                    string arg = Arg();
                    while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                    if (c == '^' && arg == "∘") { sb.Append('°'); continue; }
                    sb.Append(c).Append('(').Append(arg).Append(')');
                    if (ScriptNext()) SkipSpaces();
                    else sb.Append(' ');
                    continue;
                }
                Add(sb, Piece());
            }
            depth--;
            if (until == '\0' && i < s.Length) Ok = false;
            return sb.ToString();
        }

        /// <summary>One argument: what stands in braces, or the one thing that follows.</summary>
        string Arg()
        {
            SkipSpaces();
            if (i >= s.Length || s[i] == '}') return "";
            if (s[i] == '{')
            {
                i++;
                string inner = Row('}');
                Close();
                return inner.Trim();
            }
            return Piece().Trim();
        }

        /// <summary>An argument as it is written, not read as mathematics.</summary>
        string RawArg()
        {
            SkipSpaces();
            if (i >= s.Length || s[i] != '{') { Ok = false; return ""; }
            int level = 0, a = ++i;
            for (; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { i++; continue; }
                if (s[i] == '{') level++;
                else if (s[i] == '}')
                {
                    if (level == 0) break;
                    level--;
                }
            }
            if (i >= s.Length) { Ok = false; return ""; }
            string raw = s[a..i];
            i++;
            return raw;
        }

        static string Plain(string raw)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < raw.Length; k++)
            {
                char c = raw[k];
                if (c == '\\')
                {
                    if (k + 1 < raw.Length && !Letter(raw[k + 1])) { sb.Append(raw[++k] == '\\' ? ' ' : raw[k]); continue; }
                    while (k + 1 < raw.Length && Letter(raw[k + 1])) k++;
                    continue;
                }
                if (c is '{' or '}' or '$' or '"') continue;
                sb.Append(c == '~' || c < ' ' || c == Document.LineBreak ? ' ' : c);
            }
            return sb.ToString();
        }

        string Piece()
        {
            char c = s[i];
            if (c == '{')
            {
                i++;
                string inner = Row('}');
                Close();
                return Grouped(inner);
            }
            if (c == '\\') return Command();
            i++;
            switch (c)
            {
                case '~': return " ";
                case '&': return matrix > 0 ? ", " : "";
                case '\'': return "′";
                case '"':
                case '$': return "";
                case ' ':
                case '\t':
                case '\u00A0':
                    while (i < s.Length && s[i] is ' ' or '\t' or '\u00A0') i++;
                    return " ";
            }
            if (c < ' ' || c == Run.ObjectChar) { Ok = false; return ""; }
            if (char.IsHighSurrogate(c) && i < s.Length && char.IsLowSurrogate(s[i])) return new string(new[] { c, s[i++] });
            return c.ToString();
        }

        string Command()
        {
            i++;
            if (i >= s.Length) return "";
            char c = s[i];
            if (!Letter(c))
            {
                i++;
                switch (c)
                {
                    case ',': return "\u2009";
                    case ':': case ';': case '>': return "\u2005";
                    case ' ': return " ";
                    case '!': return "";
                    case '{': return "{";
                    case '}': return "}";
                    case '|': return "‖";
                    case '%': case '$': case '#': case '&': return c.ToString();
                    case '\\':
                        if (i < s.Length && s[i] == '*') i++;
                        if (i < s.Length && s[i] == '[')
                        {
                            int e = s.IndexOf(']', i);
                            if (e > 0 && e - i < 12) i = e + 1;
                        }
                        return matrix > 0 ? "; " : depth == 1 ? "\n" : " ";
                }
                Ok = false;
                return "";
            }
            int a = i;
            while (i < s.Length && Letter(s[i])) i++;
            string name = s[a..i];
            if (i < s.Length && s[i] == '*' && name is "operatorname" or "tag") i++;
            switch (name)
            {
                case "frac": case "dfrac": case "tfrac": case "cfrac":
                {
                    string n = Arg(), d = Arg();
                    return " (" + n + ")/(" + d + ") ";
                }
                case "binom": case "dbinom": case "tbinom":
                {
                    string n = Arg(), k = Arg();
                    return "(" + n + ", " + k + ")";
                }
                case "sqrt":
                {
                    SkipSpaces();
                    string deg = "";
                    if (i < s.Length && s[i] == '[')
                    {
                        i++;
                        deg = Row(']').Trim();
                        if (i < s.Length) i++;
                    }
                    string body = Arg();
                    return deg is "" or "2" ? "\\sqrt(" + body + ")" : " (" + body + ")^(1/" + One(deg) + ") ";
                }
                case "pmod": return "(\"mod \"" + Arg() + ")";
                case "bmod": case "mod": return "\" mod \"";
                case "quad": return "\"  \"";
                case "qquad": return "\"    \"";
                case "not":
                {
                    string next = Arg();
                    string? no = next switch { "=" => "≠", "\\in" => "∉", "<" => "≮", ">" => "≯", "\\equiv" => "≢", "\\approx" => "≉", "\\subset" => "⊄", "|" => "∤", _ => null };
                    if (no == null) Ok = false;
                    return no ?? "";
                }
                case "begin":
                case "end":
                {
                    bool begin = name == "begin";
                    string env = RawArg().TrimEnd('*');
                    switch (env)
                    {
                        case "matrix": case "smallmatrix": case "pmatrix": matrix += begin ? 1 : -1; return begin ? "(" : ")";
                        case "bmatrix": matrix += begin ? 1 : -1; return begin ? "[" : "]";
                        case "Bmatrix": matrix += begin ? 1 : -1; return begin ? "{" : "}";
                        case "vmatrix": matrix += begin ? 1 : -1; return "|";
                        case "cases": matrix += begin ? 1 : -1; return begin ? "{" : "";
                        case "array":
                            matrix += begin ? 1 : -1;
                            SkipSpaces();
                            if (begin && i < s.Length && s[i] == '{') RawArg();
                            return "";
                        case "aligned": case "align": case "alignat": case "equation": case "gather": case "gathered": case "split":
                        case "multline": case "eqnarray": case "displaymath": case "math": case "flalign":
                            return "";
                    }
                    Ok = false;
                    return "";
                }
            }
            if (TextLike.Contains(name))
            {
                string t = Plain(RawArg());
                return t.Length == 0 ? "" : "\"" + t + "\"";
            }
            if (name == "mathbb")
            {
                string set = Arg();
                return set switch { "R" => "ℝ", "N" => "ℕ", "Z" => "ℤ", "Q" => "ℚ", "C" => "ℂ", _ => Grouped(set) };
            }
            if (Through.Contains(name)) return Grouped(Arg());
            if (Nothing.Contains(name))
            {
                if (name is "left" or "right" or "middle")
                {
                    SkipSpaces();
                    if (i < s.Length && s[i] == '.') i++;
                }
                return "";
            }
            if (Dropped.Contains(name)) { RawArg(); return ""; }
            if (Spaces.Contains(name)) return " ";
            if (Chars.TryGetValue(name, out var ch)) return ch;
            if (Fn.Contains(name)) return name;
            if (Upright.Contains(name) || Known.Contains(name)) return "\\" + name;
            Ok = false;
            return "";
        }
    }

    /// <summary>One space where several stand, outside of written text. Between a name and a bracket two stay: the first
    /// belongs to the name, and without the second the two would be read as one thing.</summary>
    static string Tidy(string line)
    {
        line = line.Trim();
        var sb = new StringBuilder();
        bool quote = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') quote = !quote;
            if (c == ' ' && !quote && sb.Length > 0 && sb[^1] == ' ')
            {
                int k = sb.Length - 1;
                while (k > 0 && Letter(sb[k - 1])) k--;
                bool name = k < sb.Length - 1 && k > 0 && sb[k - 1] == '\\';
                int next = i;
                while (next < line.Length && line[next] == ' ') next++;
                if (!name || next >= line.Length || line[next] is not ('(' or '[' or '{')) continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>LaTeX as lines for the equation box: one line, or one for each row of a set of rows. Null if it is not understood.</summary>
    public static List<string>? ToLines(string tex)
    {
        var r = new Reader(tex.Trim());
        string all = r.Row('\0');
        if (!r.Ok) return null;
        var lines = all.Split('\n').Select(Tidy).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? null : lines;
    }

    static readonly Regex Marker = new(@"\\[A-Za-z]{2,}|[\^_]\{", RegexOptions.Compiled);
    static readonly Regex NotWords = new(@"\\(text|mathrm|textrm|operatorname|mbox|textnormal|textit|textbf)\s*\{[^{}]*\}|\\[A-Za-z]+", RegexOptions.Compiled);
    static readonly Regex Prose = new(@"\p{L}{3,}[ \u00A0]+\p{L}{2,}|\p{L}{2,}[ \u00A0]+\p{L}{3,}", RegexOptions.Compiled);

    /// <summary>A line that is nothing but mathematics in LaTeX, with no marks around it: it has a sign of LaTeX, no two words
    /// of a sentence side by side, and every part of it is understood.</summary>
    static List<string>? Bare(string line)
    {
        string t = line.Trim();
        if (t.Length < 3 || t.Contains('$') || !Marker.IsMatch(t)) return null;
        if (Prose.IsMatch(NotWords.Replace(t, "|"))) return null;
        return ToLines(t);
    }

    static readonly Regex Opens = new(@"\\begin\s*\{", RegexOptions.Compiled);
    static readonly Regex Closes = new(@"\\end\s*\{", RegexOptions.Compiled);

    /// <summary>Mathematics written over several lines: the last line of what opens in line k with \begin, \[ or $$
    /// and closes in a later line. k itself if nothing of the kind opens there, or it never closes.</summary>
    public static int LastOfGroup(IReadOnlyList<string> lines, int k)
    {
        string t = lines[k].Trim();
        int open = Opens.Matches(t).Count - Closes.Matches(t).Count;
        if (open > 0)
        {
            for (int j = k + 1; j < lines.Count && j <= k + 80; j++)
            {
                open += Opens.Matches(lines[j]).Count - Closes.Matches(lines[j]).Count;
                if (open <= 0) return j;
            }
            return k;
        }
        string? close = t.StartsWith("\\[", StringComparison.Ordinal) && !t.Contains("\\]") ? "\\]"
            : t.StartsWith("$$", StringComparison.Ordinal) && t.IndexOf("$$", 2, StringComparison.Ordinal) < 0 ? "$$" : null;
        if (close == null) return k;
        for (int j = k + 1; j < lines.Count && j <= k + 80; j++)
            if (lines[j].Contains(close)) return j;
        return k;
    }

    /// <summary>What stands between $ … $, \( … \), $$ … $$ or \[ … \]. A single $ counts only where it touches what it holds,
    /// so that prices are left alone.</summary>
    static IEnumerable<(int From, int To, string Tex, bool Display)> Marked(string t)
    {
        int i = 0;
        while (i < t.Length)
        {
            char c = t[i];
            if (c == '\\' && i + 1 < t.Length && t[i + 1] is '[' or '(')
            {
                bool display = t[i + 1] == '[';
                int e = t.IndexOf(display ? "\\]" : "\\)", i + 2, StringComparison.Ordinal);
                if (e > i + 2)
                {
                    yield return (i, e + 2, t[(i + 2)..e], display);
                    i = e + 2;
                    continue;
                }
            }
            if (c == '\\') { i += 2; continue; }
            if (c != '$') { i++; continue; }
            bool two = i + 1 < t.Length && t[i + 1] == '$';
            int a = i + (two ? 2 : 1), end = a;
            while (end < t.Length && !(t[end] == '$' && (!two || end + 1 < t.Length && t[end + 1] == '$'))) end += t[end] == '\\' ? 2 : 1;
            if (end < t.Length && end > a)
            {
                string tex = t[a..end];
                int after = end + (two ? 2 : 1);
                if (two || !char.IsWhiteSpace(tex[0]) && !char.IsWhiteSpace(tex[^1]) && !(after < t.Length && char.IsDigit(t[after])))
                {
                    yield return (i, after, tex, two);
                    i = after;
                    continue;
                }
            }
            i = a;
        }
    }

    /// <summary>Makes equations of the LaTeX in a paragraph: of a line that is nothing else, and of what stands between
    /// $ … $, \( … \), $$ … $$ or \[ … \]. With blocks, an equation that had a line to itself gets a line of its own. Returns how many it made.</summary>
    public static int Apply(Paragraph p, bool blocks)
    {
        string text = p.Text;
        if (p.Table != null || text.IndexOf('\\') < 0 && text.IndexOf('$') < 0 && !text.Contains("^{") && !text.Contains("_{")) return 0;
        var found = new List<(int From, int To, List<string> Lines, bool Block)>();
        int at = 0;
        string[] parts = text.Split(Document.LineBreak);
        for (int k = 0; k < parts.Length; k++)
        {
            // Rows between \begin and \end on lines of their own are read as one; a space stands where each line ended.
            int last = LastOfGroup(parts, k);
            string line = last > k ? string.Join(" ", parts[k..(last + 1)]) : parts[k];
            k = last;
            int end = at + line.Length;
            if (line.IndexOf(Run.ObjectChar) < 0 && Bare(line) is { } lines)
                found.Add((at, blocks && end < text.Length ? end + 1 : end, lines, true));
            else
                foreach (var m in Marked(line))
                    if (ToLines(m.Tex) is { } inner) found.Add((at + m.From, at + m.To, inner, m.Display));
            at = end + 1;
        }
        if (found.Count == 0) return 0;
        var runs = new List<Run>();
        int pos = 0, made = 0;
        foreach (var f in found)
        {
            runs.AddRange(p.Slice(pos, f.From));
            var first = p.Slice(f.From, f.From + 1);
            var format = new Run("", new CharProps(Size: first.Count > 0 ? first[0].Props.Size : null));
            bool block = f.Block && blocks;
            for (int k = 0; k < f.Lines.Count; k++)
            {
                if (k > 0 && !block) runs.Add(format with { Text = Document.LineBreak.ToString() });
                runs.Add(Run.Object(Equation.From(f.Lines[k], block), format));
                made++;
            }
            pos = f.To;
        }
        runs.AddRange(p.Slice(pos, text.Length));
        p.Runs = runs;
        p.Touch();
        p.Normalize();
        return made;
    }
}
