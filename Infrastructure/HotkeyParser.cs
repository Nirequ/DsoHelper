using System;
using System.Collections.Generic;

namespace DsoHelper.Infrastructure;

internal readonly record struct HotkeyDefinition(uint Modifiers, uint VirtualKey, string Display);

internal static class HotkeyParser
{
    public static bool TryParseHotkey(string? text, out HotkeyDefinition hotkey, out string error)
    {
        hotkey = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Укажите хоткей, например F8 или Ctrl+F8.";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "Укажите хоткей, например F8 или Ctrl+F8.";
            return false;
        }

        uint modifiers = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "ALT":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= NativeMethods.ModWin;
                    break;
                default:
                    error = $"Неизвестный модификатор «{parts[i]}». Используйте Ctrl, Alt, Shift или Win.";
                    return false;
            }
        }

        if (!KeyCatalog.TryParseKey(parts[^1], out var key) || key.IsModifier)
        {
            error = $"Не удалось распознать клавишу «{parts[^1]}».";
            return false;
        }

        var displayParts = new List<string>();
        if ((modifiers & NativeMethods.ModControl) != 0) displayParts.Add("Ctrl");
        if ((modifiers & NativeMethods.ModAlt) != 0) displayParts.Add("Alt");
        if ((modifiers & NativeMethods.ModShift) != 0) displayParts.Add("Shift");
        if ((modifiers & NativeMethods.ModWin) != 0) displayParts.Add("Win");
        displayParts.Add(key.Display);

        hotkey = new HotkeyDefinition(modifiers, key.VirtualKey, string.Join("+", displayParts));
        error = string.Empty;
        return true;
    }

    public static bool TryParseKey(string? text, out KeyInfo key, out string error)
    {
        if (!KeyCatalog.TryParseKey(text, out key) || key.IsModifier)
        {
            key = default;
            error = string.IsNullOrWhiteSpace(text)
                ? "Укажите клавишу."
                : $"Не удалось распознать клавишу «{text.Trim()}».";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
