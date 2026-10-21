using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DsoHelper.Infrastructure;

internal readonly record struct ScreenCaptureResult(byte[] Pixels, int Width, int Height);

/// <summary>
/// Grabs the given screen rectangle through GDI. The result is a top-down
/// BGRA buffer, ready to be wrapped into an Avalonia bitmap.
/// </summary>
internal static class ScreenCapture
{
    public static ScreenCaptureResult Capture(ClientBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Область снимка пуста.");
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось получить контекст экрана.");
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);

            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось подготовить снимок экрана.");
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            if (previous == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось выбрать изображение для снимка.");
            }

            if (!NativeMethods.BitBlt(
                    memoryDc,
                    0,
                    0,
                    bounds.Width,
                    bounds.Height,
                    screenDc,
                    bounds.Left,
                    bounds.Top,
                    NativeMethods.Srccopy))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось скопировать изображение экрана.");
            }

            // GetDIBits requires the bitmap not to be selected into a DC.
            NativeMethods.SelectObject(memoryDc, previous);
            previous = IntPtr.Zero;

            var info = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                    Width = bounds.Width,
                    Height = -bounds.Height, // top-down rows
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = 0,
                    XPelsPerMeter = 0,
                    YPelsPerMeter = 0,
                    ClrUsed = 0,
                    ClrImportant = 0
                },
                Colors = 0
            };

            var pixels = new byte[bounds.Width * bounds.Height * 4];
            var lines = NativeMethods.GetDIBits(
                memoryDc,
                bitmap,
                0,
                (uint)bounds.Height,
                pixels,
                ref info,
                0);

            if (lines != bounds.Height)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось прочитать пиксели снимка.");
            }

            return new ScreenCaptureResult(pixels, bounds.Width, bounds.Height);
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
