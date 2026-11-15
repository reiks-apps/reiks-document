using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ReiksDocument;

/// <summary>Tells Windows, for this user only, that files ending in .docu are Reiks documents: that this program
/// opens them, and which icon they have. Nothing here runs unless the user asks for it, under Preferences or when installing.</summary>
static class FileType
{
    const string Ending = ".docu", Earlier = ".udoc", Id = "ReiksDocument.Document", EarlierId = "ReiksWriter.Document", Classes = @"Software\Classes\";

    /// <summary>The icon of a document, a file beside the program.</summary>
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "assets", "DocuFile.ico");

    /// <summary>What is written under the user's classes: each key and its value.</summary>
    public static List<(string Key, string Value)> Entries(string program, string icon) => new()
    {
        (Ending, Id),
        (Id, "Reiks document"),
        (Id + @"\DefaultIcon", icon),
        (Id + @"\shell\open\command", "\"" + program + "\" \"%1\""),
    };

    /// <summary>True when .docu files are opened by this very program.</summary>
    public static bool IsSet() => Environment.ProcessPath is { } me && IsSetFor(me);

    /// <summary>True when .docu files are opened by the program at this place.</summary>
    public static bool IsSetFor(string program)
    {
        try
        {
            using var ending = Registry.CurrentUser.OpenSubKey(Classes + Ending);
            using var command = Registry.CurrentUser.OpenSubKey(Classes + Id + @"\shell\open\command");
            return ending?.GetValue(null) as string == Id && command?.GetValue(null) is string c
                && c.Contains(program, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    public static void Set()
    {
        if (Environment.ProcessPath is { } me) SetFor(me, IconPath);
    }

    /// <summary>The same for a program and an icon at given places, as when the program is being installed.</summary>
    public static void SetFor(string program, string icon)
    {
        foreach (var (key, value) in Entries(program, icon))
        {
            using var k = Registry.CurrentUser.CreateSubKey(Classes + key);
            k.SetValue(null, value);
        }
        // What the program wrote under its earlier name, and for the ending its documents had before, is no longer needed.
        Registry.CurrentUser.DeleteSubKeyTree(Classes + EarlierId, false);
        GiveBack(Earlier);
        Changed();
    }

    /// <summary>An ending is given back to Windows only if it still points to this program.</summary>
    static void GiveBack(string ending)
    {
        bool mine;
        using (var key = Registry.CurrentUser.OpenSubKey(Classes + ending)) mine = key?.GetValue(null) is Id or EarlierId;
        if (mine) Registry.CurrentUser.DeleteSubKeyTree(Classes + ending, false);
    }

    public static void Remove()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Classes + Id, false);
        Registry.CurrentUser.DeleteSubKeyTree(Classes + EarlierId, false);
        GiveBack(Ending);
        GiveBack(Earlier);
        Changed();
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int what, uint flags, IntPtr first, IntPtr second);

    /// <summary>Tells Explorer that the programs for file endings changed, so that it shows the icon at once.</summary>
    static void Changed() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
