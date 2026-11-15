using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ReiksDocument;

/// <summary>The colours of the program itself, light or dark. The pages of a document stay white in both.</summary>
static class Theme
{
    public static bool Dark { get; private set; }

    // The brushes of the program's own parts. Apply changes their colours, and whatever is drawn with them follows.
    public static readonly SolidColorBrush Window = new(), Bar = new(), Desk = new(), Line = new(), Dim = new(), Sheet = new(), Field = new(), Edge = new(), Paper = new(), Accent = new();

    // Fills of the toolbar buttons, found by name so that they can change while the window is open.
    public const string HoverKey = "Tool.Hover", PressKey = "Tool.Press", OnKey = "Tool.On";

    static Theme() => SetColors();

    /// <summary>What Windows is set to for apps: dark or light.</summary>
    public static bool WindowsIsDark()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    /// <summary>True while the user's own colour is on the window.</summary>
    static bool own;

    /// <summary>Puts the chosen colours on everything that is open.</summary>
    public static void Apply()
    {
        own = Settings.Colors == "custom";
        // With a colour of the user's own the controls turn dark or light to suit it.
        Dark = own ? Light(Settings.WindowColor) < 120 : Settings.Colors == "dark" || Settings.Colors != "light" && WindowsIsDark();
        SetColors();
        if (Application.Current is not { } app) return;
        app.ThemeMode = Dark ? ThemeMode.Dark : ThemeMode.Light;
        int w = Settings.WindowColor;
        app.Resources[HoverKey] = Fill(own ? Shade(w, Dark ? 0.10 : -0.07) : Dark ? 0x3A3A3A : 0xE6E6E6);
        app.Resources[PressKey] = Fill(own ? Shade(w, Dark ? 0.06 : -0.11) : Dark ? 0x333333 : 0xD9D9D9);
        app.Resources[OnKey] = Fill(own ? Shade(w, Dark ? 0.20 : -0.16) : Dark ? 0x565656 : 0xCDCDCD);
    }

    static void SetColors()
    {
        int w = Settings.WindowColor;
        Set(Window, own ? w : Dark ? 0x202020 : 0xF3F3F3);                              // behind the controls
        Set(Bar, own ? w : Dark ? 0x2B2B2B : 0xF3F3F3);                                 // status line and find bar
        Set(Desk, own ? Shade(w, Dark ? -0.30 : -0.09) : Dark ? 0x161616 : 0xE1E1E1);   // what the pages lie on
        Set(Line, own ? Shade(w, Dark ? 0.18 : -0.20) : Dark ? 0x454545 : 0xCCCCCC);    // thin lines between parts
        Set(Dim, Dark ? 0xA8A8A8 : 0x606060);      // explaining text
        Set(Sheet, Dark ? 0x2B2B2B : 0xFFFFFF);    // the sheet of colours
        Set(Field, Dark ? 0x161616 : 0xFFFFFF);    // inside the boxes of the toolbar
        Set(Edge, Dark ? 0x6A6A6A : 0x9A9A9A);     // around them
        Set(Paper, SheetColor);                    // the sheet on the screen
        Set(Accent, Dark ? 0x6CB8F5 : 0x1A6FC4);   // what is to be found at a glance
        onDark.Clear();
    }

    /// <summary>The sheet of the dark page, and the lightest that anything on it is shown.</summary>
    public const int DarkSheet = 0x262626, DarkInk = 0xEDEDED;

    /// <summary>The colour of the sheet on the screen: that of the dark page, else the one chosen under Preferences.</summary>
    public static int SheetColor => Settings.DarkPage ? DarkSheet : Settings.PaperColor;

    /// <summary>True when what stands on the sheet has to be shown light: on the dark page, and on a paper colour of the
    /// user's own that is dark.</summary>
    public static bool SheetIsDark => Light(SheetColor) < 110;

    static readonly Dictionary<int, int> onDark = new();

    /// <summary>The colour in which something of the document is shown on a dark sheet: its lightness is turned round, so that
    /// black becomes the light ink and white the colour of the sheet, and what was lighter than something else is now darker
    /// than it; the kind of colour (red, blue) stays. Pictures are not changed by this.</summary>
    public static int OnDarkSheet(int rgb)
    {
        rgb &= 0xFFFFFF;
        if (onDark.TryGetValue(rgb, out int known)) return known;
        return onDark[rgb] = Turned(rgb, SheetColor);
    }

