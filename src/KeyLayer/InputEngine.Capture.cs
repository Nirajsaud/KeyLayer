using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyLayer;

// Key capture for the shortcut editor: waiting for a key and learning which keyboard it came from.
internal sealed unsafe partial class InputEngine
{
    // ------------------------------------------------------------------ capture & timers

    void ArmCapture()
    {
        if (capture is null || capture.ArmedTick != 0) return;
        capture.ArmedTick = Environment.TickCount;
        Native.SetTimer(window!.Handle, SettleTimer, 100, 0);
    }

    void OnSettleTick()
    {
        if (capture is not { } c) return;
        int now = Environment.TickCount;
        if (now - c.ArmedTick < SettleMs) return;

        // For an ordinary key, wait for its release: that's when Windows says which keyboard it came from.
        bool needsRelease = c.Key is { Keyboard: null } key && !KeyNames.HasMediaSignal(key.Vk);
        bool releaseSettled = c.KeyUpTick != 0 && now - c.KeyUpTick > 300;
        if (needsRelease && !releaseSettled && now - c.ArmedTick < CaptureReleaseMs) return;

        FinishCapture(timedOut: false);
    }

    void FinishCapture(bool timedOut)
    {
        var c = capture;
        capture = null;
        StopCaptureTimers();
        if (c is null) return;
        if (timedOut)
        {
            c.Done(null);
            return;
        }

        // Prefer a key code (it can be swallowed), then a media/consumer signal, then anything else.
        var chosen = c.Key ?? c.Consumer ?? c.Other;
        if (chosen == c.Key && c.Key is { Keyboard: null } key && TakeMediaSignal(key.Vk, CaptureReleaseMs) is { } media)
        {
            key.Keyboard = media.Keyboard;
            key.KeyboardName = media.Name;
        }
        c.Done(chosen);
    }

    void StopCaptureTimers()
    {
        Native.KillTimer(window!.Handle, SettleTimer);
        Native.KillTimer(window.Handle, CaptureTimeoutTimer);
    }

    sealed class Capture(Action<Trigger?> done)
    {
        public Action<Trigger?> Done { get; } = done;
        public Trigger? Key { get; set; }
        public Trigger? Consumer { get; set; }
        public Trigger? Other { get; set; }
        public HashSet<int> ModifiersPressed { get; } = [];
        public int ArmedTick { get; set; }
        public int KeyUpTick { get; set; }
    }
}
