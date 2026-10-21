using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyLayer;

// HID signals (media, launch, vendor keys) and matching media signals to the key presses Windows makes from them.
internal sealed unsafe partial class InputEngine
{
    void OnHidReport(HidDevice device, byte[] report)
    {
        if (!device.IsNewState(report)) return;
        if (hidHold?.Device == device) StopHidHold(); // what's pressed on that device changed (or was released)
        var signal = device.Decode(report);
        if (signal is null) return; // released

        string keyboardName = NameOf(device.KeyboardId, device.DisplayName);
        var trigger = new Trigger
        {
            Kind = TriggerKind.Hid,
            HidKey = signal.Key,
            UsagePage = device.UsagePage,
            Description = signal.Meaning,
            Signal = signal.Code,
            Device = device.DisplayName,
            Keyboard = device.KeyboardId,
            KeyboardName = keyboardName,
        };

        var signalRow = new ObservedInput(DateTime.Now, "Signal", signal.Code, signal.Meaning, keyboardName, trigger);
        bool shown = !MonitorEnabled;
        foreach (var (page, usage) in signal.Usages)
        {
            if (page != 0x0C || !KeyNames.ConsumerToVk.TryGetValue(usage, out int vk)) continue;
            // Windows also turns this signal into a key press: the Key monitor shows the two as one row.
            if (OnMediaSignal(device.KeyboardId, keyboardName, vk, shown ? null : signalRow))
            {
                shown = true;
            }
            else if (!shown)
            {
                signalRows.Add(new SignalRow(vk, Environment.TickCount, signalRow));
                StartResolveTimer();
                shown = true;
            }
        }
        if (!shown) Raise(signalRow);

        if (capture is { } cap)
        {
            if (device.UsagePage == 0x0C) cap.Consumer ??= trigger;
            else cap.Other ??= trigger;
            ArmCapture();
        }
        else if (!Paused && !(Editing && appInForeground) && table.TryGetValue(trigger.MatchKey, out var m) && RunsInFront(m))
        {
            // Holding and repeating need the signal's release, which only button-style signals (with usages) report.
            bool canHold = signal.Usages.Count > 0;
            if (canHold && m.Action.Type == ActionType.HoldKey)
            {
                Hold(m);
                hidHold = new HidHold(device, m, Holding: true, Environment.TickCount);
            }
            else
            {
                Fire(m, mask: false);
                if (canHold && m.RepeatWhileHeld)
                {
                    hidHold = new HidHold(device, m, Holding: false, Environment.TickCount);
                    Native.SetTimer(window!.Handle, RepeatTimer, RepeatDelayMs, 0);
                }
            }
        }
    }

    /// <summary>
    /// A media signal names the keyboard behind the key code Windows makes from it. The key code may already be
    /// waiting (held key, unshown monitor row) or may arrive just after. Returns true when it was waiting as a
    /// monitor row, which then also carries <paramref name="signalRow"/>.
    /// </summary>
    bool OnMediaSignal(string keyboard, string name, int vk, ObservedInput? signalRow)
    {
        bool used = false, merged = false;
        if (deferred.FirstOrDefault(d => d.Vk == vk) is { } waiting)
        {
            ResolveDeferred(waiting, keyboard);
            used = true;
        }
        if (observations.FirstOrDefault(o => o.Vk == vk) is { } row)
        {
            observations.Remove(row);
            row.Trigger.Keyboard = keyboard;
            row.Trigger.KeyboardName = name;
            var keyRow = row.ToObserved(name);
            Raise(signalRow is null ? keyRow : Merge(keyRow, signalRow));
            used = true;
            merged = signalRow is not null;
        }
        if (!used)
        {
            int now = Environment.TickCount;
            recentMedia.RemoveAll(r => now - r.Tick > 1000);
            recentMedia.Add(new MediaSignal(keyboard, name, vk, now));
        }
        return merged;
    }

    MediaSignal? TakeMediaSignal(int vk, int windowMs)
    {
        if (!KeyNames.HasMediaSignal(vk)) return null;
        int now = Environment.TickCount;
        for (int i = recentMedia.Count - 1; i >= 0; i--)
        {
            var r = recentMedia[i];
            if (r.Vk != vk || now - r.Tick > windowMs) continue;
            recentMedia.RemoveAt(i);
            return r;
        }
        return null;
    }

    sealed record MediaSignal(string Keyboard, string Name, int Vk, int Tick);
}
