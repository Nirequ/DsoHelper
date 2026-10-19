using System;
using System.Text;

namespace DsoHelper.Infrastructure;

internal readonly record struct ClientBounds(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

internal static class GameWindow
{
    public static bool TryFind(out IntPtr window)
    {
        var found = IntPtr.Zero;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                return true;
            }

            var title = new StringBuilder(512);
            if (NativeMethods.GetWindowText(hWnd, title, title.Capacity) <= 0)
            {
                return true;
            }

            if (!title.ToString().Contains("Drakensang", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            found = hWnd;
            return false;
        }, IntPtr.Zero);

        window = found;
        return found != IntPtr.Zero;
    }

    public static bool TryGetClientBounds(out ClientBounds bounds)
    {
        bounds = default;

        if (!TryFind(out var window) || NativeMethods.IsIconic(window))
        {
            return false;
        }

        if (!NativeMethods.GetClientRect(window, out var client))
        {
            return false;
        }

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var origin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(window, ref origin))
        {
            return false;
        }

        bounds = new ClientBounds(origin.X, origin.Y, width, height);
        return true;
    }

    public static bool TryActivate()
    {
        if (!TryFind(out var window))
        {
            return false;
        }

        if (NativeMethods.IsIconic(window))
        {
            NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        }

        return NativeMethods.SetForegroundWindow(window);
    }

    /// <summary>
    /// True when the Drakensang window currently owns the foreground.
    /// Cheap enough to be called from a keyboard hook.
    /// </summary>
    public static bool IsForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var title = new StringBuilder(256);
        if (NativeMethods.GetWindowText(foreground, title, title.Capacity) > 0 &&
            title.ToString().Contains("Drakensang", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The active window may be a different top-level window of the same game process.
        if (!TryFind(out var game) || game == IntPtr.Zero)
        {
            return false;
        }

        if (game == foreground)
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(game, out var gameProcess);
        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundProcess);
        return gameProcess != 0 && gameProcess == foregroundProcess;
    }
}
