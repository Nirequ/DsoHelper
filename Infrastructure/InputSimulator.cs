using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DsoHelper.Infrastructure;

internal static class InputSimulator
{
    public static void KeyPress(KeyInfo key)
    {
        Send(
        [
            CreateKeyboardInput(key.VirtualKey, key.IsExtended, keyUp: false),
            CreateKeyboardInput(key.VirtualKey, key.IsExtended, keyUp: true)
        ], "SendInput (клавиатура)");
    }

    public static void LeftClick(int x, int y)
    {
        Click(x, y, NativeMethods.MouseEventLeftDown, NativeMethods.MouseEventLeftUp);
    }

    public static void RightClick(int x, int y)
    {
        Click(x, y, NativeMethods.MouseEventRightDown, NativeMethods.MouseEventRightUp);
    }

    private static void Click(int x, int y, uint downFlag, uint upFlag)
    {
        if (!NativeMethods.SetCursorPos(x, y))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Не удалось переместить курсор в точку {x}, {y}.");
        }

        Send(
        [
            CreateMouseInput(downFlag),
            CreateMouseInput(upFlag)
        ], "SendInput (мышь)");
    }

    private static NativeMethods.Input CreateKeyboardInput(ushort virtualKey, bool extended, bool keyUp)
    {
        var flags = 0u;
        if (extended)
        {
            flags |= NativeMethods.KeyEventExtendedKey;
        }

        if (keyUp)
        {
            flags |= NativeMethods.KeyEventKeyUp;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static NativeMethods.Input CreateMouseInput(uint flags) => new()
    {
        Type = NativeMethods.InputMouse,
        Union = new NativeMethods.InputUnion
        {
            Mouse = new NativeMethods.MouseInput
            {
                Dx = 0,
                Dy = 0,
                MouseData = 0,
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero
            }
        }
    };

    private static void Send(NativeMethods.Input[] inputs, string operation)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        if (sent == inputs.Length)
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        var detail = error == 0
            ? $"отправлено {sent} из {inputs.Length} событий"
            : new Win32Exception(error).Message;

        throw new InvalidOperationException($"{operation}: {detail}");
    }
    
    
}
