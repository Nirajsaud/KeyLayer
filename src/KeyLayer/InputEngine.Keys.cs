using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyLayer;

// The keyboard hook: deciding what happens to every key press, and keys that wait to learn their keyboard.
internal sealed unsafe partial class InputEngine
{
    /// <summary>Installs the keyboard hook, replacing the current one (which Windows may have dropped already).</summary>
    void InstallHook()
    {
        if (hook != 0) Native.UnhookWindowsHookEx(hook);
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc!, Native.GetModuleHandle(null), 0);
        lastHookInstallTick = Environment.TickCount;
        hookInstalls++;
    }

    // After sleep or unlocking, put the hook back to be safe (these run on a system thread).
    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Post(InstallHook);
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) Post(InstallHook);
    }

    nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            lastHookTick = Environment.TickCount;
            try
            {
                var k = *(Native.KBDLLHOOKSTRUCT*)lParam;
                if (k.dwExtraInfo != InputSender.Marker)
                {
                    bool down = wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN;
                    InputSender.NotePhysical((int)k.vkCode, down);
                    if (down ? OnKeyDown(k) : OnKeyUp(k)) return 1;
                }
            }
            catch
            {
                // Never let an exception escape into the system hook chain.
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    /// <summary>Returns true to swallow the key-down.</summary>
    bool OnKeyDown(in Native.KBDLLHOOKSTRUCT k)
    {
        int vk = (int)k.vkCode;
        int fnVk = CustomFnVk;

        if (held.TryGetValue(vk, out var previous) && unchecked(k.time - previous.Time) < RepeatWindowMs)
        {
            held[vk] = previous with { Time = k.time }; // auto-repeat: same decision as the first press
            if (previous.Fired is { } m)
            {
                if (previous.Release == KeyRelease.Unhold) Post(() => InputSender.Repeat(m.Action.ShortcutVk));
                else if (m.RepeatWhileHeld && !Paused) Fire(m, mask: false);
            }
            return previous.Blocked;
        }

        DrainRawInput(); // media signals that name the keyboard may already be waiting

        // While paused, or while an app with shortcuts off is in front, the Fn key is just a key (it can be held there).
        if (vk == fnVk && fnVk != 0 && !Paused && !OffIn(AppInFront()))
        {
            // Home-made Fn key. Modifiers pass through so e.g. Right Ctrl + C still copies.
            fnHeld = true;
            fnUsed = false;
            bool swallow = !KeyNames.IsModifier(vk);
            held[vk] = new HeldKey(k.time, swallow, KeyRelease.Normal, 0, false);
            return swallow;
        }

        int mods = CurrentModifiers(fnHeld ? fnVk : 0) & ~KeyNames.ModFlag(vk);
        bool winOrAltDown = (CurrentModifiers(0) & (KeyNames.Alt | KeyNames.Win)) != 0;
        if (fnHeld && !KeyNames.IsModifier(vk))
        {
            mods |= KeyNames.Fn;
            fnUsed = true;
        }

        var trigger = Trigger.ForKey(vk, mods);
        if (TakeMediaSignal(vk, MediaWindowMs) is { } media)
        {
            trigger.Keyboard = media.Keyboard;
            trigger.KeyboardName = media.Name;
        }

        if (MonitorEnabled) Observe(k, trigger);

        KeyAction action;
        KeyRelease release = KeyRelease.Normal;
        justFired = null;
        if (capture is { } cap)
        {
            if (KeyNames.IsModifier(vk))
            {
                cap.ModifiersPressed.Add(vk);
                action = KeyAction.Pass;
            }
            else
            {
                cap.Key ??= trigger;
                ArmCapture();
                action = KeyAction.Block;
                release = KeyRelease.Pass; // its release tells us which keyboard it came from
            }
        }
        else
        {
            action = Paused ? KeyAction.Pass : Decide(vk, mods, trigger.Keyboard, winOrAltDown);
            if (action == KeyAction.BlockUntilRelease) release = KeyRelease.Decide;
            else if (justFired is { Action.Type: ActionType.HoldKey }) release = KeyRelease.Unhold;
        }

        bool block = action != KeyAction.Pass;
        held[vk] = new HeldKey(k.time, block, release, mods, winOrAltDown, justFired);
        return block;
    }

    /// <summary>Returns true to swallow the key-up.</summary>
    bool OnKeyUp(in Native.KBDLLHOOKSTRUCT k)
    {
        int vk = (int)k.vkCode;

        // A modifier pressed and released on its own during capture is the captured key itself.
        if (capture is { Key: null, Consumer: null, Other: null } c && c.ModifiersPressed.Contains(vk) && vk != CustomFnVk)
        {
            c.Key = Trigger.ForKey(vk, 0);
            ArmCapture();
        }

        bool found = held.Remove(vk, out var h);
        if (vk == CustomFnVk && fnHeld)
        {
            fnHeld = false;
            if (!fnUsed && found && h.Blocked) Replay(vk); // tapped on its own: e.g. Caps Lock still toggles
            return found && h.Blocked;
        }
        if (!found) return false;

        switch (h.Release)
        {
            case KeyRelease.Decide:
                // Let the release through: Windows will say which keyboard it came from.
                releaseWaits.Add(new ReleaseWait(vk, h.Mods, h.WinOrAlt, Environment.TickCount));
                StartResolveTimer();
                return false;
            case KeyRelease.Pass:
                if (capture is { Key: { } key } cap && key.Vk == vk) cap.KeyUpTick = Environment.TickCount;
                return false;
            case KeyRelease.Unhold:
                Unhold(h.Fired!);
                return h.Blocked;
            default:
                return h.Blocked;
        }
    }

    /// <summary>Picks and fires the shortcut for a key press.</summary>
    KeyAction Decide(int vk, int mods, string? keyboard, bool winOrAltDown)
    {
        string? app = AppInFront();
        if (OffIn(app)) return KeyAction.Pass;
        var t = table;
        var anyKeyboard = RunsIn(t.GetValueOrDefault(Trigger.KeyMatch(vk, mods)), app);

        if (keyboard is not null)
            return FireAndBlock(RunsIn(t.GetValueOrDefault(Trigger.KeyMatch(vk, mods, keyboard)), app) ?? anyKeyboard, winOrAltDown);

        if (!keyboardSpecific.Contains(Trigger.KeyMatch(vk, mods)))
            return FireAndBlock(anyKeyboard, winOrAltDown);

        // None of this key's one-keyboard shortcuts runs in the app in front, so the keyboard doesn't matter.
        if (app is not null && !t.Values.Any(m => m.Trigger is { Kind: TriggerKind.Key, Keyboard: not null } k
                                                  && k.Vk == vk && k.Modifiers == mods && RunsIn(m, app) is not null))
            return FireAndBlock(anyKeyboard, winOrAltDown);

        // A shortcut on this key is limited to one keyboard, but Windows hasn't said which keyboard this is yet.
        if (KeyNames.HasMediaSignal(vk))
        {
            // Media/launch keys come with a HID signal naming the keyboard: hold the key until it arrives.
            deferred.Add(new Deferred(vk, mods, Environment.TickCount));
            StartResolveTimer();
            return KeyAction.Block;
        }

        // Other keys: hold the key back and decide when it's released (the release names the keyboard).
        return KeyAction.BlockUntilRelease;
    }

    KeyAction FireAndBlock(Mapping? m, bool winOrAltDown)
    {
        if (m is null) return KeyAction.Pass;
        justFired = m;
        if (m.Action.Type == ActionType.HoldKey) Hold(m); // and let go when the key is released
        // Swallowing a key while Win/Alt is held would make Windows open Start / a menu bar on release.
        else Fire(m, mask: m.BlockOriginal && winOrAltDown);
        return m.BlockOriginal ? KeyAction.Block : KeyAction.Pass;
    }

    Mapping? Lookup(int vk, int mods, string? keyboard)
    {
        string? app = AppInFront();
        if (OffIn(app)) return null;
        var t = table;
        return (keyboard is null ? null : RunsIn(t.GetValueOrDefault(Trigger.KeyMatch(vk, mods, keyboard)), app))
               ?? RunsIn(t.GetValueOrDefault(Trigger.KeyMatch(vk, mods)), app);
    }

    void ResolveDeferred(Deferred d, string? keyboard)
    {
        deferred.Remove(d);
        var m = Lookup(d.Vk, d.Mods, keyboard);
        bool stillHeld = held.TryGetValue(d.Vk, out var h);
        if (m is { Action.Type: ActionType.HoldKey } && stillHeld)
        {
            Hold(m);
            held[d.Vk] = h with { Release = KeyRelease.Unhold, Fired = m };
        }
        else if (m is not null)
        {
            Fire(m, mask: false);
            if (stillHeld) held[d.Vk] = h with { Fired = m }; // so holding the key can repeat it
        }
        if (m is null || !m.BlockOriginal) Replay(d.Vk);
    }

    void ResolveRelease(ReleaseWait w, string? keyboard)
    {
        releaseWaits.Remove(w);
        var m = Lookup(w.Vk, w.Mods, keyboard);
        if (m is not null) Fire(m, mask: m.BlockOriginal && w.WinOrAlt);
        if (m is null || !m.BlockOriginal) Replay(w.Vk); // another keyboard: the key does its normal job
    }

    static int CurrentModifiers(int fnVk)
    {
        static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        int mods = 0;
        // When the home-made Fn key is itself a modifier, only the other side counts as that modifier.
        if (fnVk == 0xA3 ? Down(0xA2) : Down(0x11)) mods |= KeyNames.Ctrl;
        if (fnVk == 0xA1 ? Down(0xA0) : Down(0x10)) mods |= KeyNames.Shift;
        if (fnVk == 0xA5 ? Down(0xA4) : Down(0x12)) mods |= KeyNames.Alt;
        if (Down(0x5B) || Down(0x5C)) mods |= KeyNames.Win;
        return mods;
    }

    void StartResolveTimer() => Native.SetTimer(window!.Handle, ResolveTimer, 40, 0);

    void OnResolveTimer()
    {
        int now = Environment.TickCount;
        foreach (var d in deferred.Where(d => now - d.Tick >= DeferMs).ToList()) ResolveDeferred(d, null);
        foreach (var w in releaseWaits.Where(w => now - w.Tick >= ReleaseMs).ToList()) ResolveRelease(w, null);
        foreach (var row in observations.Where(o => now - o.Tick >= ObservationMs).ToList())
        {
            observations.Remove(row);
            RaiseKey(row.ToObserved("Unknown keyboard"));
        }
        // A media signal whose key press never showed up: show the signal on its own.
        foreach (var pending in signalRows.Where(s => now - s.Tick >= MediaWindowMs).ToList())
        {
            signalRows.Remove(pending);
            Raise(pending.Row);
        }
        if (deferred.Count == 0 && releaseWaits.Count == 0 && observations.Count == 0 && signalRows.Count == 0)
            Native.KillTimer(window!.Handle, ResolveTimer);
    }

    /// <summary>A swallowed media/launch key waiting to learn which keyboard sent it.</summary>
    sealed class Deferred(int vk, int mods, int tick)
    {
        public int Vk { get; } = vk;
        public int Mods { get; } = mods;
        public int Tick { get; } = tick;
    }

    /// <summary>A held-back key, now released, waiting for Windows to name the keyboard of the release.</summary>
    sealed class ReleaseWait(int vk, int mods, bool winOrAlt, int tick)
    {
        public int Vk { get; } = vk;
        public int Mods { get; } = mods;
        public bool WinOrAlt { get; } = winOrAlt;
        public int Tick { get; } = tick;
    }
}
