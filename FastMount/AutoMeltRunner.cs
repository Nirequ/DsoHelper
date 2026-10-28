using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DsoHelper.Infrastructure;

namespace DsoHelper.FastMount;

/// <summary>Итог одного прохода авторасплавки.</summary>
internal sealed record AutoMeltResult(
    int SentToFurnace,
    int MeltPresses,
    int ProductiveTabs,
    int TabsVisited,
    string Summary);

/// <summary>
/// Авторасплавка: проходит выбранные вкладки инвентаря (как автопродажа), сканирует
/// сетку 7×4 и правым кликом отправляет в открытую плавильню только неопознанные
/// предметы («?») — опознанные не трогает. Печь принимает 3×3 = 9 предметов за партию,
/// поэтому каждые 9 отправок (и в конце, если остался хвост) нажимается кнопка
/// «Переплавить».
///
/// Перед запуском пользователь должен открыть в игре окно плавильни и инвентарь так,
/// чтобы они не перекрывали друг друга: клики идут по запомненным точкам, а скан
/// сетки читает экран. После первого правого клика прогон проверяет, что предмет
/// действительно ушёл в печь, и останавливается, если окно плавильни закрыто.
/// Курсор после прохода возвращается на исходную позицию.
/// </summary>
internal static class AutoMeltRunner
{
    /// <summary>Верхняя граница паузы после переплавки, мс (ползунок на странице «MELT»).</summary>
    public const int ProcessDelayMaxMs = 2000;

    /// <summary>Верхняя граница пауз переключения вкладки и отправки предмета, мс.</summary>
    private const int InputDelayMaxMs = 1000;

    public static bool TryValidate(FastMountSettings settings, out string error)
    {
        if (!HotkeyParser.TryParseHotkey(settings.MeltHotkey, out _, out var hotkeyError))
        {
            error = hotkeyError;
            return false;
        }

        if (!InventoryGridResolver.HasSlot1(settings) || !InventoryGridResolver.HasSlot28(settings))
        {
            error = "Сетка инвентаря не откалибрована: укажите слоты #1 и #28 на странице «ИНВЕНТАРЬ».";
            return false;
        }

        if (!MeltFurnaceResolver.HasButton(settings))
        {
            error = "Не указана кнопка «Переплавить»: откройте окно плавильни и укажите кнопку на странице «MELT».";
            return false;
        }

        if (TabResolver.ParseSelection(settings.MeltTabs).Count == 0)
        {
            error = "Не выбрана ни одна вкладка для обхода авторасплавкой.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static async Task<AutoMeltResult> RunAsync(
        FastMountSettings settings,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(settings, out var error))
        {
            throw new InvalidOperationException(error);
        }

        if (!InventoryGridResolver.TryResolve(settings, out var slots, out var gridError))
        {
            throw new InvalidOperationException(gridError);
        }

        if (!MeltFurnaceResolver.TryResolveButton(settings, out var meltX, out var meltY))
        {
            throw new InvalidOperationException(
                "Не удалось определить кнопку «Переплавить»: укажите её заново.");
        }

        var tabs = TabResolver.ParseSelection(settings.MeltTabs);
        foreach (var tab in tabs)
        {
            if (TabResolver.IndexOf(tab) > 1 && !TabResolver.TryResolve(settings, tab, out _, out _))
            {
                throw new InvalidOperationException(
                    $"Вкладка {TabResolver.DisplayName(tab)}: не задана позиция вкладок. " +
                    "Откалибруйте их на странице «ИНВЕНТАРЬ» в блоке «II. Калибровка вкладок».");
            }
        }

        if (!GameWindow.IsForeground())
        {
            throw new InvalidOperationException(
                "Окно игры не в фокусе — переключитесь в Drakensang и запустите авторасплавку снова.");
        }

        var switchDelay = Math.Clamp(settings.MeltSwitchDelayMs, 0, InputDelayMaxMs);
        var itemDelay = Math.Clamp(settings.MeltItemDelayMs, 0, InputDelayMaxMs);
        var processDelay = Math.Clamp(settings.MeltProcessDelayMs, 0, ProcessDelayMaxMs);

        var hasOriginalCursor = NativeMethods.GetCursorPos(out var originalCursor);

        var sent = 0;
        var presses = 0;
        var productiveTabs = 0;
        var tabsVisited = 0;
        var furnaceFlushed = false;
        var firstSendChecked = false;

        try
        {
            foreach (var tab in tabs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureGameIsForeground();

                var tabName = TabResolver.DisplayName(tab);

                // 1-я вкладка при одном выборе — оставляем как есть (инвентарь уже открыт на ней).
                if (tabs.Count > 1 || TabResolver.IndexOf(tab) > 1)
                {
                    TabResolver.TryResolve(settings, tab, out var tabX, out var tabY);
                    InputSimulator.LeftClick(tabX, tabY);
                    report($"Вкладка {tabName}: открываю…");
                    await DelayAsync(switchDelay, cancellationToken);
                }

                tabsVisited++;
                report($"Вкладка {tabName}: сканирую сетку…");
                var (results, captureError) = await InventoryScanner.CaptureAndClassifyAsync(slots);
                if (results is null)
                {
                    throw new InvalidOperationException(captureError);
                }

                var targets = results.Where(MatchesMeltFilter).ToList();
                if (targets.Count == 0)
                {
                    continue;
                }

                productiveTabs++;

                // В печи могли остаться предметы прошлой партии: переплавляем их до того,
                // как начнём отправлять новые, иначе партии перемешаются.
                if (!furnaceFlushed)
                {
                    EnsureGameIsForeground();
                    report("Очищаю печь от остатков прошлой партии…");
                    InputSimulator.LeftClick(meltX, meltY);
                    presses++;
                    await DelayAsync(processDelay, cancellationToken);
                    furnaceFlushed = true;
                }

                var inTab = 0;
                foreach (var target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureGameIsForeground();
                    InputSimulator.RightClick(target.CenterX, target.CenterY);
                    sent++;
                    inTab++;
                    report(
                        $"Вкладка {tabName}: отправляю в печь слот #{target.Index} " +
                        $"({inTab}/{targets.Count})…");
                    await DelayAsync(itemDelay, cancellationToken);

                    // Первый отправленный предмет проверяем по экрану: если ПКМ не отправил
                    // его в печь (окно плавильни закрыто, сетка перекрыта, клик потерян),
                    // дальше кликать вслепую опасно — останавливаемся с понятным текстом.
                    if (!firstSendChecked)
                    {
                        await EnsureItemLeftInventoryAsync(
                            slots,
                            target,
                            meltX,
                            meltY,
                            cancellationToken);
                        firstSendChecked = true;
                    }

                    if (sent % MeltFurnaceResolver.FurnaceSlots == 0)
                    {
                        EnsureGameIsForeground();
                        report(
                            $"Печь заполнена ({MeltFurnaceResolver.FurnaceSlots} предметов) — " +
                            "переплавляю…");
                        InputSimulator.LeftClick(meltX, meltY);
                        presses++;
                        await DelayAsync(processDelay, cancellationToken);
                    }
                }
            }

            if (sent % MeltFurnaceResolver.FurnaceSlots != 0)
            {
                EnsureGameIsForeground();
                report("Переплавляю остаток…");
                InputSimulator.LeftClick(meltX, meltY);
                presses++;
                await DelayAsync(processDelay, cancellationToken);
            }

            var summary = sent == 0
                ? "Подходящих предметов не найдено — печь не запускалась."
                : $"В печь отправлено {sent} предмет(ов) за {presses} запуск(ов) печи; " +
                  $"вкладок с предметами: {productiveTabs} из {tabsVisited}, курсор возвращён.";

            return new AutoMeltResult(sent, presses, productiveTabs, tabsVisited, summary);
        }
        finally
        {
            if (hasOriginalCursor)
            {
                NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            }
        }
    }

