using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DsoHelper.FastMount;
using DsoHelper.Infrastructure;
using DsoHelper.UI;

namespace DsoHelper;

public partial class MainWindow : Window
{
    /// <summary>Диапазон и шаг задержки макроса, мс.</summary>
    private const int DelayMaxMs = 1000;
    private const int DelayStepMs = 15;

    // Mirrors UI/Palette.axaml (Drakensang helper palette).
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#8C93A2"));
    private static readonly IBrush BusyBrush = new SolidColorBrush(Color.Parse("#E0A93B"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#2CB67D"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#C93B3B"));

    private readonly FastMountSettings _settings;
    private readonly DispatcherTimer _saveTimer;

    private GlobalHotkey? _hotkey;
    private GlobalHotkey? _sellHotkey;
    private SlotPickerWindow? _slotPicker;
    private SlotPickerWindow? _tabPicker;
    private SlotPickerWindow? _tabStepPicker;
    private SlotPickerWindow? _invSlot1Picker;
    private SlotPickerWindow? _invSlot28Picker;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _hoverCts;
    private CancellationTokenSource? _sellCts;
    private bool _running;
    private bool _picking;
    private bool _checking;
    private bool _hovering;
    private bool _selling;
    private bool _checkingUpdates;
    private string _pendingUpdateTag = string.Empty;
    private string? _pendingUpdateUrl;
    private bool _delaySyncing;

    /// <summary>Пауза на каждой занятой ячейке при обходе курсором, мс.</summary>
    private const int HoverPerSlotMs = 550;

    /// <summary>Допустимый диапазон и шаг задержек автопродажи, мс.</summary>
    private const int SellDelayMaxMs = 1000;
    private const int SellDelayStepMs = 15;

    public MainWindow()
    {
        InitializeComponent();

        _settings = FastMountSettings.Load();
        LoadSettingsIntoUi();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };

        WireEvents();

        Topmost = _settings.AlwaysOnTop;
        ApplyHotkey(showError: false);
        ApplySellHotkey(showError: false);
        SetStatus("Готово к работе", StatusKind.Idle);

        SettingsPopup.PlacementTarget = SettingsButton;

        Closing += OnClosing;
        Opened += (_, _) =>
        {
            if (TryGetPlatformHandle() is { } handle)
            {
                WindowAppearance.UseDarkTitleBar(handle.Handle);
            }
        };
    }

    private void WireEvents()
    {
        AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        HotkeyBox.LostFocus += (_, _) => CommitHotkey();
        InventoryKeyBox.LostFocus += (_, _) => RevertKeyIfInvalid(InventoryKeyBox, _settings.InventoryKey);
        MountKeyBox.LostFocus += (_, _) => RevertKeyIfInvalid(MountKeyBox, _settings.MountKey);

        HotkeyBox.TextChanged += (_, _) => ScheduleSave();
        InventoryKeyBox.TextChanged += (_, _) => ScheduleSave();
        MountKeyBox.TextChanged += (_, _) => ScheduleSave();

        SellHotkeyBox.LostFocus += (_, _) => CommitSellHotkey();
        SellHotkeyBox.TextChanged += (_, _) => ScheduleSave();

        WireQualityChecks();

        WireDelayControls();
        WireDelayPair(SellSwitchSlider, SellSwitchValueBox, 200, v => _settings.SellSwitchDelayMs = v);
        WireDelayPair(SellItemSlider, SellItemValueBox, 150, v => _settings.SellItemDelayMs = v);

        SwitchTabCheck.IsCheckedChanged += (_, _) =>
        {
            _settings.SwitchInventoryTab = SwitchTabCheck.IsChecked == true;
            UpdateTabUi();
            ScheduleSave();
        };

        AlwaysOnTopCheck.IsCheckedChanged += (_, _) =>
        {
            Topmost = AlwaysOnTopCheck.IsChecked == true;
            ScheduleSave();
        };
    }

    /// <summary>Чекбоксы качеств автопродажи: каждое поле настройки привязано к своему боксу.</summary>
    private void WireQualityChecks()
    {
        void Wire(CheckBox box, Action<bool> set)
        {
            box.IsCheckedChanged += (_, _) =>
            {
                set(box.IsChecked == true);
                ScheduleSave();
            };
        }

        Wire(SellCommonCheck, value => _settings.SellCommon = value);
        Wire(SellImprovedCheck, value => _settings.SellImproved = value);
        Wire(SellMagicCheck, value => _settings.SellMagic = value);
        Wire(SellExtraordinaryCheck, value => _settings.SellExtraordinary = value);
        Wire(SellLegendaryCheck, value => _settings.SellLegendary = value);
        Wire(SellUniqueCheck, value => _settings.SellUnique = value);
        Wire(SellMythicCheck, value => _settings.SellMythic = value);
    }

    /// <summary>Ползунок задержки и поле ручного ввода: шаг 15 мс, диапазон 0..1000.</summary>
    private void WireDelayControls()
    {
        DelaySlider.ValueChanged += DelaySliderValueChanged;
        DelayValueBox.LostFocus += (_, _) => CommitDelayText();
        DelayValueBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitDelayText();
            }
        };
    }

    private void DelaySliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var clamped = Math.Clamp((int)Math.Round(e.NewValue), 0, DelayMaxMs);

        if (!_delaySyncing)
        {
            // Приводим шаг к DelayStepMs (ползунок).
            clamped = Math.Min((int)Math.Round(clamped / (double)DelayStepMs) * DelayStepMs, DelayMaxMs);

            if (Math.Abs(clamped - DelaySlider.Value) > 0.5)
            {
                DelaySlider.Value = clamped;
                return;
            }
        }

        if (!DelayValueBox.IsFocused && DelayValueBox.Text != clamped.ToString())
        {
            DelayValueBox.Text = clamped.ToString();
        }

        ScheduleSave();
    }

    private void LoadSettingsIntoUi()
    {
        HotkeyBox.Text = HotkeyParser.TryParseHotkey(_settings.Hotkey, out var hotkey, out _)
            ? hotkey.Display
            : _settings.Hotkey;
        SellHotkeyBox.Text = HotkeyParser.TryParseHotkey(_settings.SellHotkey, out var sellHotkey, out _)
            ? sellHotkey.Display
            : _settings.SellHotkey;
        InventoryKeyBox.Text = CanonicalKey(_settings.InventoryKey, "I");
        MountKeyBox.Text = CanonicalKey(_settings.MountKey, "M");

        var delay = Math.Clamp(_settings.DelayMs, 0, DelayMaxMs);
        DelaySlider.Value = delay;
        DelayValueBox.Text = delay.ToString();

        SellSwitchSlider.Value = Math.Clamp(_settings.SellSwitchDelayMs, 0, SellDelayMaxMs);
        SellItemSlider.Value = Math.Clamp(_settings.SellItemDelayMs, 0, SellDelayMaxMs);

        SwitchTabCheck.IsChecked = _settings.SwitchInventoryTab;

        AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;

        SellCommonCheck.IsChecked = _settings.SellCommon;
        SellImprovedCheck.IsChecked = _settings.SellImproved;
        SellMagicCheck.IsChecked = _settings.SellMagic;
        SellExtraordinaryCheck.IsChecked = _settings.SellExtraordinary;
        SellLegendaryCheck.IsChecked = _settings.SellLegendary;
        SellUniqueCheck.IsChecked = _settings.SellUnique;
        SellMythicCheck.IsChecked = _settings.SellMythic;

        UpdateSlotUi();
        UpdateTabUi();
        UpdateInvUi();
        UpdateSellTabsUi();
    }

    private void ReadSettingsFromUi()
    {
        if (HotkeyParser.TryParseHotkey(HotkeyBox.Text, out var hotkey, out _))
        {
            _settings.Hotkey = hotkey.Display;
        }

        _settings.InventoryKey = CanonicalKey(InventoryKeyBox.Text, _settings.InventoryKey);
        _settings.MountKey = CanonicalKey(MountKeyBox.Text, _settings.MountKey);

        _settings.DelayMs = (int)DelaySlider.Value;
        _settings.SwitchInventoryTab = SwitchTabCheck.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
    }
    private static string CanonicalKey(string? text, string fallback) =>
        KeyCatalog.TryParseKey(text, out var key) && !key.IsModifier ? key.Display : fallback;

    private void CommitDelayText()
    {
        if (!int.TryParse(DelayValueBox.Text?.Trim(), out var value) || value < 0)
        {
            value = _settings.DelayMs;
        }

        value = Math.Clamp(value, 0, DelayMaxMs);

        _delaySyncing = true;
        DelaySlider.Value = value;
        _delaySyncing = false;

        DelayValueBox.Text = value.ToString();
        ScheduleSave();
    }

    private void ApplyHotkey(bool showError)
    {
        _hotkey?.Dispose();
        _hotkey = null;

        var text = HotkeyBox.Text?.Trim() ?? string.Empty;
        if (!HotkeyParser.TryParseHotkey(text, out var definition, out var error))
        {
            HotkeyStatusText.Text = "некорректный хоткей";
            HotkeyStatusText.Foreground = ErrorBrush;
            if (showError)
            {
                SetStatus(error, StatusKind.Error);
            }

            return;
        }

        try
        {
            var hotkey = new GlobalHotkey(definition.Modifiers, definition.VirtualKey);
            hotkey.Pressed += OnHotkeyPressed;
            _hotkey = hotkey;

            HotkeyBox.Text = definition.Display;
            HotkeyStatusText.Text = $"активен · {definition.Display}";
            HotkeyStatusText.Foreground = SuccessBrush;
            _settings.Hotkey = definition.Display;
            ScheduleSave();

            if (showError)
            {
                SetStatus($"Хоткей {definition.Display} активен", StatusKind.Idle);
            }
        }
        catch (Exception ex)
        {
            HotkeyStatusText.Text = "недоступен";
            HotkeyStatusText.Foreground = ErrorBrush;
            if (showError)
            {
                SetStatus(ex.Message, StatusKind.Error);
            }
        }
    }

    private void OnHotkeyPressed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // The hook already filters by foreground window; re-check on the UI
            // thread in case focus changed while the callback was queued.
            if (!GameWindow.IsForeground())
            {
                return;
            }

            _ = RunFastMountAsync(interactive: false);
        });
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox box)
        {
            return;
        }

        if (ReferenceEquals(box, HotkeyBox))
        {
            HandleHotkeyKey(e);
        }
        else if (ReferenceEquals(box, SellHotkeyBox))
        {
            HandleSellHotkeyKey(e);
        }
        else if (ReferenceEquals(box, InventoryKeyBox) || ReferenceEquals(box, MountKeyBox))
        {
            HandleGameKey(box, e);
        }
    }

    private void HandleHotkeyKey(KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            HotkeyBox.Text = _settings.Hotkey;
            return;
        }

        if (!KeyCatalog.TryDescribe(e.Key, out var key))
        {
            return;
        }

        if (key.IsModifier)
        {
            var preview = KeyCatalog.DescribeModifiers(e.KeyModifiers);
            if (!string.IsNullOrEmpty(preview))
            {
                HotkeyBox.Text = preview;
            }

            return;
        }

        HotkeyBox.Text = KeyCatalog.ComposeHotkey(e.KeyModifiers, key.Display);
        ApplyHotkey(showError: true);
    }

    private void HandleGameKey(TextBox box, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            RevertKeyIfInvalid(box, box == InventoryKeyBox ? _settings.InventoryKey : _settings.MountKey);
            return;
        }

        if (!KeyCatalog.TryDescribe(e.Key, out var key) || key.IsModifier)
        {
            return;
        }

        box.Text = key.Display;
    }

    private void CommitHotkey()
    {
        if (!HotkeyParser.TryParseHotkey(HotkeyBox.Text, out _, out _))
        {
            HotkeyBox.Text = _settings.Hotkey;
        }

        ApplyHotkey(showError: true);
    }

    private void ApplySellHotkey(bool showError)
    {
        _sellHotkey?.Dispose();
        _sellHotkey = null;

        var text = SellHotkeyBox.Text?.Trim() ?? string.Empty;
        if (!HotkeyParser.TryParseHotkey(text, out var definition, out var error))
        {
            SellHotkeyStatusText.Text = "некорректный хоткей";
            SellHotkeyStatusText.Foreground = ErrorBrush;
            if (showError)
            {
                SetStatus(error, StatusKind.Error);
            }

            return;
        }

        try
        {
            var hotkey = new GlobalHotkey(definition.Modifiers, definition.VirtualKey);
            hotkey.Pressed += OnSellHotkeyPressed;
            _sellHotkey = hotkey;

            SellHotkeyBox.Text = definition.Display;
            SellHotkeyStatusText.Text = $"активен · {definition.Display}";
            SellHotkeyStatusText.Foreground = SuccessBrush;
            _settings.SellHotkey = definition.Display;
            ScheduleSave();

            if (showError)
            {
                SetStatus($"Хоткей автопродажи {definition.Display} активен", StatusKind.Idle);
            }
        }
        catch (Exception ex)
        {
            SellHotkeyStatusText.Text = "недоступен";
            SellHotkeyStatusText.Foreground = ErrorBrush;
            if (showError)
            {
                SetStatus(ex.Message, StatusKind.Error);
            }
        }
    }

    private void OnSellHotkeyPressed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!GameWindow.IsForeground())
            {
                return;
            }

            _ = RunAutoSellAsync();
        });
    }

    private void HandleSellHotkeyKey(KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            SellHotkeyBox.Text = _settings.SellHotkey;
            return;
        }

        if (!KeyCatalog.TryDescribe(e.Key, out var key))
        {
            return;
        }

        if (key.IsModifier)
        {
            var preview = KeyCatalog.DescribeModifiers(e.KeyModifiers);
            if (!string.IsNullOrEmpty(preview))
            {
                SellHotkeyBox.Text = preview;
            }

            return;
        }

        SellHotkeyBox.Text = KeyCatalog.ComposeHotkey(e.KeyModifiers, key.Display);
        ApplySellHotkey(showError: true);
    }

    private void CommitSellHotkey()
    {
        if (!HotkeyParser.TryParseHotkey(SellHotkeyBox.Text, out _, out _))
        {
            SellHotkeyBox.Text = _settings.SellHotkey;
        }

        ApplySellHotkey(showError: true);
    }

    private void RevertKeyIfInvalid(TextBox box, string fallback)
    {
        var canonical = CanonicalKey(box.Text, fallback);
        box.Text = canonical;
        ScheduleSave();
    }

    private async Task RunFastMountAsync(bool interactive)
    {
        if (_running || _picking || _checking || _hovering || _selling)
        {
            return;
        }

        ReadSettingsFromUi();
        if (!FastMountRunner.TryValidate(_settings, out var error))
        {
            SetStatus(error, StatusKind.Error);
            return;
        }

        _running = true;
        _runCts = new CancellationTokenSource();

        try
        {
            if (interactive)
            {
                if (GameWindow.TryActivate())
                {
                    SetStatus("Окно игры активировано…", StatusKind.Busy);
                    await Task.Delay(350);
                }
                else
                {
                    for (var i = 3; i >= 1; i--)
                    {
                        SetStatus($"Переключитесь в игру: {i}…", StatusKind.Busy);
                        await Task.Delay(1000);
                    }
                }
            }

            await FastMountRunner.RunAsync(
                _settings,
                message => Dispatcher.UIThread.Post(() => SetStatus(message, StatusKind.Busy)),
                _runCts.Token);

            SetStatus("Fast Mount выполнен", StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Выполнение отменено", StatusKind.Idle);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            _running = false;
        }
    }

    private void OnPickSlotClick(object? sender, RoutedEventArgs e) =>
        ShowSlotPicker(
            () => _slotPicker,
            picker => _slotPicker = picker,
            enabled => PickSlotButton.IsEnabled = enabled,
            precondition: null,
            markerSize: SlotPickerWindow.SlotMarkerSize,
            point =>
            {
                SlotResolver.Capture(_settings, point.X, point.Y);
                UpdateSlotUi();
                SaveSettings();
                SetStatus(
                    _settings.SlotRightRatio is not null
                        ? "Слот выбран — позиция подстроится под окно игры"
                        : "Слот выбран (игра не найдена, сохранены координаты экрана)",
                    StatusKind.Success);
            },
            cancelStatus: "Выбор слота отменён",
            busyStatus: "Наведите квадрат на слот и кликните левой кнопкой (Esc — отмена)");

    private void OnPickInvSlot1Click(object? sender, RoutedEventArgs e) =>
        ShowSlotPicker(
            () => _invSlot1Picker,
            picker => _invSlot1Picker = picker,
            enabled => PickInvSlot1Button.IsEnabled = enabled,
            precondition: null,
            markerSize: SlotPickerWindow.SlotMarkerSize,
            point =>
            {
                InventoryGridResolver.CaptureSlot1(_settings, point.X, point.Y);
                UpdateInvUi();
                SaveSettings();
                SetStatus("Центр слота #1 сохранён", StatusKind.Success);
            },
            cancelStatus: "Выбор слота #1 отменён",
            busyStatus: "Наведите квадрат на ЦЕНТР левой верхней ячейки инвентаря и кликните (Esc — отмена)");

    private void OnPickInvSlot28Click(object? sender, RoutedEventArgs e) =>
        ShowSlotPicker(
            () => _invSlot28Picker,
            picker => _invSlot28Picker = picker,
            enabled => PickInvSlot28Button.IsEnabled = enabled,
            precondition: null,
            markerSize: SlotPickerWindow.SlotMarkerSize,
            point =>
            {
                InventoryGridResolver.CaptureSlot28(_settings, point.X, point.Y);
                UpdateInvUi();
                SaveSettings();
                SetStatus("Центр слота #28 сохранён", StatusKind.Success);
            },
            cancelStatus: "Выбор слота #28 отменён",
            busyStatus: "Наведите квадрат на ЦЕНТР правой нижней ячейки инвентаря и кликните (Esc — отмена)");

    private void UpdateInvUi()
    {
        var has1 = InventoryGridResolver.HasSlot1(_settings);
        var has28 = InventoryGridResolver.HasSlot28(_settings);

        if (!has1 && !has28)
        {
            InvStatusText.Text = "Сетка не откалибрована";
            InvStatusText.Foreground = ErrorBrush;
            InvSummaryText.Text = "Укажите центр левой верхней ячейки (слот #1) и правой нижней (слот #28).";
            return;
        }

        if (!has1)
        {
            InvStatusText.Text = "Слот #28 указан";
            InvStatusText.Foreground = IdleBrush;
            InvSummaryText.Text = "Осталось указать слот #1 — левой верхней ячейки.";
            return;
        }

        if (!has28)
        {
            InvStatusText.Text = "Слот #1 указан";
            InvStatusText.Foreground = IdleBrush;
            InvSummaryText.Text = "Осталось указать слот #28 — правой нижней ячейки.";
            return;
        }

        if (InventoryGridResolver.TryResolve(_settings, out var slots, out _))
        {
            InvStatusText.Text = "Сетка готова";
            InvStatusText.Foreground = SuccessBrush;
            InvSummaryText.Text = $"Ячеек: {InventoryGridResolver.SlotCount}, прямоугольник ~{slots[0].W} × {slots[0].H} px. Откройте вкладку инвентаря и запустите проверку.";
        }
        else
        {
            InvStatusText.Text = "Игровое окно не найдено";
            InvStatusText.Foreground = IdleBrush;
            InvSummaryText.Text = "Точки сохранены как экранные координаты — проверка требует открытого окна игры.";
        }
    }

    /// <summary>Захватывает клиент игры и классифицирует сетку.</summary>
    private async Task<(List<InventorySlotResult>? Results, string Error)> CaptureAndClassifyAsync(List<InventorySlotRect> slots)
    {
        if (!GameWindow.TryGetClientBounds(out var client))
        {
            return (null, "Окно игры не найдено — откройте Drakensang.");
        }

        var shot = await Task.Run(() =>
        {
            if (!ScreenCapture.TryCapture(
                    client.Left, client.Top, client.Width, client.Height,
                    out var frame, out var captureError))
            {
                return (Frame: (byte[]?)null, Error: captureError, Results: (List<InventorySlotResult>?)null);
            }

            var results = InventoryChecker.CheckByGrid(
                frame!, client.Width, client.Height, slots, client.Left, client.Top);
            return (Frame: (byte[]?)frame, Error: string.Empty, Results: (List<InventorySlotResult>?)results);
        });

        if (shot.Error.Length > 0 || shot.Results is null)
        {
            return (null, shot.Error);
        }

        return (shot.Results, string.Empty);
    }

    private async Task RunInventoryCheckAsync()
    {
        if (_running || _picking || _checking || _hovering || _selling)
        {
            return;
        }

        if (!InventoryGridResolver.TryResolve(_settings, out var slots, out var gridError))
        {
            SetStatus(gridError, StatusKind.Error);
            return;
        }

        _checking = true;
        CheckInventoryButton.IsEnabled = false;

        try
        {
            SetStatus("Снимаю экран и анализирую 28 ячеек…", StatusKind.Busy);

            var (results, error) = await CaptureAndClassifyAsync(slots);
            if (results is null)
            {
                SetStatus(error, StatusKind.Error);
                return;
            }

            var summary = InventoryChecker.Summarize(results);
            InvSummaryText.Text = summary;

            var empty = results.Count(r => r.IsEmpty);
            SetStatus(
                $"Инвентарь проверен: свободно {empty} из {results.Count} ячеек.",
                empty == 0 ? StatusKind.Error : StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка проверки инвентаря: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            _checking = false;
            CheckInventoryButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Обход курсором занятых ячеек: сначала быстрый скан сетки, затем курсор
    /// тянется по предметам с паузой на каждом (тултипы успевают показаться).
    /// В конце курсор возвращается на исходную позицию.
    /// </summary>
    private async Task RunHoverInventoryAsync()
    {
        if (_running || _picking || _checking || _hovering || _selling)
        {
            return;
        }

        if (!InventoryGridResolver.TryResolve(_settings, out var slots, out var gridError))
        {
            SetStatus(gridError, StatusKind.Error);
            return;
        }

        _hovering = true;
        _hoverCts = new CancellationTokenSource();
        HoverInventoryButton.IsEnabled = false;
        var token = _hoverCts.Token;

        var hasOriginalCursor = NativeMethods.GetCursorPos(out var originalCursor);

        try
        {
            if (GameWindow.TryActivate())
            {
                SetStatus("Окно игры активировано…", StatusKind.Busy);
                await Task.Delay(350, token);
            }

            SetStatus("Сканирую сетку, чтобы найти предметы…", StatusKind.Busy);
            var (results, captureError) = await CaptureAndClassifyAsync(slots);
            if (results is null)
            {
                SetStatus(captureError, StatusKind.Error);
                return;
            }

            var items = results.Where(r => !r.IsEmpty).ToList();
            if (items.Count == 0)
            {
                SetStatus("Занятых ячеек нет — обходить нечего.", StatusKind.Idle);
                return;
            }

            var index = 0;
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                NativeMethods.SetCursorPos(item.CenterX, item.CenterY);
                index++;
                SetStatus($"Тултип: слот #{item.Index} ({index}/{items.Count})…", StatusKind.Busy);
                await Task.Delay(HoverPerSlotMs, token);
            }

            SetStatus($"Курсор обошёл {items.Count} из {results.Count} ячеек и возвращён.", StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Обход предметов прерван", StatusKind.Idle);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка обхода: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            if (hasOriginalCursor)
            {
                NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            }

            _hoverCts?.Dispose();
            _hoverCts = null;
            _hovering = false;
            HoverInventoryButton.IsEnabled = true;
        }
    }

    private void OnHoverInventoryClick(object? sender, RoutedEventArgs e) =>
        _ = RunHoverInventoryAsync();

    /// <summary>
    /// Проходит ли ячейка под фильтр автопродажи: только неопознанные («?»)
    /// предметы, и лишь те, чьё качество (по цвету знака «?») отмечено.
    /// </summary>
    private bool MatchesAutoSellFilter(InventorySlotResult slot)
    {
        if (!slot.Unidentified)
        {
            return false;
        }

        return slot.Rarity switch
        {
            InventoryRarity.Common => _settings.SellCommon,
            InventoryRarity.Improved => _settings.SellImproved,
            InventoryRarity.Magic => _settings.SellMagic,
            InventoryRarity.Extraordinary => _settings.SellExtraordinary,
            InventoryRarity.Legendary => _settings.SellLegendary,
            InventoryRarity.Unique => _settings.SellUnique,
            InventoryRarity.Mythic => _settings.SellMythic,
            _ => false
        };
    }

    /// <summary>
    /// Автопродажа: для каждой выбранной вкладки — клик по её заголовку, скан
    /// сетки 7×4 и ПКМ по каждой ячейке, попавшей под фильтр (по умолчанию —
    /// только неопознанные «?»). Один проход по вкладкам. Курсор возвращается
    /// на исходную позицию.
    /// </summary>
    private async Task RunAutoSellAsync()
    {
        if (_running || _picking || _checking || _hovering || _selling)
        {
            return;
        }

        if (!InventoryGridResolver.TryResolve(_settings, out var slots, out var gridError))
        {
            SetStatus(gridError, StatusKind.Error);
            return;
        }

        var tabs = ParseSellTabs(_settings.SellTabs);
        if (tabs.Count == 0)
        {
            SetStatus("Не выбрана ни одна вкладка для обхода автопродажей.", StatusKind.Error);
            return;
        }

        if (!_settings.SellCommon && !_settings.SellImproved && !_settings.SellMagic &&
            !_settings.SellExtraordinary && !_settings.SellLegendary && !_settings.SellUnique &&
            !_settings.SellMythic)
        {
            SetStatus("Не отмечено ни одного качества — отметьте, какие «?» продавать.", StatusKind.Error);
            return;
        }

        foreach (var tab in tabs)
        {
            if (!TabResolver.TryResolve(_settings, tab, out _, out _))
            {
                SetStatus(
                    $"Вкладка {TabResolver.DisplayName(tab)}: не задана позиция вкладок. " +
                    "Откалибруйте их во вкладке «ИНВЕНТАРЬ» («Указать вкладку 1» / «Указать вкладку 2»).",
                    StatusKind.Error);
                return;
            }
        }

        _selling = true;
        _sellCts = new CancellationTokenSource();
        AutoSellButton.IsEnabled = false;
        var token = _sellCts.Token;

        var hasOriginalCursor = NativeMethods.GetCursorPos(out var originalCursor);

        try
        {
            if (GameWindow.TryActivate())
            {
                SetStatus("Окно игры активировано…", StatusKind.Busy);
                await Task.Delay(200, token);
            }

            var totalSold = 0;
            var productiveTabs = 0;

            foreach (var tab in tabs)
            {
                token.ThrowIfCancellationRequested();
                var tabName = TabResolver.DisplayName(tab);

                // 1-я вкладка при одном выборе — оставляем как есть (инвентарь уже открыт на ней).
                if (tabs.Count > 1 || TabResolver.IndexOf(tab) > 1)
                {
                    TabResolver.TryResolve(_settings, tab, out var tabX, out var tabY);
                    InputSimulator.LeftClick(tabX, tabY);
                    SetStatus($"Вкладка {tabName}: открываю…", StatusKind.Busy);
                    await Task.Delay(Math.Clamp(_settings.SellSwitchDelayMs, 0, SellDelayMaxMs), token);
                }

                SetStatus($"Вкладка {tabName}: сканирую сетку…", StatusKind.Busy);
                var (results, captureError) = await CaptureAndClassifyAsync(slots);
                if (results is null)
                {
                    SetStatus(captureError, StatusKind.Error);
                    return;
                }

                var targets = results.Where(MatchesAutoSellFilter).ToList();
                if (targets.Count == 0)
                {
                    continue;
                }

                var sold = 0;
                foreach (var target in targets)
                {
                    token.ThrowIfCancellationRequested();
                    InputSimulator.RightClick(target.CenterX, target.CenterY);
                    sold++;
                    SetStatus(
                        $"Вкладка {tabName}: ПКМ слот #{target.Index} ({sold}/{targets.Count})…",
                        StatusKind.Busy);
                    await Task.Delay(Math.Clamp(_settings.SellItemDelayMs, 0, SellDelayMaxMs), token);
                }

                totalSold += sold;
                productiveTabs++;
            }

            SetStatus(
                $"Продано {totalSold} предмет(ов) в {productiveTabs} из {tabs.Count} вкладок, курсор возвращён.",
                StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Автопродажа прервана", StatusKind.Idle);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка автопродажи: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            if (hasOriginalCursor)
            {
                NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            }

            _sellCts?.Dispose();
            _sellCts = null;
            _selling = false;
            AutoSellButton.IsEnabled = true;
        }
    }

    private void OnAutoSellClick(object? sender, RoutedEventArgs e) =>
        _ = RunAutoSellAsync();

    /// <summary>Список вкладок автопродажи в порядке обхода.</summary>
    private static List<string> ParseSellTabs(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(key => TabResolver.IndexOf(key) > 0)
            .Distinct()
            .OrderBy(TabResolver.IndexOf)
            .ToList();

    private static string JoinSellTabs(IEnumerable<string> tabs) =>
        string.Join(",", tabs.OrderBy(TabResolver.IndexOf).Select(TabResolver.DisplayName));

    private void UpdateSellTabsUi()
    {
        var selected = ParseSellTabs(_settings.SellTabs);

        foreach (var child in SellTabsPanel.Children)
        {
            if (child is not Button button)
            {
                continue;
            }

            var isSelected = button.Tag is string key && selected.Contains(key);
            button.Classes.Set("selected", isSelected);
        }
    }

    private void OnSellTabToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || TabResolver.IndexOf(key) == 0)
        {
            return;
        }

        var tabs = ParseSellTabs(_settings.SellTabs);
        if (!tabs.Remove(key))
        {
            tabs.Add(key);
        }

        _settings.SellTabs = JoinSellTabs(tabs);
        UpdateSellTabsUi();
        ScheduleSave();
    }

    private void UpdateSlotUi()
    {
        if (!SlotResolver.HasSlot(_settings))
        {
            SlotStatusText.Text = "Слот не выбран";
            SlotStatusText.Foreground = ErrorBrush;
            SlotCoordsText.Text = "Нажмите «Выбрать слот» и кликните по ячейке: квадрат следует за курсором.";
            return;
        }

        SlotStatusText.Text = "Слот выбран";
        SlotStatusText.Foreground = SuccessBrush;

        SlotCoordsText.Text = _settings.SlotRightRatio is not null
            ? " "
            : $"Координаты экрана: {_settings.SlotX}, {_settings.SlotY}";
    }

    private void OnPickTabClick(object? sender, RoutedEventArgs e) => ShowTabPicker(step: false);

    private void OnPickTabStepClick(object? sender, RoutedEventArgs e) => ShowTabPicker(step: true);

    private void ShowTabPicker(bool step) =>
        ShowSlotPicker(
            () => step ? _tabStepPicker : _tabPicker,
            picker =>
            {
                if (step)
                {
                    _tabStepPicker = picker;
                }
                else
                {
                    _tabPicker = picker;
                }
            },
            enabled =>
            {
                PickTabButton.IsEnabled = enabled;
                PickTabStepButton.IsEnabled = enabled;
            },
            precondition: step
                ? () => TabResolver.HasBase(_settings)
                    ? null
                    : "Сначала укажите позицию 1-й вкладки."
                : null,
            markerSize: SlotPickerWindow.TabMarkerSize,
            point =>
            {
                if (step)
                {
                    if (TabResolver.CaptureStep(_settings, point.X, point.Y))
                    {
                        SetStatus(
                            $"Шаг вкладок: {TabResolver.StepPixelsX(_settings)} × {TabResolver.StepPixelsY(_settings)} px",
                            StatusKind.Success);
                        UpdateTabUi();
                        SaveSettings();
                    }
                    else
                    {
                        SetStatus("Сначала укажите позицию 1-й вкладки.", StatusKind.Error);
                    }
                }
                else
                {
                    TabResolver.CaptureBase(_settings, point.X, point.Y);
                    SetStatus(
                        _settings.TabRightRatio is not null
                            ? "Позиция 1-й вкладки сохранена"
                            : "Позиция 1-й вкладки сохранена (игра не найдена, сохранены координаты экрана)",
                        StatusKind.Success);
                    UpdateTabUi();
                    SaveSettings();
                }
            },
            cancelStatus: "Выбор отменён",
            busyStatus: step
                ? "Наведите квадрат на 2-ю вкладку и кликните (Esc — отмена)"
                : "Наведите квадрат на 1-ю вкладку и кликните (Esc — отмена)");

    /// <summary>
    /// Пара «слайдер + компактное поле» для задержек автопродажи: шаг 15 мс,
    /// диапазон 0..1000, ручной ввод по Enter/потере фокуса.
    /// </summary>
    private void WireDelayPair(Slider slider, TextBox box, int fallbackMs, Action<int> apply)
    {
        var syncing = false;

        slider.ValueChanged += (_, e) =>
        {
            var clamped = Math.Clamp((int)Math.Round(e.NewValue), 0, SellDelayMaxMs);

            if (!syncing)
            {
                var snapped = Math.Clamp(
                    (int)Math.Round(clamped / (double)SellDelayStepMs) * SellDelayStepMs,
                    0, SellDelayMaxMs);

                if (snapped != clamped && Math.Abs(snapped - slider.Value) > 0.5)
                {
                    syncing = true;
                    slider.Value = snapped;
                    syncing = false;
                    clamped = snapped;
                }
            }

            if (!box.IsFocused && box.Text != clamped.ToString())
            {
                box.Text = clamped.ToString();
            }

            apply(clamped);
            ScheduleSave();
        };

        void CommitText()
        {
            if (!int.TryParse(box.Text?.Trim(), out var value) || value < 0)
            {
                value = fallbackMs;
            }

            value = Math.Clamp(value, 0, SellDelayMaxMs);

            syncing = true;
            slider.Value = value;
            syncing = false;

            box.Text = value.ToString();
            apply(value);
            ScheduleSave();
        }

        box.LostFocus += (_, _) => CommitText();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitText();
            }
        };
    }

    /// <summary>
    /// Общий поток выбора точки на экране: guard от запуска/повторного picker-а,
    /// предусловие, позиция курсора, следящий квадрат, restore главного окна.
    /// </summary>
    private void ShowSlotPicker(
        Func<SlotPickerWindow?> existingPicker,
        Action<SlotPickerWindow?> storePicker,
        Action<bool> setPickButtons,
        Func<string?>? precondition,
        double markerSize,
        Action<PixelPoint> onPicked,
        string cancelStatus,
        string busyStatus)
    {
        if (_running || _checking || _hovering || _selling)
        {
            return;
        }

        var existing = existingPicker();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        var preError = precondition?.Invoke();
        if (preError is not null)
        {
            SetStatus(preError, StatusKind.Error);
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            SetStatus("Не удалось определить позицию курсора.", StatusKind.Error);
            return;
        }

        var half = (int)Math.Round(markerSize * (Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y))?.Scaling ?? 1.0) / 2);

        _picking = true;
        setPickButtons(false);

        var picked = false;
        var picker = new SlotPickerWindow(markerSize);
        picker.SlotPicked += (_, point) =>
        {
            picked = true;
            onPicked(point);
        };
        picker.Closed += (_, _) =>
        {
            storePicker(null);
            _picking = false;
            setPickButtons(true);

            Show();
            Activate();

            if (!picked)
            {
                SetStatus(cancelStatus, StatusKind.Idle);
            }
        };

        picker.Position = new PixelPoint(cursor.X - half, cursor.Y - half);
        storePicker(picker);
        Hide();
        picker.Show();
        SetStatus(busyStatus, StatusKind.Busy);
    }

    private void OnTabSelectClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
        {
            return;
        }

        _settings.TabKey = key;
        UpdateTabUi();
        ScheduleSave();
    }

    private void OnCheckInventoryClick(object? sender, RoutedEventArgs e) =>
        _ = RunInventoryCheckAsync();

    private void UpdateTabUi()
    {
        foreach (var child in TabButtonsPanel.Children)
        {
            if (child is not Button button)
            {
                continue;
            }

            var selected = string.Equals(button.Tag as string, _settings.TabKey, StringComparison.Ordinal);
            button.Classes.Set("selected", selected);
        }

        var name = TabResolver.DisplayName(_settings.TabKey);

        if (!_settings.SwitchInventoryTab)
        {
            TabSelectStatusText.Text = "Выключено — макрос не будет переключать вкладку.";
            TabSelectStatusText.Foreground = IdleBrush;
            TabStatusText.Text = "Калибровка вкладок не требуется.";
            TabStatusText.Foreground = IdleBrush;
            return;
        }

        TabSelectStatusText.Text = $"Fast Mount переключается на вкладку {name}.";
        TabSelectStatusText.Foreground = SuccessBrush;

        if (!TabResolver.HasBase(_settings))
        {
            TabStatusText.Text = "Позиция 1-й вкладки не задана — кнопка «Указать вкладку 1» выше.";
            TabStatusText.Foreground = ErrorBrush;
            return;
        }

        if (TabResolver.IndexOf(_settings.TabKey) > 1 && !TabResolver.HasStep(_settings))
        {
            TabStatusText.Text = $"Для вкладки {name} не задан шаг — кнопка «Указать вкладку 2» выше, или выберите вкладку 1 в FAST MOUNT.";
            TabStatusText.Foreground = ErrorBrush;
            return;
        }

        var step = TabResolver.HasStep(_settings)
            ? $"шаг {TabResolver.StepPixelsX(_settings)} × {TabResolver.StepPixelsY(_settings)} px"
            : "шаг не требуется";

        TabStatusText.Foreground = SuccessBrush;
        TabStatusText.Text = $"Позиция 1-й вкладки: {step}.";
    }

    private void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettings()
    {
        try
        {
            ReadSettingsFromUi();
            _settings.Save();
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось сохранить настройки: " + ex.Message, StatusKind.Error);
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual source &&
            source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        SettingsVersionText.Text = "Версия: " + GitHubUpdater.BuildVersionDisplay(
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));
        UpdateStatusText.Text = _checkingUpdates
            ? "Проверяю обновления…"
            : _pendingUpdateUrl is not null
                ? $"Доступна v{_pendingUpdateTag} — кнопка подтвердит скачивание и перезапуск."
                : "Проверяется при каждом запуске приложения.";
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }
    private async void OnCheckUpdatesClick(object? sender, RoutedEventArgs e)
    {
        if (_checkingUpdates)
        {
            return;
        }

        _checkingUpdates = true;
        CheckUpdatesButton.IsEnabled = false;

        try
        {
            // Второй клик по кнопке — это подтверждение: качаем и ставим.
            if (_pendingUpdateUrl is not null)
            {
                UpdateStatusText.Text = $"Скачиваю v{_pendingUpdateTag}…";
                UpdateStatusText.Foreground = BusyBrush;

                var result = await Task.Run(() =>
                    GitHubUpdater.PerformUpdateAsync(_pendingUpdateUrl, _pendingUpdateTag, message =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            UpdateStatusText.Text = message;
                            UpdateStatusText.Foreground = BusyBrush;
                        });
                    }));

                if (result.Outcome == UpdateOutcome.Updated)
                {
                    return;
                }

                ResetPendingUpdate();
                UpdateStatusText.Text = result.Message;
                UpdateStatusText.Foreground = ErrorBrush;
                return;
            }

            // Первый клик — только проверка.
            UpdateStatusText.Text = "Проверяю обновления…";
            UpdateStatusText.Foreground = BusyBrush;

            var check = await Task.Run(() => GitHubUpdater.CheckForUpdateAsync(message =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateStatusText.Text = message;
                    UpdateStatusText.Foreground = BusyBrush;
                });
            }));

            if (check.Outcome == CheckOutcome.Available)
            {
                _pendingUpdateTag = check.DisplayTag;
                _pendingUpdateUrl = check.DownloadUrl;
                CheckUpdatesButton.Content = $"Обновить до v{check.DisplayTag}";
                CheckUpdatesButton.Classes.Set("selected", true);
                UpdateStatusText.Text =
                    "Доступна новая версия. Кнопка начала скачивание, приложение перезапустится само. Настройки не пострадают.";
                UpdateStatusText.Foreground = BusyBrush;
                return;
            }

            CheckUpdatesButton.Content = "Проверить обновления";
            UpdateStatusText.Text = check.Message;
            UpdateStatusText.Foreground = check.Outcome == CheckOutcome.UpToDate
                ? SuccessBrush
                : ErrorBrush;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Проверка не удалась: " + ex.Message;
            UpdateStatusText.Foreground = ErrorBrush;
        }
        finally
        {
            _checkingUpdates = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void ResetPendingUpdate()
    {
        _pendingUpdateTag = string.Empty;
        _pendingUpdateUrl = null;
        CheckUpdatesButton.Content = "Проверить обновления";
        CheckUpdatesButton.Classes.Set("selected", false);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _runCts?.Cancel();
        _hoverCts?.Cancel();
        _sellCts?.Cancel();
        _saveTimer.Stop();
        SaveSettings();
        _hotkey?.Dispose();
        _hotkey = null;
        _sellHotkey?.Dispose();
        _sellHotkey = null;
    }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusDot.Fill = kind switch
        {
            StatusKind.Busy => BusyBrush,
            StatusKind.Success => SuccessBrush,
            StatusKind.Error => ErrorBrush,
            _ => IdleBrush
        };
    }

    private enum StatusKind
    {
        Idle,
        Busy,
        Success,
        Error
    }
}
