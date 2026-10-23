using System;
using System.Collections.Generic;
using System.Text;

namespace DsoHelper.Infrastructure;

internal readonly record struct ClientBounds(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

internal static class GameWindow
{
    /// <summary>Признак имени исполняемого файла клиента.</summary>
    private const string ProcessImageMarker = "drakensang";

    /// <summary>
    /// PID всех процессов, чей exe похож на клиент Drakensang.
    /// Быстрее снепшота каждого вызова: миллисекунда, безопасно для хука.
    /// </summary>
    public static bool TryCollectGameProcessIds(HashSet<uint> processIds)
    {
        processIds.Clear();

        var snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapProcess,
            0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            var entry = new NativeMethods.ProcessEntry32
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.ProcessEntry32>()
            };

            if (NativeMethods.Process32First(snapshot, ref entry))
            {
                do
                {
                    if (entry.ImageFile?.Contains(
                            ProcessImageMarker,
                            StringComparison.OrdinalIgnoreCase) == true)
                    {
                        processIds.Add(entry.ProcessId);
                    }
                }
                while (NativeMethods.Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return processIds.Count > 0;
    }

    public static bool TryFind(out IntPtr window)
    {
        // Первичный путь: окно процесса клиента (имя exe содержит маркер).
        HashSet<uint> processIds = [];
        if (TryCollectGameProcessIds(processIds) &&
            FindWindowOfProcesses(processIds, out window))
        {
            return true;
        }

        // Резерв: нестандартное имя exe — старый поиск по заголовку.
        return TryFindByTitle(out window);
    }

    private static bool FindWindowOfProcesses(HashSet<uint> processIds, out IntPtr window)
    {
        IntPtr any = IntPtr.Zero;
        IntPtr withTitle = IntPtr.Zero;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
            if (processId == 0 || !processIds.Contains(processId))
            {
                return true;
            }

            if (any == IntPtr.Zero)
            {
                any = hWnd;
            }

            var title = new StringBuilder(260);
            if (NativeMethods.GetWindowText(hWnd, title, title.Capacity) > 0)
            {
                if (withTitle == IntPtr.Zero)
                {
                    withTitle = hWnd;
                }

                // Главное окно игры всегда с заголовком — останавливаемся.
                return false;
            }

            return true;
        }, IntPtr.Zero);

        window = withTitle != IntPtr.Zero ? withTitle : any;
        return window != IntPtr.Zero;
    }

    private static bool TryFindByTitle(out IntPtr window)
    {
        var found = IntPtr.Zero;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                return true;
            }

            var title = new StringBuilder(512);
            if (NativeMethods.GetWindowText(hWnd, title, title.Capacity) <= 0)
            {
                return true;
            }

            if (!title.ToString().Contains("Drakensang", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            found = hWnd;
            return false;
        }, IntPtr.Zero);

        window = found;
        return found != IntPtr.Zero;
    }

    public static bool TryGetClientBounds(out ClientBounds bounds)
    {
        bounds = default;

        if (!TryFind(out var window) || NativeMethods.IsIconic(window))
        {
            return false;
        }

        if (!NativeMethods.GetClientRect(window, out var client))
        {
            return false;
        }

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var origin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(window, ref origin))
        {
            return false;
        }

        bounds = new ClientBounds(origin.X, origin.Y, width, height);
        return true;
    }

    public static bool TryActivate()
    {
        if (!TryFind(out var window))
        {
            return false;
        }

        if (NativeMethods.IsIconic(window))
        {
            NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        }

        return NativeMethods.SetForegroundWindow(window);
    }

    /// <summary>
    /// True, когда активное окно принадлежит процессу клиента Drakensang.
    /// Проверка по PID процесса, а не по заголовку: таб браузера с «Drakensang»
    /// в заголовке больше не считается игрой.
    /// </summary>
    public static bool IsForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        HashSet<uint> processIds = [];
        if (TryCollectGameProcessIds(processIds))
        {
            NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundProcess);
            return processIds.Contains(foregroundProcess);
        }

        // Резерв (exe не распознан): окно заголовка должно совпасть с активным,
        // но ничего кроме игры приложению-фокуснику не прощаем.
        return TryFindByTitle(out var game) && game == foreground;
    }
}
