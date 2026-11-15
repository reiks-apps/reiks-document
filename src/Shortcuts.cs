using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ReiksDocument;

/// <summary>The keyboard shortcuts: for commands of the program, and for symbols in the equation box.
/// Each has standard keys; the user can give it others or none. Only what differs from the standard is kept.</summary>
static class Shortcuts
{
    /// <summary>Something that can have a shortcut. Symbol: a sign typed in the equation box; otherwise a command.</summary>
    public sealed record Item(string Id, string Name, string Standard, bool Symbol);

    static readonly (string Id, string Name, string Keys)[] Commands =
    {
        ("file.new", "New document", "Ctrl+N"), ("file.open", "Open", "Ctrl+O"), ("file.save", "Save", "Ctrl+S"), ("file.saveas", "Save under a new name", "Ctrl+Shift+S"),
        ("file.pdf", "Save as PDF", "Ctrl+Shift+P"), ("file.print", "Print", "Ctrl+P"), ("file.page", "Page: paper and margins", ""), ("fmt.styles", "Styles", ""), ("fmt.paragraph", "Paragraph: spacing and indents", ""), ("edit.find", "Find and replace", "Ctrl+F"),
        ("edit.undo", "Undo", "Ctrl+Z"), ("edit.redo", "Redo", "Ctrl+Y"),
        ("fmt.bold", "Bold", "Ctrl+B"), ("fmt.italic", "Italic", "Ctrl+I"), ("fmt.underline", "Underline", "Ctrl+U"), ("fmt.latex", "LaTeX: equations from what is typed or selected", "Ctrl+Shift+E"),
        ("fmt.copy", "Copy the format", "Ctrl+Shift+C"), ("fmt.paste", "Put the copied format on", "Ctrl+Shift+V"), ("fmt.erase", "Erase the format", "Ctrl+Space"),
        ("para.left", "Align left", "Ctrl+L"), ("para.centre", "Centre", "Ctrl+E"), ("para.right", "Align right", "Ctrl+R"), ("para.justify", "Justify", "Ctrl+J"),
        ("para.in", "Indent more, or a list level in", "Ctrl+M"), ("para.out", "Indent less, or a list level out", "Ctrl+Shift+M"),
        ("ins.page", "New page", "Ctrl+Enter"), ("ins.footnote", "Footnote", "Ctrl+Alt+F"), ("ins.image", "Image", ""), ("ins.symbol", "Table of symbols", ""),
        ("ins.equation", "Equation", ""), ("ins.chart", "Chart", ""), ("ins.table", "Table", ""), ("ins.box", "Box: a frame for text or a picture", ""), ("ins.citation", "Sources and citations", ""), ("ins.toc", "Table of contents", ""), ("ins.link", "Link", "Ctrl+K"),
        ("ins.header", "Header and footer", ""), ("app.preferences", "Preferences", ""),
    };

    /// <summary>The standard keys of signs other than the Greek letters, which go by their letter.</summary>
    static readonly Dictionary<char, string> SignKeys = new()
    {
        ['≤'] = "Alt+1", ['≥'] = "Alt+2", ['≠'] = "Alt+3", ['≈'] = "Alt+4", ['±'] = "Alt+5", ['×'] = "Alt+6", ['÷'] = "Alt+7", ['∞'] = "Alt+8", ['→'] = "Alt+9",
        ['∂'] = "Alt+0", ['∈'] = "Alt+J", ['√'] = "Alt+V",
    };

    public static readonly List<Item> Items = Build();

    /// <summary>What the user changed: the keys of an item, or an empty text for none.</summary>
    public static Dictionary<string, string> Custom = new();

    static List<Item> Build()
    {
        var list = Commands.Select(c => new Item(c.Id, c.Name, c.Keys, false)).ToList();
        foreach (var (_, signs) in EquationDialog.SignGroups)
            foreach (char c in signs)
                if (list.All(i => i.Id != "sym." + c))
                    list.Add(new Item("sym." + c, c + "   " + (SignNames.NameOf(c) ?? ""), SignNames.KeysOf(c) ?? (SignKeys.TryGetValue(c, out string? k) ? k : ""), true));
        return list;
    }

    /// <summary>The keys an item has now.</summary>
    public static string Of(string id, Dictionary<string, string>? custom = null)
    {
        if ((custom ?? Custom).TryGetValue(id, out string? keys)) return keys;
        return Items.Find(i => i.Id == id)?.Standard ?? "";
    }

    /// <summary>The item these keys belong to, among the commands or among the symbols; null if to none.</summary>
    public static string? Find(string keys, bool symbol, Dictionary<string, string>? custom = null) =>
        keys.Length == 0 ? null : Items.Find(i => i.Symbol == symbol && Of(i.Id, custom) == keys)?.Id;

