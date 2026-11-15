using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReiksDocument;

/// <summary>A work the document refers to. Authors are written family name first: "Smith, Anna".</summary>
sealed record Source(string Id, IReadOnlyList<string> Authors, string Year, string Title, string In, string Url);

/// <summary>A citation standing in the text: one item that shows the text worked out for its source.</summary>
sealed record Cite(string Source) : Inline(false);

/// <summary>What citations read (FORMAT.md §4.5) and what the list of references is made of.</summary>
static class Citations
{
    public const string Numbered = "numbered", ByName = "author-year", Harvard = "harvard", Chicago = "chicago", Mla = "mla", Ieee = "ieee", Vancouver = "vancouver";

    /// <summary>The reference systems: what the document calls each, its name, and how a citation of two authors looks in it.
    /// The first is what a document has that names none.</summary>
    public static readonly (string Id, string Name, string Example)[] Systems =
    {
        (ByName, "APA", "(Smith & Jones, 2020)"),
        (Harvard, "Harvard", "(Smith and Jones 2020)"),
        (Chicago, "Chicago (author and date)", "(Smith and Jones 2020)"),
        (Mla, "MLA", "(Smith and Jones)"),
        (Ieee, "IEEE", "[1]"),
        (Vancouver, "Vancouver", "[1]"),
        (Numbered, "Numbers, plain", "[1]"),
    };

    /// <summary>The system a name stands for; APA for a name that is none.</summary>
    public static string Known(string? style) => Systems.Any(x => x.Id == style) ? style! : ByName;

    /// <summary>True for the systems whose citations are numbers.</summary>
    public static bool ByNumber(string style) => style is Numbered or Ieee or Vancouver;

    /// <summary>The family name of an author: what stands before the first comma, or the whole name.</summary>
    public static string FamilyName(string author)
    {
        int k = author.IndexOf(',');
        return (k >= 0 ? author[..k] : author).Trim();
    }

    /// <summary>The authors as a citation names them: one, two with an ampersand, or the first "et al."; without authors the title.</summary>
    public static string ShortAuthors(Source s) => s.Authors.Count switch
    {
        0 => s.Title,
        1 => FamilyName(s.Authors[0]),
        2 => FamilyName(s.Authors[0]) + " & " + FamilyName(s.Authors[1]),
        _ => FamilyName(s.Authors[0]) + " et al.",
    };

    /// <summary>The family names with "and": one, two, with three as the most also three ("A, B, and C"), beyond the most the
    /// first "et al."; without authors the title.</summary>
    public static string Names(Source s, int most)
    {
        var f = s.Authors.Select(FamilyName).ToList();
        return f.Count switch
        {
            0 => s.Title,
            1 => f[0],
            2 => f[0] + " and " + f[1],
            3 when most >= 3 => f[0] + ", " + f[1] + ", and " + f[2],
            _ => f[0] + " et al.",
        };
    }

    public static string Text(Document doc, Cite c, IReadOnlyDictionary<string, int> numbers)
    {
        var s = doc.Sources.Find(x => x.Id == c.Source);
        if (s == null) return "[?]";
        if (ByNumber(doc.CiteStyle)) return "[" + numbers.GetValueOrDefault(s.Id).ToString(CultureInfo.InvariantCulture) + "]";
        string year = s.Year.Length > 0 ? s.Year : "n.d.";
        return doc.CiteStyle switch
        {
            Harvard => "(" + Names(s, 2) + " " + year + ")",
            Chicago => "(" + Names(s, 3) + " " + year + ")",
            Mla => "(" + Names(s, 2) + ")",
            _ => "(" + ShortAuthors(s) + ", " + year + ")",
        };
    }

    /// <summary>The sources the body cites, in the order of their first citation.</summary>
    public static List<Source> Cited(Document doc)
    {
        var list = new List<Source>();
        foreach (var p in doc.Body)
            foreach (var (_, o) in p.Objects)
                if (o is Cite c && doc.Sources.Find(x => x.Id == c.Source) is { } s && !list.Contains(s)) list.Add(s);
        return list;
    }

