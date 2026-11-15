using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ReiksDocument;

/// <summary>What the user has chosen for the program itself, kept in a small file on this PC
/// (in the user's local application data, folder ReiksDocument). Nothing here changes a document.</summary>
static class Settings
{
    /// <summary>Lengths in centimetres (the first choice) or in inches.</summary>
    public static bool Metric = true;

    /// <summary>Colours of the program: "system" (as Windows is set), "light" or "dark".</summary>
    public static string Colors = "system";

    /// <summary>With Colors "custom": the colour of the window, 0xRRGGBB. The controls turn dark or light to suit it.</summary>
    public static int WindowColor = 0xDCDCD8;

    /// <summary>The colour the sheet has on the screen, 0xRRGGBB. Print and PDF are not touched by it.</summary>
    public static int PaperColor = 0xFFFFFF;
    /// <summary>The dark page: on the screen the sheet is dark and what stands on it is shown light. Print and PDF are not touched.</summary>
    public static bool DarkPage;

    /// <summary>The symbols put into a text last, the newest first.</summary>
    public static string RecentSymbols = "";

    /// <summary>The spelling check: "auto" (the language of Windows, if it can be checked), "off", or a language such as es-ES.</summary>
    public static string Spelling = "auto";

    /// <summary>The equation box opens on the typed line (true) or on the equation that is typed into (false): whichever was used last.</summary>
    public static bool EquationInLine;

    /// <summary>Whether Windows may put the program's windows into screenshots and screen recordings.</summary>
    public static bool AllowCapture;

    /// <summary>Whether a recovery copy of a document with unsaved changes is kept while the user works.</summary>
    public static bool Recovery = true;

    /// <summary>How far the indent buttons move a paragraph: 1 cm, or half an inch.</summary>
    public static int IndentStep => Metric ? 360000 : 457200;
    public static string IndentText => Metric ? "1 cm" : "half an inch";

    /// <summary>The file the choices are kept in.</summary>
    public static string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReiksDocument", "settings.json");

    public static void Load()
    {
        try
        {
            // The choices made while the program was called Reiks Writer are taken over once.
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string earlier = Path.Combine(local, "ReiksWriter", "settings.json");
            if (!File.Exists(FilePath) && FilePath == Path.Combine(local, "ReiksDocument", "settings.json") && File.Exists(earlier))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(earlier, FilePath);
            }
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            if (doc.RootElement.TryGetProperty("units", out var u) && u.ValueKind == JsonValueKind.String) Metric = u.GetString() != "inch";
            if (doc.RootElement.TryGetProperty("colours", out var c) && c.GetString() is "light" or "dark" or "system" or "custom") Colors = c.GetString()!;
            if (doc.RootElement.TryGetProperty("window", out var wc) && Hex(wc) is { } window) WindowColor = window;
            if (doc.RootElement.TryGetProperty("paper", out var pc) && Hex(pc) is { } paper) PaperColor = paper;
            if (doc.RootElement.TryGetProperty("darkPage", out var dp)) DarkPage = dp.ValueKind == JsonValueKind.True;
            if (doc.RootElement.TryGetProperty("symbols", out var sy) && sy.ValueKind == JsonValueKind.String) RecentSymbols = sy.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("equationLine", out var el)) EquationInLine = el.ValueKind == JsonValueKind.True;
            if (doc.RootElement.TryGetProperty("shortcuts", out var sc) && sc.ValueKind == JsonValueKind.Object)
            {
                Shortcuts.Custom.Clear();
                foreach (var one in sc.EnumerateObject())
                    if (one.Value.ValueKind == JsonValueKind.String) Shortcuts.Custom[one.Name] = one.Value.GetString() ?? "";
                // Keys the user gave to something stay with it, also when a newer version made them the standard of another command.
                Shortcuts.Settle();
            }
            if (doc.RootElement.TryGetProperty("spelling", out var sl) && sl.ValueKind == JsonValueKind.String) Spelling = sl.GetString() ?? "auto";
            if (doc.RootElement.TryGetProperty("screenshots", out var a)) AllowCapture = a.ValueKind == JsonValueKind.True;
            if (doc.RootElement.TryGetProperty("recovery", out var rc)) Recovery = rc.ValueKind != JsonValueKind.False;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
    }

    static int? Hex(JsonElement e) =>
        e.ValueKind == JsonValueKind.String && e.GetString() is { Length: 6 } s && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : null;

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, "{ \"units\": \"" + (Metric ? "cm" : "inch") + "\", \"colours\": \"" + Colors + "\", \"screenshots\": " + (AllowCapture ? "true" : "false") + ", \"window\": \"" + WindowColor.ToString("X6", CultureInfo.InvariantCulture) + "\", \"paper\": \"" + PaperColor.ToString("X6", CultureInfo.InvariantCulture)
                + "\", \"symbols\": " + JsonSerializer.Serialize(RecentSymbols) + ", \"spelling\": " + JsonSerializer.Serialize(Spelling) + ", \"shortcuts\": " + JsonSerializer.Serialize(Shortcuts.Custom) + ", \"equationLine\": " + (EquationInLine ? "true" : "false") + ", \"recovery\": " + (Recovery ? "true" : "false") + ", \"darkPage\": " + (DarkPage ? "true" : "false") + " }\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
