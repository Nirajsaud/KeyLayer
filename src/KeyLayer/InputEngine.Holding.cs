using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyLayer;

// "Act as another key" and "repeat while held": keys held down and actions run again while a key is held.
internal sealed unsafe partial class InputEngine
{
    // ---- "Act as another key": the key goes down with the trigger and up with it, in order, on this thread.

    void Hold(Mapping m) => Post(() => InputSender.Press(m.Action.ShortcutMods, m.Action.ShortcutVk));

    void Unhold(Mapping m) => Post(() => InputSender.Release(m.Action.ShortcutMods, m.Action.ShortcutVk));

    /// <summary>
    /// Lets go of every key we're holding for a trigger. Used when the trigger's release may never reach the hook
    /// (KeyLayer's own window came to the front), so no key stays stuck down.
    /// </summary>
    void UnholdAll()
    {
        foreach (var (vk, h) in held.Where(e => e.Value.Release == KeyRelease.Unhold).ToList())
        {
            held.Remove(vk);
            Unhold(h.Fired!);
        }
        StopHidHold();
    }

    /// <summary>A HID signal's shortcut that's held (acting as a key) or repeating until the signal is released.</summary>
    sealed record HidHold(HidDevice Device, Mapping Mapping, bool Holding, int Tick);

    HidHold? hidHold;

    void StopHidHold()
    {
        if (hidHold is not { } h) return;
        hidHold = null;
        Native.KillTimer(window!.Handle, RepeatTimer);
        if (h.Holding) Unhold(h.Mapping);
    }

    void OnRepeatTick()
    {
        // A release that never comes shouldn't repeat forever.
        if (hidHold is not { Holding: false } h || Paused || Environment.TickCount - h.Tick > MaxRepeatMs)
        {
            if (hidHold is { Holding: false }) hidHold = null;
            Native.KillTimer(window!.Handle, RepeatTimer);
            return;
        }
        Fire(h.Mapping, mask: false);
        Native.SetTimer(window!.Handle, RepeatTimer, RepeatIntervalMs, 0);
    }

    // The keyboard repeat delay and rate from Windows' keyboard settings.
    static uint RepeatDelayMs => (uint)((SystemInformation.KeyboardDelay + 1) * 250);
    static uint RepeatIntervalMs => (uint)(1000 / (2.5 + SystemInformation.KeyboardSpeed * 27.5 / 31));
}
