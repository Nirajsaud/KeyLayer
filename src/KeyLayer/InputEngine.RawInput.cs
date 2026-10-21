using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyLayer;

// Raw input: which keyboard a key came from, and keys while one of KeyLayer's own windows is active.
internal sealed unsafe partial class InputEngine
{
    // ------------------------------------------------------------------ raw input

    void OnRawInput(nint lParam)
    {
        uint size = RawBufferSize;
        if (Native.GetRawInputData(lParam, Native.RID_INPUT, rawBuffer, ref size, (uint)sizeof(Native.RAWINPUTHEADER)) == uint.MaxValue)
            return;
        ProcessRawInput((byte*)rawBuffer);
    }

    /// <summary>Processes raw input already queued for this thread (called from the hook, before deciding).</summary>
    void DrainRawInput()
    {
        uint headerSize = (uint)sizeof(Native.RAWINPUTHEADER);
        for (int round = 0; round < 8; round++)
        {
            uint size = RawBufferSize;
            uint count = Native.GetRawInputBuffer(drainBuffer, ref size, headerSize);
            if (count == 0 || count == uint.MaxValue) return;
            byte* p = (byte*)drainBuffer;
            for (uint i = 0; i < count; i++)
            {
                uint recordSize = ((Native.RAWINPUTHEADER*)p)->dwSize;
                ProcessRawInput(p);
                p = (byte*)(((nuint)p + recordSize + 7) & ~(nuint)7);
            }
        }
    }

    void ProcessRawInput(byte* p)
    {
        var header = *(Native.RAWINPUTHEADER*)p;
        byte* body = p + sizeof(Native.RAWINPUTHEADER);

        if (header.dwType == Native.RIM_TYPEKEYBOARD)
        {
            ushort makeCode = *(ushort*)body;
            ushort flags = *(ushort*)(body + 2);
            ushort vkey = *(ushort*)(body + 6);
            uint extra = *(uint*)(body + 12);
            if (extra == (uint)InputSender.Marker) return; // our own keystrokes
            OnRawKey(header.hDevice, SideSpecificVk(vkey, makeCode, flags), (flags & Native.RI_KEY_BREAK) == 0);
        }
        else if (header.dwType == Native.RIM_TYPEHID)
        {
            var device = GetDevice(header.hDevice);
            if (device is null) return;
            // Vendor channels are only interesting on keyboards (other devices can be chatty).
            if (device.UsagePage >= 0xFF00 && !externalKeyboards.ContainsKey(device.KeyboardId)) return;

            int reportSize = *(int*)body;
            int count = *(int*)(body + 4);
            byte* reports = body + 8;
            for (int i = 0; i < count; i++)
                OnHidReport(device, new ReadOnlySpan<byte>(reports + i * reportSize, reportSize).ToArray());
        }
    }

    /// <summary>Raw input reports plain Shift/Ctrl/Alt; tell left from right like the hook does.</summary>
    static int SideSpecificVk(int vkey, int makeCode, ushort flags) => vkey switch
    {
        0x10 => makeCode == 0x36 ? 0xA1 : 0xA0,
        0x11 => (flags & Native.RI_KEY_E0) != 0 ? 0xA3 : 0xA2,
        0x12 => (flags & Native.RI_KEY_E0) != 0 ? 0xA5 : 0xA4,
        _ => vkey,
    };

