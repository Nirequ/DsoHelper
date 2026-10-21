using System;
using System.Collections.Generic;
using DsoHelper.FastMount;

namespace DsoHelper.AutoSell;

/// <summary>DSO item quality, in the order used by the game UI.</summary>
internal enum ItemRarity
{
    Unknown = 0,
    Basic,
    Improved,
    Extraordinary,
    Legendary,
    Unique
}

internal static class RarityCatalog
{
    // DSO canonical rarity colours (see UI/Palette.axaml).
    public static readonly (ItemRarity Rarity, int R, int G, int B)[] Colors =
    [
        (ItemRarity.Basic, 0xB0, 0xB3, 0xB8),
        (ItemRarity.Improved, 0x3B, 0x82, 0xF6),
        (ItemRarity.Extraordinary, 0xA8, 0x55, 0xF7),
        (ItemRarity.Legendary, 0xF9, 0x73, 0x16),
        (ItemRarity.Unique, 0xEA, 0xB3, 0x08)
    ];

    public static string DisplayName(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Basic => "обычный",
        ItemRarity.Improved => "улучшенный",
        ItemRarity.Extraordinary => "необычный",
        ItemRarity.Legendary => "легендарный",
        ItemRarity.Unique => "уникальный",
        _ => "не распознан"
    };

    public static bool AnySelected(FastMountSettings settings) =>
        settings.SellBasic ||
        settings.SellImproved ||
        settings.SellExtraordinary ||
        settings.SellLegendary ||
        settings.SellUnique;

    public static bool IsSellable(FastMountSettings settings, ItemRarity rarity)
    {
        if (!settings.SellUseRarity)
        {
            return true;
        }

        return rarity switch
        {
            ItemRarity.Basic => settings.SellBasic,
            ItemRarity.Improved => settings.SellImproved,
            ItemRarity.Extraordinary => settings.SellExtraordinary,
            ItemRarity.Legendary => settings.SellLegendary,
            ItemRarity.Unique => settings.SellUnique,
            _ => false
        };
    }

    public static IEnumerable<ItemRarity> Allowed(FastMountSettings settings)
    {
        if (settings.SellBasic) yield return ItemRarity.Basic;
        if (settings.SellImproved) yield return ItemRarity.Improved;
        if (settings.SellExtraordinary) yield return ItemRarity.Extraordinary;
        if (settings.SellLegendary) yield return ItemRarity.Legendary;
        if (settings.SellUnique) yield return ItemRarity.Unique;
    }
}

/// <summary>
/// Reads the rarity from the coloured frame around an inventory cell.
/// Only a ring near the cell border is sampled, so the item artwork (and the
/// bag background) cannot easily dominate the result. A cell that cannot be
/// classified is reported as <see cref="ItemRarity.Unknown"/> and is not sold
/// while the rarity filter is enabled.
/// </summary>
internal static class RarityDetector
{
    private const int MaxDistanceSquared = 38 * 38;
    private const int MinimumPixels = 15;

    public static ItemRarity Detect(
        byte[] pixels,
        int width,
        int height,
        int left,
        int top,
        int rectWidth,
        int rectHeight)
    {
        if (rectWidth <= 0 || rectHeight <= 0 || width <= 0 || height <= 0)
        {
            return ItemRarity.Unknown;
        }

        var x0 = Math.Max(0, left);
        var y0 = Math.Max(0, top);
        var x1 = Math.Min(width, left + rectWidth);
        var y1 = Math.Min(height, top + rectHeight);

        var cellWidth = x1 - x0;
        var cellHeight = y1 - y0;
        if (cellWidth < 8 || cellHeight < 8)
        {
            return ItemRarity.Unknown;
        }

        var inset = Math.Max(1, Math.Min(cellWidth, cellHeight) / 20);
        var band = Math.Max(4, Math.Min(cellWidth, cellHeight) / 4);
        var counts = new int[6];

        Scan(x0 + inset, y0 + inset, cellWidth - inset * 2, band);
        Scan(x0 + inset, y1 - inset - band, cellWidth - inset * 2, band);
        Scan(x0 + inset, y0 + inset, band, cellHeight - inset * 2);
        Scan(x1 - inset - band, y0 + inset, band, cellHeight - inset * 2);

        var best = ItemRarity.Unknown;
        var bestCount = 0;
        var secondCount = 0;

        for (var i = 1; i < counts.Length; i++)
        {
            if (counts[i] > bestCount)
            {
                secondCount = bestCount;
                bestCount = counts[i];
                best = (ItemRarity)i;
            }
            else if (counts[i] > secondCount)
            {
                secondCount = counts[i];
            }
        }

        if (bestCount < MinimumPixels || bestCount < secondCount * 1.4)
        {
            return ItemRarity.Unknown;
        }

        return best;

        void Scan(int startX, int startY, int scanWidth, int scanHeight)
        {
            var scanRight = Math.Min(x1, startX + scanWidth);
            var scanBottom = Math.Min(y1, startY + scanHeight);

            for (var y = Math.Max(y0, startY); y < scanBottom; y++)
            {
                var row = y * width;
                for (var x = Math.Max(x0, startX); x < scanRight; x++)
                {
                    var offset = (row + x) * 4;
                    if (offset + 2 >= pixels.Length)
                    {
                        continue;
                    }

                    // GetDIBits returns BGRA.
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];

                    var bestIndex = -1;
                    var bestDistance = int.MaxValue;

                    for (var c = 0; c < RarityCatalog.Colors.Length; c++)
                    {
                        var color = RarityCatalog.Colors[c];
                        var dr = red - color.R;
                        var dg = green - color.G;
                        var db = blue - color.B;
                        var distance = dr * dr + dg * dg + db * db;

                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestIndex = c;
                        }
                    }

                    if (bestIndex >= 0 && bestDistance <= MaxDistanceSquared)
                    {
                        counts[(int)RarityCatalog.Colors[bestIndex].Rarity]++;
                    }
                }
            }
        }
    }
}
