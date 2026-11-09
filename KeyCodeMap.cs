namespace DeskDock;

internal static class KeyCodeMap
{
    public static bool IsModifier(uint virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or // Shift, Ctrl, Alt, Windows
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5; // left/right variants

    public static bool IsControl(uint key) => key is 0x11 or 0xA2 or 0xA3;
    public static bool IsAlt(uint key) => key is 0x12 or 0xA4 or 0xA5;

    public static string? ModifierCode(uint virtualKey) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => "KEYCODE_SHIFT_LEFT",
        0x11 or 0xA2 or 0xA3 => "KEYCODE_CTRL_LEFT",
        0x12 or 0xA4 or 0xA5 => "KEYCODE_ALT_LEFT",
        0x5B or 0x5C => "KEYCODE_META_LEFT",
        _ => null
    };

    public static string? AndroidCode(uint key)
    {
        if (key is >= 0x30 and <= 0x39)
            return $"KEYCODE_{(char)key}";
        if (key is >= 0x41 and <= 0x5A)
            return $"KEYCODE_{(char)key}";
        if (key is >= 0x60 and <= 0x69)
            return $"KEYCODE_NUMPAD_{key - 0x60}";
        if (key is >= 0x70 and <= 0x7B)
            return $"KEYCODE_F{key - 0x70 + 1}";

        return key switch
        {
            0x08 => "KEYCODE_DEL",
            0x09 => "KEYCODE_TAB",
            0x0D => "KEYCODE_ENTER",
            0x13 => "KEYCODE_BREAK",
            0x14 => "KEYCODE_CAPS_LOCK",
            0x1B => "KEYCODE_ESCAPE",
            0x20 => "KEYCODE_SPACE",
            0x21 => "KEYCODE_PAGE_UP",
            0x22 => "KEYCODE_PAGE_DOWN",
            0x23 => "KEYCODE_MOVE_END",
            0x24 => "KEYCODE_MOVE_HOME",
            0x25 => "KEYCODE_DPAD_LEFT",
            0x26 => "KEYCODE_DPAD_UP",
            0x27 => "KEYCODE_DPAD_RIGHT",
            0x28 => "KEYCODE_DPAD_DOWN",
            0x2D => "KEYCODE_INSERT",
            0x2E => "KEYCODE_FORWARD_DEL",
            0x5D => "KEYCODE_MENU",
            0x6A => "KEYCODE_NUMPAD_MULTIPLY",
            0x6B => "KEYCODE_NUMPAD_ADD",
            0x6D => "KEYCODE_NUMPAD_SUBTRACT",
            0x6E => "KEYCODE_NUMPAD_DOT",
            0x6F => "KEYCODE_NUMPAD_DIVIDE",
            0x90 => "KEYCODE_NUM_LOCK",
            0x91 => "KEYCODE_SCROLL_LOCK",
            0xA6 => "KEYCODE_BACK",
            0xA7 => "KEYCODE_FORWARD",
            0xAD => "KEYCODE_VOLUME_MUTE",
            0xAE => "KEYCODE_VOLUME_DOWN",
            0xAF => "KEYCODE_VOLUME_UP",
            0xBA => "KEYCODE_SEMICOLON",
            0xBB => "KEYCODE_EQUALS",
            0xBC => "KEYCODE_COMMA",
            0xBD => "KEYCODE_MINUS",
            0xBE => "KEYCODE_PERIOD",
            0xBF => "KEYCODE_SLASH",
            0xC0 => "KEYCODE_GRAVE",
            0xDB => "KEYCODE_LEFT_BRACKET",
            0xDC => "KEYCODE_BACKSLASH",
            0xDD => "KEYCODE_RIGHT_BRACKET",
            0xDE => "KEYCODE_APOSTROPHE",
            0xE2 => "KEYCODE_BACKSLASH",
            _ => null
        };
    }
}
