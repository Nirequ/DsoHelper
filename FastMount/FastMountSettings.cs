using System;
using System.Collections.Generic;
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

    /// <summary>Saved positions for the inventory tabs.</summary>
    public Dictionary<string, TabPoint> Tabs { get; set; } = new();

    public int InventoryDelayMs { get; set; } = 30;

    /// <summary>Пауза после смены плаща и до нажатия клавиши маунта, мс.</summary>
    public int CapeSwapDelayMs { get; set; } = 30;

    /// <summary>Пауза после вызова маунта и до возврата старого плаща, мс.</summary>
    public int MountDelayMs { get; set; } = 30;

    /// <summary>Пауза после возврата старого плаща и до закрытия инвентаря, мс.</summary>
    public int PostActionDelayMs { get; set; } = 30;

    /// <summary>
    /// Закрывать инвентарь перед нажатием клавиши маунта. Нужно, если игра
    /// не учитывает бонус плаща, пока инвентарь открыт.
    /// </summary>
    public bool CloseInventoryBeforeMount { get; set; }

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
            settings.Tabs ??= new();
            settings.MigrateLegacyTab(json);
            return settings;
        }
        catch
        {
            return new FastMountSettings();
        }
    }

    /// <summary>
    /// Older versions stored one tab position in TabX/TabY/TabRightRatio/TabTopRatio.
    /// Move it into the per-tab dictionary once.
    /// </summary>
    private void MigrateLegacyTab(string json)
    {
        if (Tabs.Count > 0)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var x = ReadInt(root, "TabX");
            var y = ReadInt(root, "TabY");
            var right = ReadDouble(root, "TabRightRatio");
            var top = ReadDouble(root, "TabTopRatio");

            if (x == 0 && y == 0 && right is null && top is null)
            {
                return;
            }

            Tabs["1"] = new TabPoint { X = x, Y = y, RightRatio = right, TopRatio = top };
            TabKey = "1";
            SwitchInventoryTab = true;
        }
        catch (JsonException)
        {
            // Broken legacy section: the user can simply pick the tab again.
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
