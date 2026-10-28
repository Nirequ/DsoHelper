using System;
using System.Collections.Generic;
using System.Linq;

namespace DsoHelper.FastMount;

/// <summary>Редкости DSO: empty, common, improved, magic, extraordinary, legendary, unique, mythic.</summary>
internal enum InventoryRarity
{
    Empty,
    Common,
    Improved,
    Magic,
    Extraordinary,
    Legendary,
    Unique,
    Mythic
}

/// <summary>Результат проверки одной ячейки инвентаря.</summary>
internal sealed record InventorySlotResult(
    int Index,
    int Row,
    int Col,
    int CenterX,
    int CenterY,
    bool IsEmpty,
    bool Unidentified,
    double Confidence,
    InventoryRarity Rarity);

/// <summary>
/// Чекер ячеек инвентаря 7×4 для Drakensang Online.
///
/// Портирован один в один из dso_inventory_checker.py (методы classify_slot_patch и
/// _detect_question_mark_rarity), без OpenCV и без сторонних NuGet — все операции
/// (HSV, Собель, среднее/дисперсия, медианы) реализованы вручную поверх BGRA-кадра:
/// 1. Не анализирует цвет иконки предмета в центре — редкость берётся строго из
///    градиента ФОНА ячейки у краёв / рамки / цвета знака «?».
/// 2. Отдельно детектирует неопознанные предметы со знаком «?» (обесцвеченная рамка)
///    и определяет их редкость по цвету самого «?».
/// </summary>
internal static class InventoryChecker
{
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Классификация одного слота (patch) во фрейме. patch задаётся в локальных
    /// координатах фрейма. Возвращает (is_empty, unidentified, confidence, rarity).
    /// </summary>
    public static (bool IsEmpty, bool Unidentified, double Confidence, InventoryRarity Rarity)
        ClassifySlotPatch(byte[] frame, int frameWidth, int px, int py, int pw, int ph)
    {
        var w = pw;
        var h = ph;

        if (frameWidth < w || h < 16 || w < 16)
        {
            return (true, false, 0.0, InventoryRarity.Empty);
        }

        // 2. Проверяем центральную область на наличие иконки предмета (пусто / занято).
        var padX = Math.Max(3, (int)(w * 0.18));
        var padY = Math.Max(3, (int)(h * 0.18));
        var innerW = w - padX * 2;
        var innerH = h - padY * 2;
        var innerCount = innerW * innerH;

        var valSum = 0.0;
        var gray = new byte[innerCount];
        var gi = 0;
        for (var y = padY; y < h - padY; y++)
        {
            var rowBase = (py + y) * frameWidth + px;
            for (var x = padX; x < w - padX; x++)
            {
                var o = (rowBase + x) * BytesPerPixel;
                var pb = frame[o];
                var pg = frame[o + 1];
                var pr = frame[o + 2];

                valSum += Math.Max(pr, Math.Max(pg, pb));

                // COLOR_BGR2GRAY: 0.299R + 0.587G + 0.114B с округлением.
                gray[gi++] = (byte)((299 * pr + 587 * pg + 114 * pb + 500) / 1000);
            }
        }

        var valMean = valSum / innerCount;
        var valStd = StdDev(gray, gi);
        var edgeEnergy = SobelEdgeEnergy(gray, innerWidth: innerW, innerHeight: innerH);

        // Пустой слот в DSO — тёмный коричневатый фон без иконки. Насыщенность (HSV)
        // для этого не годится: у коричневого фона она высокая (100+), поэтому раньше
        // пустые ячейки уходили в «занято». Признак пустоты — ровная заливка без
        // разброса яркости и без краёв: у пустых ячеек std ≈ 2.5 и edge ≈ 3.5,
        // у самой «плоской» занятой — std ≈ 32 и edge ≈ 60.
        var isEmpty = valStd < 15.0 && edgeEnergy < 30.0 && valMean < 100.0;
        if (isEmpty)
        {
            var conf = Math.Clamp(1.0 - valStd / 30.0, 0.60, 0.99);
            return (true, false, Math.Round(conf, 3), InventoryRarity.Empty);
        }

        // 3. Анализируем узорчатую рамку слота (отступ 3%..8% от стороны).
        var minSide = Math.Min(w, h);
        var fIn = Math.Max(2, (int)(minSide * 0.03));
        var fOut = Math.Max(5, (int)(minSide * 0.08));

        var fbValues = new List<byte>();
        var fgValues = new List<byte>();
        var frValues = new List<byte>();
        for (var y = fIn; y < h - fIn; y++)
        {
            var rowBase = (py + y) * frameWidth + px;
            for (var x = fIn; x < w - fIn; x++)
            {
                if (x >= fOut && x < w - fOut && y >= fOut && y < h - fOut)
                {
                    continue; // внутри кольца
                }

                var o = (rowBase + x) * BytesPerPixel;
                fbValues.Add(frame[o]);
                fgValues.Add(frame[o + 1]);
                frValues.Add(frame[o + 2]);
            }
        }

        var (fb, fg, fr) = MedianBgr(fbValues, fgValues, frValues);

        // 4. Случай А: НЕОПОЗНАННЫЙ ПРЕДМЕТ (знак вопроса «?»).
        // В DSO у опознанных слотов рамка бронзово-коричневая (fr - fb >= 15),
        // а у неопознанных со «?» — полностью обесцвечена (|fr - fb| < 10 и fr < 58).
        if (Math.Abs(fr - fb) < 10.0 && fr < 58.0)
        {
            var rarity = DetectQuestionMarkRarity(frame, frameWidth, px, py, w, h);
            return (false, true, 0.95, rarity);
        }

        // 5. Случай Б: УНИКАЛЬНЫЙ ПРЕДМЕТ — яркая светящаяся золотая рамка.
        if (fg > 115.0 && fr > 130.0 && fr - fb > 40.0)
        {
            return (false, false, 0.96, InventoryRarity.Unique);
        }

        // 6. Случай В: опознанный предмет — смотрим ТОЛЬКО на градиент ФОНА у краёв:
        // полоски вдоль левого/правого края в нижней половине и слева снизу,
        // НЕ задевая иконку в центре и цифры стака в правом нижнем углу.
        var y1 = (int)(h * 0.52);
        var y2 = (int)(h * 0.84);
        var xPad1 = Math.Max(6, (int)(w * 0.10));
        var xPad2 = Math.Max(10, (int)(w * 0.16));

        var bgB = new List<byte>();
        var bgG = new List<byte>();
        var bgR = new List<byte>();
        CollectRegion(frame, frameWidth, px, py, w, h, y1, y2, xPad1, xPad2, bgB, bgG, bgR);          // left_lower
        CollectRegion(frame, frameWidth, px, py, w, h, y1, (int)(h * 0.75), w - xPad2, w - xPad1, bgB, bgG, bgR); // right_lower
        CollectRegion(frame, frameWidth, px, py, w, h, h - xPad2, h - xPad1, xPad2, (int)(w * 0.42), bgB, bgG, bgR); // bot_left
        var (b, g, r) = MedianBgr(bgB, bgG, bgR);

        // Верхняя кромка фона (для отделения вертикального градиента редкости
        // шмотки от круглых светящихся сфер/эссенций).
        var topB = new List<byte>();
        var topG = new List<byte>();
        var topR = new List<byte>();
        CollectRegion(frame, frameWidth, px, py, w, h, xPad1, xPad2, (int)(w * 0.25), (int)(w * 0.75), topB, topG, topR);
        var (_, tg, _) = MedianBgr(topB, topG, topR);

        // Классификация по цвету фона ячейки (обычный серый слот: B≈45..55, G≈40..48, R≈33..42).
        // Синий фон (Magic): B заметно выше G и R.
        if (b - g >= 18.0 && b - r >= 24.0 && b >= 65.0)
        {
            return (false, false, 0.94, InventoryRarity.Magic);
        }

        // Зелёный фон (Improved): внизу G выше R и B, сверху фон тёмный (tg < 60).
        if (g - r >= 12.0 && g - b >= 6.0 && g >= 70.0 && tg < 60.0)
        {
            return (false, false, 0.92, InventoryRarity.Improved);
        }

        // Фиолетовый/розовый фон (Extraordinary): B и R высокие, G провален.
        if (b > 75.0 && r > 75.0 && b - g >= 15.0 && r - g >= 12.0)
        {
            return (false, false, 0.92, InventoryRarity.Extraordinary);
        }

        // Оранжево-золотистый фон (Legendary).
        if (r - b >= 35.0 && r - g >= 12.0 && r >= 80.0)
        {
            return (false, false, 0.92, InventoryRarity.Legendary);
        }

        // Красный фон (Mythic).
        if (r - g >= 35.0 && r - b >= 35.0 && r >= 75.0)
        {
            return (false, false, 0.92, InventoryRarity.Mythic);
        }

        return (false, false, 0.90, InventoryRarity.Common);
    }

