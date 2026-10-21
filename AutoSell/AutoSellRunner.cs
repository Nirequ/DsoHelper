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

        if (settings.SellUseRarity && !RarityCatalog.AnySelected(settings))
        {
            error = "Выберите хотя бы одну редкость для продажи или отключите фильтр редкости.";
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
        var cellWidth = Math.Abs(stepX) > 0 ? Math.Abs(stepX) : 44;
        var cellHeight = Math.Abs(stepY) > 0 ? Math.Abs(stepY) : 44;
        var targets = new List<SellTarget>();

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

                targets.Add(new SellTarget(
                    index,
                    x,
                    y,
                    x - cellWidth / 2 - bounds.Left,
                    y - cellHeight / 2 - bounds.Top,
                    cellWidth,
                    cellHeight));
            }
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException("Все ячейки области отключены — нечего продавать.");
        }

        var hasOriginalCursor = NativeMethods.GetCursorPos(out var originalCursor);

        try
        {
            if (hasOriginalCursor && settings.SellUseRarity)
            {
                // Move the cursor away so a tooltip or hover highlight cannot hide
                // the item borders while the screen is being read.
                NativeMethods.SetCursorPos(bounds.Left + 2, bounds.Top + 2);
                await Task.Delay(120, cancellationToken);
            }

            if (settings.SellUseRarity)
            {
                if (!RarityCatalog.AnySelected(settings))
                {
                    throw new InvalidOperationException(
                        "Выберите хотя бы одну редкость для продажи или отключите фильтр редкости.");
                }

                report("Определяю редкость предметов");
                ScreenCaptureResult capture;
                try
                {
                    capture = ScreenCapture.Capture(bounds);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Не удалось снять экран для определения редкости: " + ex.Message, ex);
                }

                var allowed = new List<SellTarget>();
                var unknown = 0;

                foreach (var target in targets)
                {
                    var rarity = RarityDetector.Detect(
                        capture.Pixels,
                        capture.Width,
                        capture.Height,
                        target.Left,
                        target.Top,
                        target.Width,
                        target.Height);

                    if (rarity == ItemRarity.Unknown)
                    {
                        unknown++;
                    }

                    if (RarityCatalog.IsSellable(settings, rarity))
                    {
                        allowed.Add(target);
                    }
                }

                if (allowed.Count == 0)
                {
                    throw new InvalidOperationException(
                        unknown == targets.Count
                            ? "Редкость не распознана ни у одного предмета. Отключите фильтр редкости или проверьте область по снимку."
                            : "Среди предметов нет выбранных редкостей — продавать нечего.");
                }

                targets = allowed;

                if (unknown > 0)
                {
                    report($"Редкость не распознана у {unknown} — пропускаю их");
                }
            }

            var clicked = 0;

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Never click on another application if the game lost focus mid-run.
                if (!GameWindow.IsForeground())
                {
                    throw new InvalidOperationException(
                        "Игра перестала быть активной — продажа остановлена.");
                }

                InputSimulator.RightClick(target.X, target.Y);
                clicked++;

                if (clicked % 5 == 0)
                {
                    report($"Продаю: {clicked} из {targets.Count}");
                }

                await DelayAsync(settings.DelayMs, cancellationToken);
            }

            report($"Обработано ячеек: {clicked} из {targets.Count}");
        }
        finally
        {
            if (hasOriginalCursor)
            {
                NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            }
        }
    }

    private readonly record struct SellTarget(
        int Index,
        int X,
        int Y,
        int Left,
        int Top,
        int Width,
        int Height);

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
