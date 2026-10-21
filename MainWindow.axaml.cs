using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DsoHelper.AutoSell;
using DsoHelper.FastMount;
using DsoHelper.Infrastructure;
using DsoHelper.UI;

namespace DsoHelper;

public partial class MainWindow : Window
{
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
    private SlotPickerWindow? _sellCellPicker;
    private SlotPickerWindow? _sellStepPicker;
    private SellPreviewWindow? _sellPreview;
    private CancellationTokenSource? _runCts;
    private bool _running;
    private bool _picking;
    private bool _loadingUi;

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
        SellHotkeyBox.LostFocus += (_, _) => CommitSellHotkey();
        InventoryKeyBox.LostFocus += (_, _) => RevertKeyIfInvalid(InventoryKeyBox, _settings.InventoryKey);
        MountKeyBox.LostFocus += (_, _) => RevertKeyIfInvalid(MountKeyBox, _settings.MountKey);

        HotkeyBox.TextChanged += (_, _) => ScheduleSave();
        SellHotkeyBox.TextChanged += (_, _) => ScheduleSave();
        InventoryKeyBox.TextChanged += (_, _) => ScheduleSave();
        MountKeyBox.TextChanged += (_, _) => ScheduleSave();

        DelayBox.ValueChanged += (_, _) => ScheduleSave();

        SellColumnsBox.ValueChanged += (_, _) =>
        {
            _settings.SellColumns = Math.Clamp(ToInt(SellColumnsBox), 1, SellGridResolver.MaxColumns);
            UpdateSellUi();
            ScheduleSave();
        };

        SellRowsBox.ValueChanged += (_, _) =>
        {
            _settings.SellRows = Math.Clamp(ToInt(SellRowsBox), 1, SellGridResolver.MaxRows);
            UpdateSellUi();
            ScheduleSave();
        };

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

    private void LoadSettingsIntoUi()
    {
        _loadingUi = true;

        try
        {
            HotkeyBox.Text = HotkeyParser.TryParseHotkey(_settings.Hotkey, out var hotkey, out _)
                ? hotkey.Display
                : _settings.Hotkey;
            InventoryKeyBox.Text = CanonicalKey(_settings.InventoryKey, "I");
            MountKeyBox.Text = CanonicalKey(_settings.MountKey, "M");

            DelayBox.Value = _settings.DelayMs;

            SellHotkeyBox.Text = HotkeyParser.TryParseHotkey(_settings.SellHotkey, out var sellHotkey, out _)
                ? sellHotkey.Display
                : _settings.SellHotkey;
            SellColumnsBox.Value = Math.Clamp(_settings.SellColumns, 1, SellGridResolver.MaxColumns);
            SellRowsBox.Value = Math.Clamp(_settings.SellRows, 1, SellGridResolver.MaxRows);

            SwitchTabCheck.IsChecked = _settings.SwitchInventoryTab;

            SellUseRarityCheck.IsChecked = _settings.SellUseRarity;
            SellBasicCheck.IsChecked = _settings.SellBasic;
            SellImprovedCheck.IsChecked = _settings.SellImproved;
            SellExtraordinaryCheck.IsChecked = _settings.SellExtraordinary;
            SellLegendaryCheck.IsChecked = _settings.SellLegendary;
            SellUniqueCheck.IsChecked = _settings.SellUnique;

            AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;
            UpdateSlotUi();
            UpdateTabUi();
            UpdateRarityUi();
            UpdateSellUi();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void ReadSettingsFromUi()
    {
        if (HotkeyParser.TryParseHotkey(HotkeyBox.Text, out var hotkey, out _))
        {
            _settings.Hotkey = hotkey.Display;
        }

        _settings.InventoryKey = CanonicalKey(InventoryKeyBox.Text, _settings.InventoryKey);
        _settings.MountKey = CanonicalKey(MountKeyBox.Text, _settings.MountKey);

        _settings.DelayMs = ToInt(DelayBox);
        if (HotkeyParser.TryParseHotkey(SellHotkeyBox.Text, out var sellHotkey, out _))
        {
            _settings.SellHotkey = sellHotkey.Display;
        }

        _settings.SellColumns = Math.Clamp(ToInt(SellColumnsBox), 1, SellGridResolver.MaxColumns);
        _settings.SellRows = Math.Clamp(ToInt(SellRowsBox), 1, SellGridResolver.MaxRows);
        _settings.SellUseRarity = SellUseRarityCheck.IsChecked == true;
        _settings.SellBasic = SellBasicCheck.IsChecked == true;
        _settings.SellImproved = SellImprovedCheck.IsChecked == true;
        _settings.SellExtraordinary = SellExtraordinaryCheck.IsChecked == true;
        _settings.SellLegendary = SellLegendaryCheck.IsChecked == true;
        _settings.SellUnique = SellUniqueCheck.IsChecked == true;
        _settings.SwitchInventoryTab = SwitchTabCheck.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
    }

    private void OnSellRarityChanged(object? sender, RoutedEventArgs e)
    {
        UpdateRarityUi();

        if (_loadingUi)
        {
            return;
        }

        _settings.SellUseRarity = SellUseRarityCheck.IsChecked == true;
        _settings.SellBasic = SellBasicCheck.IsChecked == true;
        _settings.SellImproved = SellImprovedCheck.IsChecked == true;
        _settings.SellExtraordinary = SellExtraordinaryCheck.IsChecked == true;
        _settings.SellLegendary = SellLegendaryCheck.IsChecked == true;
        _settings.SellUnique = SellUniqueCheck.IsChecked == true;

        UpdateSellUi();
        ScheduleSave();
    }

    private void UpdateRarityUi()
    {
        var useRarity = SellUseRarityCheck.IsChecked == true;
        SellRarityPanel.IsEnabled = useRarity;
        SellRarityPanel.Opacity = useRarity ? 1 : 0.45;
    }

    private void OnSectionTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section })
        {
            return;
        }

