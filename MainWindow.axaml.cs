using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
    // Mirrors UI/Palette.axaml (Drakensang helper palette).
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#8C93A2"));
    private static readonly IBrush BusyBrush = new SolidColorBrush(Color.Parse("#E0A93B"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#2CB67D"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#C93B3B"));

    private readonly FastMountSettings _settings;
    private readonly DispatcherTimer _saveTimer;

    private GlobalHotkey? _hotkey;
    private SlotPickerWindow? _slotPicker;
    private SlotPickerWindow? _tabPicker;
    private SlotPickerWindow? _tabStepPicker;
    private CancellationTokenSource? _runCts;
    private bool _running;
    private bool _picking;

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
        InventoryKeyBox.LostFocus += (_, _) => RevertKeyIfInvalid(InventoryKeyBox, _settings.InventoryKey);
        MountKeyBox.LostFocus += (_, _) => RevertKeyIfInvalid(MountKeyBox, _settings.MountKey);

        HotkeyBox.TextChanged += (_, _) => ScheduleSave();
        InventoryKeyBox.TextChanged += (_, _) => ScheduleSave();
        MountKeyBox.TextChanged += (_, _) => ScheduleSave();

        InventoryDelayBox.ValueChanged += (_, _) => ScheduleSave();
        CapeSwapDelayBox.ValueChanged += (_, _) => ScheduleSave();
        MountDelayBox.ValueChanged += (_, _) => ScheduleSave();
        PostActionDelayBox.ValueChanged += (_, _) => ScheduleSave();
        CloseInventoryBeforeMountCheck.IsCheckedChanged += (_, _) => ScheduleSave();

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
        HotkeyBox.Text = HotkeyParser.TryParseHotkey(_settings.Hotkey, out var hotkey, out _)
            ? hotkey.Display
            : _settings.Hotkey;
        InventoryKeyBox.Text = CanonicalKey(_settings.InventoryKey, "I");
        MountKeyBox.Text = CanonicalKey(_settings.MountKey, "M");

        InventoryDelayBox.Value = _settings.InventoryDelayMs;
        CapeSwapDelayBox.Value = _settings.CapeSwapDelayMs;
        MountDelayBox.Value = _settings.MountDelayMs;
        PostActionDelayBox.Value = _settings.PostActionDelayMs;

        CloseInventoryBeforeMountCheck.IsChecked = _settings.CloseInventoryBeforeMount;

        SwitchTabCheck.IsChecked = _settings.SwitchInventoryTab;

        AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;
        UpdateSlotUi();
        UpdateTabUi();
    }

    private void ReadSettingsFromUi()
    {
        if (HotkeyParser.TryParseHotkey(HotkeyBox.Text, out var hotkey, out _))
        {
            _settings.Hotkey = hotkey.Display;
        }

        _settings.InventoryKey = CanonicalKey(InventoryKeyBox.Text, _settings.InventoryKey);
        _settings.MountKey = CanonicalKey(MountKeyBox.Text, _settings.MountKey);

        _settings.InventoryDelayMs = ToInt(InventoryDelayBox);
        _settings.CapeSwapDelayMs = ToInt(CapeSwapDelayBox);
        _settings.MountDelayMs = ToInt(MountDelayBox);
        _settings.PostActionDelayMs = ToInt(PostActionDelayBox);
        _settings.CloseInventoryBeforeMount = CloseInventoryBeforeMountCheck.IsChecked == true;
        _settings.SwitchInventoryTab = SwitchTabCheck.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
    }

    private static int ToInt(NumericUpDown box) =>
        box.Value is { } value ? (int)value : 0;

    private static string CanonicalKey(string? text, string fallback) =>
        KeyCatalog.TryParseKey(text, out var key) && !key.IsModifier ? key.Display : fallback;

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
