using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DsoHelper.FastMount;

internal sealed class FastMountSettings
{
    public string Hotkey { get; set; } = "F8";

    public string InventoryKey { get; set; } = "I";

    public string MountKey { get; set; } = "M";

    /// <summary>Inventory cell used for the cape swap. Both capes alternate in this cell.</summary>
    public int SlotX { get; set; }

    public int SlotY { get; set; }

    /// <summary>
    /// Distance from the right edge of the game client area, in client-height units.
    /// Keeps the slot position valid when the game window changes size.
    /// </summary>
    public double? SlotRightRatio { get; set; }

    /// <summary>Distance from the top edge of the game client area, in client-height units.</summary>
    public double? SlotTopRatio { get; set; }

    /// <summary>
    /// Click an inventory tab right after opening the inventory so the cape is
    /// always in the same cell (like the reference tool).
    /// </summary>
    public bool SwitchInventoryTab { get; set; }

    /// <summary>Selected inventory tab: "1".."8" or "prem".</summary>
    public string TabKey { get; set; } = "1";

    /// <summary>Position of the first inventory tab.</summary>
    public int TabX { get; set; }

    public int TabY { get; set; }

    public double? TabRightRatio { get; set; }

    public double? TabTopRatio { get; set; }

    /// <summary>Distance between neighbouring tabs, in screen pixels.</summary>
    public int TabStepX { get; set; }

    public int TabStepY { get; set; }

    /// <summary>The same step scaled to the client height, so it survives a resolution change.</summary>
    public double? TabStepRightRatio { get; set; }

    public double? TabStepTopRatio { get; set; }

    /// <summary>Пауза между действиями макроса, мс. Одна на весь сценарий.</summary>
    public int DelayMs { get; set; } = 30;

    public bool AlwaysOnTop { get; set; }

    // --- Чекер сетки инвентаря 7x4 ---

    /// <summary>Клик-центр левой верхней ячейки (слот #1).</summary>
    public int InvSlot1X { get; set; }

    public int InvSlot1Y { get; set; }

    public double? InvSlot1RightRatio { get; set; }

    public double? InvSlot1TopRatio { get; set; }

    /// <summary>Клик-центр правой нижней ячейки (слот #28).</summary>
    public int InvSlot28X { get; set; }

    public int InvSlot28Y { get; set; }

    public double? InvSlot28RightRatio { get; set; }

    public double? InvSlot28TopRatio { get; set; }

    // --- Автопродажа ---

    /// <summary>Глобальный хоткей автопродажи, например "F9".</summary>
    public string SellHotkey { get; set; } = "F9";

    /// <summary>Продаются только неопознанные («?») предметы — ниже их качества. Обычное качество не продаётся никогда.</summary>
    public bool SellCommon { get; set; }

    public bool SellImproved { get; set; } = true;

    public bool SellMagic { get; set; } = true;

    public bool SellExtraordinary { get; set; }

    public bool SellLegendary { get; set; }

    public bool SellUnique { get; set; }

    public bool SellMythic { get; set; }

    /// <summary>
    /// Вкладки инвентаря, по которым проходит автопродажа: ключи через запятую,
    /// например "1,2,3" (см. TabResolver.Keys).
    /// </summary>
    public string SellTabs { get; set; } = "1";

    /// <summary>Пауза после клика по заголовку вкладки, перед сканом сетки, мс.</summary>
    public int SellSwitchDelayMs { get; set; } = 200;

    /// <summary>Пауза после ПКМ на каждом продаваемом предмете, мс.</summary>
    public int SellItemDelayMs { get; set; } = 150;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static FastMountSettings Load()    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new FastMountSettings();
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize(json, FastMountSettingsJsonContext.Default.FastMountSettings) ??
                           new FastMountSettings();
            settings.MigrateTabPoints(json);
            settings.MigrateDelay(json);
            return settings;
        }
        catch
        {
            return new FastMountSettings();
        }
    }

    /// <summary>
    /// The previous version stored four separate delays. Use the largest one once
    /// and drop the rest.
    /// </summary>
    private void MigrateDelay(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("DelayMs", out _))
            {
                return;
            }

            var delay = 0;
            foreach (var name in new[]
                     {
                         "InventoryDelayMs", "CapeSwapDelayMs",
                         "MountDelayMs", "PostActionDelayMs"
                     })
            {
                delay = Math.Max(delay, ReadInt(document.RootElement, name));
            }

            if (delay > 0)
            {
                DelayMs = delay;
            }
        }
        catch (JsonException)
        {
            // Broken section: the default delay is fine.
        }
    }

    /// <summary>
    /// The previous version stored one point per tab in a "Tabs" dictionary.
    /// Take the first tab from there; older configs already use the flat fields.
    /// </summary>
    private void MigrateTabPoints(string json)
    {
        if (TabRightRatio is not null || TabX != 0 || TabY != 0)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Tabs", out var tabs) ||
                tabs.ValueKind != JsonValueKind.Object ||
                !tabs.TryGetProperty("1", out var first) ||
                first.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            TabX = ReadInt(first, "X");
            TabY = ReadInt(first, "Y");
            TabRightRatio = ReadDouble(first, "RightRatio");
            TabTopRatio = ReadDouble(first, "TopRatio");
            TabKey = "1";
            SwitchInventoryTab = true;
        }
        catch (JsonException)
        {
            // Broken section: the user can simply pick the tab again.
        }
    }

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public void Save()
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, FastMountSettingsJsonContext.Default.FastMountSettings));
    }
}

/// <summary>
/// Source-generated сериализатор: корректно работает с триммингом сборки
/// (метаданные генерируются на компиляции, reflection не используется).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FastMountSettings))]
internal sealed partial class FastMountSettingsJsonContext : JsonSerializerContext
{
}
