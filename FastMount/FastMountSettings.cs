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

    // ---- Auto-sell -------------------------------------------------------

    /// <summary>Hotkey that sells the configured bag area while the game is focused.</summary>
    public string SellHotkey { get; set; } = "F9";

    /// <summary>First cell of the selling grid (its centre).</summary>
    public int SellX { get; set; }

    public int SellY { get; set; }

    public double? SellRightRatio { get; set; }

    public double? SellTopRatio { get; set; }

    /// <summary>Distance between neighbouring cells, in screen pixels.</summary>
    public int SellStepX { get; set; }

    public int SellStepY { get; set; }

    public double? SellStepRightRatio { get; set; }

    public double? SellStepTopRatio { get; set; }

    public int SellColumns { get; set; } = 8;

    public int SellRows { get; set; } = 6;

    /// <summary>
    /// Row-major cell indices that must not be sold (unchecked in the preview).
    /// Missing entries mean "sell everything in the grid".
    /// </summary>
    public List<int> SellSkip { get; set; } = new();

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
            settings.MigrateDelay(json);

            // Hand-edited or older configs may miss the auto-sell section.
            settings.SellSkip ??= new List<int>();
            settings.SellColumns = Math.Clamp(settings.SellColumns, 1, SellGridResolver.MaxColumns);
            settings.SellRows = Math.Clamp(settings.SellRows, 1, SellGridResolver.MaxRows);

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
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, options));
    }
}