    /// <summary>
    /// Подходит ли ячейка под фильтр авторасплавки: расплавляются только неопознанные
    /// предметы («?»), независимо от их редкости. Пустые ячейки и опознанные предметы
    /// не трогаются — опознанные остаются в сумке.
    /// </summary>
    private static bool MatchesMeltFilter(InventorySlotResult slot)
    {
        return !slot.IsEmpty && slot.Unidentified;
    }

    /// <summary>
    /// Проверяет, что первый отправленный правым кликом предмет действительно ушёл из
    /// ячейки. Окно плавильни забирает «?»-предмет: ячейка либо пустеет (предмет ушёл
    /// в печь), либо предмет перестаёт быть неопознанным (плавильня его опознала).
    /// Если же в ячейке всё ещё лежит «?», клик не сработал (плавильня закрыта, окно
    /// плавильни перекрыло сетку, ПКМ ушёл мимо) — прогон останавливается. Курсор на
    /// время проверки уводится на кнопку «Переплавить», чтобы тултип не перекрывал
    /// ячейку. Нечитаемый кадр не считается провалом — сама проверка не должна ломать
    /// прогон.
    /// </summary>
    private static async Task EnsureItemLeftInventoryAsync(
        IReadOnlyList<InventorySlotRect> slots,
        InventorySlotResult sentSlot,
        int parkX,
        int parkY,
        CancellationToken cancellationToken)
    {
        const int attempts = 6;
        const int waitMs = 250;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            NativeMethods.SetCursorPos(parkX, parkY);
            await DelayAsync(waitMs, cancellationToken);

            var (results, _) = await InventoryScanner.CaptureAndClassifyAsync(slots);
            if (results is null)
            {
                return;
            }

            var current = results.FirstOrDefault(result => result.Index == sentSlot.Index);
            if (current is null || current.IsEmpty || !current.Unidentified)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Предмет «?» из ячейки #{sentSlot.Index} не ушёл в плавильню после правого клика. " +
            "Проверьте, что окно плавильни открыто и не перекрывает сетку инвентаря, " +
            "затем запустите авторасплавку снова.");
    }

    /// <summary>
    /// Клики вслепую опасны: если фокус ушёл из игры, ПКМ и «Переплавить» попадут
    /// в другое окно. Лучше остановиться и сказать об этом.
    /// </summary>
    private static void EnsureGameIsForeground()
    {
        if (!GameWindow.IsForeground())
        {
            throw new InvalidOperationException("Окно игры потеряло фокус — авторасплавка остановлена.");
        }
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
