using System;
using System.Runtime.InteropServices;

namespace DsoHelper.Infrastructure;

internal static class WindowAppearance
{
    private const int DwmwaUseImmersiveDarkMode = 20;

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
        }
        catch
        {
            // Older Windows versions may not support the attribute. The title bar stays light.
        }
    }
}
