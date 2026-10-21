using System;
using DsoHelper.Infrastructure;

namespace DsoHelper.FastMount;

internal readonly record struct SellCell(int Index, int Column, int Row, int X, int Y);

/// <summary>
/// Bag grid used by the auto-sell feature. The user calibrates the first cell
/// and the distance to the second one; every other cell is calculated from that
/// step, exactly like the inventory tabs.
/// </summary>
internal static class SellGridResolver
{
    public const int MaxColumns = 20;
    public const int MaxRows = 20;

    public static bool HasBase(FastMountSettings settings) =>
        ClientPointResolver.IsSet(
            settings.SellRightRatio,
            settings.SellTopRatio,
            settings.SellX,
            settings.SellY);

    public static bool HasStep(FastMountSettings settings) =>
        settings.SellStepRightRatio is not null ||
        settings.SellStepTopRatio is not null ||
        settings.SellStepX != 0 ||
        settings.SellStepY != 0;

    public static int StepPixelsX(FastMountSettings settings) => StepX(settings);

    public static int StepPixelsY(FastMountSettings settings) => StepY(settings);

    private static int StepX(FastMountSettings settings)
    {
        if (settings.SellStepRightRatio is { } ratio && GameWindow.TryGetClientBounds(out var bounds))
        {
            return (int)Math.Round(ratio * bounds.Height);
        }

        return settings.SellStepX;
    }

    private static int StepY(FastMountSettings settings)
    {
        if (settings.SellStepTopRatio is { } ratio && GameWindow.TryGetClientBounds(out var bounds))
        {
            return (int)Math.Round(ratio * bounds.Height);
        }

        return settings.SellStepY;
    }

    public static void CaptureBase(FastMountSettings settings, int screenX, int screenY)
    {
        ClientPointResolver.Capture(
            screenX,
            screenY,
            out var x,
            out var y,
            out var rightRatio,
            out var topRatio);

        settings.SellX = x;
        settings.SellY = y;
        settings.SellRightRatio = rightRatio;
        settings.SellTopRatio = topRatio;
    }

    /// <summary>
    /// The user points at the second cell; the distance from the first one becomes
    /// the step. One cell grids do not need a step.
    /// </summary>
    public static bool CaptureStep(FastMountSettings settings, int screenX, int screenY)
    {
        if (!TryResolveBase(settings, out var firstX, out var firstY))
        {
            return false;
        }

        var stepX = screenX - firstX;
        var stepY = screenY - firstY;

        settings.SellStepX = stepX;
        settings.SellStepY = stepY;

        if (GameWindow.TryGetClientBounds(out var bounds))
        {
            settings.SellStepRightRatio = (double)stepX / bounds.Height;
            settings.SellStepTopRatio = (double)stepY / bounds.Height;
        }
        else
        {
            settings.SellStepRightRatio = null;
            settings.SellStepTopRatio = null;
        }

        return true;
    }

    public static bool TryResolveBase(FastMountSettings settings, out int x, out int y) =>
        ClientPointResolver.TryResolve(
            settings.SellRightRatio,
            settings.SellTopRatio,
            settings.SellX,
            settings.SellY,
            out x,
            out y);

    /// <summary>
    /// Enumerates the grid in the order the macro clicks it: row by row, left to right.
    /// </summary>
    public static bool TryResolveGrid(
        FastMountSettings settings,
        out int firstX,
        out int firstY,
        out int stepX,
        out int stepY)
    {
        stepX = StepX(settings);
        stepY = StepY(settings);

        return TryResolveBase(settings, out firstX, out firstY) &&
               settings.SellColumns > 0 &&
               settings.SellRows > 0;
    }

    public static int IndexOf(int column, int row, int columns) => row * columns + column;

    public static void Normalize(FastMountSettings settings)
    {
        settings.SellColumns = Math.Clamp(settings.SellColumns, 1, MaxColumns);
        settings.SellRows = Math.Clamp(settings.SellRows, 1, MaxRows);
    }
}
