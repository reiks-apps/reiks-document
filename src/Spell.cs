using System.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ReiksDocument;

/// <summary>The spelling check: the one that Windows has, with the dictionaries installed on this PC.
/// Everything stays on this PC. Words the check does not know are only marked on the screen; nothing in a document changes.</summary>
static class Spell
{
    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    class SpellCheckerFactory { }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages { [return: MarshalAs(UnmanagedType.Interface)] get; }
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string language);
        [return: MarshalAs(UnmanagedType.Interface)]
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string language);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellChecker
    {
        string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
        void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
        void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);
        // What follows in the interface of Windows is not used here.
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumSpellingError
    {
        [PreserveSig]
        int Next([MarshalAs(UnmanagedType.Interface)] out ISpellingError? error);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellingError
    {
        uint StartIndex { get; }
        uint Length { get; }
        int CorrectiveAction { get; }
        string Replacement { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
    }

    sealed class Checked
    {
        public string Text = "", Language = "";
        public int Round;
        public List<(int Start, int Length)> Wrong = new();
    }

    static readonly ConditionalWeakTable<Paragraph, Checked> cache = new();
    static ISpellCheckerFactory? factory;
    static ISpellChecker? checker;
    static string checkerLanguage = "";
    static List<string>? languages;
    static bool broken;
    /// <summary>Goes up when a word is ignored or added, so that what was checked before is checked again.</summary>
    static int round;

    /// <summary>The languages the user has in the language list of Windows but that Windows cannot check yet:
    /// the language is there, its dictionary (the feature "Basic typing") is not.</summary>
    public static List<string> WithoutDictionary()
    {
        var missing = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile");
            if (key?.GetValue("Languages") is not string[] mine) return missing;
            var have = Languages();
            foreach (string tag in mine)
                if (tag.Length > 0 && Find(have, tag) == null) missing.Add(tag);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return missing;
    }

    /// <summary>Forgets what was found, so that a language added in Windows meanwhile is seen.</summary>
    public static void LookAgain()
    {
        languages = null;
        factory = null;
        checker = null;
        checkerLanguage = "";
        broken = false;
        round++;
    }

    static ISpellCheckerFactory? Factory()
    {
        if (factory != null || broken) return factory;
        try { factory = (ISpellCheckerFactory)new SpellCheckerFactory(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or TypeLoadException) { broken = true; }
        return factory;
    }

    static List<string> Strings(IEnumString e)
    {
        var list = new List<string>();
        var one = new string[1];
        while (e.Next(1, one, IntPtr.Zero) == 0 && one[0] != null) list.Add(one[0]);
        return list;
    }

    /// <summary>The languages Windows can check on this PC, as tags like es-ES.</summary>
    public static IReadOnlyList<string> Languages()
    {
        if (languages != null) return languages;
        languages = new List<string>();
        try
        {
            if (Factory() is { } f) languages = Strings(f.SupportedLanguages);
        }
        catch (COMException) { }
        languages.Sort(StringComparer.OrdinalIgnoreCase);
        return languages;
    }

    /// <summary>The language that "as Windows" stands for: that of Windows if it can be checked, else English; empty if there
    /// is nothing to check with.</summary>
    public static string Automatic
    {
        get
        {
            var all = Languages();
            return Find(all, CultureInfo.CurrentUICulture.Name) ?? Find(all, CultureInfo.CurrentCulture.Name) ?? Find(all, "en-US") ?? Find(all, "en-GB") ?? "";
        }
    }

    /// <summary>The name of a language as it is shown, such as Spanish (Spain) for es-ES.</summary>
    public static string NameOf(string tag)
    {
        try { return CultureInfo.GetCultureInfo(tag).EnglishName; }
        catch (CultureNotFoundException) { return tag; }
    }

    /// <summary>How many words of a document the check does not know: in its text, its table cells and its footnotes.
    /// 0 when the check is off.</summary>
    public static int Count(Document doc) => Everywhere(doc).Sum(p => Of(p)?.Count ?? 0);

    /// <summary>The word the check does not know that follows a selection (way 1) or stands before it (way -1), among the
    /// paragraphs given; behind the last paragraph it goes on at the first, and the other way round. Null if there is none.</summary>
    public static (int Para, int Start, int Length)? Step(IReadOnlyList<Paragraph> body, Pos selStart, Pos selEnd, int way)
    {
        if (body.Count == 0) return null;
        int first = way > 0 ? selEnd.Para : selStart.Para;
        for (int step = 0; step <= body.Count; step++)
        {
            int k = ((first + way * step) % body.Count + body.Count) % body.Count;
            if (body[k].Table != null || Of(body[k]) is not { } wrong) continue;
            if (way > 0)
            {
                foreach (var (start, length) in wrong)
                    if (step > 0 || start >= selEnd.Off) return (k, start, length);
            }
            else
                for (int i = wrong.Count - 1; i >= 0; i--)
                    if (step > 0 || wrong[i].Start + wrong[i].Length <= selStart.Off) return (k, wrong[i].Start, wrong[i].Length);
        }
        return null;
    }

    /// <summary>Every paragraph of a document that holds text: of the body, of the table cells, of the footnotes.</summary>
    public static IEnumerable<Paragraph> Everywhere(Document doc) =>
        doc.Body.Where(p => p.Table == null).Concat(doc.Cells.Values.SelectMany(c => c.Paras)).Concat(doc.Notes.Values.SelectMany(n => n.Paras));

    /// <summary>The language that is checked: the one chosen under Preferences; with "auto" the language of Windows
    /// if it can be checked, else English; empty when the check is off or there is nothing to check with.</summary>
    public static string Language
    {
        get
        {
            string want = Settings.Spelling;
            if (want == "off") return "";
            var all = Languages();
            if (want != "auto") return Find(all, want) ?? "";
            return Find(all, CultureInfo.CurrentUICulture.Name) ?? Find(all, CultureInfo.CurrentCulture.Name) ?? Find(all, "en-US") ?? Find(all, "en-GB") ?? "";
        }
    }

    static string? Find(IReadOnlyList<string> all, string tag)
    {
        foreach (string l in all)
            if (l.Equals(tag, StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    static ISpellChecker? Checker(string language)
    {
        if (language.Length == 0) return null;
        if (checker != null && checkerLanguage == language) return checker;
        try
        {
            checker = Factory()?.CreateSpellChecker(language);
            checkerLanguage = language;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException) { checker = null; }
        return checker;
    }

    /// <summary>The stretches of a text that the check does not know, as places in the text.</summary>
    public static List<(int Start, int Length)> Check(string text, string language)
    {
        var wrong = new List<(int, int)>();
        if (text.Trim().Length == 0 || Checker(language) is not { } c) return wrong;
        try
        {
            var errors = c.Check(text);
            while (errors.Next(out var e) == 0 && e != null)
                wrong.Add(((int)e.StartIndex, (int)e.Length));
        }
        catch (COMException) { }
        return wrong;
    }

    /// <summary>The unknown words of a paragraph, kept as long as its text stays the same; null when the check is off.</summary>
    public static IReadOnlyList<(int Start, int Length)>? Of(Paragraph p)
    {
        string language = Language;
        if (language.Length == 0) return null;
        string text = p.Text;
        var known = cache.GetOrCreateValue(p);
        if (known.Round != round + 1 || known.Language != language || known.Text != text)
        {
            // Objects and line breaks inside the paragraph count as spaces; the places in the text stay the same.
            known.Wrong = Check(text.Replace(Run.ObjectChar, ' ').Replace(Document.LineBreak, ' ').Replace('\t', ' '), language);
            known.Text = text;
            known.Language = language;
            known.Round = round + 1;
        }
        return known.Wrong;
    }

    /// <summary>Other spellings Windows offers for a word, the likeliest first.</summary>
    public static List<string> Suggest(string word)
    {
        try { return Checker(Language) is { } c ? Strings(c.Suggest(word)) : new List<string>(); }
        catch (COMException) { return new List<string>(); }
    }

    /// <summary>Takes a word as right until the program is closed.</summary>
    public static void Ignore(string word)
    {
        try { Checker(Language)?.Ignore(word); }
        catch (COMException) { }
        round++;
    }

    /// <summary>Puts a word into the user's own dictionary of Windows for this language, where it stays.</summary>
    public static void Add(string word)
    {
        try { Checker(Language)?.Add(word); }
        catch (COMException) { }
        round++;
    }
}
