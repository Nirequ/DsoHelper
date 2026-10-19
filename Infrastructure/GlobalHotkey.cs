using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace DsoHelper.Infrastructure;

/// <summary>
/// Watches the configured key system-wide through a low-level keyboard hook.
/// The key is only consumed (and <see cref="Pressed"/> raised) while the game
/// window is in the foreground; everywhere else the key is passed through,
/// so a hotkey like Space keeps working in other applications.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const uint WmQuit = 0x0012;

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private readonly uint _modifiers;
    private readonly uint _virtualKey;
    private string? _error;
    private uint _threadId;
    private bool _disposed;

    // Live only on the hook thread.
    private NativeMethods.LowLevelKeyboardProc? _callback;
    private bool _holding;
    private bool _consumed;

    public event Action? Pressed;

    public GlobalHotkey(uint modifiers, uint virtualKey)
    {
        _modifiers = modifiers;
        _virtualKey = virtualKey;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DsoHelper.GlobalHotkey"
        };
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Не удалось запустить обработчик глобального хоткея.");
        }

        if (_error is not null)
        {
            throw new InvalidOperationException(_error);
        }
    }

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _callback = HookCallback;

        var hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);

        if (hook == IntPtr.Zero)
        {
            _error = "Не удалось включить перехват клавиатуры: " +
                     new Win32Exception(Marshal.GetLastWin32Error()).Message;
            _ready.Set();
            return;
        }

        _ready.Set();

        try
        {
            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        finally
        {
            NativeMethods.UnhookWindowsHookEx(hook);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);

            var injected = (data.Flags & (NativeMethods.LlkhfInjected | NativeMethods.LlkhfLowerIlInjected)) != 0;
            var isDown = message == NativeMethods.WmKeyDown || message == NativeMethods.WmSysKeyDown;
            var isUp = message == NativeMethods.WmKeyUp || message == NativeMethods.WmSysKeyUp;

            if (!injected && data.VirtualKey == _virtualKey)
            {
                if (isDown)
                {
                    if (_consumed)
                    {
                        // Keep swallowing auto-repeat of a press we already took.
                        return new IntPtr(1);
                    }

                    var firstDown = !_holding;
                    _holding = true;

                    if (firstDown && ModifiersMatch() && GameWindow.IsForeground())
                    {
                        _consumed = true;
                        NotifyPressed();
                        return new IntPtr(1);
                    }
                }
                else if (isUp)
                {
                    _holding = false;

                    if (_consumed)
                    {
                        _consumed = false;
                        return new IntPtr(1);
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void NotifyPressed()
    {
        try
        {
            Pressed?.Invoke();
        }
        catch
        {
            // A subscriber must never break the hook.
        }
    }

    private bool ModifiersMatch()
    {
        var control = IsDown(NativeMethods.VkControl);
        var alt = IsDown(NativeMethods.VkMenu);
        var shift = IsDown(NativeMethods.VkShift);
        var win = IsDown(NativeMethods.VkLeftWin) || IsDown(NativeMethods.VkRightWin);

        var wantControl = (_modifiers & NativeMethods.ModControl) != 0;
        var wantAlt = (_modifiers & NativeMethods.ModAlt) != 0;
        var wantShift = (_modifiers & NativeMethods.ModShift) != 0;
        var wantWin = (_modifiers & NativeMethods.ModWin) != 0;

        return control == wantControl
            && alt == wantAlt
            && shift == wantShift
            && win == wantWin;
    }

    private static bool IsDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_threadId != 0)
        {
            NativeMethods.PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }
}
