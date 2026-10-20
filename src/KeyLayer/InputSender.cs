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

    [DllImport("user32.dll")]
    static extern uint MapVirtualKeyEx(uint code, uint mapType, nint layout);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern short VkKeyScanEx(char c, nint layout);

    [DllImport("user32.dll")]
    static extern nint GetKeyboardLayout(uint threadId);

    const uint KEYEVENTF_SCANCODE = 0x8, MAPVK_VK_TO_VSC_EX = 4;

    public static void Tap(int vk) => Send([Key(vk, up: false), Key(vk, up: true)]);

    // ---- "Act as another key": these run on the input thread, in order, and never throw.

    /// <summary>Holds a key (and its modifiers) down.</summary>
    public static void Press(int mods, int vk) =>
        TrySend([.. ModifierVks(mods).Select(m => Key(m, up: false)), Key(vk, up: false)]);

    /// <summary>Repeats a held key, as a keyboard does while a key is held.</summary>
    public static void Repeat(int vk) => TrySend([Key(vk, up: false)]);

    /// <summary>Lets go of a key held with <see cref="Press"/>.</summary>
    public static void Release(int mods, int vk) =>
        TrySend([Key(vk, up: true), .. ModifierVks(mods).AsEnumerable().Reverse().Select(m => Key(m, up: true))]);

    static void TrySend(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    // ---- typing methods

    /// <summary>
    /// Types text as real key presses (scan codes, with Shift or AltGr where the keyboard layout needs them), one key
    /// at a time, for games and remote desktop windows that ignore typed characters. Characters the layout has no key
    /// for are typed as characters.
    /// </summary>
    public static void TypeAsKeys(string text)
    {
        uint thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        nint layout = GetKeyboardLayout(thread);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue; // CR LF is one Enter
            int vk, shift = 0;
            if (c is '\r' or '\n') vk = 0x0D;
            else if (c == '\t') vk = 0x09;
            else
            {
                short scan = VkKeyScanEx(c, layout);
                if (scan == -1 || (scan & 0xF800) != 0)
                {
                    Send([Unicode(c, up: false), Unicode(c, up: true)]);
                    continue;
                }
                vk = scan & 0xFF;
                shift = (scan >> 8) & 0x7; // 1 Shift, 2 Ctrl, 4 Alt (Ctrl + Alt is AltGr)
            }

            var mods = new List<int>(3);
            if ((shift & 1) != 0) mods.Add(0xA0);
            if ((shift & 2) != 0) mods.Add(0xA2);
            if ((shift & 4) != 0) mods.Add(0xA4);
            // Down, a short wait, then up: games read the keyboard once per frame and miss instant taps.
            Send([.. mods.Select(m => ScanKey(m, up: false, layout)), ScanKey(vk, up: false, layout)]);
            Thread.Sleep(8);
            Send([ScanKey(vk, up: true, layout), .. Enumerable.Reverse(mods).Select(m => ScanKey(m, up: true, layout))]);
            Thread.Sleep(8);
        }
    }

    /// <summary>A key press sent as the scan code the keyboard itself would send (falls back to the key code).</summary>
    static INPUT ScanKey(int vk, bool up, nint layout)
    {
        uint scan = MapVirtualKeyEx((uint)vk, MAPVK_VK_TO_VSC_EX, layout);
        if (scan == 0) return Key(vk, up);
        uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) | ((scan & 0xFF00) == 0xE000 ? KEYEVENTF_EXTENDEDKEY : 0);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)(scan & 0xFF), dwFlags = flags, dwExtraInfo = Marker } },
        };
    }

    /// <summary>
    /// Pastes text with Ctrl + V, then puts back what was on the clipboard before. The pasted text is kept out of
    /// clipboard history and cloud clipboard.
    /// </summary>
    public static void Paste(string text)
    {
        // The clipboard needs a single-threaded apartment; actions run on the thread pool.
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { PasteNow(text); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }

    static void PasteNow(string text)
    {
        var saved = SaveClipboard();
        var data = new System.Windows.Forms.DataObject();
        data.SetText(text, System.Windows.Forms.TextDataFormat.UnicodeText);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        System.Windows.Forms.Clipboard.SetDataObject(data, true, 5, 50);

        Shortcut(KeyNames.Ctrl, 'V');
        Thread.Sleep(700); // the app reads the clipboard after the keys arrive (remote desktop fetches it over the network)

        if (saved is not null) System.Windows.Forms.Clipboard.SetDataObject(saved, true, 5, 50);
        else System.Windows.Forms.Clipboard.Clear();
    }

    /// <summary>A copy of everything on the clipboard that can be read back, or null when it's empty.</summary>
    static System.Windows.Forms.DataObject? SaveClipboard()
    {
        var current = System.Windows.Forms.Clipboard.GetDataObject();
        var formats = current?.GetFormats(autoConvert: false) ?? [];
        if (current is null || formats.Length == 0) return null;
        var copy = new System.Windows.Forms.DataObject();
        foreach (string format in formats)
        {
            try
            {
                if (current.GetData(format, autoConvert: false) is { } value) copy.SetData(format, value);
            }
            catch
            {
                // Some formats can't be read back (data rendered on request by the app that copied it).
            }
        }
        return copy;
    }

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