    /// <summary>A colour with its lightness turned round for a dark ground of the given colour: black becomes the light ink,
    /// white the ground, and the kind of colour stays.</summary>
    public static int Turned(int rgb, int ground)
    {
        rgb &= 0xFFFFFF;
        double low = Light(ground), high = Light(DarkInk);
        double want = low + (high - low) * (255 - Light(rgb)) / 255.0;
        // The same hue and saturation, with the lightness that gives the wanted brightness.
        double r = ((rgb >> 16) & 255) / 255.0, g = ((rgb >> 8) & 255) / 255.0, b = (rgb & 255) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d == 0)
        {
            // A grey stays a grey: white becomes exactly the sheet, black the light ink.
            int level = (int)Math.Round(want);
            return level << 16 | level << 8 | level;
        }
        double s = d / (1 - Math.Abs(2 * l - 1)), h = 0;
        h = max == r ? ((g - b) / d + 6) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        int Made(double light)
        {
            double c = (1 - Math.Abs(2 * light - 1)) * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = light - c / 2;
            var (r1, g1, b1) = h < 1 ? (c, x, 0.0) : h < 2 ? (x, c, 0.0) : h < 3 ? (0.0, c, x) : h < 4 ? (0.0, x, c) : h < 5 ? (x, 0.0, c) : (c, 0.0, x);
            int R = (int)Math.Round(Math.Clamp(r1 + m, 0, 1) * 255), G = (int)Math.Round(Math.Clamp(g1 + m, 0, 1) * 255), B = (int)Math.Round(Math.Clamp(b1 + m, 0, 1) * 255);
            return R << 16 | G << 8 | B;
        }
        double lo = 0, hi = 1;
        for (int i = 0; i < 24; i++)
        {
            double mid = (lo + hi) / 2;
            if (Light(Made(mid)) < want) lo = mid;
            else hi = mid;
        }
        return Made((lo + hi) / 2);
    }

    /// <summary>How light a colour is, 0 to 255.</summary>
    public static int Light(int rgb) => (((rgb >> 16) & 255) * 299 + ((rgb >> 8) & 255) * 587 + (rgb & 255) * 114) / 1000;

    /// <summary>A colour made darker (by below 0, down to -1) or lighter (by above 0, up to 1).</summary>
    public static int Shade(int rgb, double by)
    {
        int One(int c) => (int)Math.Round(by < 0 ? c * (1 + by) : c + (255 - c) * by);
        return One((rgb >> 16) & 255) << 16 | One((rgb >> 8) & 255) << 8 | One(rgb & 255);
    }

    static SolidColorBrush Fill(int rgb)
    {
        var b = new SolidColorBrush();
        Set(b, rgb);
        b.Freeze();
        return b;
    }

    static void Set(SolidColorBrush b, int rgb) => b.Color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

/// <summary>Whether Windows may put the program's windows into screenshots and screen recordings.</summary>
static class ScreenCapture
{
    [DllImport("user32.dll")]
    static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    const uint Excluded = 0x11;

    public static void Apply(Window w)
    {
        IntPtr h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) SetWindowDisplayAffinity(h, Settings.AllowCapture ? 0u : Excluded);
    }

    /// <summary>Makes a window that is not open yet follow the choice when it opens.</summary>
    public static void Follow(Window w) => w.SourceInitialized += (_, _) => Apply(w);

    /// <summary>True when Windows leaves this window out of captures.</summary>
    public static bool IsExcluded(Window w) =>
        GetWindowDisplayAffinity(new WindowInteropHelper(w).Handle, out uint a) && a == Excluded;
}

/// <summary>The colour box of Windows, to choose any colour.</summary>
static class ColorPicker
{
    [StructLayout(LayoutKind.Sequential)]
    struct ChooseColorData
    {
        public int Size;
        public IntPtr Owner, Instance;
        public int Result;
        public IntPtr Custom;
        public int Flags;
        public IntPtr Data, Hook, Template;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    static extern bool ChooseColor(ref ChooseColorData data);

    static readonly int[] kept = Enumerable.Repeat(0xFFFFFF, 16).ToArray();

    static int Swap(int c) => (c & 255) << 16 | c & 0xFF00 | (c >> 16) & 255;

    /// <summary>Shows the box with a colour (0xRRGGBB) set; the chosen colour, or null if the user gave up.</summary>
    public static int? Pick(Window owner, int rgb)
    {
        IntPtr custom = Marshal.AllocHGlobal(64);
        try
        {
            Marshal.Copy(kept, 0, custom, 16);
            var data = new ChooseColorData
            {
                Size = Marshal.SizeOf<ChooseColorData>(), Owner = new WindowInteropHelper(owner).Handle, Result = Swap(rgb), Custom = custom,
                Flags = 0x1 | 0x2 | 0x100,   // start on the given colour, fully open, any colour
            };
            bool ok = ChooseColor(ref data);
            Marshal.Copy(custom, kept, 0, 16);
            return ok ? Swap(data.Result) & 0xFFFFFF : null;
        }
        finally { Marshal.FreeHGlobal(custom); }
    }
}
