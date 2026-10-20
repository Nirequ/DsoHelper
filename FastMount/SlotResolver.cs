namespace DsoHelper.FastMount;

internal static class SlotResolver
{
    public static bool HasSlot(FastMountSettings settings) =>
        ClientPointResolver.IsSet(
            settings.SlotRightRatio,
            settings.SlotTopRatio,
            settings.SlotX,
            settings.SlotY);

    /// <summary>
    /// Recalculates the slot position for the current game window size.
    /// The ratio is based on the client height (like the reference AHK script),
    /// so the slot stays in place after switching resolutions or window modes.
    /// </summary>
    public static bool TryResolve(FastMountSettings settings, out int x, out int y) =>
        ClientPointResolver.TryResolve(
            settings.SlotRightRatio,
            settings.SlotTopRatio,
            settings.SlotX,
            settings.SlotY,
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

        settings.SlotX = x;
        settings.SlotY = y;
        settings.SlotRightRatio = rightRatio;
        settings.SlotTopRatio = topRatio;
    }
}
