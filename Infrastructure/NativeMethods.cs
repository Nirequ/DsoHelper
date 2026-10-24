using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DsoHelper.Infrastructure;

internal static class NativeMethods
{
    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;

    internal const uint KeyEventExtendedKey = 0x0001;
    internal const uint KeyEventKeyUp = 0x0002;
    internal const uint MouseEventLeftDown = 0x0002;
    internal const uint MouseEventLeftUp = 0x0004;
    internal const uint MouseEventRightDown = 0x0008;
    internal const uint MouseEventRightUp = 0x0010;

    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;

    internal const int SwRestore = 9;

    internal const int WhKeyboardLl = 13;

    internal const int WmKeyDown = 0x0100;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmSysKeyUp = 0x0105;

    internal const int VkShift = 0x10;
    internal const int VkControl = 0x11;
    internal const int VkMenu = 0x12;
    internal const int VkLeftWin = 0x5B;
    internal const int VkRightWin = 0x5C;

    internal const uint LlkhfExtended = 0x01;
    internal const uint LlkhfLowerIlInjected = 0x02;
    internal const uint LlkhfInjected = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public IntPtr HWnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out Msg message, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    // Snapshot процессов (поиск окна игры по владельцу, а не по заголовку).
    internal const uint Th32csSnapProcess = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry32
    {
        public uint Size;
        public uint CountUsage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint CountThreads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ImageFile;
    }

    // Захват экрана (чекер инвентаря 7x4).
    internal const uint SrCopy = 0x00CC0020;

    internal const uint DibRgbColors = 0;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(
        IntPtr dest,
        int destX,
        int destY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint rop);

    [DllImport("gdi32.dll")]
    internal static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbmp,
        uint startScan,
        uint scanLines,
        [Out] byte[] bits,
        ref BitmapInfoHeader bmi,
        uint usage);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;   // Отрицательная высота => строки сверху вниз.
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }
}
