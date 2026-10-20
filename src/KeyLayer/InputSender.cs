using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KeyLayer;

/// <summary>Synthesizes keystrokes with SendInput. Everything sent is tagged so our own hook ignores it.</summary>
internal static class InputSender
{
    public const nuint Marker = 0x4B4C5952; // "KLYR"

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
    const int VK_MASK = 0xE8; // unassigned key code, used to keep Win/Alt from opening Start or a menu bar

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nuint dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);

    public static void Tap(int vk) => Send([Key(vk, up: false), Key(vk, up: true)]);

    public static void Mask() => Tap(VK_MASK);

    public static void Shortcut(int mods, int vk)
    {
        var modifierKeys = ModifierVks(mods);
        var inputs = new List<INPUT>();
        foreach (var m in modifierKeys) inputs.Add(Key(m, up: false));
        inputs.Add(Key(vk, up: false));
        inputs.Add(Key(vk, up: true));
        for (int i = modifierKeys.Count - 1; i >= 0; i--) inputs.Add(Key(modifierKeys[i], up: true));
        Send([.. inputs]);
    }

    public static void Text(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                inputs.Add(Key(0x0D, up: false));
                inputs.Add(Key(0x0D, up: true));
            }
            else if (c == '\t')
            {
                inputs.Add(Key(0x09, up: false));
                inputs.Add(Key(0x09, up: true));
            }
            else
            {
                inputs.Add(Unicode(c, up: false));
                inputs.Add(Unicode(c, up: true));
            }
        }
        if (inputs.Count > 0) Send([.. inputs]);
    }

    static readonly int[] modifierVks = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];

    // Modifiers the user's fingers are on, one bit per entry of modifierVks. Once we lift a held modifier, Windows
    // reports it as up, so the keyboard hook keeps track of the real key presses instead.
    static int physicalModifiers;

    /// <summary>Called for real (not injected) key presses and releases.</summary>
    public static void NotePhysical(int vk, bool down)
    {
        int bit = Array.IndexOf(modifierVks, vk);
        if (bit < 0) return;
        if (down) Interlocked.Or(ref physicalModifiers, 1 << bit);
        else Interlocked.And(ref physicalModifiers, ~(1 << bit));
    }

    /// <summary>
    /// Lifts modifier keys that are still held (e.g. from the triggering combo), except the ones in
    /// <paramref name="keep"/>, so they don't mix into what we are about to send. Returns the keys it lifted.
    /// </summary>
    public static List<int> ReleaseHeldModifiers(int keep)
    {
        var lifted = modifierVks.Where(vk => (Native.GetAsyncKeyState(vk) & 0x8000) != 0 && (keep & KeyNames.ModFlag(vk)) == 0).ToList();
        if (lifted.Count == 0) return lifted;
        Send([Key(VK_MASK, up: false), Key(VK_MASK, up: true), .. lifted.Select(vk => Key(vk, up: true))]);
        return lifted;
    }

    /// <summary>
    /// Presses modifiers we let go of back down if the user is still holding them, so holding Ctrl and pressing a
    /// shortcut twice runs it twice. Never throws: it runs after the action, whatever happened there.
    /// </summary>
    public static void RestoreModifiers(IEnumerable<int> released)
    {
        int held = Volatile.Read(ref physicalModifiers);
        var downs = released.Distinct()
            .Where(vk => Array.IndexOf(modifierVks, vk) is var bit and >= 0 && (held & (1 << bit)) != 0)
            .Select(vk => Key(vk, up: false))
            .ToList();
        if (downs.Count == 0) return;
        // Keep Win or Alt from opening Start or a menu bar when the user lets go of them later.
        downs.Add(Key(VK_MASK, up: false));
        downs.Add(Key(VK_MASK, up: true));
        SendInput((uint)downs.Count, [.. downs], Marshal.SizeOf<INPUT>());
    }

    /// <summary>The modifier keys <see cref="Shortcut"/> presses for these modifier flags.</summary>
    public static List<int> ModifierVks(int mods)
    {
        var list = new List<int>(4);
        if ((mods & KeyNames.Ctrl) != 0) list.Add(0xA2);
        if ((mods & KeyNames.Shift) != 0) list.Add(0xA0);
        if ((mods & KeyNames.Alt) != 0) list.Add(0xA4);
        if ((mods & KeyNames.Win) != 0) list.Add(0x5B);
        return list;
    }

    static bool IsExtended(int vk) =>
        vk is >= 0x21 and <= 0x28 or 0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5
            or >= 0xA6 and <= 0xB7;

    static INPUT Key(int vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)MapVirtualKey((uint)vk, 0),
                    dwFlags = flags,
                    dwExtraInfo = Marker,
                },
            },
        };
    }

    static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
                dwExtraInfo = Marker,
            },
        },
    };

    static void Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new Win32Exception("Windows blocked the keystrokes. If the window in front is running as administrator, run KeyLayer as administrator too.");
    }
}
