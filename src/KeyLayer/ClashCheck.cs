namespace KeyLayer;

internal enum ClashLevel { Safe, MightClash, Windows }

/// <summary>How likely a shortcut is to take over a shortcut that Windows or other apps already use.</summary>
internal sealed record ClashInfo(ClashLevel Level, string Title, string Text);

/// <summary>
/// Apps don't tell Windows which shortcuts they use, so this rates a key combination from what Windows and
/// common apps are known to use. Fn-layer combinations, media keys and F13–F24 are never app shortcuts.
/// </summary>
internal static class ClashCheck
{
    static readonly Dictionary<int, string> winUses = new()
    {
        ['A'] = "opens Quick Settings", ['B'] = "jumps to the tray icons", ['C'] = "opens Copilot", ['D'] = "shows the desktop",
        ['E'] = "opens File Explorer", ['F'] = "opens Feedback Hub", ['G'] = "opens Game Bar", ['H'] = "starts voice typing",
        ['I'] = "opens Settings", ['K'] = "opens Cast", ['M'] = "minimises all windows", ['N'] = "opens notifications",
        ['O'] = "locks screen rotation", ['P'] = "switches display mode", ['Q'] = "opens Search", ['R'] = "opens Run",
        ['S'] = "opens Search", ['T'] = "moves through the taskbar", ['U'] = "opens Accessibility settings",
        ['V'] = "opens clipboard history", ['W'] = "opens Widgets", ['X'] = "opens the Quick Link menu", ['Z'] = "opens Snap layouts",
        [0x09] = "opens Task View", [0x20] = "switches the keyboard language", [0x2C] = "saves a screenshot",
        [0x24] = "minimises the other windows", [0x13] = "opens system info",
        [0x25] = "snaps windows", [0x26] = "snaps windows", [0x27] = "snaps windows", [0x28] = "snaps windows",
        [0xBB] = "opens Magnifier", [0xBC] = "peeks at the desktop", [0xBE] = "opens the emoji panel",
    };

    static readonly Dictionary<int, string> ctrlUses = new()
    {
        ['A'] = "select everything", ['B'] = "make text bold", ['C'] = "copy", ['D'] = "bookmark or duplicate",
        ['E'] = "search", ['F'] = "find", ['G'] = "find the next match", ['H'] = "replace or show history",
        ['I'] = "make text italic", ['J'] = "show downloads", ['K'] = "insert a link", ['L'] = "go to the address bar",
        ['N'] = "make something new", ['O'] = "open a file", ['P'] = "print", ['Q'] = "quit", ['R'] = "refresh",
        ['S'] = "save", ['T'] = "open a new tab", ['U'] = "underline text", ['V'] = "paste", ['W'] = "close a tab or window",
        ['X'] = "cut", ['Y'] = "redo", ['Z'] = "undo", [0x09] = "switch tabs", ['0'] = "reset the zoom",
        [0xBB] = "zoom in", [0xBD] = "zoom out", [0x0D] = "send or confirm", [0x08] = "delete a word", [0x2E] = "delete a word",
    };

    static readonly Dictionary<int, string> fUses = new()
    {
        [0x70] = "open help", [0x71] = "rename things", [0x72] = "search", [0x73] = "go to the address bar",
        [0x74] = "refresh", [0x75] = "move between panes", [0x76] = "check spelling", [0x79] = "show the menu bar",
        [0x7A] = "go full screen", [0x7B] = "open developer tools or Save As",
    };