    /// <summary>
    /// У неопознанного предмета иконка и фон чёрно-белые, а знак «?» в центре
    /// окрашен в цвет редкости предмета. Пороги Hue — по 8-битной конвенции OpenCV (0..179).
    /// </summary>
    private static InventoryRarity DetectQuestionMarkRarity(byte[] frame, int frameWidth, int px, int py, int w, int h)
    {
        var rowStart = (int)(h * 0.18);
        var rowEnd = (int)(h * 0.84);
        var colStart = (int)(w * 0.24);
        var colEnd = (int)(w * 0.76);

        var hues = new List<byte>();
        for (var y = rowStart; y < rowEnd && y < h; y++)
        {
            var rowBase = (py + y) * frameWidth + px;
            for (var x = colStart; x < colEnd && x < w; x++)
            {
                var o = (rowBase + x) * BytesPerPixel;
                var b = frame[o];
                var g = frame[o + 1];
                var r = frame[o + 2];

                var v = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var delta = v - min;
                var s = v == 0 ? 0 : Math.Round(255.0 * delta / v);
                if (!(s > 85) || !(v > 60))
                {
                    continue;
                }

                var hue = HueOpenCv(r, g, b, v, delta);
                hues.Add(hue);
            }
        }

        if (hues.Count < 20)
        {
            return InventoryRarity.Common;
        }

        var medH = Median(hues);

        if (medH >= 90.0 && medH <= 128.0)
        {
            return InventoryRarity.Magic;
        }

        if (medH >= 35.0 && medH < 90.0)
        {
            return InventoryRarity.Improved;
        }

        // Пурпурно-розовый (Extraordinary) тянется дальше 168: у «?» особенного
        // предмета Hue ≈ 169..171 (RGB ≈ 150,40,80), и раньше этот «розовый хвост»
        // проваливался в мифический «по умолчанию». Отрезаем его только там, где
        // начинается чистый красный (Mythic) — Hue у 0/179.
        if (medH >= 129.0 && medH <= 175.0)
        {
            return InventoryRarity.Extraordinary;
        }

        if (medH >= 8.0 && medH <= 34.0)
        {
            return InventoryRarity.Legendary;
        }

        // Красный «?» (Mythic): Hue прижат к нулю или к 179.
        return InventoryRarity.Mythic;
    }

