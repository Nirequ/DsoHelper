using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DsoHelper.FastMount;
using DsoHelper.Infrastructure;
using DsoHelper.UI;

namespace DsoHelper;

public partial class MainWindow : Window
{
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush BusyBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#F87171"));

    private readonly FastMountSettings _settings;
    private readonly DispatcherTimer _saveTimer;

    private GlobalHotkey? _hotkey;
    private SlotPickerWindow? _slotPicker;
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

        AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;
        UpdateSlotUi();
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
            if (!picked)
            {
                SetStatus("Выбор слота отменён", StatusKind.Idle);
            }
        };

        picker.Position = new PixelPoint(cursor.X - half, cursor.Y - half);
        _slotPicker = picker;
        picker.Show(this);
        picker.Activate();
        SetStatus("Перетащите квадрат на слот и отпустите", StatusKind.Busy);
    }

    private void UpdateSlotUi()
    {
        if (!SlotResolver.HasSlot(_settings))
        {
            SlotStatusText.Text = "Слот не выбран";
            SlotStatusText.Foreground = ErrorBrush;
            SlotCoordsText.Text = "Нажмите «Выбрать слот» и перетащите квадрат на ячейку инвентаря.";
            return;
        }

        SlotStatusText.Text = "Слот выбран";
        SlotStatusText.Foreground = SuccessBrush;

        SlotCoordsText.Text = _settings.SlotRightRatio is not null
            ? "Позиция привязана к окну игры и подстраивается под разрешение."
            : $"Координаты экрана: {_settings.SlotX}, {_settings.SlotY}";
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
