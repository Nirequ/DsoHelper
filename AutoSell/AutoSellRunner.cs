using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DsoHelper.FastMount;
using DsoHelper.Infrastructure;

namespace DsoHelper.AutoSell;

/// <summary>
/// Sells items in a calibrated bag grid with right clicks. The trader window
/// ("Купить и продать") must already be open: the macro only clicks, it never
/// opens or closes game windows on its own.
/// </summary>
internal static class AutoSellRunner
{
    public static bool TryValidate(FastMountSettings settings, out string error)
    {
        if (!HotkeyParser.TryParseHotkey(settings.SellHotkey, out _, out var hotkeyError))
        {
            error = "Хоткей авто-продажи: " + hotkeyError;
            return false;
        }

        if (!SellGridResolver.HasBase(settings))
        {
            error = "Выберите первую ячейку области продажи: нажмите «Ячейка 1» и кликните по сумке.";
            return false;
        }

        if (settings.SellColumns < 1 || settings.SellRows < 1 ||
            settings.SellColumns > SellGridResolver.MaxColumns ||
            settings.SellRows > SellGridResolver.MaxRows)
        {
            error = $"Столбцы и строки должны быть от 1 до {SellGridResolver.MaxColumns}.";
            return false;
        }

        if ((settings.SellColumns > 1 || settings.SellRows > 1) &&
            !SellGridResolver.HasStep(settings))
        {
            error = "Укажите шаг ячеек: нажмите «Шаг» и кликните по соседней ячейке.";
            return false;
        }

        if (settings.DelayMs < 0)
        {
            error = "Задержка не может быть отрицательной.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static async Task RunAsync(
        FastMountSettings settings,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(settings, out var error))
        {
            throw new InvalidOperationException(error);
        }

        if (!SellGridResolver.TryResolveGrid(
                settings,
                out var firstX,
                out var firstY,
                out var stepX,
                out var stepY))
        {
            throw new InvalidOperationException(
                "Не удалось определить область продажи. Выберите ячейки заново.");
        }

        if (!GameWindow.TryGetClientBounds(out var bounds))
        {
            throw new InvalidOperationException("Окно Drakensang не найдено — откройте игру и окно торговца.");
        }

        var skip = settings.SellSkip;
        var targets = new List<(int X, int Y)>();

        for (var row = 0; row < settings.SellRows; row++)
        {
            for (var column = 0; column < settings.SellColumns; column++)
            {
                var index = SellGridResolver.IndexOf(column, row, settings.SellColumns);
                if (skip.Contains(index))
                {
                    continue;
                }

                var x = firstX + column * stepX;
                var y = firstY + row * stepY;

                if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
                {
                    throw new InvalidOperationException(
                        "Ячейка области оказалась вне окна игры. Проверьте калибровку области продажи.");
                }

                targets.Add((x, y));
            }
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException("Все ячейки области отключены — нечего продавать.");
        }

        var hasOriginalCursor = NativeMethods.GetCursorPos(out var originalCursor);
        var clicked = 0;

        try
        {
            foreach (var (x, y) in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Never click on another application if the game lost focus mid-run.
                if (!GameWindow.IsForeground())
                {
                    throw new InvalidOperationException(
                        "Игра перестала быть активной — продажа остановлена.");
                }

                InputSimulator.RightClick(x, y);
                clicked++;

                if (clicked % 5 == 0)
                {
                    report($"Продаю: {clicked} из {targets.Count}");
                }

                await DelayAsync(settings.DelayMs, cancellationToken);
            }
        }
        finally
        {
            if (hasOriginalCursor)
            {
                NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            }
        }

        report($"Обработано ячеек: {clicked} из {targets.Count}");
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds > 0)
        {
            await Task.Delay(milliseconds, cancellationToken);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
