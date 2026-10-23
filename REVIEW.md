# DsoHelper — обзор проекта (handoff)

Дата: 24.10.2026. Статус: рабочий, собирается без ошибок и предупреждений (`dotnet build`).

## Что это

`DsoHelper` — настольный помощник (Windows) для MMO **Drakensang Online**: автоматизирует
свап «плаща с быстрым маунтом» в бою. Работает как **внешний** тул: находит окно игры,
симулирует клавиатуру/мышь через WinAPI (`SendInput`), рисует поверх игры калибровочные
окна. Ничего не инжектит, память процесса не читает.

Сценарий Fast Mount одной кнопкой: открыть инвентарь → (опционально) переключить вкладку
инвентаря → двойной клик по слоту с плащом пуш-маунта → нажать клавишу маунта → двойной
клик по тому же слоту (вернуть обычный плащ) → закрыть инвентарь. Пауза между шагами —
одна настройка `DelayMs`.

**Функция «AUTO-SELL» (скан + автопродажа мусора у торговца) была удалена 24.10.2026**
по решению владельца. Удалены: вкладка AUTO-SELL в `MainWindow.axaml`, вся логика
в code-behind `MainWindow.axaml.cs`, файлы `FastMount/JunkSeller.cs`,
`JunkClassifier.cs`, `JunkGrid.cs`, `JunkTemplate.cs`, `Infrastructure/ScreenCapture.cs`
и GDI-блок P/Invoke (захват экрана) в `NativeMethods.cs`. В `FastMountSettings` убраны
поля `SellJunkHotkey`, `Sell*`, `SellColor*`, `Junk*` и метод `ApplyDefaultSellColors()`.
Старые ключи в существующем `config.json` при десериализации просто игнорируются —
миграция не требуется.

## Стек и сборка

- **Avalonia 12.1.4** (XAML UI), **.NET 10** (`net10.0`), C#, `WinExe`,
  `Nullable=enable`, `AllowUnsafeBlocks=true`.
- NuGet-пакеты — только Avalonia (12.1.4: `Avalonia`, `Avalonia.Desktop`,
  `Avalonia.Themes.Fluent`) + `AvaloniaUI.DiagnosticsSupport` (dev-only).
  SkiaSharp шёл транзитивно и использовался только удалённым захватом экрана.
- Публикация с trimming: корни триммера в `trimmer-root.xml`
  (`FastMountSettings`, `HotkeyDefinition`, `KeyInfo`) — сериализация настроек
  через source-generated `System.Text.Json` (`FastMountSettingsJsonContext`),
  reflection не используется.
- Целевая ОС — только Windows (Win32 P/Invoke). UI — тёмная тема в стилистике DSO,
  палитра вся в `UI/Palette.axaml`.
- Иконка: `Assets/app.ico`, шрифты и текстуры в `Assets/Fonts`, `Assets/Textures`.
- Запуск: `dotnet run` или `dotnet build` + exe из `bin/`. В `dist/` — готовая сборка.

## Структура

```
Program.cs / App.axaml(.cs)   — точка входа, конфиг Avalonia
MainWindow.axaml(.cs)         — единственное окно приложения (TabControl: вкладка FAST MOUNT)
UI/SlotPickerWindow.axaml(.cs)— прозрачное «следящее за мышью» окно-квадрат для калибровки точек
UI/Palette.axaml              — цвета, ResourceDictionary
FastMount/                    — доменная логика макроса
Infrastructure/               — WinAPI-обвязка, хоткеи, ввод
```

### FastMount/ (домен)

- **FastMountSettings.cs** — модель конфига, `config.json` рядом с exe
  (`AppContext.BaseDirectory`). Поля: хоткей, клавиши инвентаря/маунта, `DelayMs`,
  позиция слота, вкладка и шаг вкладок, `SwitchInventoryTab`, `AlwaysOnTop`.
  Позиции хранятся в двух видах: абсолютные пиксели (fallback) и **ratio**
  (`SlotRightRatio`/`SlotTopRatio` — расстояние до правого/верхнего края клиента в
  долях высоты клиента) — так точки пересчитываются при смене разрешения окна игры.
  Методы миграции старых форматов конфига: `MigrateDelay`, `MigrateTabPoints`.
