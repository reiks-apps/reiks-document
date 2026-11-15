using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ReiksDocument;

/// <summary>Finds the places inside words where a line may end with a hyphen. The places are kept with each paragraph
/// and written into the file, so that the layout of a document never depends on the rules of the program that reads it.
/// Spanish goes by its rules for syllables, which need no word list. Other languages go by lists of patterns:
/// plain text files in the folder "hyph" beside the program, read as data and nothing else.</summary>
static class Hyphenator
{
    /// <summary>The folder with the pattern files: languages.txt names the languages (id|name|least letters before|least letters behind),
    /// hyph-id.pat.txt holds the patterns of one, hyph-id.hyp.txt (if there) the words divided by hand.</summary>
    public static string Folder = Path.Combine(AppContext.BaseDirectory, "hyph");

    sealed class Node
    {
        public Dictionary<char, Node>? Next;
        public byte[]? Levels;
    }

    sealed class Patterns
    {
        public readonly Node Root = new();
        public readonly Dictionary<string, int[]> ByHand = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int[]> Known = new(StringComparer.Ordinal);
        public int Left = 2, Right = 3;
    }

    static (string Id, string Name)[]? languages;
    static readonly Dictionary<string, (int Left, int Right)> least = new();
    static readonly Dictionary<string, Patterns?> loaded = new();

    /// <summary>The languages words can be divided in on this PC: what a document names, and what the user reads.</summary>
    public static (string Id, string Name)[] Languages => languages ??= Find();

    public static bool Knows(string id) => Array.Exists(Languages, l => l.Id == id);

    /// <summary>Forgets what was read, so that the folder is looked at again.</summary>
    public static void Reload()
    {
        languages = null;
        least.Clear();
        loaded.Clear();
    }

