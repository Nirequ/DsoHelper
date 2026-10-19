using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace DsoHelper.Infrastructure;

/// <summary>A single keyboard key: its display name and its Windows virtual-key code.</summary>
internal readonly record struct KeyInfo(string Display, ushort VirtualKey, bool IsModifier, bool IsExtended);

/// <summary>
/// Maps Avalonia keys to Windows virtual-key codes with user-facing names, and back.
/// Used by the key-capture fields and by parsing the stored settings.
/// </summary>
internal static class KeyCatalog
{
    // Keys that must be sent with KEYEVENTF_EXTENDEDKEY so games see them correctly.
    private static readonly HashSet<ushort> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PageUp/PageDown, End, Home, arrows
        0x2C, 0x2D, 0x2E,                               // PrintScreen, Insert, Delete
        0x5B, 0x5C, 0x5D,                               // LWin, RWin, Menu
        0x6F, 0x90, 0xA3, 0xA5,                         // Divide, NumLock, RightCtrl, RightAlt
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC,       // browser keys
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3        // volume and media keys
    ];

    private static readonly Dictionary<string, KeyInfo> ByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, KeyInfo> ByDisplay = new(StringComparer.OrdinalIgnoreCase);

    static KeyCatalog()
    {
        for (var c = 'A'; c <= 'Z'; c++)
        {
            Add(c.ToString(), (ushort)c, c.ToString());
        }

        for (var d = 0; d <= 9; d++)
        {
            Add(d.ToString(), (ushort)('0' + d), "D" + d);
        }

        for (var n = 1; n <= 24; n++)
        {
            Add("F" + n, (ushort)(0x6F + n), "F" + n);
        }

        Add("Backspace", 0x08, "Back");
        Add("Tab", 0x09, "Tab");
        Add("Enter", 0x0D, "Enter", "Return");
        Add("Esc", 0x1B, "Escape");
        Add("Space", 0x20, "Space");
        Add("PageUp", 0x21, "PageUp", "Prior");
        Add("PageDown", 0x22, "PageDown", "Next");
        Add("End", 0x23, "End");
        Add("Home", 0x24, "Home");
        Add("Left", 0x25, "Left");
        Add("Up", 0x26, "Up");
        Add("Right", 0x27, "Right");
        Add("Down", 0x28, "Down");
        Add("Insert", 0x2D, "Insert");
        Add("Delete", 0x2E, "Delete");
        Add("CapsLock", 0x14, "CapsLock", "Capital");
        Add("NumLock", 0x90, "NumLock");
        Add("ScrollLock", 0x91, "Scroll");
        Add("PrintScreen", 0x2C, "PrintScreen", "Snapshot");
        Add("Pause", 0x13, "Pause");
        Add("Menu", 0x5D, "Apps");
        Add("Clear", 0x0C, "Clear");
        Add("Select", 0x29, "Select");
        Add("Print", 0x2A, "Print");
        Add("Help", 0x2F, "Help");
        Add("Sleep", 0x5F, "Sleep");

        for (var n = 0; n <= 9; n++)
        {
            Add("Num" + n, (ushort)(0x60 + n), "NumPad" + n);
        }

        Add("NumAdd", 0x6B, "Add");
        Add("NumSubtract", 0x6D, "Subtract");
        Add("NumMultiply", 0x6A, "Multiply");
        Add("NumDivide", 0x6F, "Divide");
        Add("NumDecimal", 0x6E, "Decimal");
        Add("NumSeparator", 0x6C, "Separator");

        Add(";", 0xBA, "OemSemicolon", "Oem1");
        Add("=", 0xBB, "OemPlus");
        Add(",", 0xBC, "OemComma");
        Add("-", 0xBD, "OemMinus");
        Add(".", 0xBE, "OemPeriod");
        Add("/", 0xBF, "OemQuestion", "Oem2");
        Add("`", 0xC0, "OemTilde", "Oem3");
        Add("[", 0xDB, "OemOpenBrackets", "Oem4");
        Add("\\", 0xDC, "OemPipe", "Oem5", "OemBackslash");
        Add("]", 0xDD, "OemCloseBrackets", "Oem6");
        Add("'", 0xDE, "OemQuotes", "Oem7");
        Add("Oem8", 0xDF, "Oem8");
        Add("Oem102", 0xE2, "Oem102");

        Add("VolumeMute", 0xAD, "VolumeMute");
        Add("VolumeDown", 0xAE, "VolumeDown");
        Add("VolumeUp", 0xAF, "VolumeUp");
        Add("MediaNextTrack", 0xB0, "MediaNextTrack");
        Add("MediaPreviousTrack", 0xB1, "MediaPreviousTrack");
        Add("MediaStop", 0xB2, "MediaStop");
        Add("MediaPlayPause", 0xB3, "MediaPlayPause");
        Add("BrowserBack", 0xA6, "BrowserBack");
        Add("BrowserForward", 0xA7, "BrowserForward");
        Add("BrowserRefresh", 0xA8, "BrowserRefresh");
        Add("BrowserStop", 0xA9, "BrowserStop");
        Add("BrowserSearch", 0xAA, "BrowserSearch");
        Add("BrowserFavorites", 0xAB, "BrowserFavorites");
        Add("BrowserHome", 0xAC, "BrowserHome");

        // Modifiers are recognised while capturing but are not valid keys on their own.
        AddModifier("Ctrl", 0xA2, "LeftCtrl");
        AddModifier("Ctrl", 0xA3, "RightCtrl");
        AddModifier("Shift", 0xA0, "LeftShift");
        AddModifier("Shift", 0xA1, "RightShift");
        AddModifier("Alt", 0xA4, "LeftAlt");
        AddModifier("Alt", 0xA5, "RightAlt");
        AddModifier("Win", 0x5B, "LWin");
        AddModifier("Win", 0x5C, "RWin");
    }

    private static void Add(string display, ushort virtualKey, params string[] names)
    {
        var info = new KeyInfo(display, virtualKey, IsModifier: false, IsExtended: ExtendedKeys.Contains(virtualKey));
        ByDisplay[display] = info;

        foreach (var name in names)
        {
            ByName[name] = info;
        }
    }

    private static void AddModifier(string display, ushort virtualKey, string name)
    {
        ByName[name] = new KeyInfo(display, virtualKey, IsModifier: true, IsExtended: ExtendedKeys.Contains(virtualKey));
    }

    public static bool TryDescribe(Key key, out KeyInfo info) =>
        ByName.TryGetValue(key.ToString(), out info);

    public static bool TryParseKey(string? text, out KeyInfo info)
    {
        info = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var token = text.Trim();
        if (ByDisplay.TryGetValue(token, out var found) || ByName.TryGetValue(token, out found))
        {
            info = found;
            return true;
        }

        return false;
    }

    public static string DescribeModifiers(KeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        return string.Join("+", parts);
    }

    public static string ComposeHotkey(KeyModifiers modifiers, string keyDisplay)
    {
        var prefix = DescribeModifiers(modifiers);
        return string.IsNullOrEmpty(prefix) ? keyDisplay : prefix + "+" + keyDisplay;
    }
}