- **FastMountRunner.cs** — сам сценарий: валидация (`TryValidate`) и `RunAsync`
  (открыть инвентарь → клик по вкладке → двойной клик по слоту ×2, клавиша маунта
  между ними). Двойной клик = два `LeftClick` с зазором 30 мс.
- **SlotResolver.cs** — позиция ячейки слота плаща (обёртка над `ClientPointResolver`).
- **TabResolver.cs** — вкладки инвентаря `1..8` + `prem`; позиция 1-й вкладки калибруется
  вручную, шаг — по 2-й вкладке, остальные считаются умножением шага.
- **ClientPointResolver.cs** — общий пересчёт «ratio ↔ пиксели» внутри клиентских границ
  окна игры, с clamp и fallback на абсолютные координаты (когда игра закрыта).

### Infrastructure/ (WinAPI-обвязка)

- **NativeMethods.cs** — все P/Invoke: `SendInput`, низкоуровневый хук клавиатуры,
  поиск окон (`EnumWindows`, `GetWindowText`), клиентские прямоугольники, snapshot
  процессов (`CreateToolhelp32Snapshot`, окно игры ищется по имени процесса-владельца,
  не по заголовку).
- **InputSimulator.cs** — нажатия клавиш и клики через `SendInput`.
- **GameWindow.cs** — поиск окна игры, клиентские границы (`GameWindow.TryGetClientBounds`),
  активация, `IsForeground`.
- **GlobalHotkey.cs** — глобальный хоткей ( RegisterHotkey-подобный механизм через хук;
  фильтрует по активному окну игры — срабатывает только когда Drakensang в фокусе).
- **HotkeyParser.cs / KeyCatalog.cs** — парсинг/составление строк хоткеев
  («Ctrl+F8» и т.п.), каталог клавиш с человекочитаемыми именами.
- **WindowAppearance.cs** — тёмный заголовок окна.

### UI

- **MainWindow** — вкладка FAST MOUNT: карточки «Горячие клавиши»
  (хоткей запуска, инвентарь, маунт, выбор вкладки), «Слот плаща» (Выбрать слот,
  Указать вкладку 1/2), «Задержка» (ползунок 0–1000 мс, шаг 15, ручной ввод).
  Внизу — статусная плашка (`SetStatus(text, kind)` с цветной точкой).
  Одиночный-tab TabControl, `Classes="nav"`, стилизация в `MainWindow.axaml`.
- **SlotPickerWindow** — при калибровке главное окно прячется, поверх экрана показывается
  квадрат-маркер, следующий за курсором; клик = выбор точки, Esc = отмена. Общий поток —
  `MainWindow.ShowSlotPicker(existingPicker, storePicker, setPickButtons, precondition,
  markerSize, onPicked, cancelStatus, busyStatus)`.
- **Поток настроек UI ↔ конфиг**: `LoadSettingsIntoUi` / `ReadSettingsFromUi`,
  сохранение через дебаунс-таймер `ScheduleSave` (500 мс) и мгновенно в важных местах.

## Поведение, важное для понимания

- Хоткеи глобальные, но срабатывают только при активном окне игры
  (`GameWindow.IsForeground()` перепроверяется в UI-потоке).
- Retrieval слота/вкладок: если игра найдена — координаты понимаются как ratio от
  высоты клиента и полностью переносимы между размерами окна; если игра закрыта в момент
  калибровки — сохраняются абсолютные экранные координаты.
- Строки статуса и ошибки валидации — по-русски, статус-плашка едина для всех операций.
- Отмена выполнения: `CancellationTokenSource` на запуск, камера закрывается — отмена
  в `OnClosing`.

## Что можно сделать дальше (заметки)

- README устарел: описывает только калибровку слота/вкладок, стоит вычитать
  после удаления AUTO-SELL.
- Вкладка UI-tests: AutomationId проставлены на ключевых полях
  (`HotkeyBox`, `DelayValueBox` и др.) — база для UI-тестов.
- Версия в csproj: 0.1.0, `Description` = «Fast Mount helper for Drakensang Online».
