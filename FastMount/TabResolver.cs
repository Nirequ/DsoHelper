using System.Collections.Generic;

namespace DsoHelper.FastMount;

/// <summary>
/// Inventory tabs. The reference tool clicks the first tab after opening the
/// inventory; here the user can pick any tab and save a position for each one.
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

    public static TabPoint? Find(FastMountSettings settings, string key) =>
        settings.Tabs.TryGetValue(key, out var point) ? point : null;

    public static bool HasPoint(FastMountSettings settings, string key) =>
        Find(settings, key) is { } point &&
        ClientPointResolver.IsSet(point.RightRatio, point.TopRatio, point.X, point.Y);

    public static bool TryResolve(FastMountSettings settings, string key, out int x, out int y)
    {
        x = 0;
        y = 0;

        return Find(settings, key) is { } point &&
               ClientPointResolver.TryResolve(
                   point.RightRatio,
                   point.TopRatio,
                   point.X,
                   point.Y,
                   out x,
                   out y);
    }

    public static void Capture(FastMountSettings settings, string key, int screenX, int screenY)
    {
        ClientPointResolver.Capture(
            screenX,
            screenY,
            out var x,
            out var y,
            out var rightRatio,
            out var topRatio);

        settings.Tabs[key] = new TabPoint
        {
            X = x,
            Y = y,
            RightRatio = rightRatio,
            TopRatio = topRatio
        };
    }
}
