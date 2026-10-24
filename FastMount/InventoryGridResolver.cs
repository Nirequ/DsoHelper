using System;
using System.Collections.Generic;
using DsoHelper.Infrastructure;

namespace DsoHelper.FastMount;

/// <summary>Прямоугольник одной из 28 ячеек инвентаря в экранных координатах.</summary>
internal sealed record InventorySlotRect(
    int Index,
    int Row,
    int Col,
    int X,
    int Y,
    int W,
    int H,
    int CenterX,
    int CenterY);

/// <summary>
/// Сетка инвентаря DSO 7×4: по клик-центрам слота #1 (левый верхний) и слота #28
/// (правый нижний) вычисляет прямоугольники всех 28 ячеек. Шаг:
/// stepX = (x28 − x1) / 6, stepY = (y28 − y1) / 3. Точки хранятся в той же
/// схеме ClientPointResolver, что и слот плаща: абсолютные пиксели +
/// ratio от высоты клиентской области окна игры (удерживаются при смене разрешения).
/// </summary>
internal static class InventoryGridResolver
{
    public const int Cols = 7;
    public const int Rows = 4;
    public const int SlotCount = Cols * Rows; // 28

    public static bool HasSlot1(FastMountSettings settings) =>
        ClientPointResolver.IsSet(
            settings.InvSlot1RightRatio,
            settings.InvSlot1TopRatio,
            settings.InvSlot1X,
            settings.InvSlot1Y);

    public static bool HasSlot28(FastMountSettings settings) =>
        ClientPointResolver.IsSet(
            settings.InvSlot28RightRatio,
            settings.InvSlot28TopRatio,
            settings.InvSlot28X,
            settings.InvSlot28Y);

    /// <summary>Разбирает сетку на 28 прямоугольников. Ячейка i: row = (i−1)/7, col = (i−1)%7.</summary>
    public static bool TryResolve(FastMountSettings settings, out List<InventorySlotRect> slots, out string error)
    {
        slots = [];

        if (!HasSlot1(settings) || !HasSlot28(settings))
        {
            error = "Сетка не откалибрована: укажите слот #1 и слот #28.";
            return false;
        }

        if (!ClientPointResolver.TryResolve(
                settings.InvSlot1RightRatio, settings.InvSlot1TopRatio, settings.InvSlot1X, settings.InvSlot1Y,
                out var x1, out var y1) ||
            !ClientPointResolver.TryResolve(
                settings.InvSlot28RightRatio, settings.InvSlot28TopRatio, settings.InvSlot28X, settings.InvSlot28Y,
                out var x28, out var y28))
        {
            error = "Не удалось вычислить сетку: запустите игру или укажите слоты 1 и 28 заново.";
            return false;
        }

        var stepX = (x28 - x1) / (double)(Cols - 1);
        var stepY = (y28 - y1) / (double)(Rows - 1);
        if (stepX <= 4 || stepY <= 4)
        {
            error = "Шаг ячеек некорректен: укажите слоты #1 и #28 заново.";
            return false;
        }

        var w = (int)Math.Round(stepX);
        var h = (int)Math.Round(stepY);
        var halfW = w / 2;
        var halfH = h / 2;

        var index = 1;
        for (var row = 0; row < Rows; row++)
        {
            for (var col = 0; col < Cols; col++)
            {
                var centerX = (int)Math.Round(x1 + col * stepX);
                var centerY = (int)Math.Round(y1 + row * stepY);
                slots.Add(new InventorySlotRect(
                    index++,
                    row,
                    col,
                    centerX - halfW,
                    centerY - halfH,
                    w,
                    h,
                    centerX,
                    centerY));
            }
        }

        error = string.Empty;
        return true;
    }

    public static void CaptureSlot1(FastMountSettings settings, int screenX, int screenY)
    {
        ClientPointResolver.Capture(screenX, screenY, out var x, out var y, out var rightRatio, out var topRatio);
        settings.InvSlot1X = x;
        settings.InvSlot1Y = y;
        settings.InvSlot1RightRatio = rightRatio;
        settings.InvSlot1TopRatio = topRatio;
    }

    public static void CaptureSlot28(FastMountSettings settings, int screenX, int screenY)
    {
        ClientPointResolver.Capture(screenX, screenY, out var x, out var y, out var rightRatio, out var topRatio);
        settings.InvSlot28X = x;
        settings.InvSlot28Y = y;
        settings.InvSlot28RightRatio = rightRatio;
        settings.InvSlot28TopRatio = topRatio;
    }
}