    /// <summary>True for keys that are the standard of an item which now has other keys, and that nothing else uses:
    /// they do nothing, rather than what they did before.</summary>
    public static bool Freed(string keys, bool symbol) =>
        keys.Length > 0 && Find(keys, symbol) == null && Items.Exists(i => i.Symbol == symbol && i.Standard == keys);

    /// <summary>Gives an item keys (or none, with an empty text). An item of the same kind that had them loses them;
    /// its name is returned, or null if there was none.</summary>
    public static string? Set(Dictionary<string, string> custom, string id, string keys)
    {
        var item = Items.Find(i => i.Id == id);
        if (item == null) return null;
        string? lost = null;
        if (keys.Length > 0)
            foreach (var other in Items)
                if (other.Id != id && other.Symbol == item.Symbol && Of(other.Id, custom) == keys)
                {
                    Put(custom, other, "");
                    lost = other.Name;
                }
        Put(custom, item, keys);
        return lost;
    }

    static void Put(Dictionary<string, string> custom, Item item, string keys)
    {
        if (keys == item.Standard) custom.Remove(item.Id);
        else custom[item.Id] = keys;
    }

    /// <summary>Makes the user's own keys the only ones of their kind: an item that has the same keys as its standard loses
    /// them. This matters when a newer version gives a command standard keys that the user had already given to something
    /// else. Returns the names of the items that lost their keys.</summary>
    public static List<string> Settle(Dictionary<string, string>? custom = null)
    {
        custom ??= Custom;
        var lost = new List<string>();
        foreach (var (id, keys) in custom.ToList())
        {
            if (keys.Length == 0 || Items.Find(i => i.Id == id) is not { } mine) continue;
            foreach (var other in Items)
                if (other.Id != id && other.Symbol == mine.Symbol && !custom.ContainsKey(other.Id) && other.Standard == keys)
                {
                    custom[other.Id] = "";
                    lost.Add(other.Name);
                }
        }
        return lost;
    }

    /// <summary>The keys the page keeps for itself whatever the commands have: no command gets them as its standard.</summary>
    public static readonly string[] OfThePage =
    {
        "Ctrl+A", "Ctrl+C", "Ctrl+X", "Ctrl+V", "Ctrl+Insert", "Shift+Insert", "Shift+Delete", "Ctrl+Left", "Ctrl+Right", "Ctrl+Home", "Ctrl+End",
        "Ctrl+Back", "Ctrl+Delete", "Ctrl+Tab", "Ctrl++", "Ctrl+-",
        "Ctrl+0", "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7", "Ctrl+8", "Ctrl+9",
        "Ctrl+Shift+0", "Ctrl+Shift+1", "Ctrl+Shift+2", "Ctrl+Shift+3", "Ctrl+Shift+4", "Ctrl+Shift+5", "Ctrl+Shift+6", "Ctrl+Shift+7", "Ctrl+Shift+8", "Ctrl+Shift+9",
    };

    /// <summary>True for keys that type a character on a keyboard in common use, where the right Alt key counts as Ctrl with Alt
    /// (the euro sign on Ctrl+Alt+E, the at sign on Ctrl+Alt+2 or Ctrl+Alt+Q): no command gets such keys as its standard.</summary>
    public static bool TypesACharacter(string keys)
    {
        if (!keys.StartsWith("Ctrl+Alt+", System.StringComparison.Ordinal)) return false;
        string key = keys[(keys.LastIndexOf('+') + 1)..];
        return key.Length == 1 && (char.IsDigit(key[0]) || "ACDEILMNOPQRSTUWXYZ".Contains(key[0]));
    }

    /// <summary>Keys that keep their meaning everywhere and cannot be given to anything else.</summary>
    public static bool Reserved(string keys) => keys is "Ctrl+C" or "Ctrl+X" or "Ctrl+V" or "Ctrl+A" or "Alt+F4";

    /// <summary>The keys of a key press, as in "Ctrl+Shift+B"; empty if it is not a shortcut
    /// (a shortcut has Ctrl or Alt in it, or is one of F1 to F12).</summary>
    public static string Gesture(KeyEventArgs e)
    {
        Key k = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (k is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.DeadCharProcessed) return "";
        var held = Keyboard.Modifiers;
        bool ctrl = (held & ModifierKeys.Control) != 0, alt = (held & ModifierKeys.Alt) != 0, shift = (held & ModifierKeys.Shift) != 0;
        if (!ctrl && !alt && !(k >= Key.F1 && k <= Key.F12)) return "";
        string name = k >= Key.A && k <= Key.Z ? ((char)('A' + (k - Key.A))).ToString()
            : k >= Key.D0 && k <= Key.D9 ? ((char)('0' + (k - Key.D0))).ToString()
            : k switch
            {
                Key.Enter => "Enter", Key.Space => "Space", Key.OemComma => ",", Key.OemPeriod => ".", Key.OemPlus => "+", Key.OemMinus => "-",
                _ => k.ToString(),
            };
        return (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + name;
    }
}
