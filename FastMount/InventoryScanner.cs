using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DsoHelper.Infrastructure;

namespace DsoHelper.FastMount;

/// <summary>
/// Захват клиента игры и классификация сетки инвентаря 7×4. Общий шаг для проверки
/// инвентаря, обхода курсором и авторасплавки: GameWindow → ScreenCapture →
/// InventoryChecker. Если окно не найдено или кадр не захватился, возвращается
/// null-результат и понятный пользователю текст ошибки.
/// </summary>
internal static class InventoryScanner
{
    /// <summary>Снимает клиент игры и классифицирует переданные прямоугольники ячеек.</summary>
    public static async Task<(List<InventorySlotResult>? Results, string Error)> CaptureAndClassifyAsync(
        IReadOnlyList<InventorySlotRect> slots)
    {
        if (!GameWindow.TryGetClientBounds(out var client))
        {
            return (null, "Окно игры не найдено — откройте Drakensang.");
        }

        return await Task.Run(() =>
        {
            if (!ScreenCapture.TryCapture(
                    client.Left, client.Top, client.Width, client.Height,
                    out var frame, out var captureError))
            {
                return (Results: (List<InventorySlotResult>?)null, Error: captureError);
            }

            var results = InventoryChecker.CheckByGrid(
                frame!, client.Width, client.Height, slots, client.Left, client.Top);
            return (Results: (List<InventorySlotResult>?)results, Error: string.Empty);
        });
    }
}
