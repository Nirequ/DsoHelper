using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DsoHelper.FastMount;
using DsoHelper.Infrastructure;
using DsoHelper.UI;
using HAlign = Avalonia.Layout.HorizontalAlignment;
using VAlign = Avalonia.Layout.VerticalAlignment;

namespace DsoHelper;

public partial class MainWindow : Window
{
    /// <summary>Диапазон и шаг задержки макроса, мс.</summary>
    private const int DelayMaxMs = 1000;
    private const int DelayStepMs = 15;

    /// <summary>Индекс вкладки «Настройки» в MainTabControl.</summary>
    private const int SettingsTabIndex = 4;

    /// <summary>Индекс вкладки «Инвентарь» — домашняя страница.</summary>
    private const int InventoryTabIndex = 0;

    // Mirrors UI/Palette.axaml (Drakensang palette).
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#B1AC9E"));
    private static readonly IBrush BusyBrush = new SolidColorBrush(Color.Parse("#C59B53"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#76BE9B"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#D97065"));

    /// <summary>Рамки ячеек визуализации инвентаря (7 × 4, индекс = номер слота − 1).</summary>
    private readonly Border[] _invCells = new Border[InventoryGridResolver.SlotCount];

    /// <summary>Символ внутри ячейки: «#1»/«#28», «·», «?» или «•».</summary>
    private readonly TextBlock[] _invCellGlyphs = new TextBlock[InventoryGridResolver.SlotCount];

    /// <summary>Золотая точка-якорь у слотов #1 и #28, когда сетка уже проверена.</summary>
    private readonly Border[] _invCellMarks = new Border[InventoryGridResolver.SlotCount];

    private readonly FastMountSettings _settings;
    private readonly DispatcherTimer _saveTimer;

    private GlobalHotkey? _hotkey;
    private GlobalHotkey? _sellHotkey;
    private GlobalHotkey? _meltHotkey;
    private SlotPickerWindow? _slotPicker;
    private SlotPickerWindow? _tabPicker;
    private SlotPickerWindow? _tabStepPicker;
    private SlotPickerWindow? _invSlot1Picker;
    private SlotPickerWindow? _invSlot28Picker;
    private SlotPickerWindow? _meltButtonPicker;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _hoverCts;
    private CancellationTokenSource? _sellCts;
    private CancellationTokenSource? _meltCts;
    private bool _running;
    private bool _picking;
    private bool _checking;
    private bool _hovering;
    private bool _selling;
    private bool _melting;
    private bool _checkingUpdates;
    private string _pendingUpdateTag = string.Empty;
    private string? _pendingUpdateUrl;
    private string? _pendingUpdateSha;

    /// <summary>Проверка при старте нашла, что установлена последняя версия.</summary>
    private bool _startupUpToDate;

    /// <summary>Готовая фраза для настроек: «У вас последняя версия: 1.4.7.».</summary>
    private string _startupCheckMessage = string.Empty;

    /// <summary>Тихая проверка при старте ещё выполняется.</summary>
    private bool _startupCheckRunning;
    private bool _delaySyncing;

    /// <summary>Пауза на каждой занятой ячейке при обходе курсором, мс.</summary>
    private const int HoverPerSlotMs = 550;

    /// <summary>Допустимый диапазон и шаг задержек автопродажи, мс.</summary>
    private const int SellDelayMaxMs = 1000;
    private const int SellDelayStepMs = 15;

    /// <summary>Максимальная пауза после вызова маунта, мс.</summary>
    private const int MountDelayMaxMs = 1500;

    /// <summary>Максимальная пауза после переплавки партии, мс.</summary>
    private const int MeltProcessDelayMaxMs = 2000;

    public MainWindow()
    {
        InitializeComponent();
        BuildInvGrid();
        ApplyInvSlotRarities(null);

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
        ApplyMeltHotkey(showError: false);
        SetStatus("Готово к работе", StatusKind.Idle);

        Closing += OnClosing;
        Opened += (_, _) =>
        {
            if (TryGetPlatformHandle() is { } handle)
            {
                WindowAppearance.UseDarkTitleBar(handle.Handle);
            }

            // Проверка наличия новой версии в фоне: только уведомление,
            // ничего не скачивается и не устанавливается без согласия.
            _ = CheckUpdateBannerAsync();
        };
    }

    /// <summary>
    /// Тихо спрашивает GitHub о новой версии и, если она есть, показывает
    /// уведомление сверху окна. Ошибки сети игнорируются.
    /// </summary>
    private async Task CheckUpdateBannerAsync()
    {
        _startupCheckRunning = true;
        try
        {
            var check = await GitHubUpdater.CheckVersionSilentlyAsync().ConfigureAwait(true);
            if (check is null)
            {
                return;
            }

            if (check.Outcome == CheckOutcome.UpToDate)
            {
                // Баннер не нужен, но панель настроек покажет «последняя версия»,
                // а не общую фразу про проверку при запуске.
                _startupUpToDate = true;
                _startupCheckMessage = check.Message;
                return;
            }

            if (check.Outcome != CheckOutcome.Available)
            {
                return;
            }

            _pendingUpdateTag = check.DisplayTag;
            _pendingUpdateUrl = check.DownloadUrl;
            _pendingUpdateSha = check.Sha256;

            Dispatcher.UIThread.Post(() =>
            {
                UpdateBannerTitle.Text = $"Доступна версия v{check.DisplayTag}";
                UpdateBannerText.Text = "Нажмите «Обновить», чтобы скачать сборку и перезапустить программу.";
                UpdateBannerButton.IsEnabled = true;
                UpdateBannerButton.Content = "Обновить";
                UpdateBannerProgressBar.IsVisible = false;
                UpdateBanner.IsVisible = true;
            });
        }
        finally
        {
            _startupCheckRunning = false;

            // Настройки могли открыть, пока проверка ещё шла: обновляем строку
            // на месте, чтобы «Проверяю…» не висело до переоткрытия панели.
            RefreshSettingsUpdateStatus();
        }
    }

    /// <summary>Скачивание и установка обновления из верхнего уведомления.</summary>
    private async void OnBannerUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_pendingUpdateUrl is null || _checkingUpdates)
        {
            return;
        }

        _checkingUpdates = true;
        UpdateBannerButton.IsEnabled = false;
        UpdateBannerButton.Content = "Скачиваю…";
        UpdateBannerText.Text = $"Загружаю v{_pendingUpdateTag}…";
        UpdateBannerProgressBar.IsVisible = true;
        UpdateBannerProgressBar.Value = 0;

        try
        {
            var result = await Task.Run(() =>
                GitHubUpdater.PerformUpdateAsync(
                    _pendingUpdateUrl,
                    _pendingUpdateTag,
                    _pendingUpdateSha,
                    message => Dispatcher.UIThread.Post(() =>
                    {
                        UpdateBannerText.Text = message;
                    }),
                    percent => Dispatcher.UIThread.Post(() =>
                    {
                        var clamped = Math.Clamp(percent, 0, 100);
                        UpdateBannerProgressBar.IsIndeterminate = false;
                        UpdateBannerProgressBar.Value = clamped;
                        UpdateBannerText.Text = $"Скачиваю v{_pendingUpdateTag}… {clamped}%";
                    })));

            if (result.Outcome == UpdateOutcome.Updated)
            {
                // Приложение перезапустится само внутри PerformUpdateAsync.
                UpdateBannerText.Text = "Установлено, перезапускаю программу…";
                return;
            }

            _pendingUpdateTag = string.Empty;
            _pendingUpdateUrl = null;
            _pendingUpdateSha = null;
            UpdateBannerButton.Content = "Обновить";
            UpdateBannerButton.IsEnabled = true;
            UpdateBannerProgressBar.IsVisible = false;
            UpdateBannerText.Text = result.Message;
        }
        catch (Exception ex)
        {
            UpdateBannerButton.Content = "Обновить";
            UpdateBannerButton.IsEnabled = true;
            UpdateBannerProgressBar.IsVisible = false;
            UpdateBannerText.Text = "Обновление не удалось: " + GitHubUpdater.DescribeError(ex);
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    /// <summary>Прячет верхнее уведомление об обновлении.</summary>
    private void OnBannerCloseClick(object? sender, RoutedEventArgs e)
    {
        UpdateBanner.IsVisible = false;
        _pendingUpdateTag = string.Empty;
        _pendingUpdateUrl = null;
        _pendingUpdateSha = null;
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

        MeltHotkeyBox.LostFocus += (_, _) => CommitMeltHotkey();
        MeltHotkeyBox.TextChanged += (_, _) => ScheduleSave();

        WireQualityChecks();

        WireDelayControls();
        WireDelayPair(
            MountDelaySlider, MountDelayValueBox, 700,
            v => _settings.MountDelayMs = v, MountDelayMaxMs);
        WireDelayPair(SellSwitchSlider, SellSwitchValueBox, 200, v => _settings.SellSwitchDelayMs = v);
        WireDelayPair(SellItemSlider, SellItemValueBox, 150, v => _settings.SellItemDelayMs = v);
        WireDelayPair(MeltSwitchSlider, MeltSwitchValueBox, 200, v => _settings.MeltSwitchDelayMs = v);
        WireDelayPair(MeltItemSlider, MeltItemValueBox, 150, v => _settings.MeltItemDelayMs = v);
        WireDelayPair(MeltProcessSlider, MeltProcessValueBox, 900, v => _settings.MeltProcessDelayMs = v, MeltProcessDelayMaxMs);

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
        MeltHotkeyBox.Text = HotkeyParser.TryParseHotkey(_settings.MeltHotkey, out var meltHotkey, out _)
            ? meltHotkey.Display
            : _settings.MeltHotkey;
        InventoryKeyBox.Text = CanonicalKey(_settings.InventoryKey, "I");
        MountKeyBox.Text = CanonicalKey(_settings.MountKey, "M");

        var delay = Math.Clamp(_settings.DelayMs, 0, DelayMaxMs);
        DelaySlider.Value = delay;
        DelayValueBox.Text = delay.ToString();

        SellSwitchSlider.Value = Math.Clamp(_settings.SellSwitchDelayMs, 0, SellDelayMaxMs);
        SellItemSlider.Value = Math.Clamp(_settings.SellItemDelayMs, 0, SellDelayMaxMs);
        SellSwitchValueBox.Text = SellSwitchSlider.Value.ToString("0");
        SellItemValueBox.Text = SellItemSlider.Value.ToString("0");

        MeltSwitchSlider.Value = Math.Clamp(_settings.MeltSwitchDelayMs, 0, SellDelayMaxMs);
        MeltItemSlider.Value = Math.Clamp(_settings.MeltItemDelayMs, 0, SellDelayMaxMs);
        MeltProcessSlider.Value = Math.Clamp(_settings.MeltProcessDelayMs, 0, MeltProcessDelayMaxMs);
        MeltSwitchValueBox.Text = MeltSwitchSlider.Value.ToString("0");
        MeltItemValueBox.Text = MeltItemSlider.Value.ToString("0");
        MeltProcessValueBox.Text = MeltProcessSlider.Value.ToString("0");

        MountDelaySlider.Value = Math.Clamp(_settings.MountDelayMs, 0, MountDelayMaxMs);
        MountDelayValueBox.Text = MountDelaySlider.Value.ToString("0");

        SwitchTabCheck.IsChecked = _settings.SwitchInventoryTab;

        AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;

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
        UpdateMeltUi();
        UpdateMeltTabsUi();
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
        else if (ReferenceEquals(box, MeltHotkeyBox))
        {
            HandleMeltHotkeyKey(e);
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

    private void ApplyMeltHotkey(bool showError)
    {
        _meltHotkey?.Dispose();
        _meltHotkey = null;

        var text = MeltHotkeyBox.Text?.Trim() ?? string.Empty;
        if (!HotkeyParser.TryParseHotkey(text, out var definition, out var error))
        {
            MeltHotkeyStatusText.Text = "некорректный хоткей";
            MeltHotkeyStatusText.Foreground = ErrorBrush;
            if (showError)
            {
                SetStatus(error, StatusKind.Error);
            }

            return;
        }

        try
        {
            var hotkey = new GlobalHotkey(definition.Modifiers, definition.VirtualKey);
            hotkey.Pressed += OnMeltHotkeyPressed;
            _meltHotkey = hotkey;

            MeltHotkeyBox.Text = definition.Display;
            MeltHotkeyStatusText.Text = $"активен · {definition.Display}";
            MeltHotkeyStatusText.Foreground = SuccessBrush;
            _settings.MeltHotkey = definition.Display;
            ScheduleSave();

            if (showError)
            {
                SetStatus($"Хоткей авторасплавки {definition.Display} активен", StatusKind.Idle);
            }
        }
        catch (Exception ex)
        {
            MeltHotkeyStatusText.Text = "недоступен";
            MeltHotkeyStatusText.Foreground = ErrorBrush;
            if (showError)
            {
                SetStatus(ex.Message, StatusKind.Error);
            }
        }
    }

    private void OnMeltHotkeyPressed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!GameWindow.IsForeground())
            {
                return;
            }

            // Повторное нажатие хоткея во время прогона — остановка.
            if (_melting)
            {
                _meltCts?.Cancel();
                SetStatus("Останавливаю авторасплавку…", StatusKind.Busy);
                return;
            }

            _ = RunAutoMeltAsync();
        });
    }

