using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyLayer;

// The Test keys page: one row per key press or signal, named after the keyboard it came from.
internal sealed unsafe partial class InputEngine
{
    void Observe(in Native.KBDLLHOOKSTRUCT k, Trigger trigger)
    {
        bool extended = (k.flags & Native.LLKHF_EXTENDED) != 0;
        var row = new PendingObservation(KeyNames.Normalize((int)k.vkCode), Environment.TickCount, DateTime.Now,
            $"Key code 0x{k.vkCode:X2}  (scan {k.scanCode:X2}{(extended ? ", ext" : "")})", trigger);
        if (trigger.KeyboardName is { } named)
        {
            RaiseKey(row.ToObserved(named));
            return;
        }
        observations.Add(row); // shown once Windows says which keyboard it was
        StartResolveTimer();
    }

    /// <summary>What the Key monitor shows for keys that come from software rather than a keyboard.</summary>
    const string FromSoftware = "Windows or another app";

    /// <summary>Shows a key row; a media key's row also takes in the signal it was made from.</summary>
    void RaiseKey(ObservedInput row)
    {
        int vk = row.Trigger.Vk;
        if (KeyNames.HasMediaSignal(vk) && signalRows.FirstOrDefault(s => s.Vk == vk) is { } pending)
        {
            signalRows.Remove(pending);
            row.Trigger.Keyboard ??= pending.Row.Trigger.Keyboard;
            row.Trigger.KeyboardName = pending.Row.Trigger.KeyboardName;
            row = Merge(row, pending.Row);
        }
        Raise(row);
    }

    static ObservedInput Merge(ObservedInput keyRow, ObservedInput signalRow) =>
        keyRow with { Signal = $"Media key  ·  {signalRow.Signal}", Device = signalRow.Device };

    /// <summary>A media signal's Key monitor row, waiting to be merged with the key press Windows makes from it.</summary>
    sealed record SignalRow(int Vk, int Tick, ObservedInput Row);

    sealed class PendingObservation(int vk, int tick, DateTime time, string signal, Trigger trigger)
    {
        public int Vk { get; } = vk;
        public int Tick { get; } = tick;
        public Trigger Trigger { get; } = trigger;
        public ObservedInput ToObserved(string device) => new(time, "Key", signal, Trigger.Description, device, Trigger);
    }
}