    public static ClashInfo For(Trigger t)
    {
        const string NoClash = "Won't clash";
        if (t.Kind == TriggerKind.Hid)
            return new(ClashLevel.Safe, NoClash, "Apps don't use this signal for their shortcuts, so it can't take over any of them.");

        int vk = t.Vk, mods = t.Modifiers;
        string combo = KeyNames.Combo(mods & ~KeyNames.Fn, vk);
        string key = KeyNames.Vk(vk);

        if ((mods & KeyNames.Fn) != 0)
            return new(ClashLevel.Safe, NoClash, "Apps don't use your custom Fn key in their shortcuts, so this can't take over any of them.");
        if (vk is >= 0x7C and <= 0x87)
            return new(ClashLevel.Safe, NoClash, "Hardly any app uses F13 to F24, so this is very unlikely to clash.");
        if (KeyNames.IsSpecialKey(vk))
            return new(ClashLevel.Safe, NoClash, "Media and launch keys aren't used for app shortcuts, so this can't take over any of them.");

        const string WindowsTitle = "Windows uses this";
        const string Takes = "Your shortcut takes it over in every app.";
        if ((mods & KeyNames.Win) != 0)
        {
            if (vk == 'L' && mods == KeyNames.Win)
                return new(ClashLevel.Windows, WindowsTitle, "Win + L always locks the PC, so this shortcut can't run. Pick another key.");
            if (mods == KeyNames.Win && winUses.TryGetValue(vk, out var does))
                return new(ClashLevel.Windows, WindowsTitle, $"{combo} {does}. Your shortcut replaces it.");
            if (mods == KeyNames.Win && vk is >= '0' and <= '9')
                return new(ClashLevel.Windows, WindowsTitle, $"{combo} opens the app pinned at that spot on the taskbar. Your shortcut replaces it.");
            return new(ClashLevel.MightClash, "Might clash", $"Windows uses many Win-key combinations and adds new ones over time. If it uses {combo}, your shortcut replaces it.");
        }

        if (mods == KeyNames.Alt && vk is 0x09 or 0x73 or 0x1B or 0x20)
            return new(ClashLevel.Windows, WindowsTitle, $"{combo} {(vk == 0x73 ? "closes the window" : vk == 0x20 ? "opens the window menu" : "switches windows")}. Your shortcut replaces it.");
        if (vk == 0x1B && (mods == KeyNames.Ctrl || mods == (KeyNames.Ctrl | KeyNames.Shift)))
            return new(ClashLevel.Windows, WindowsTitle, $"{combo} opens {(mods == KeyNames.Ctrl ? "Start" : "Task Manager")}. Your shortcut replaces it.");
        if (vk == 0x2C && mods == 0)
            return new(ClashLevel.Windows, WindowsTitle, "Windows takes a screenshot with Print Screen. Your shortcut replaces it.");

        const string Might = "Might clash";
        bool ctrl = (mods & KeyNames.Ctrl) != 0, shift = (mods & KeyNames.Shift) != 0, alt = (mods & KeyNames.Alt) != 0;
        if (ctrl && alt && shift)
            return new(ClashLevel.Safe, "Unlikely to clash", $"Few apps use Ctrl + Alt + Shift combinations, so {combo} is unlikely to clash.");
        if (ctrl && alt)
            return new(ClashLevel.MightClash, Might,
                $"Some apps use {combo}, and on many keyboard layouts Ctrl + Alt types special letters. {Takes}");
        if (ctrl && shift)
            return new(ClashLevel.MightClash, Might, $"Many apps use Ctrl + Shift combinations like {combo}. {Takes}");
        if (ctrl)
            return new(ClashLevel.MightClash, Might, ctrlUses.TryGetValue(vk, out var does)
                ? $"Almost every app uses {combo} to {does}. {Takes}"
                : $"Many apps use Ctrl + letter shortcuts like {combo}. {Takes}");
        if (alt)
            return new(ClashLevel.MightClash, Might, vk is >= 'A' and <= 'Z'
                ? $"Apps use Alt + letter to open their menus (Alt + F is often the File menu). {Takes}"
                : $"Some apps use {combo}. {Takes}");

        if (vk is >= 0x70 and <= 0x7B)
            return new(ClashLevel.MightClash, Might, (mods == 0 && fUses.TryGetValue(vk, out var fDoes)
                ? $"Many apps use {key} to {fDoes}. "
                : $"Many apps use {combo}. ") + "Your shortcut runs every time you press it, with or without Fn.");

        return vk switch
        {
            0x14 or 0x90 => new(ClashLevel.MightClash, Might, $"{key} is part of normal typing. Your shortcut replaces it."),
            0x91 or 0x13 => new(ClashLevel.Safe, "Unlikely to clash", $"Hardly any app uses {key}, so this is unlikely to clash."),
            0x5D => new(ClashLevel.MightClash, Might, "The Menu key opens right-click menus. Your shortcut replaces it."),
            _ => new(ClashLevel.MightClash, "You type with this key",
                $"Apps use {combo} all the time. Your shortcut runs every time you press it."),
        };
    }

    /// <summary>
    /// The Fn-layer combination to suggest instead (same key, held with the Fn key), or null when there isn't a good one.
    /// </summary>
    public static int? FnAlternative(Trigger t, int customFnVk)
    {
        if (t.Kind != TriggerKind.Key || (t.Modifiers & KeyNames.Fn) != 0 || KeyNames.IsModifier(t.Vk)) return null;
        int fn = customFnVk != 0 ? customFnVk : KeyNames.SuggestedFnVk;
        return t.Vk == fn || KeyNames.Normalize(t.Vk) == KeyNames.Normalize(fn) ? null : t.Vk;
    }
}