    private void HandleMeltHotkeyKey(KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            MeltHotkeyBox.Text = _settings.MeltHotkey;
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
                MeltHotkeyBox.Text = preview;
            }

            return;
        }

        MeltHotkeyBox.Text = KeyCatalog.ComposeHotkey(e.KeyModifiers, key.Display);
        ApplyMeltHotkey(showError: true);
    }

    private void CommitMeltHotkey()
    {
        if (!HotkeyParser.TryParseHotkey(MeltHotkeyBox.Text, out _, out _))
        {
            MeltHotkeyBox.Text = _settings.MeltHotkey;
        }

        ApplyMeltHotkey(showError: true);
    }

    private void RevertKeyIfInvalid(TextBox box, string fallback)
    {
        var canonical = CanonicalKey(box.Text, fallback);
        box.Text = canonical;
        ScheduleSave();
    }

    private async Task RunFastMountAsync(bool interactive)
    {
        if (_running || _picking || _checking || _hovering || _selling || _melting)
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

    private void OnPickMeltButtonClick(object? sender, RoutedEventArgs e) =>
        ShowSlotPicker(
            () => _meltButtonPicker,
            picker => _meltButtonPicker = picker,
            enabled => PickMeltButtonButton.IsEnabled = enabled,
            precondition: null,
            markerSize: SlotPickerWindow.SlotMarkerSize,
            point =>
            {
                MeltFurnaceResolver.CaptureButton(_settings, point.X, point.Y);
                UpdateMeltUi();
                SaveSettings();
                SetStatus("Кнопка «Переплавить» сохранена", StatusKind.Success);
            },
            cancelStatus: "Выбор кнопки отменён",
            busyStatus: "Наведите квадрат на кнопку «Переплавить» в окне плавильни и кликните (Esc — отмена)");

    /// <summary>Статус калибровки кнопки «Переплавить» в окне плавильни.</summary>
    private void UpdateMeltUi()
    {
        if (!MeltFurnaceResolver.HasButton(_settings))
        {
            MeltButtonStatusText.Text = "Кнопка «Переплавить» не указана.";
            MeltButtonStatusText.Foreground = ErrorBrush;
            return;
        }

        MeltButtonStatusText.Text = _settings.MeltButtonRightRatio is not null
            ? "Кнопка «Переплавить» указана — позиция подстроится под окно игры."
            : "Кнопка «Переплавить» указана (координаты экрана).";
        MeltButtonStatusText.Foreground = SuccessBrush;
    }

    private void UpdateInvUi()
    {
        var has1 = InventoryGridResolver.HasSlot1(_settings);
        var has28 = InventoryGridResolver.HasSlot28(_settings);

        if (!has1 && !has28)
        {
            InvStatusText.Text = "Сетка не откалибрована";
            InvStatusText.Foreground = ErrorBrush;
            ApplyInvSlotRarities(null);
            return;
        }

        if (!has1)
        {
            InvStatusText.Text = "Слот #28 указан";
            InvStatusText.Foreground = IdleBrush;
            return;
        }

        if (!has28)
        {
            InvStatusText.Text = "Слот #1 указан";
            InvStatusText.Foreground = IdleBrush;
            return;
        }

        if (InventoryGridResolver.TryResolve(_settings, out var slots, out _))
        {
            InvStatusText.Text = "Сетка готова";
            InvStatusText.Foreground = SuccessBrush;
            ToolTip.SetTip(
                InvGridBox,
                $"Сетка {InventoryGridResolver.Cols} × {InventoryGridResolver.Rows} · ячеек: {InventoryGridResolver.SlotCount} · " +
                $"прямоугольник ~{slots[0].W} × {slots[0].H} px");
        }
        else
        {
            InvStatusText.Text = "Игровое окно не найдено";
            InvStatusText.Foreground = IdleBrush;
        }
    }

    /// <summary>Захватывает клиент игры и классифицирует сетку.</summary>
    private async Task<(List<InventorySlotResult>? Results, string Error)> CaptureAndClassifyAsync(List<InventorySlotRect> slots) =>
        await InventoryScanner.CaptureAndClassifyAsync(slots);

    /// <summary>Строит 28 ячеек сетки инвентаря (7 × 4) для визуализации редкостей.</summary>
    private void BuildInvGrid()
    {
        if (InvCellsHost is null)
        {
            return;
        }

        InvCellsHost.Children.Clear();

        for (var row = 0; row < InventoryGridResolver.Rows; row++)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };

            for (var col = 0; col < InventoryGridResolver.Cols; col++)
            {
                var index = (row * InventoryGridResolver.Cols) + col;

                var glyph = new TextBlock
                {
                    FontSize = 10,
                    HorizontalAlignment = HAlign.Center,
                    VerticalAlignment = VAlign.Center,
                };

                // Точка-якорь: помечает опорные ячейки #1 и #28, когда сетка уже проверена.
                var mark = new Border
                {
                    Width = 4,
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Background = ResBrush("GoldBrush"),
                    HorizontalAlignment = HAlign.Left,
                    VerticalAlignment = VAlign.Top,
                    Margin = new Thickness(2),
                    IsVisible = false,
                };

                var overlay = new Grid();
                overlay.Children.Add(glyph);
                overlay.Children.Add(mark);

                var cell = new Border
                {
                    Width = 27,
                    Height = 32,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    Child = overlay,
                };

                rowPanel.Children.Add(cell);
                _invCells[index] = cell;
                _invCellGlyphs[index] = glyph;
                _invCellMarks[index] = mark;
            }

            InvCellsHost.Children.Add(rowPanel);
        }
    }

    /// <summary>
    /// Раскрашивает ячейки по редкости. <paramref name="results"/> = null возвращает
    /// исходный вид сетки (опорные ячейки #1/#28 подсвечены золотом).
    /// </summary>
    private void ApplyInvSlotRarities(IReadOnlyList<InventorySlotResult>? results)
    {
        var count = _invCells.Length;
        for (var i = 0; i < count; i++)
        {
            var cell = _invCells[i];
            var glyph = _invCellGlyphs[i];
            var mark = _invCellMarks[i];
            if (cell is null || glyph is null)
            {
                continue;
            }

            var slotNumber = i + 1;
            var isAnchor = i == 0 || i == count - 1;
            var result = results is null ? null : results.FirstOrDefault(r => r.Index == slotNumber);

            if (mark is not null)
            {
                mark.IsVisible = isAnchor && result is not null;
            }

            if (result is null)
            {
                // Ещё не проверяли: опорные ячейки остаются золотыми с номером.
                glyph.Text = isAnchor ? $"#{slotNumber}" : "·";
                glyph.FontSize = 10;
                glyph.Foreground = isAnchor ? ResBrush("GoldBrush") : ResBrush("TextMutedBrush");
                cell.BorderThickness = new Thickness(1);
                cell.Background = isAnchor ? ResBrush("GoldTintBrush", Brushes.Transparent) : ResBrush("CardSurfaceBrush", Brushes.Transparent);
                cell.BorderBrush = isAnchor ? ResBrush("GoldBrush") : ResBrush("BorderBrush");
                ToolTip.SetTip(cell, isAnchor
                    ? $"Слот #{slotNumber} · опорная ячейка сетки"
                    : $"Слот #{slotNumber}");
                continue;
            }

            if (result.IsEmpty)
            {
                glyph.Text = "·";
                glyph.FontSize = 10;
                glyph.Foreground = ResBrush("TextMutedBrush");
                cell.BorderThickness = new Thickness(1);
                cell.Background = ResBrush("CardSurfaceBrush", Brushes.Transparent);
                cell.BorderBrush = ResBrush("BorderBrush");
                ToolTip.SetTip(cell, $"Слот #{slotNumber} · пусто");
                continue;
            }

            var rarityBrush = InvRarityBrush(result.Rarity);
            var rarityLabel = InventoryChecker.RarityLabel(result.Rarity);

            glyph.Text = result.Unidentified ? "?" : "•";
            glyph.FontSize = result.Unidentified ? 11 : 13;
            glyph.Foreground = rarityBrush;
            cell.BorderThickness = new Thickness(2);
            cell.Background = ResBrush("CardSurfaceBrush", Brushes.Transparent);
            cell.BorderBrush = rarityBrush;
            ToolTip.SetTip(cell, result.Unidentified
                ? $"Слот #{slotNumber} · {rarityLabel} · неопознан"
                : $"Слот #{slotNumber} · {rarityLabel}");
        }
    }

    /// <summary>Кисть рамки для конкретной редкости из палитры UI/Palette.axaml.</summary>
    private static IBrush InvRarityBrush(InventoryRarity rarity) => rarity switch
    {
        InventoryRarity.Improved => ResBrush("RarityImprovedBrush"),
        InventoryRarity.Magic => ResBrush("RarityMagicBrush"),
        InventoryRarity.Extraordinary => ResBrush("RarityExtraordinaryBrush"),
        InventoryRarity.Legendary => ResBrush("RarityLegendaryBrush"),
        InventoryRarity.Unique => ResBrush("RarityUniqueBrush"),
        InventoryRarity.Mythic => ResBrush("RarityMythicBrush"),
        _ => ResBrush("RarityCommonBrush"),
    };

    /// <summary>Берёт кисть из ресурсов приложения (палитра) с запасным вариантом.</summary>
    private static IBrush ResBrush(string key, IBrush? fallback = null)
    {
        if (Application.Current is { } app &&
            app.TryFindResource(key, out var value) &&
            value is IBrush brush)
        {
            return brush;
        }

        return fallback ?? IdleBrush;
    }

    private async Task RunInventoryCheckAsync()
    {
        if (_running || _picking || _checking || _hovering || _selling || _melting)
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
            //  Сводка по ячейкам — в подсказке к сетке: отдельного текста в карточке больше нет.
            ToolTip.SetTip(InvGridBox, summary);
            ApplyInvSlotRarities(results);

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
        if (_running || _picking || _checking || _hovering || _selling || _melting)
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
            ApplyInvSlotRarities(results);
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
        if (_running || _picking || _checking || _hovering || _selling || _melting)
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

        if (!_settings.SellImproved && !_settings.SellMagic &&
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
                ApplyInvSlotRarities(results);
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
        }
    }

    /// <summary>
    /// Авторасплавка: обходит выбранные вкладки инвентаря, отправляет правой
    /// кнопкой опознанные предметы отмеченных качеств в плавильню и каждые
    /// 9 предметов нажимает «Переплавить».
    /// </summary>
    private async Task RunAutoMeltAsync()
    {
        if (_running || _picking || _checking || _hovering || _selling || _melting)
        {
            return;
        }

        ReadSettingsFromUi();
        if (!AutoMeltRunner.TryValidate(_settings, out var error))
        {
            SetStatus(error, StatusKind.Error);
            return;
        }

        _melting = true;
        _meltCts = new CancellationTokenSource();
        AutoMeltStatusText.Text = "Авторасплавка запущена";
        AutoMeltStatusText.Foreground = IdleBrush;
        AutoMeltSummaryText.Text = "Активирую окно игры…";
        var token = _meltCts.Token;

        try
        {
            if (GameWindow.TryActivate())
            {
                SetStatus("Окно игры активировано…", StatusKind.Busy);
                await Task.Delay(200, token);
            }

            var result = await AutoMeltRunner.RunAsync(
                _settings,
                message => Dispatcher.UIThread.Post(() =>
                {
                    AutoMeltSummaryText.Text = message;
                    SetStatus(message, StatusKind.Busy);
                }),
                token);

            AutoMeltStatusText.Text = result.SentToFurnace == 0
                ? "Готово · подходящих предметов не найдено"
                : $"Готово · в печь: {result.SentToFurnace} · запусков печи: {result.MeltPresses}";
            AutoMeltStatusText.Foreground = SuccessBrush;
            AutoMeltSummaryText.Text = result.Summary;
            SetStatus(result.Summary, StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            AutoMeltStatusText.Text = "Авторасплавка прервана";
            AutoMeltStatusText.Foreground = IdleBrush;
            AutoMeltSummaryText.Text =
                "Прогон остановлен досрочно: остаток партии может остаться в печи — проверьте окно плавильни.";
            SetStatus("Авторасплавка прервана", StatusKind.Idle);
        }
        catch (Exception ex)
        {
            AutoMeltStatusText.Text = "Авторасплавка остановлена";
            AutoMeltStatusText.Foreground = ErrorBrush;
            AutoMeltSummaryText.Text = ex.Message;
            SetStatus("Ошибка авторасплавки: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            _meltCts?.Dispose();
            _meltCts = null;
            _melting = false;
        }
    }

    /// <summary>Список вкладок автопродажи в порядке обхода.</summary>
    private static List<string> ParseSellTabs(string raw) => TabResolver.ParseSelection(raw);

    private static string JoinSellTabs(IEnumerable<string> tabs) => TabResolver.JoinSelection(tabs);

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

    /// <summary>Список вкладок авторасплавки в порядке обхода.</summary>
    private static List<string> ParseMeltTabs(string raw) => TabResolver.ParseSelection(raw);

    private static string JoinMeltTabs(IEnumerable<string> tabs) => TabResolver.JoinSelection(tabs);

    private void UpdateMeltTabsUi()
    {
        var selected = ParseMeltTabs(_settings.MeltTabs);

        foreach (var child in MeltTabsPanel.Children)
        {
            if (child is not Button button)
            {
                continue;
            }

            var isSelected = button.Tag is string key && selected.Contains(key);
            button.Classes.Set("selected", isSelected);
        }
    }

    private void OnMeltTabToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || TabResolver.IndexOf(key) == 0)
        {
            return;
        }

        var tabs = ParseMeltTabs(_settings.MeltTabs);
        if (!tabs.Remove(key))
        {
            tabs.Add(key);
        }

        _settings.MeltTabs = JoinMeltTabs(tabs);
        UpdateMeltTabsUi();
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
    private void WireDelayPair(
        Slider slider, TextBox box, int fallbackMs, Action<int> apply, int maxMs = SellDelayMaxMs)
    {
        var syncing = false;

        slider.ValueChanged += (_, e) =>
        {
            var clamped = Math.Clamp((int)Math.Round(e.NewValue), 0, maxMs);

            if (!syncing)
            {
                var snapped = Math.Clamp(
                    (int)Math.Round(clamped / (double)SellDelayStepMs) * SellDelayStepMs,
                    0, maxMs);

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

            value = Math.Clamp(value, 0, maxMs);

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
        if (_running || _checking || _hovering || _selling || _melting)
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

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => ShowSettingsTab();

    /// <summary>
    /// Открывает вкладку настроек и подтягивает в неё свежие строки о версии и обновлении.
    /// </summary>
    private void ShowSettingsTab()
    {
        var version = GitHubUpdater.BuildVersionDisplay(
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));
        SettingsVersionText.Text = "Версия " + version;
        SettingsBuildVersionText.Text = version;
        UpdateStatusText.Text = DescribeUpdateStatus();
        UpdateStatusText.Foreground = UpdateStatusBrush();
        MainTabControl.SelectedIndex = SettingsTabIndex;
    }

    /// <summary>Пользователь переключил вкладку — обновляем содержимое настроек, когда она открыта.</summary>
    private void OnMainTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MainTabControl is null || MainTabControl.SelectedIndex != SettingsTabIndex)
        {
            return;
        }

        ShowSettingsTab();
    }

    /// <summary>Строка состояния в настройках: что известно об обновлении на этот момент.</summary>
    private string DescribeUpdateStatus()
    {
        if (_checkingUpdates || _startupCheckRunning)
        {
            return "Проверяю обновления…";
        }

        if (_pendingUpdateUrl is not null)
        {
            return $"Доступна v{_pendingUpdateTag} — кнопка подтвердит скачивание и перезапуск.";
        }

        // Проверка при старте уже отработала и новой версии нет — так и пишем,
        // вместо заглушки «проверяется при запуске».
        if (_startupUpToDate && !string.IsNullOrEmpty(_startupCheckMessage))
        {
            return _startupCheckMessage;
        }

        return "Проверяется при каждом запуске приложения.";
    }

    private IBrush UpdateStatusBrush()
    {
        if (_checkingUpdates || _startupCheckRunning || _pendingUpdateUrl is not null)
        {
            return BusyBrush;
        }

        return _startupUpToDate ? SuccessBrush : IdleBrush;
    }

    /// <summary>Обновляет строку в настройках, если вкладка настроек открыта прямо сейчас.</summary>
    private void RefreshSettingsUpdateStatus()
    {
        if (_checkingUpdates || MainTabControl is null || MainTabControl.SelectedIndex != SettingsTabIndex)
        {
            return;
        }

        UpdateStatusText.Text = DescribeUpdateStatus();
        UpdateStatusText.Foreground = UpdateStatusBrush();
    }

    private void OnSettingsBackClick(object? sender, RoutedEventArgs e) =>
        MainTabControl.SelectedIndex = InventoryTabIndex;

    /// <summary>Открывает страницу релизов проекта в браузере по умолчанию.</summary>
    private void OnOpenReleasesClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Nirequ/DsoHelper/releases/latest",
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Браузер не открылся — приложение продолжает работать как обычно.
        }
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
            // Второй клик по кнопке — это подтверждение: качаем и ставим с отображением ProgressBar.
            if (_pendingUpdateUrl is not null)
            {
                UpdateStatusText.Text = $"Скачиваю v{_pendingUpdateTag}…";
                UpdateStatusText.Foreground = BusyBrush;

                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 0;
                UpdateProgressBar.IsVisible = true;
                UpdatePercentText.Text = "0%";
                UpdatePercentText.IsVisible = true;

                var result = await Task.Run(() =>
                    GitHubUpdater.PerformUpdateAsync(
                        _pendingUpdateUrl,
                        _pendingUpdateTag,
                        _pendingUpdateSha,
                        message =>
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                UpdateStatusText.Text = message;
                                UpdateStatusText.Foreground = message.Contains("100%")
                                    ? SuccessBrush
                                    : BusyBrush;
                            });
                        },
                        percent =>
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                var clamped = Math.Clamp(percent, 0, 100);
                                UpdateProgressBar.IsIndeterminate = false;
                                UpdateProgressBar.Value = clamped;
                                UpdatePercentText.Text = $"{(int)Math.Round(clamped)}%";
                                if (clamped < 100)
                                {
                                    UpdateStatusText.Text = $"Скачиваю v{_pendingUpdateTag}… {(int)Math.Round(clamped)}%";
                                    UpdateStatusText.Foreground = BusyBrush;
                                }
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
                _pendingUpdateSha = check.Sha256;
                CheckUpdatesButton.Content = $"Обновить до v{check.DisplayTag}";
                CheckUpdatesButton.Classes.Set("selected", true);
                UpdateStatusText.Text = "Доступна новая версия.";
                UpdateStatusText.Foreground = BusyBrush;
                return;
            }

            CheckUpdatesButton.Content = "Проверить сейчас";
            UpdateStatusText.Text = check.Message;
            UpdateStatusText.Foreground = check.Outcome == CheckOutcome.UpToDate
                ? SuccessBrush
                : ErrorBrush;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Проверка не удалась: " + GitHubUpdater.DescribeError(ex);
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
        _pendingUpdateSha = null;
        CheckUpdatesButton.Content = "Проверить сейчас";
        CheckUpdatesButton.Classes.Set("selected", false);
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.IsVisible = false;
        UpdatePercentText.IsVisible = false;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _runCts?.Cancel();
        _hoverCts?.Cancel();
        _sellCts?.Cancel();
        _meltCts?.Cancel();
        _saveTimer.Stop();
        SaveSettings();
        _hotkey?.Dispose();
        _hotkey = null;
        _sellHotkey?.Dispose();
        _sellHotkey = null;
        _meltHotkey?.Dispose();
        _meltHotkey = null;
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