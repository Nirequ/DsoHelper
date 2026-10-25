using System;
using System.Threading;
using System.Threading.Tasks;
using DsoHelper.Infrastructure;

namespace DsoHelper.FastMount;

internal static class FastMountRunner
{
    private const int DoubleClickGapMs = 30;

    /// <summary>Пауза перед закрытием инвентаря, мс: клиент должен успеть обработать смену плаща.</summary>
    private const int PostActionDelayMs = 200;

    public static bool TryValidate(FastMountSettings settings, out string error)
    {
        if (!HotkeyParser.TryParseHotkey(settings.Hotkey, out _, out var hotkeyError))
        {
            error = hotkeyError;
            return false;
        }

        if (!HotkeyParser.TryParseKey(settings.InventoryKey, out _, out var inventoryError))
        {
            error = "Клавиша инвентаря: " + inventoryError;
            return false;
        }

        if (!HotkeyParser.TryParseKey(settings.MountKey, out _, out var mountError))
        {
            error = "Клавиша маунта: " + mountError;
            return false;
        }

        if (!SlotResolver.HasSlot(settings))
        {
            error = "Выберите слот плаща: нажмите «Выбрать слот» и перетащите квадрат на нужную ячейку.";
            return false;
        }

        if (settings.SwitchInventoryTab)
        {
            var tabIndex = TabResolver.IndexOf(settings.TabKey);
            if (tabIndex == 0)
            {
                error = "Выбрана неизвестная вкладка инвентаря.";
                return false;
            }

            if (!TabResolver.HasBase(settings))
            {
                error = "Укажите позицию 1-й вкладки: нажмите «Указать вкладку 1» во вкладке «ИНВЕНТАРЬ».";
                return false;
            }

            if (tabIndex > 1 && !TabResolver.HasStep(settings))
            {
                error = "Задайте шаг вкладок: нажмите «Указать вкладку 2» во вкладке «ИНВЕНТАРЬ».";
                return false;
            }
        }

        if (settings.DelayMs < 0)
        {
            error = "Задержка не может быть отрицательной.";
            return false;
        }

        if (settings.MountDelayMs < 0)
        {
            error = "Пауза после вызова маунта не может быть отрицательной.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Клики вслепую опасны: если фокус ушёл из игры, они попадут в другое окно, а плащ
    /// останется непереодетым. Лучше остановиться и сказать об этом, чем промолчать.
    /// </summary>
    private static void EnsureGameIsForeground()
    {
        if (!GameWindow.IsForeground())
        {
            throw new InvalidOperationException(
                "Окно игры потеряло фокус — макрос остановлен. Проверьте, вернулся ли плащ на место.");
        }
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

        if (!HotkeyParser.TryParseKey(settings.InventoryKey, out var inventoryKey, out _) ||
            !HotkeyParser.TryParseKey(settings.MountKey, out var mountKey, out _))
        {
            throw new InvalidOperationException("Не удалось распознать клавиши инвентаря или маунта.");
        }

        if (!SlotResolver.TryResolve(settings, out var slotX, out var slotY))
        {
            throw new InvalidOperationException(
                "Не удалось определить позицию слота. Выберите слот заново или запустите игру.");
        }

        var hasOriginalCursor = NativeMethods.GetCursorPos(out var originalCursor);

        try
        {
            await OpenInventoryAsync(settings, inventoryKey, report, cancellationToken);

            report("Надеваю плащ с быстрым маунтом");
            await DoubleLeftClickAsync(slotX, slotY, cancellationToken);
            await DelayAsync(settings.DelayMs, cancellationToken);

            report("Вызываю маунта");
            EnsureGameIsForeground();
            InputSimulator.KeyPress(mountKey);

            // Пока идёт призыв, клиент не принимает клики по инвентарю: без отдельной
            // паузы второй клик по плащу теряется и старый плащ остаётся в сумке.
            await DelayAsync(Math.Max(settings.DelayMs, settings.MountDelayMs), cancellationToken);

            report("Возвращаю старый плащ");
            EnsureGameIsForeground();
            await DoubleLeftClickAsync(slotX, slotY, cancellationToken);

            // Даём клиенту обработать смену плаща, прежде чем закрывать инвентарь.
            await DelayAsync(Math.Max(settings.DelayMs, PostActionDelayMs), cancellationToken);

            report("Закрываю инвентарь");
            InputSimulator.KeyPress(inventoryKey);
        }
        finally
        {
            if (hasOriginalCursor)
            {
                NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            }
        }
    }

    private static async Task OpenInventoryAsync(
        FastMountSettings settings,
        KeyInfo inventoryKey,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        report("Открываю инвентарь");
        InputSimulator.KeyPress(inventoryKey);
        await DelayAsync(settings.DelayMs, cancellationToken);

        if (!settings.SwitchInventoryTab)
        {
            return;
        }

        if (!TabResolver.TryResolve(settings, settings.TabKey, out var tabX, out var tabY))
        {
            throw new InvalidOperationException(
                $"Не удалось определить позицию вкладки {TabResolver.DisplayName(settings.TabKey)}. " +
                "Проверьте позицию 1-й вкладки и шаг вкладок.");
        }

        report($"Переключаюсь на вкладку {TabResolver.DisplayName(settings.TabKey)}");
        InputSimulator.LeftClick(tabX, tabY);
        await DelayAsync(settings.DelayMs, cancellationToken);
    }

    private static async Task DoubleLeftClickAsync(int x, int y, CancellationToken cancellationToken)
    {
        InputSimulator.LeftClick(x, y);
        await DelayAsync(DoubleClickGapMs, cancellationToken);
        InputSimulator.LeftClick(x, y);
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
