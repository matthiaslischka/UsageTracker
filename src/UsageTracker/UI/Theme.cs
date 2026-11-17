using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using UsageTracker.Core;

namespace UsageTracker.UI;

internal sealed record Theme(
    bool Dark,
    Color Background,
    Color Border,
    Color Text,
    Color SubText,
    Color Track,
    Color Separator,
    Color ButtonFill,
    Color ButtonHover,
    Color ButtonPressed,
    Color PaceTick)
{
    public static readonly Color Good = Color.FromArgb(0x10, 0x7C, 0x10);
    public static readonly Color Warning = Color.FromArgb(0xC2, 0x70, 0x00);
    public static readonly Color Critical = Color.FromArgb(0xC4, 0x2B, 0x1C);
    public static readonly Color Unknown = Color.FromArgb(0x70, 0x70, 0x70);

    public static Color ForSeverity(Severity s) => s switch
    {
        Severity.Good => Good,
        Severity.Warning => Warning,
        _ => Critical,
    };

    // Brighter variants read better on the dark flyout background.
    public Color Bar(Severity s) => !Dark ? ForSeverity(s) : s switch
    {
        Severity.Good => Color.FromArgb(0x6C, 0xCB, 0x5F),
        Severity.Warning => Color.FromArgb(0xFC, 0xE1, 0x00),
        _ => Color.FromArgb(0xFF, 0x99, 0xA4),
    };

    public static Theme Light { get; } = new(false,
        Background: Color.FromArgb(0xF9, 0xF9, 0xF9),
        Border: Color.FromArgb(0xE5, 0xE5, 0xE5),
        Text: Color.FromArgb(0x1A, 0x1A, 0x1A),
        SubText: Color.FromArgb(0x5F, 0x5F, 0x5F),
        Track: Color.FromArgb(0xDD, 0xDD, 0xDD),
        Separator: Color.FromArgb(0xE5, 0xE5, 0xE5),
        ButtonFill: Color.FromArgb(0xFF, 0xFF, 0xFF),
        ButtonHover: Color.FromArgb(0xF3, 0xF3, 0xF3),
        ButtonPressed: Color.FromArgb(0xEA, 0xEA, 0xEA),
        PaceTick: Color.FromArgb(0x1A, 0x1A, 0x1A));

    public static Theme DarkTheme { get; } = new(true,
        Background: Color.FromArgb(0x2B, 0x2B, 0x2B),
        Border: Color.FromArgb(0x40, 0x40, 0x40),
        Text: Color.FromArgb(0xFF, 0xFF, 0xFF),
        SubText: Color.FromArgb(0xC5, 0xC5, 0xC5),
        Track: Color.FromArgb(0x4A, 0x4A, 0x4A),
        Separator: Color.FromArgb(0x3D, 0x3D, 0x3D),
        ButtonFill: Color.FromArgb(0x37, 0x37, 0x37),
        ButtonHover: Color.FromArgb(0x3F, 0x3F, 0x3F),
        ButtonPressed: Color.FromArgb(0x33, 0x33, 0x33),
        PaceTick: Color.FromArgb(0xFF, 0xFF, 0xFF));

    /// <summary>Flyouts follow the system (taskbar) mode, like the Windows 11 quick settings.</summary>
    public static Theme Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 0 ? DarkTheme : Light;
        }
        catch (Exception)
        {
            return Light;
        }
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class Native
{
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    public const int SmCxSmIcon = 49;
    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwaBorderColor = 34;
    public const int DwmwcpRound = 2;

    /// <summary>Small-icon size in the taskbar's DPI (16 at 100%, 20 at 125%, 24 at 150%).</summary>
    public static int SmallIconSize()
    {
        try
        {
            var size = GetSystemMetricsForDpi(SmCxSmIcon, GetDpiForSystem());
            return size > 0 ? size : SystemInformation.SmallIconSize.Width;
        }
        catch (EntryPointNotFoundException)
        {
            return SystemInformation.SmallIconSize.Width;
        }
    }

    public static uint DpiAt(Point screenPoint)
    {
        try
        {
            var monitor = MonitorFromPoint(screenPoint, 2 /* MONITOR_DEFAULTTONEAREST */);
            return GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var x, out _) == 0 && x > 0 ? x : 96;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 96;
        }
    }
}
