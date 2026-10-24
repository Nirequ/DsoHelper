using System;
using System.Runtime.InteropServices;

namespace DsoHelper.Infrastructure;

/// <summary>
/// Захват фрагмента экрана через GDI (BitBlt + GetDIBits) без сторонних библиотек.
/// Результат — плотный BGRA-буфер: строки сверху вниз, stride = ширина × 4 байта
/// (порядок каналов B, G, R, A). Координаты физические (PerMonitorV2, app.manifest).
/// </summary>
internal static class ScreenCapture
{
    public const int BytesPerPixel = 4;

    public static bool TryCapture(int x, int y, int width, int height, out byte[]? bgra, out string error)
    {
        bgra = null;
        error = string.Empty;

        if (width <= 0 || height <= 0)
        {
            error = "Некорректный размер области захвата.";
            return false;
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            error = "Не удалось получить контекст экрана.";
            return false;
        }

        try
        {
            var memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                error = "Не удалось создать совместимый контекст устройства.";
                return false;
            }

            try
            {
                var bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
                if (bitmap == IntPtr.Zero)
                {
                    error = "Не удалось создать bitmap для захвата.";
                    return false;
                }

                var previous = IntPtr.Zero;
                try
                {
                    previous = NativeMethods.SelectObject(memDc, bitmap);
                    if (previous == IntPtr.Zero)
                    {
                        error = "Не удалось подключить bitmap к контексту.";
                        return false;
                    }

                    if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, x, y, NativeMethods.SrCopy))
                    {
                        error = "BitBlt не смог скопировать область экрана.";
                        return false;
                    }

                    // GetDIBits требует, чтобы bitmap не был выбран в DC: возвращаем предыдущий
                    // объект и отдаём байты прямо в managed-массив (без копирования из IntPtr).
                    NativeMethods.SelectObject(memDc, previous);

                    var bmi = new NativeMethods.BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                        Width = width,
                        Height = -height, // отрицательная высота: строки сверху вниз
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0
                    };

                    var pixels = new byte[checked(width * height * BytesPerPixel)];
                    if (NativeMethods.GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref bmi, NativeMethods.DibRgbColors) == 0)
                    {
                        error = "GetDIBits не смог вынуть пиксели из bitmap.";
                        return false;
                    }

                    bgra = pixels;
                    return true;
                }
                finally
                {
                    if (previous != IntPtr.Zero)
                    {
                        NativeMethods.SelectObject(memDc, previous);
                    }
                    NativeMethods.DeleteObject(bitmap);
                }
            }
            finally
            {
                NativeMethods.DeleteDC(memDc);
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