    /// <summary>
    /// Проверяет все 28 слотов сетки на захваченном фрейме клиента.
    /// slots — экранные прямоугольники из InventoryGridResolver; captureLeft/Tap —
    /// экранные координаты левого верхнего угла фрейма.
    /// </summary>
    public static List<InventorySlotResult> CheckByGrid(
        byte[] frame,
        int frameWidth,
        int frameHeight,
        IReadOnlyList<InventorySlotRect> slots,
        int captureLeft,
        int captureTop)
    {
        var results = new List<InventorySlotResult>(slots.Count);

        foreach (var slot in slots)
        {
            var x = Math.Clamp(slot.X - captureLeft, 0, frameWidth - 1);
            var y = Math.Clamp(slot.Y - captureTop, 0, frameHeight - 1);
            var w = Math.Min(slot.W, frameWidth - x);
            var h = Math.Min(slot.H, frameHeight - y);

            var (isEmpty, unid, conf, rarity) = w > 0 && h > 0
                ? ClassifySlotPatch(frame, frameWidth, x, y, w, h)
                : (true, false, 0.0, InventoryRarity.Empty);

            results.Add(new InventorySlotResult(
                slot.Index,
                slot.Row,
                slot.Col,
                slot.CenterX,
                slot.CenterY,
                isEmpty,
                unid,
                conf,
                rarity));
        }

        return results;
    }

    /// <summary>Сводка по 28 слотам — как summarize() из dso_inventory_checker.py.</summary>
    public static string Summarize(IReadOnlyList<InventorySlotResult> slots)
    {
        var empty = 0;
        var occupied = 0;
        var unidentified = 0;

        foreach (var slot in slots)
        {
            if (slot.IsEmpty)
            {
                empty++;
                continue;
            }

            occupied++;
            if (slot.Unidentified)
            {
                unidentified++;
            }
        }

        var full = slots.Count > 0 && empty == 0;

        // Редкости по слотам показывает сама сетка на вкладке «Инвентарь»,
        // поэтому в строку результата проверки они больше не попадают.
        return "Слотов: " + slots.Count +
               " · свободно: " + empty +
               " · занято: " + occupied +
               " (из них «?»: " + unidentified + ")" +
               " · инвентарь " + (full ? "ПОЛОН" : "есть место");
    }

    public static string RarityLabel(InventoryRarity rarity) => rarity switch
    {
        InventoryRarity.Empty => "пусто",
        InventoryRarity.Common => "обычный",
        InventoryRarity.Improved => "улучшенный",
        InventoryRarity.Magic => "магический",
        InventoryRarity.Extraordinary => "особенный",
        InventoryRarity.Legendary => "легендарный",
        InventoryRarity.Unique => "уникальный",
        InventoryRarity.Mythic => "мифический",
        _ => "неизвестно"
    };

