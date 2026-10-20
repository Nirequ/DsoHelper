using System;
using DsoHelper.Infrastructure;

namespace DsoHelper.FastMount;

/// <summary>
/// Inventory tabs. The user calibrates the first tab and the distance to the
/// second one; every other tab is calculated from that step (like the AHK script).
/// </summary>
internal static class TabResolver
{
    public const string PremiumKey = "prem";

    public static readonly string[] Keys =
    [
        "1", "2", "3", "4", "5", "6", "7", "8", PremiumKey
    ];

    public static string DisplayName(string key) =>
        key == PremiumKey ? PremiumKey : key;

    /// <summary>1-based tab number, or 0 for an unknown key.</summary>
    public static int IndexOf(string key)
    {
        var index = Array.IndexOf(Keys, key);
        return index < 0 ? 0 : index + 1;
    }

    public static bool HasBase(FastMountSettings settings) =>
        ClientPointResolver.IsSet(
            settings.TabRightRatio,
            settings.TabTopRatio,
            settings.TabX,
            settings.TabY);

    public static bool HasStep(FastMountSettings settings) =>
        settings.TabStepRightRatio is not null ||
        settings.TabStepTopRatio is not null ||
        settings.TabStepX != 0 ||
        settings.TabStepY != 0;

    public static bool TryResolve(FastMountSettings settings, string key, out int x, out int y)
    {
        x = 0;
        y = 0;

        var index = IndexOf(key);
        if (index == 0)
        {
            return false;
        }

        if (!ClientPointResolver.TryResolve(
                settings.TabRightRatio,
                settings.TabTopRatio,
                settings.TabX,
                settings.TabY,
                out x,
                out y))
        {
            return false;
        }

        if (index > 1)
        {
            x += StepX(settings) * (index - 1);
            y += StepY(settings) * (index - 1);
        }

        return true;
    }

    public static int StepPixelsX(FastMountSettings settings) => StepX(settings);

    public static int StepPixelsY(FastMountSettings settings) => StepY(settings);

    private static int StepX(FastMountSettings settings)
    {
        if (settings.TabStepRightRatio is { } ratio && GameWindow.TryGetClientBounds(out var bounds))
        {
            return (int)Math.Round(ratio * bounds.Height);
        }

        return settings.TabStepX;
    }

    private static int StepY(FastMountSettings settings)
    {
        if (settings.TabStepTopRatio is { } ratio && GameWindow.TryGetClientBounds(out var bounds))
        {
            return (int)Math.Round(ratio * bounds.Height);
        }

        return settings.TabStepY;
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

        settings.TabX = x;
        settings.TabY = y;
        settings.TabRightRatio = rightRatio;
        settings.TabTopRatio = topRatio;
    }

    /// <summary>
    /// The user points at the second tab; the distance from the first tab becomes the step.
    /// </summary>
    public static bool CaptureStep(FastMountSettings settings, int screenX, int screenY)
    {
        if (!ClientPointResolver.TryResolve(
                settings.TabRightRatio,
                settings.TabTopRatio,
                settings.TabX,
                settings.TabY,
                out var firstX,
                out var firstY))
        {
            return false;
        }

        var stepX = screenX - firstX;
        var stepY = screenY - firstY;

        settings.TabStepX = stepX;
        settings.TabStepY = stepY;

        if (GameWindow.TryGetClientBounds(out var bounds))
        {
            settings.TabStepRightRatio = (double)stepX / bounds.Height;
            settings.TabStepTopRatio = (double)stepY / bounds.Height;
        }
        else
        {
            settings.TabStepRightRatio = null;
            settings.TabStepTopRatio = null;
        }

        return true;
    }
}