        var fastMount = !string.Equals(section, "autosell", StringComparison.OrdinalIgnoreCase);

        FastMountSection.IsVisible = fastMount;
        AutoSellSection.IsVisible = !fastMount;
        FastMountActions.IsVisible = fastMount;
        AutoSellActions.IsVisible = !fastMount;

        FastMountTabButton.Classes.Set("selected", fastMount);
        AutoSellTabButton.Classes.Set("selected", !fastMount);

        SectionSubtitleText.Text = fastMount
            ? "Fast Mount — быстрая смена плаща"
            : "Авто-продажа — фильтр по редкости";

        ChaptersScroll.Offset = default;

        if (!fastMount)
        {
            UpdateSellUi();
        }
    }

    private static int ToInt(NumericUpDown box) =>
        box.Value is { } value ? (int)value : 0;

    private static string CanonicalKey(string? text, string fallback) =>
        KeyCatalog.TryParseKey(text, out var key) && !key.IsModifier ? key.Display : fallback;

    private void ApplyHotkey(bool showError) =>
        ApplyHotkeyCore(
            ref _hotkey,
            HotkeyBox,
            HotkeyStatusText,
            SellHotkeyBox,
            OnHotkeyPressed,
            value => _settings.Hotkey = value,
            showError);

    private void ApplySellHotkey(bool showError) =>
        ApplyHotkeyCore(
            ref _sellHotkey,
            SellHotkeyBox,
            SellHotkeyStatusText,
            HotkeyBox,
            OnSellHotkeyPressed,
            value => _settings.SellHotkey = value,
            showError);

    private void ApplyHotkeyCore(
        ref GlobalHotkey? slot,
        TextBox box,
        TextBlock status,
        TextBox otherBox,
        Action pressed,
        Action<string> store,
        bool showError)
    {
        var text = box.Text?.Trim() ?? string.Empty;
        if (!HotkeyParser.TryParseHotkey(text, out var definition, out var error))
        {
            status.Text = "некорректный хоткей";
            status.Foreground = ErrorBrush;
            if (showError)
            {
                SetStatus(error, StatusKind.Error);
            }

            return;
        }

        if (HotkeyParser.TryParseHotkey(otherBox.Text, out var other, out _) &&
            other.VirtualKey == definition.VirtualKey &&
            other.Modifiers == definition.Modifiers)
        {
            status.Text = "занят другим действием";
            status.Foreground = ErrorBrush;
            if (showError)
            {
                SetStatus("Этот хоткей уже используется другим действием.", StatusKind.Error);
            }

            return;
        }

        slot?.Dispose();
        slot = null;

        try
        {
            var hotkey = new GlobalHotkey(definition.Modifiers, definition.VirtualKey);
            hotkey.Pressed += pressed;
            slot = hotkey;

            box.Text = definition.Display;
            status.Text = $"активен · {definition.Display}";
            status.Foreground = SuccessBrush;
            store(definition.Display);
            ScheduleSave();

            if (showError)
            {
                SetStatus($"Хоткей {definition.Display} активен", StatusKind.Idle);
            }
        }
        catch (Exception ex)
        {
            status.Text = "недоступен";
            status.Foreground = ErrorBrush;
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

    private void OnSellHotkeyPressed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!GameWindow.IsForeground())
            {
                return;
            }

            _ = RunAutoSellAsync(interactive: false);
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
            HandleHotkeyKey(e, HotkeyBox, ApplyHotkey);
        }
        else if (ReferenceEquals(box, SellHotkeyBox))
        {
            HandleHotkeyKey(e, SellHotkeyBox, ApplySellHotkey);
        }
        else if (ReferenceEquals(box, InventoryKeyBox) || ReferenceEquals(box, MountKeyBox))
        {
            HandleGameKey(box, e);
        }
    }

    private void HandleHotkeyKey(KeyEventArgs e, TextBox box, Action<bool> apply)
    {
        if (e.Key == Key.Tab)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            box.Text = ReferenceEquals(box, HotkeyBox) ? _settings.Hotkey : _settings.SellHotkey;
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
                box.Text = preview;
            }

            return;
        }

        box.Text = KeyCatalog.ComposeHotkey(e.KeyModifiers, key.Display);
        apply(true);
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

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        await RunFastMountAsync(interactive: true);
    }

    private async Task RunFastMountAsync(bool interactive)
    {
        if (_running || _picking)
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
        RunButton.IsEnabled = false;
        SellNowButton.IsEnabled = false;
        PreviewSellButton.IsEnabled = false;
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
            RunButton.IsEnabled = true;
            SellNowButton.IsEnabled = true;
            PreviewSellButton.IsEnabled = true;
        }
    }

    private async void OnSellNowClick(object? sender, RoutedEventArgs e)
    {
        await RunAutoSellAsync(interactive: true);
    }

    private async Task RunAutoSellAsync(bool interactive)
    {
        if (_running || _picking)
        {
            return;
        }

        ReadSettingsFromUi();
        if (!AutoSellRunner.TryValidate(_settings, out var error))
        {
            SetStatus(error, StatusKind.Error);
            return;
        }

        _running = true;
        RunButton.IsEnabled = false;
        SellNowButton.IsEnabled = false;
        PreviewSellButton.IsEnabled = false;
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

            await AutoSellRunner.RunAsync(
                _settings,
                message => Dispatcher.UIThread.Post(() => SetStatus(message, StatusKind.Busy)),
                _runCts.Token);

            SetStatus("Авто-продажа выполнена", StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Продажа отменена", StatusKind.Idle);
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
            RunButton.IsEnabled = true;
            SellNowButton.IsEnabled = true;
            PreviewSellButton.IsEnabled = true;
        }
    }

    private void OnPickSlotClick(object? sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        if (_slotPicker is not null)
        {
            _slotPicker.Activate();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            SetStatus("Не удалось определить позицию курсора.", StatusKind.Error);
            return;
        }

        var scaling = Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y))?.Scaling ?? 1.0;
        var half = (int)Math.Round(32 * scaling);

        _picking = true;
        PickSlotButton.IsEnabled = false;
        RunButton.IsEnabled = false;

        var picked = false;
        var picker = new SlotPickerWindow();
        picker.SlotPicked += (_, point) =>
        {
            picked = true;
            SlotResolver.Capture(_settings, point.X, point.Y);
            UpdateSlotUi();
            SaveSettings();
            SetStatus(
                _settings.SlotRightRatio is not null
                    ? "Слот выбран — позиция подстроится под окно игры"
                    : "Слот выбран (игра не найдена, сохранены координаты экрана)",
                StatusKind.Success);
        };
        picker.Closed += (_, _) =>
        {
            _slotPicker = null;
            _picking = false;
            PickSlotButton.IsEnabled = true;
            RunButton.IsEnabled = true;

            Show();
            Activate();

            if (!picked)
            {
                SetStatus("Выбор слота отменён", StatusKind.Idle);
            }
        };

        picker.Position = new PixelPoint(cursor.X - half, cursor.Y - half);
        _slotPicker = picker;
        Hide();
        picker.Show();
        SetStatus("Наведите квадрат на слот и кликните левой кнопкой (Esc — отмена)", StatusKind.Busy);
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
            ? "Позиция привязана к окну игры и подстраивается под разрешение."
            : $"Координаты экрана: {_settings.SlotX}, {_settings.SlotY}";
    }

    private void OnPickTabClick(object? sender, RoutedEventArgs e) => ShowTabPicker(step: false);

    private void OnPickTabStepClick(object? sender, RoutedEventArgs e) => ShowTabPicker(step: true);

    private void ShowTabPicker(bool step)
    {
        if (_running)
        {
            return;
        }

        var existing = step ? _tabStepPicker : _tabPicker;
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        if (step && !TabResolver.HasBase(_settings))
        {
            SetStatus("Сначала укажите позицию 1-й вкладки.", StatusKind.Error);
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            SetStatus("Не удалось определить позицию курсора.", StatusKind.Error);
            return;
        }

        var scaling = Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y))?.Scaling ?? 1.0;
        var half = (int)Math.Round(32 * scaling);

        _picking = true;
        PickTabButton.IsEnabled = false;
        PickTabStepButton.IsEnabled = false;
        RunButton.IsEnabled = false;

        var picked = false;
        var picker = new SlotPickerWindow();
        picker.SlotPicked += (_, point) =>
        {
            picked = true;

            if (step)
            {
                if (TabResolver.CaptureStep(_settings, point.X, point.Y))
                {
                    SetStatus(
                        $"Шаг вкладок: {TabResolver.StepPixelsX(_settings)} × {TabResolver.StepPixelsY(_settings)} px",
                        StatusKind.Success);
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
            }

            UpdateTabUi();
            SaveSettings();
        };
        picker.Closed += (_, _) =>
        {
            if (step)
            {
                _tabStepPicker = null;
            }
            else
            {
                _tabPicker = null;
            }

            _picking = false;
            PickTabButton.IsEnabled = true;
            PickTabStepButton.IsEnabled = true;
            RunButton.IsEnabled = true;

            Show();
            Activate();

            if (!picked)
            {
                SetStatus("Выбор отменён", StatusKind.Idle);
            }
        };

        picker.Position = new PixelPoint(cursor.X - half, cursor.Y - half);
        if (step)
        {
            _tabStepPicker = picker;
        }
        else
        {
            _tabPicker = picker;
        }

        Hide();
        picker.Show();
        SetStatus(
            step
                ? "Наведите квадрат на 2-ю вкладку и кликните (Esc — отмена)"
                : "Наведите квадрат на 1-ю вкладку и кликните (Esc — отмена)",
            StatusKind.Busy);
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
            TabStatusText.Text = "Выключено — макрос не будет переключать вкладку.";
            TabStatusText.Foreground = IdleBrush;
            return;
        }

        if (!TabResolver.HasBase(_settings))
        {
            TabStatusText.Text = "Позиция 1-й вкладки не задана — нажмите «Указать вкладку 1».";
            TabStatusText.Foreground = ErrorBrush;
            return;
        }

        if (TabResolver.IndexOf(_settings.TabKey) > 1 && !TabResolver.HasStep(_settings))
        {
            TabStatusText.Text = $"Вкладка {name}: нужен шаг — нажмите «Указать вкладку 2».";
            TabStatusText.Foreground = ErrorBrush;
            return;
        }

        var step = TabResolver.HasStep(_settings)
            ? $"шаг {TabResolver.StepPixelsX(_settings)} × {TabResolver.StepPixelsY(_settings)} px"
            : "шаг не задан";

        TabStatusText.Foreground = SuccessBrush;
        TabStatusText.Text = $"Вкладка {name}: {step}.";
    }

    private void OnPickSellCellClick(object? sender, RoutedEventArgs e) => ShowSellPicker(step: false);

    private void OnPickSellStepClick(object? sender, RoutedEventArgs e) => ShowSellPicker(step: true);

    private void ShowSellPicker(bool step)
    {
        if (_running)
        {
            return;
        }

        var existing = step ? _sellStepPicker : _sellCellPicker;
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        if (step && !SellGridResolver.HasBase(_settings))
        {
            SetStatus("Сначала укажите первую ячейку области продажи.", StatusKind.Error);
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            SetStatus("Не удалось определить позицию курсора.", StatusKind.Error);
            return;
        }

        var scaling = Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y))?.Scaling ?? 1.0;
        var half = (int)Math.Round(32 * scaling);

        _picking = true;
        PickSellCellButton.IsEnabled = false;
        PickSellStepButton.IsEnabled = false;
        PreviewSellButton.IsEnabled = false;
        SellNowButton.IsEnabled = false;
        RunButton.IsEnabled = false;

        var picked = false;
        var picker = new SlotPickerWindow();
        picker.SlotPicked += (_, point) =>
        {
            picked = true;

            if (step)
            {
                if (SellGridResolver.CaptureStep(_settings, point.X, point.Y))
                {
                    SetStatus(
                        $"Шаг ячеек: {SellGridResolver.StepPixelsX(_settings)} × {SellGridResolver.StepPixelsY(_settings)} px",
                        StatusKind.Success);
                }
                else
                {
                    SetStatus("Сначала укажите первую ячейку области продажи.", StatusKind.Error);
                }
            }
            else
            {
                SellGridResolver.CaptureBase(_settings, point.X, point.Y);
                SetStatus(
                    _settings.SellRightRatio is not null
                        ? "Первая ячейка сохранена — область подстроится под окно игры"
                        : "Первая ячейка сохранена (игра не найдена, сохранены координаты экрана)",
                    StatusKind.Success);
            }

            UpdateSellUi();
            SaveSettings();
        };
        picker.Closed += (_, _) =>
        {
            if (step)
            {
                _sellStepPicker = null;
            }
            else
            {
                _sellCellPicker = null;
            }

            _picking = false;
            SetSellButtonsEnabled(true);

            Show();
            Activate();

            if (!picked)
            {
                SetStatus("Выбор отменён", StatusKind.Idle);
            }
        };

        picker.Position = new PixelPoint(cursor.X - half, cursor.Y - half);
        if (step)
        {
            _sellStepPicker = picker;
        }
        else
        {
            _sellCellPicker = picker;
        }

        Hide();
        picker.Show();
        SetStatus(
            step
                ? "Наведите квадрат на соседнюю ячейку и кликните (Esc — отмена)"
                : "Наведите квадрат на первую ячейку сумки и кликните (Esc — отмена)",
            StatusKind.Busy);
    }

    private void UpdateSellUi()
    {
        if (!SellGridResolver.HasBase(_settings))
        {
            SellGridStatusText.Text = "Область не задана";
            SellGridStatusText.Foreground = ErrorBrush;
            SellGridCoordsText.Text = "Нажмите «Ячейка 1» и кликните по левой верхней ячейке сумки.";
            return;
        }

        var columns = Math.Clamp(_settings.SellColumns, 1, SellGridResolver.MaxColumns);
        var rows = Math.Clamp(_settings.SellRows, 1, SellGridResolver.MaxRows);
        var hasStep = SellGridResolver.HasStep(_settings);

        if ((columns > 1 || rows > 1) && !hasStep)
        {
            SellGridStatusText.Text = "Нужен шаг ячеек";
            SellGridStatusText.Foreground = ErrorBrush;
            SellGridCoordsText.Text = "Нажмите «Шаг» и кликните по соседней ячейке.";
            return;
        }

        SellGridStatusText.Text = "Область задана";
        SellGridStatusText.Foreground = SuccessBrush;

        var step = hasStep
            ? $"шаг {SellGridResolver.StepPixelsX(_settings)} × {SellGridResolver.StepPixelsY(_settings)} px"
            : "одна ячейка";
        var cellCount = columns * rows;
        var skipped = _settings.SellSkip.Count(index => index >= 0 && index < cellCount);
        var rarity = _settings.SellUseRarity
            ? $"редкости: {RarityCatalog.Allowed(_settings).Count()} из 5"
            : "все редкости";

        SellGridCoordsText.Text = $"{columns} × {rows} ячеек, {step}, {rarity}" +
                                  (skipped > 0 ? $", исключено: {skipped}" : string.Empty) + ".";
    }

    private async void OnPreviewSellClick(object? sender, RoutedEventArgs e)
    {
        if (_running || _picking)
        {
            return;
        }

        ReadSettingsFromUi();

        if (!SellGridResolver.HasBase(_settings))
        {
            SetStatus("Сначала выберите первую ячейку области продажи.", StatusKind.Error);
            return;
        }

        if ((_settings.SellColumns > 1 || _settings.SellRows > 1) && !SellGridResolver.HasStep(_settings))
        {
            SetStatus("Сначала укажите шаг ячеек: «Шаг» и клик по соседней ячейке.", StatusKind.Error);
            return;
        }

        if (!GameWindow.TryGetClientBounds(out var bounds))
        {
            SetStatus("Окно Drakensang не найдено — откройте игру и окно торговца.", StatusKind.Error);
            return;
        }

        _picking = true;
        SetSellButtonsEnabled(false);
        Hide();

        try
        {
            // Let the game repaint without our window on top.
            await Task.Delay(400);

            var capture = ScreenCapture.Capture(bounds);
            var bitmap = CreateBitmap(capture.Pixels, capture.Width, capture.Height);
            var cells = BuildSellPreviewCells(bounds, capture);

            if (cells.Count == 0)
            {
                _picking = false;
                SetSellButtonsEnabled(true);
                Show();
                Activate();
                SetStatus("Область продажи вне окна игры — выберите ячейки заново.", StatusKind.Error);
                return;
            }

            var preview = new SellPreviewWindow();
            preview.Initialize(
                bitmap,
                capture.Width,
                capture.Height,
                cells,
                _settings.SellSkip,
                _settings.SellUseRarity,
                RarityCatalog.Allowed(_settings));
            preview.Saved += (_, skip) =>
            {
                _settings.SellSkip = skip.ToList();
                UpdateSellUi();
                SaveSettings();
                SetStatus($"Область продажи сохранена: исключено ячеек {skip.Count}.", StatusKind.Success);
            };
            preview.Closed += (_, _) =>
            {
                _sellPreview = null;
                _picking = false;
                SetSellButtonsEnabled(true);
                Show();
                Activate();
            };

            _sellPreview = preview;
            preview.Show();

            var expected = _settings.SellColumns * _settings.SellRows;
            if (cells.Count < expected)
            {
                SetStatus(
                    $"Внимание: {expected - cells.Count} ячеек вне окна игры — проверьте калибровку области.",
                    StatusKind.Error);
            }
            else
            {
                SetStatus("Проверьте область: клик по ячейке исключает её из продажи.", StatusKind.Busy);
            }
        }
        catch (Exception ex)
        {
            _picking = false;
            SetSellButtonsEnabled(true);
            Show();
            Activate();
            SetStatus("Не удалось сделать снимок: " + ex.Message, StatusKind.Error);
        }
    }

    private List<SellPreviewWindow.Cell> BuildSellPreviewCells(ClientBounds bounds, ScreenCaptureResult capture)
    {
        var cells = new List<SellPreviewWindow.Cell>();

        if (!SellGridResolver.TryResolveGrid(
                _settings,
                out var firstX,
                out var firstY,
                out var stepX,
                out var stepY))
        {
            return cells;
        }

        var width = Math.Abs(stepX) > 0 ? Math.Abs(stepX) : 44;
        var height = Math.Abs(stepY) > 0 ? Math.Abs(stepY) : 44;

        for (var row = 0; row < _settings.SellRows; row++)
        {
            for (var column = 0; column < _settings.SellColumns; column++)
            {
                var x = firstX + column * stepX;
                var y = firstY + row * stepY;

                if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
                {
                    continue;
                }

                var left = x - width / 2 - bounds.Left;
                var top = y - height / 2 - bounds.Top;

                var rarity = RarityDetector.Detect(
                    capture.Pixels,
                    capture.Width,
                    capture.Height,
                    left,
                    top,
                    width,
                    height);

                cells.Add(new SellPreviewWindow.Cell(
                    SellGridResolver.IndexOf(column, row, _settings.SellColumns),
                    left,
                    top,
                    width,
                    height,
                    rarity));
            }
        }

        return cells;
    }

    private static WriteableBitmap CreateBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var buffer = bitmap.Lock();
        var rowBytes = buffer.RowBytes;

        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(pixels, y * width * 4, IntPtr.Add(buffer.Address, y * rowBytes), width * 4);
        }

        return bitmap;
    }

    private void SetSellButtonsEnabled(bool enabled)
    {
        PickSellCellButton.IsEnabled = enabled;
        PickSellStepButton.IsEnabled = enabled;
        PreviewSellButton.IsEnabled = enabled;
        SellNowButton.IsEnabled = enabled;
        RunButton.IsEnabled = enabled;
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

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _runCts?.Cancel();
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