    static (string, string)[] Find()
    {
        var list = new List<(string, string)> { ("es", "Spanish") };
        try
        {
            string index = Path.Combine(Folder, "languages.txt");
            if (File.Exists(index))
                foreach (string raw in File.ReadAllLines(index, Encoding.UTF8))
                {
                    string[] f = raw.Split('|');
                    if (raw.StartsWith('#') || f.Length < 2) continue;
                    string id = f[0].Trim(), name = f[1].Trim();
                    if (id.Length == 0 || id.Length > 20 || name.Length == 0 || id == "es" || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                    if (!File.Exists(Path.Combine(Folder, "hyph-" + id + ".pat.txt")) || list.Exists(l => l.Item1 == id)) continue;
                    list.Add((id, name));
                    least[id] = (f.Length > 2 && int.TryParse(f[2], out int l1) ? Math.Clamp(l1, 1, 5) : 2, f.Length > 3 && int.TryParse(f[3], out int r1) ? Math.Clamp(r1, 1, 5) : 3);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.ToArray();
    }

    /// <summary>The patterns of a language, read once. A pattern is letters with digits between them, as in "a1b" or ".ex5am":
    /// a digit says how much a break at that place is wanted (odd) or unwanted (even); the dot stands for the edge of the word.</summary>
    static Patterns? Load(string id)
    {
        if (loaded.TryGetValue(id, out var have)) return have;
        Patterns? p = null;
        try
        {
            string file = Path.Combine(Folder, "hyph-" + id + ".pat.txt");
            if (Knows(id) && File.Exists(file) && new FileInfo(file).Length < 8 << 20)
            {
                p = new Patterns();
                if (least.TryGetValue(id, out var m)) (p.Left, p.Right) = m;
                var levels = new List<byte>();
                var letters = new StringBuilder();
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                    foreach (string word in raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (word[0] == '%' || word[0] == '#') break;
                        letters.Clear();
                        levels.Clear();
                        levels.Add(0);
                        foreach (char c in word.ToLowerInvariant())
                            if (c >= '0' && c <= '9') levels[^1] = (byte)(c - '0');
                            else
                            {
                                letters.Append(c);
                                levels.Add(0);
                            }
                        if (letters.Length == 0 || letters.Length > 40) continue;
                        var node = p.Root;
                        foreach (char c in letters.ToString())
                        {
                            node.Next ??= new Dictionary<char, Node>();
                            if (!node.Next.TryGetValue(c, out var next)) node.Next[c] = next = new Node();
                            node = next;
                        }
                        node.Levels = levels.ToArray();
                    }
                string byHand = Path.Combine(Folder, "hyph-" + id + ".hyp.txt");
                if (File.Exists(byHand) && new FileInfo(byHand).Length < 8 << 20)
                    foreach (string raw in File.ReadAllLines(byHand, Encoding.UTF8))
                    {
                        string word = raw.Trim().ToLowerInvariant();
                        if (word.Length == 0 || word[0] == '%' || word[0] == '#') continue;
                        var at = new List<int>();
                        var plain = new StringBuilder();
                        foreach (char c in word)
                            if (c == '-') at.Add(plain.Length);
                            else plain.Append(c);
                        p.ByHand[plain.ToString()] = at.ToArray();
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { p = null; }
        return loaded[id] = p;
    }

    /// <summary>Gives every paragraph of the document its places, where the text has changed since they were found.</summary>
    public static void Fill(Document doc)
    {
        if (!Knows(doc.Hyphenation)) return;
        foreach (var p in doc.Body) Fill(doc, p);
        foreach (var n in doc.Notes.Values)
            foreach (var p in n.Paras) Fill(doc, p);
        foreach (var c in doc.Cells.Values)
            foreach (var p in c.Paras) Fill(doc, p);
    }

    static void Fill(Document doc, Paragraph p)
    {
        if (p.Table != null) return;
        string text = p.Text;
        if (p.Hyphens != null && (ReferenceEquals(p.HyphenText, text) || p.HyphenText == text))
        {
            p.HyphenText = text;
            return;
        }
        // Titles, headings and the table of contents are left whole.
        bool whole = p.Toc || p.Style == "Title" || p.Style.StartsWith("Heading", StringComparison.Ordinal);
        p.Hyphens = whole ? Array.Empty<int>() : Points(text, doc.Hyphenation);
        p.HyphenText = text;
    }

    /// <summary>The places in a text where a word may be divided: each is the index of the character the next line would begin with.</summary>
    public static int[] Points(string text, string language)
    {
        var patterns = language == "es" ? null : Load(language);
        if (language != "es" && patterns == null) return Array.Empty<int>();
        var points = new List<int>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            if (!char.IsLetter(text[i])) { i++; continue; }
            int a = i;
            while (i < n && char.IsLetter(text[i])) i++;
            // A word beside a digit, as in a code or a number with a unit, is left whole.
            bool digits = a > 0 && char.IsDigit(text[a - 1]) || i < n && char.IsDigit(text[i]);
            if (i - a < 5 || digits) continue;
            if (patterns == null) Spanish(text, a, i, points);
            else
                foreach (int at in ByPatterns(patterns, text.Substring(a, i - a).ToLowerInvariant())) points.Add(a + at);
        }
        return points.ToArray();
    }

    /// <summary>The places inside one word, by the patterns: wherever the largest digit of all patterns that fit is odd.
    /// A word divided by hand in the list of the language goes by that instead.</summary>
    static int[] ByPatterns(Patterns p, string word)
    {
        if (p.Known.TryGetValue(word, out var known)) return known;
        int n = word.Length;
        var found = new List<int>();
        if (p.ByHand.TryGetValue(word, out var hand))
        {
            foreach (int at in hand)
                if (at >= 1 && at < n) found.Add(at);
        }
        else
        {
            string dotted = "." + word + ".";
            var levels = new byte[dotted.Length + 1];
            for (int from = 0; from < dotted.Length; from++)
            {
                var node = p.Root;
                for (int k = from; k < dotted.Length; k++)
                {
                    if (node.Next == null || !node.Next.TryGetValue(dotted[k], out var next)) break;
                    node = next;
                    if (node.Levels is not { } l) continue;
                    for (int q = 0; q < l.Length; q++)
                        if (l[q] > levels[from + q]) levels[from + q] = l[q];
                }
            }
            // The place before letter j of the word is the place before character j + 1 of the dotted word.
            for (int j = p.Left; j <= n - p.Right; j++)
                if (j >= 1 && (levels[j + 1] & 1) == 1) found.Add(j);
        }
        var result = found.ToArray();
        if (p.Known.Count < 200000) p.Known[word] = result;
        return result;
    }

    const string Vowels = "aeiouáéíóúü";

    /// <summary>Spanish: a consonant between vowels goes with the vowel after it; of two, the first stays, unless they are
    /// a pair that begins a syllable (pr, bl, tr …) or one sound written with two letters (ch, ll, rr); of three or more,
    /// such a pair or the last consonant goes on. Vowels that follow each other stay together, a single letter is never
    /// left alone at either end, and an h behind a consonant is not parted from it.</summary>
    static void Spanish(string text, int a, int b, List<int> points)
    {
        string w = text.Substring(a, b - a).ToLowerInvariant();
        int n = w.Length;
        bool V(int k) => Vowels.IndexOf(w[k]) >= 0 || w[k] == 'y' && k == n - 1;
        int i = 0;
        while (i < n && !V(i)) i++;
        while (i < n)
        {
            while (i < n && V(i)) i++;
            int from = i;
            while (i < n && !V(i)) i++;
            if (i >= n) break;
            // The consonants between two groups of vowels, as sounds: ch, ll and rr are one each.
            var units = new List<int>();
            for (int k = from; k < i; k++)
            {
                units.Add(k);
                if (k + 1 < i && (w[k] == 'c' && w[k + 1] == 'h' || w[k] == 'l' && w[k + 1] == 'l' || w[k] == 'r' && w[k + 1] == 'r')) k++;
            }
            int at;
            if (units.Count == 1) at = units[0];
            else
            {
                int u1 = units[^2], u2 = units[^1];
                bool single1 = u2 - u1 == 1, single2 = i - u2 == 1;
                char c1 = w[u1], c2 = w[u2];
                if (single1 && single2 && c1 == 't' && c2 == 'l') continue;
                bool pair = single1 && single2 && (c2 == 'r' && "pbfcgtdk".IndexOf(c1) >= 0 || c2 == 'l' && "pbfcgk".IndexOf(c1) >= 0);
                at = pair ? u1 : u2;
            }
            if (w[at] == 'h' && at > from) continue;
            if (at >= 2 && n - at >= 2) points.Add(a + at);
        }
    }
}
