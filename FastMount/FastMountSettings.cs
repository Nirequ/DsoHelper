using System;
using System.IO;
using System.Text.Json;

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

    public int InventoryDelayMs { get; set; } = 30;

    /// <summary>Пауза после смены плаща и до нажатия клавиши маунта, мс.</summary>
    public int CapeSwapDelayMs { get; set; } = 30;

    /// <summary>Пауза после вызова маунта и до возврата старого плаща, мс.</summary>
    public int MountDelayMs { get; set; } = 30;

    /// <summary>Пауза после возврата старого плаща и до закрытия инвентаря, мс.</summary>
    public int PostActionDelayMs { get; set; } = 30;

    public bool AlwaysOnTop { get; set; }

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static FastMountSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new FastMountSettings();
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<FastMountSettings>(json) ?? new FastMountSettings();
            settings.MigrateTabPoints(json);
            return settings;
        }
        catch
        {
            return new FastMountSettings();
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
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, options));
    }
}
