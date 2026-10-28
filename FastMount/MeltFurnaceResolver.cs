namespace DsoHelper.FastMount;

/// <summary>
/// Кнопка «Переплавить» (Melt Down) в окне плавильни DSO. Точка хранится в той же
/// схеме ClientPointResolver, что и остальные: абсолютные экранные пиксели плюс
/// смещение от правого/верхнего края клиентской области в долях её высоты, поэтому
/// кнопка остаётся на месте при смене разрешения или размера окна игры.
/// </summary>
internal static class MeltFurnaceResolver
{
    /// <summary>Сколько предметов вмещает печь за одну переплавку: сетка 3×3.</summary>
    public const int FurnaceSlots = 9;

    public static bool HasButton(FastMountSettings settings) =>
        ClientPointResolver.IsSet(
            settings.MeltButtonRightRatio,
            settings.MeltButtonTopRatio,
            settings.MeltButtonX,
            settings.MeltButtonY);

    public static bool TryResolveButton(FastMountSettings settings, out int x, out int y) =>
        ClientPointResolver.TryResolve(
            settings.MeltButtonRightRatio,
            settings.MeltButtonTopRatio,
            settings.MeltButtonX,
            settings.MeltButtonY,
            out x,
            out y);

    public static void CaptureButton(FastMountSettings settings, int screenX, int screenY)
    {
        ClientPointResolver.Capture(
            screenX,
            screenY,
            out var x,
            out var y,
            out var rightRatio,
            out var topRatio);

        settings.MeltButtonX = x;
        settings.MeltButtonY = y;
        settings.MeltButtonRightRatio = rightRatio;
        settings.MeltButtonTopRatio = topRatio;
    }
}