    /// <summary>"Smith, Anna Maria" becomes "Smith, A. M."; a name without a comma stays as it is.</summary>
    static string WithInitials(string author)
    {
        int k = author.IndexOf(',');
        if (k < 0) return author.Trim();
        var parts = author[(k + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(g => string.Join("-", g.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(x => char.ToUpperInvariant(x[0]) + ".")));
        string initials = string.Join(" ", parts);
        return initials.Length > 0 ? FamilyName(author) + ", " + initials : FamilyName(author);
    }

    static string AuthorList(Source s)
    {
        var a = s.Authors.Select(WithInitials).ToList();
        return a.Count switch
        {
            0 => "",
            1 => a[0],
            _ => string.Join(", ", a.Take(a.Count - 1)) + ", & " + a[^1],
        };
    }

    /// <summary>The given names of an author ("Anna Maria" of "Smith, Anna Maria"); empty for a name without a comma.</summary>
    static string Given(string author)
    {
        int k = author.IndexOf(',');
        return k < 0 ? "" : author[(k + 1)..].Trim();
    }

    /// <summary>The first letters of the given names, each followed by `after` and with `between` between two: "A. M.", "A.M." or "AM".</summary>
    static string Initials(string author, string after, string between) =>
        string.Join(between, Given(author).Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries).Select(x => char.ToUpperInvariant(x[0]) + after));

    static string Joined(List<string> a, string two, string last) => a.Count switch
    {
        0 => "",
        1 => a[0],
        2 => a[0] + two + a[1],
        _ => string.Join(", ", a.Take(a.Count - 1)) + last + a[^1],
    };

    /// <summary>"Smith, Anna Maria" as it is; one without a comma as it is.</summary>
    static string Turned(string author) => Given(author).Length > 0 ? FamilyName(author) + ", " + Given(author) : author.Trim();

    /// <summary>"Anna Maria Smith".</summary>
    static string Straight(string author) => Given(author).Length > 0 ? Given(author) + " " + FamilyName(author) : author.Trim();

    /// <summary>The authors of an entry of the list of references, as the system writes them.</summary>
    static string RefAuthors(Source s, string style)
    {
        var a = s.Authors;
        if (a.Count == 0) return "";
        switch (style)
        {
            case Harvard:
                return Joined(a.Select(x => Given(x).Length > 0 ? FamilyName(x) + ", " + Initials(x, ".", "") : x.Trim()).ToList(), " and ", " and ");
            case Chicago:
                return Joined(a.Select((x, i) => i == 0 ? Turned(x) : Straight(x)).ToList(), ", and ", ", and ");
            case Mla:
                return a.Count >= 3 ? Turned(a[0]) + ", et al" : Joined(a.Select((x, i) => i == 0 ? Turned(x) : Straight(x)).ToList(), ", and ", ", and ");
            case Ieee:
                return Joined(a.Select(x => Given(x).Length > 0 ? Initials(x, ".", " ") + " " + FamilyName(x) : x.Trim()).ToList(), " and ", ", and ");
            case Vancouver:
                return string.Join(", ", a.Select(x => Given(x).Length > 0 ? FamilyName(x) + " " + Initials(x, "", "") : x.Trim()));
            case Numbered:
                return string.Join("; ", a);
            default:
                return AuthorList(s);
        }
    }

    /// <summary>The heading of the list of references in a system.</summary>
    public static string RefsHeading(string style) => style == Mla ? "Works Cited" : style == Harvard ? "Reference list" : "References";

    /// <summary>The paragraphs of the list of references: a heading and one paragraph for each cited source, written as the
    /// reference system of the document has it. Where citations are numbers the entries are in the order of the numbers,
    /// otherwise in the order of the alphabet.</summary>
    public static List<Paragraph> BuildRefs(Document doc)
    {
        string style = Known(doc.CiteStyle);
        bool numbered = ByNumber(style);
        var cited = Cited(doc);
        if (!numbered)
            cited = cited.OrderBy(ShortAuthors, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Year, StringComparer.Ordinal).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var list = new List<Paragraph>
        {
            new() { Style = doc.FindPara("Heading1") != null ? "Heading1" : doc.ParaStyles[0].Id, Refs = true, Runs = { new Run(RefsHeading(style)) } },
        };
        for (int i = 0; i < cited.Count; i++)
        {
            var s = cited[i];
            string n = (i + 1).ToString(CultureInfo.InvariantCulture), who = RefAuthors(s, style), year = s.Year.Length > 0 ? s.Year : "n.d.";
            // What stands behind the title: where it appeared, with the year where the system has it there.
            string InAnd(string before) => s.In.Length > 0 ? " " + s.In + (s.Year.Length > 0 ? before + s.Year : "") + "." : s.Year.Length > 0 ? " " + s.Year + "." : "";
            string head, tail;
            bool italic = true;
            switch (style)
            {
                case Numbered:
                    head = "[" + n + "] " + (who.Length > 0 ? who + ". " : "");
                    tail = InAnd(", ") + (s.Url.Length > 0 ? " " + s.Url : "");
                    break;
                case Ieee:
                    head = "[" + n + "] " + (who.Length > 0 ? who + ", " : "");
                    tail = InAnd(", ") + (s.Url.Length > 0 ? " [Online]. Available: " + s.Url : "");
                    break;
                case Vancouver:
                    head = n + ". " + (who.Length > 0 ? who + ". " : "");
                    tail = InAnd("; ") + (s.Url.Length > 0 ? " Available from: " + s.Url : "");
                    italic = false;
                    break;
                case Mla:
                    head = who.Length > 0 ? who.TrimEnd('.') + ". " : "";
                    tail = InAnd(", ") + (s.Url.Length > 0 ? " " + s.Url : "");
                    break;
                case Chicago:
                    head = (who.Length > 0 ? who.TrimEnd('.') + ". " : "") + year.TrimEnd('.') + ". ";
                    tail = (s.In.Length > 0 ? " " + s.In + "." : "") + (s.Url.Length > 0 ? " " + s.Url : "");
                    break;
                case Harvard:
                    head = (who.Length > 0 ? who + " " : "") + "(" + year + ") ";
                    tail = (s.In.Length > 0 ? " " + s.In + "." : "") + (s.Url.Length > 0 ? " Available at: " + s.Url : "");
                    break;
                default:
                    head = (who.Length > 0 ? who + " " : "") + "(" + year + "). ";
                    tail = (s.In.Length > 0 ? " " + s.In + "." : "") + (s.Url.Length > 0 ? " " + s.Url : "");
                    break;
            }
            // Without authors the title stands first where the authors would, and the year follows it.
            if (who.Length == 0 && s.Title.Length > 0 && style is not (Numbered or Ieee or Vancouver or Mla))
            {
                tail = " " + head.TrimEnd() + tail;
                head = "";
            }
            var p = new Paragraph { Style = doc.ParaStyles[0].Id, Refs = true };
            if (head.Length > 0) p.Runs.Add(new Run(head));
            if (s.Title.Length > 0)
            {
                p.Runs.Add(italic ? new Run(s.Title, new CharProps(Italic: true)) : new Run(s.Title));
                tail = "." + tail;
            }
            if (tail.Length > 0) p.Runs.Add(new Run(tail));
            list.Add(p);
        }
        return list;
    }
}
