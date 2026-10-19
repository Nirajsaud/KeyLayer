namespace KeyLayer;

/// <summary>Human-readable names for key codes and HID usages, plus modifier helpers.</summary>
internal static class KeyNames
{
    public const int Ctrl = 1, Shift = 2, Alt = 4, Win = 8;
    public const int Fn = 16; // the user's custom Fn key (see CustomFnChoices) is held

    static readonly Dictionary<int, string> vkNames = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt",
        [0x13] = "Pause", [0x14] = "Caps Lock", [0x1B] = "Esc", [0x20] = "Space",
        [0x21] = "Page Up", [0x22] = "Page Down", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2C] = "Print Screen", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x5B] = "Left Win", [0x5C] = "Right Win", [0x5D] = "Menu", [0x5F] = "Sleep",
        [0x6A] = "Num *", [0x6B] = "Num +", [0x6D] = "Num -", [0x6E] = "Num .", [0x6F] = "Num /",
        [0x90] = "Num Lock", [0x91] = "Scroll Lock",
        [0xA0] = "Left Shift", [0xA1] = "Right Shift", [0xA2] = "Left Ctrl", [0xA3] = "Right Ctrl",
        [0xA4] = "Left Alt", [0xA5] = "Right Alt",
        [0xA6] = "Browser Back", [0xA7] = "Browser Forward", [0xA8] = "Browser Refresh", [0xA9] = "Browser Stop",
        [0xAA] = "Search", [0xAB] = "Favorites", [0xAC] = "Browser Home",
        [0xAD] = "Mute", [0xAE] = "Volume Down", [0xAF] = "Volume Up",
        [0xB0] = "Next Track", [0xB1] = "Previous Track", [0xB2] = "Stop", [0xB3] = "Play/Pause",
        [0xB4] = "Mail", [0xB5] = "Media Player", [0xB6] = "This PC", [0xB7] = "Calculator",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`",
        [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
    };

    static readonly Dictionary<int, string> consumerNames = new()
    {
        [0x030] = "Power", [0x032] = "Sleep", [0x034] = "Sleep Mode", [0x040] = "Menu",
        [0x06F] = "Brightness Up", [0x070] = "Brightness Down", [0x072] = "Backlight Toggle",
        [0x0B0] = "Play", [0x0B1] = "Pause", [0x0B2] = "Record", [0x0B3] = "Fast Forward", [0x0B4] = "Rewind",
        [0x0B5] = "Next Track", [0x0B6] = "Previous Track", [0x0B7] = "Stop", [0x0B8] = "Eject",
        [0x0CD] = "Play/Pause", [0x0CF] = "Voice Command", [0x0E2] = "Mute", [0x0E5] = "Bass Boost",
        [0x0E9] = "Volume Up", [0x0EA] = "Volume Down",
        [0x183] = "Media Player", [0x18A] = "Mail", [0x192] = "Calculator", [0x194] = "This PC",
        [0x196] = "Web Browser", [0x19E] = "Lock Screen", [0x19F] = "Control Panel", [0x1A7] = "Documents",
        [0x1AE] = "Keyboard Layout", [0x1B6] = "Pictures", [0x1B7] = "Music",
        [0x221] = "Search", [0x223] = "Browser Home", [0x224] = "Browser Back", [0x225] = "Browser Forward",
        [0x226] = "Browser Stop", [0x227] = "Browser Refresh", [0x22A] = "Favorites",
        [0x29F] = "Show All Windows", [0x2A2] = "Show All Apps",
    };

    public static string Vk(int vk)
    {
        if (vkNames.TryGetValue(vk, out var name)) return name;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        if (vk is >= 0x60 and <= 0x69) return "Num " + (vk - 0x60);
        return $"Key 0x{vk:X2}";
    }

    public static string ModText(int mods)
    {
        var parts = new List<string>(5);
        if ((mods & Fn) != 0) parts.Add("Custom Fn");
        if ((mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & Shift) != 0) parts.Add("Shift");
        if ((mods & Alt) != 0) parts.Add("Alt");
        if ((mods & Win) != 0) parts.Add("Win");
        return parts.Count == 0 ? "" : string.Join(" + ", parts) + " + ";
    }

    public static string Combo(int mods, int vk) => ModText(mods) + Vk(vk);

    public static int ModFlag(int vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => Ctrl,
        0x10 or 0xA0 or 0xA1 => Shift,
        0x12 or 0xA4 or 0xA5 => Alt,
        0x5B or 0x5C => Win,
        _ => 0,
    };

    public static bool IsModifier(int vk) => ModFlag(vk) != 0;

    /// <summary>Raw input reports Shift/Ctrl/Alt without the left/right distinction the hook makes.</summary>
    public static int Normalize(int vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => vk,
    };

    /// <summary>Keys that can be held as a home-made Fn key on keyboards that lack one.</summary>
    public static readonly (int Vk, string Name)[] CustomFnChoices =
    [
        (0, "Off"),
        (0x14, "Caps Lock"),
        (0xA3, "Right Ctrl"),
        (0xA5, "Right Alt"),
        (0x5D, "Menu key"),
        (0x91, "Scroll Lock"),
        (0x13, "Pause"),
        (0x2D, "Insert"),
    ];

    /// <summary>Consumer-page usages that Windows turns into these key codes.</summary>
    public static readonly IReadOnlyDictionary<int, int> ConsumerToVk = new Dictionary<int, int>
    {
        [0x0B5] = 0xB0, [0x0B6] = 0xB1, [0x0B7] = 0xB2, [0x0CD] = 0xB3,
        [0x0E2] = 0xAD, [0x0E9] = 0xAF, [0x0EA] = 0xAE,
        [0x183] = 0xB5, [0x18A] = 0xB4, [0x192] = 0xB7, [0x194] = 0xB6,
        [0x221] = 0xAA, [0x223] = 0xAC, [0x224] = 0xA6, [0x225] = 0xA7,
        [0x226] = 0xA9, [0x227] = 0xA8, [0x22A] = 0xAB,
    };

    static readonly HashSet<int> mediaVks = [.. ConsumerToVk.Values];

    /// <summary>
    /// Key codes Windows makes from a keyboard's media (consumer) signal. That signal names the keyboard, so these
    /// can be held briefly and switched off for one keyboard only.
    /// </summary>
    public static bool HasMediaSignal(int vk) => mediaVks.Contains(vk);

    /// <summary>
    /// Keys that only exist as Fn-layer / multimedia keys (media, browser, launch, F13–F24, Sleep).
    /// Anything else is a key the keyboard also has without Fn, so remapping it changes that key too.
    /// </summary>
    public static bool IsSpecialKey(int vk) => vk is >= 0xA6 and <= 0xB7 or 0x5F or >= 0x7C and <= 0x87;

    public static string PageName(int page) => page switch
    {
        0x01 => "System",
        0x07 => "Keyboard",
        0x0C => "Consumer",
        >= 0xFF00 => "Vendor",
        _ => $"Page {page:X2}",
    };

    public static string HidUsage(int page, int usage)
    {
        if (page == 0x0C && consumerNames.TryGetValue(usage, out var name)) return name;
        if (page == 0x01)
        {
            switch (usage)
            {
                case 0x81: return "Power Down";
                case 0x82: return "Sleep";
                case 0x83: return "Wake Up";
            }
        }
        return $"{PageName(page)} 0x{usage:X3}";
    }
}
