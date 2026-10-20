namespace DsoHelper.FastMount;

/// <summary>
/// Optional point on the first inventory tab. The reference tool clicks that tab
/// right after opening the inventory, so the cape always sits in the same cell.
/// </summary>
internal static class TabResolver
{
    public static bool HasTab(FastMountSettings settings) =>
        ClientPointResolver.IsSet(
            settings.TabRightRatio,
            settings.TabTopRatio,
            settings.TabX,
            settings.TabY);

    public static bool TryResolve(FastMountSettings settings, out int x, out int y) =>
        ClientPointResolver.TryResolve(
            settings.TabRightRatio,
            settings.TabTopRatio,
            settings.TabX,
            settings.TabY,
            out x,
            out y);

    public static void Capture(FastMountSettings settings, int screenX, int screenY)
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
}
