namespace Zuul.Core;

// Hotkeys are saved as text like "Alt+Space" and turned into RegisterHotKey's modifiers and virtual key.
public static class HotKeyText
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8;

    // Names follow Avalonia's Key enum, so a captured key round-trips: D5 is the 5 key.
    private static readonly Dictionary<string, uint> Keys = BuildKeys();

    private static Dictionary<string, uint> BuildKeys()
    {
        var keys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Back"] = 0x08, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
            ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
            ["OemTilde"] = 0xC0, ["OemPeriod"] = 0xBE, ["OemComma"] = 0xBC, ["OemQuestion"] = 0xBF
        };
        for (var c = 'A'; c <= 'Z'; c++) keys[c.ToString()] = c;
        for (var d = 0; d <= 9; d++) keys["D" + d] = (uint)('0' + d);
        for (var f = 1; f <= 24; f++) keys["F" + f] = (uint)(0x6F + f);
        return keys;
    }

    public static bool IsKey(string name) => Keys.ContainsKey(name);

    // Function keys work alone; anything else needs a modifier, or typing it would open the launcher.
    public static bool TryParse(string? text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "alt": modifiers |= Alt; break;
                case "ctrl" or "control": modifiers |= Control; break;
                case "shift": modifiers |= Shift; break;
                case "win": modifiers |= Win; break;
                default:
                    if (key != 0 || !Keys.TryGetValue(part, out key)) return false;
                    break;
            }
        }
        return key != 0 && (modifiers != 0 || key is >= 0x70 and <= 0x87);
    }

    public static string Format(bool ctrl, bool alt, bool shift, bool win, string key) =>
        string.Join("+", new[] { ctrl ? "Ctrl" : null, alt ? "Alt" : null, shift ? "Shift" : null, win ? "Win" : null, key }.OfType<string>());
}
