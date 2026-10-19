using System;
using DsoHelper.Infrastructure;

namespace DsoHelper.FastMount;

internal static class SlotResolver
{
    public static bool HasSlot(FastMountSettings settings) =>
        (settings.SlotRightRatio is not null && settings.SlotTopRatio is not null) ||
        settings.SlotX != 0 ||
        settings.SlotY != 0;

    /// <summary>
    /// Recalculates the slot position for the current game window size.
    /// The ratio is based on the client height (like the reference AHK script),
    /// so the slot stays in place after switching resolutions or window modes.
    /// </summary>
    public static bool TryResolve(FastMountSettings settings, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (settings.SlotRightRatio is { } rightRatio &&
            settings.SlotTopRatio is { } topRatio &&
            GameWindow.TryGetClientBounds(out var bounds))
        {
            x = bounds.Right - (int)Math.Round(rightRatio * bounds.Height);
            y = bounds.Top + (int)Math.Round(topRatio * bounds.Height);
            x = Math.Clamp(x, bounds.Left, bounds.Right - 1);
            y = Math.Clamp(y, bounds.Top, bounds.Bottom - 1);
            return true;
        }

        if (settings.SlotX == 0 && settings.SlotY == 0)
        {
            return false;
        }

        x = settings.SlotX;
        y = settings.SlotY;
        return true;
    }

    public static void Capture(FastMountSettings settings, int screenX, int screenY)
    {
        settings.SlotX = screenX;
        settings.SlotY = screenY;

        if (GameWindow.TryGetClientBounds(out var bounds) &&
            screenX >= bounds.Left && screenX < bounds.Right &&
            screenY >= bounds.Top && screenY < bounds.Bottom)
        {
            settings.SlotRightRatio = (double)(bounds.Right - screenX) / bounds.Height;
            settings.SlotTopRatio = (double)(screenY - bounds.Top) / bounds.Height;
        }
        else
        {
            settings.SlotRightRatio = null;
            settings.SlotTopRatio = null;
        }
    }
}