    void OnRawKey(nint handle, int rawVk, bool down)
    {
        var keyboard = KeyboardFor(handle);
        if (keyboard is not null) NoteActivity(keyboard);

        if (appInForeground)
        {
            OnRawKeyInForeground(handle, keyboard, rawVk, down);
            return;
        }

        // The hook always sees a key before raw input does. A real key press it never saw means Windows dropped
        // the hook (it does that, without telling, to a hook that once answered too slowly): put it back.
        int now = Environment.TickCount;
        if (down && handle != 0 && now - lastHookTick > HookSilentMs && now - lastHookInstallTick > HookSilentMs)
            InstallHook();

        int vk = KeyNames.Normalize(rawVk);
        string name = keyboard is not null ? NameOf(keyboard) : handle == 0 ? FromSoftware : "Unknown keyboard";

        if (!down)
        {
            if (releaseWaits.FirstOrDefault(w => KeyNames.Normalize(w.Vk) == vk) is { } wait)
                ResolveRelease(wait, keyboard?.Id);
            if (keyboard is not null && capture is { Key: { Keyboard: null } key } && KeyNames.Normalize(key.Vk) == vk)
            {
                key.Keyboard = keyboard.Id;
                key.KeyboardName = name;
            }
        }

        // A media key Windows made from a keyboard's signal has no keyboard of its own: the signal names it.
        if (handle == 0 && KeyNames.HasMediaSignal(vk)) return;

        // Key monitor rows waiting for the keyboard's name (a key held back is only named when released).
        if (observations.FirstOrDefault(o => o.Vk == vk) is { } row)
        {
            observations.Remove(row);
            row.Trigger.Keyboard ??= keyboard?.Id;
            row.Trigger.KeyboardName ??= keyboard is null ? null : name;
            RaiseKey(row.ToObserved(name));
        }
    }

    /// <summary>
    /// Keys while a KeyLayer window is active (the hook isn't called then): feed the Key monitor and key capture,
    /// and run shortcuts, all knowing the keyboard. Keys can't be switched off here.
    /// </summary>
    void OnRawKeyInForeground(nint handle, KeyboardInfo? keyboard, int vk, bool down)
    {
        if (handle != 0) InputSender.NotePhysical(vk, down); // the hook isn't told about these keys
        if (down ? !rawHeld.Add((handle, vk)) : !rawHeld.Remove((handle, vk)) && !KeyNames.IsModifier(vk))
            return; // auto-repeat, or a release we never saw pressed

        int fnVk = CustomFnVk;
        if (fnVk != 0 && vk == fnVk)
        {
            rawFnHeld = down;
            return;
        }

        if (!down)
        {
            // A modifier pressed and released on its own during capture is the captured key itself.
            if (capture is { Key: null, Consumer: null, Other: null } lone && lone.ModifiersPressed.Contains(vk))
            {
                lone.Key = WithKeyboard(Trigger.ForKey(vk, 0), handle, keyboard);
                ArmCapture();
            }
            if (capture is { Key: { } key } c && key.Vk == vk) c.KeyUpTick = Environment.TickCount;
            return;
        }

        int mods = CurrentModifiers(rawFnHeld ? fnVk : 0) & ~KeyNames.ModFlag(vk);
        if (rawFnHeld && !KeyNames.IsModifier(vk)) mods |= KeyNames.Fn;
        var trigger = WithKeyboard(Trigger.ForKey(vk, mods), handle, keyboard);
        if (keyboard is null && TakeMediaSignal(vk, MediaWindowMs) is { } media)
        {
            // A media key Windows made from a keyboard's signal: that keyboard is the one that sent it.
            trigger.Keyboard = media.Keyboard;
            trigger.KeyboardName = media.Name;
        }

        if (MonitorEnabled)
            RaiseKey(new ObservedInput(DateTime.Now, "Key", $"Key code 0x{vk:X2}", trigger.Description, trigger.KeyboardName!, trigger));

        if (capture is { } cap)
        {
            if (KeyNames.IsModifier(vk)) cap.ModifiersPressed.Add(vk);
            else
            {
                cap.Key ??= trigger;
                ArmCapture();
            }
        }
        else if (!Paused && !Editing && Lookup(vk, mods, trigger.Keyboard) is { } m)
        {
            Fire(m, mask: false);
        }
    }

    Trigger WithKeyboard(Trigger t, nint handle, KeyboardInfo? keyboard)
    {
        t.Keyboard = keyboard?.Id;
        t.KeyboardName = keyboard is not null ? NameOf(keyboard) : handle == 0 ? FromSoftware : "Unknown keyboard";
        return t;
    }
}
