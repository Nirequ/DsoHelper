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

    public int InventoryDelayMs { get; set; } = 300;

    public int ClickDelayMs { get; set; } = 250;

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
            return JsonSerializer.Deserialize<FastMountSettings>(json) ?? new FastMountSettings();
        }
        catch
        {
            return new FastMountSettings();
        }
    }

    public void Save()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, options));
    }
}
