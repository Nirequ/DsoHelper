using System;
using DsoHelper.Infrastructure;

namespace DsoHelper.FastMount;

/// <summary>
/// Resolves a point inside the game client area. The point is stored as a ratio
/// based on the client height, so it stays valid after a resolution change.
/// </summary>
internal static class ClientPointResolver
{
    public static bool IsSet(double? rightRatio, double? topRatio, int fallbackX, int fallbackY) =>
        (rightRatio is not null && topRatio is not null) ||
        fallbackX != 0 ||
        fallbackY != 0;

    public static bool TryResolve(
        double? rightRatio,
        double? topRatio,
        int fallbackX,
        int fallbackY,
        out int x,
        out int y)
    {
        x = 0;
        y = 0;

        if (rightRatio is { } right &&
            topRatio is { } top &&
            GameWindow.TryGetClientBounds(out var bounds))
        {
            x = bounds.Right - (int)Math.Round(right * bounds.Height);
            y = bounds.Top + (int)Math.Round(top * bounds.Height);
            x = Math.Clamp(x, bounds.Left, bounds.Right - 1);
            y = Math.Clamp(y, bounds.Top, bounds.Bottom - 1);
            return true;
        }

        if (fallbackX == 0 && fallbackY == 0)
        {
            return false;
        }

        x = fallbackX;
        y = fallbackY;
        return true;
    }

    public static void Capture(
        int screenX,
        int screenY,
        out int x,
        out int y,
        out double? rightRatio,
        out double? topRatio)
    {
        x = screenX;
        y = screenY;

        if (GameWindow.TryGetClientBounds(out var bounds) &&
            screenX >= bounds.Left && screenX < bounds.Right &&
            screenY >= bounds.Top && screenY < bounds.Bottom)
        {
            rightRatio = (double)(bounds.Right - screenX) / bounds.Height;
            topRatio = (double)(screenY - bounds.Top) / bounds.Height;
        }
        else
        {
            rightRatio = null;
            topRatio = null;
        }
    }
}
