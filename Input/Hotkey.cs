namespace MagniReader.Input;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A shortcut such as "Ctrl+Alt" (modifiers only, used with a mouse click) or
/// "Ctrl+Alt+S" (modifiers plus one key). Stored in settings as text.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public bool HasKey => VirtualKey != 0;

    public static Hotkey DefaultRead => new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0);
    public static Hotkey DefaultSnip => new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'S');
    public static Hotkey DefaultReadSelection => new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'R');

    /// <summary>
    /// Parses "Ctrl+Alt", "Ctrl+Shift+F8", "Win+Alt+R"… Case-insensitive.
    /// Requires at least one modifier, and at most one non-modifier key.
    /// When <paramref name="requireKey"/> is true a key must be present; when false
    /// (click shortcuts) a key is not allowed.
    /// </summary>
    public static bool TryParse(string? text, bool requireKey, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = HotkeyModifiers.None;
        int vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Ctrl; continue;
                case "alt": mods |= HotkeyModifiers.Alt; continue;
                case "shift": mods |= HotkeyModifiers.Shift; continue;
                case "win" or "windows" or "meta": mods |= HotkeyModifiers.Win; continue;
            }
            if (vk != 0) return false; // two keys
            vk = KeyFromName(raw);
            if (vk == 0) return false;
        }

        if (mods == HotkeyModifiers.None) return false;
        if (requireKey && vk == 0) return false;
        if (!requireKey && vk != 0) return false;
        hotkey = new Hotkey(mods, vk);
        return true;
    }

    /// <summary>Parses, falling back to <paramref name="fallback"/> when the text is invalid.</summary>
    public static Hotkey ParseOr(string? text, bool requireKey, Hotkey fallback) =>
        TryParse(text, requireKey, out var h) ? h : fallback;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (VirtualKey != 0) parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    /// <summary>Human text for tooltips, e.g. "Ctrl+Alt+Click".</summary>
    public string ToClickText() => HasKey ? ToString() : ToString() + "+Click";

    // ─── Key names ───────────────────────────────────────────────

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Backspace"] = 0x08,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Left"] = 0x25, ["Up"] = 0x26,
        ["Right"] = 0x27, ["Down"] = 0x28, ["Pause"] = 0x13,
    };

    public static int KeyFromName(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            return 0;
        }
        if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24)
            return 0x70 + f - 1;
        return NamedKeys.TryGetValue(name, out int vk) ? vk : 0;
    }

    public static string KeyName(int vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        foreach (var kv in NamedKeys)
            if (kv.Value == vk) return kv.Key;
        return $"0x{vk:X2}";
    }
}