    // ------------------------------------------------------------------
    // Вспомогательные операции над пикселями (замена cv2).
    // ------------------------------------------------------------------

    /// <summary>Собирает BGR-каналы области patch в локальных координатах (с отсечением по границам).</summary>
    private static void CollectRegion(
        byte[] frame,
        int frameWidth,
        int px,
        int py,
        int pw,
        int ph,
        int rowStart,
        int rowEnd,
        int colStart,
        int colEnd,
        List<byte> bs,
        List<byte> gs,
        List<byte> rs)
    {
        rowStart = Math.Max(0, rowStart);
        colStart = Math.Max(0, colStart);
        rowEnd = Math.Min(rowEnd, ph);
        colEnd = Math.Min(colEnd, pw);
        if (rowStart >= rowEnd || colStart >= colEnd)
        {
            return;
        }

        for (var y = rowStart; y < rowEnd; y++)
        {
            var rowBase = (py + y) * frameWidth + px;
            for (var x = colStart; x < colEnd; x++)
            {
                var o = (rowBase + x) * BytesPerPixel;
                bs.Add(frame[o]);
                gs.Add(frame[o + 1]);
                rs.Add(frame[o + 2]);
            }
        }
    }

    private static (double B, double G, double R) MedianBgr(List<byte> bs, List<byte> gs, List<byte> rs)
    {
        if (bs.Count == 0)
        {
            return (0.0, 0.0, 0.0);
        }

        return (Median(bs), Median(gs), Median(rs));
    }

    /// <summary>Медиана: одинаково с np.median (для чётной длины — среднее двух центральных).</summary>
    private static double Median(List<byte> values)
    {
        var arr = values.ToArray();
        Array.Sort(arr);
        var n = arr.Length;
        return (n & 1) == 1 ? arr[n / 2] : (arr[n / 2 - 1] + arr[n / 2]) / 2.0;
    }

    /// <summary>Среднеквадратичное отклонение (как np.std: ddof=0) первых count байт.</summary>
    private static double StdDev(byte[] values, int count)
    {
        if (count == 0)
        {
            return 0.0;
        }

        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += values[i];
        }

        var mean = sum / count;
        double acc = 0;
        for (var i = 0; i < count; i++)
        {
            var d = values[i] - mean;
            acc += d * d;
        }

        return Math.Sqrt(acc / count);
    }

    /// <summary>
    /// Собель 3×3 как в OpenCV (CV_32F, бордюр BORDER_REFLECT_101);
    /// возвращает mean(sqrt(gx² + gy²)) — edge_energy из classify_slot_patch.
    /// </summary>
    private static double SobelEdgeEnergy(byte[] gray, int innerWidth, int innerHeight)
    {
        double G(int x, int y)
        {
            // BORDER_REFLECT_101: индекс -1 -> 1, индекс N -> N-2.
            var cx = x < 0 ? -x : x >= innerWidth ? 2 * innerWidth - 2 - x : x;
            var cy = y < 0 ? -y : y >= innerHeight ? 2 * innerHeight - 2 - y : y;
            return gray[cy * innerWidth + cx];
        }

        double Sum = 0;
        for (var y = 0; y < innerHeight; y++)
        {
            for (var x = 0; x < innerWidth; x++)
            {
                var gx = (G(x + 1, y - 1) + 2 * G(x + 1, y) + G(x + 1, y + 1))
                       - (G(x - 1, y - 1) + 2 * G(x - 1, y) + G(x - 1, y + 1));
                var gy = (G(x - 1, y + 1) + 2 * G(x, y + 1) + G(x + 1, y + 1))
                       - (G(x - 1, y - 1) + 2 * G(x, y - 1) + G(x + 1, y - 1));
                Sum += Math.Sqrt(gx * gx + gy * gy);
            }
        }

        return Sum / (innerWidth * innerHeight);
    }

    /// <summary>Hue по конвенции OpenCV: 0..179 (полный круг 360° кодируется половиной).</summary>
    private static byte HueOpenCv(int r, int g, int b, int max, int delta)
    {
        double hue;
        if (delta == 0)
        {
            hue = 0;
        }
        else if (max == r)
        {
            hue = 60.0 * (((double)(g - b)) / delta);
        }
        else if (max == g)
        {
            hue = 60.0 * (2.0 + ((double)(b - r)) / delta);
        }
        else
        {
            hue = 60.0 * (4.0 + ((double)(r - g)) / delta);
        }

        if (hue < 0)
        {
            hue += 360.0;
        }

        return (byte)Math.Round(hue / 2.0);
    }
}
