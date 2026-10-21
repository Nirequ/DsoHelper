using System;
using System.Runtime.InteropServices;

namespace DsoHelper.Infrastructure;

internal static class WindowAppearance
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    // Palette: #121316 obsidian, #E6E8EC text, #373C4A border.
    private const int CaptionColor = 0x121316;
    private const int TextColor = 0xE6E8EC;
    private const int BorderColor = 0x373C4A;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

            // Windows 11+: paint the non-client area with the same dark fantasy surfaces.
            var caption = ToColorRef(CaptionColor);
            var text = ToColorRef(TextColor);
            var border = ToColorRef(BorderColor);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch
        {
            // Older Windows versions may not support the attributes. The title bar stays default.
        }
    }

    private static int ToColorRef(int rgb) =>
        ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
}
