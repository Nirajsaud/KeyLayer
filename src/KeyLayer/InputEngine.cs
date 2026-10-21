using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyLayer;

internal sealed record ObservedInput(DateTime Time, string Source, string Signal, string Meaning, string Device, Trigger Trigger);

/// <summary>A keyboard Windows knows about. <see cref="Product"/> is what the device calls itself.</summary>
internal sealed record KeyboardInfo(string Id, string Product, string Connection, bool External)
{
    /// <summary>Everything built into the PC (laptop keyboard, its hotkey devices) counts as one keyboard.</summary>
    public static readonly KeyboardInfo BuiltIn = new(RawDevices.BuiltInId, "", "Built-in", false);

    public bool IsReceiver => Connection.EndsWith("receiver", StringComparison.Ordinal);
}

/// <summary>
/// Runs a dedicated input thread with a low-level keyboard hook (catches keys and can swallow them) and raw input
/// listeners (say which keyboard sent a key, and catch media/vendor signals Windows ignores). Matches what arrives
/// against the shortcut table and fires actions. Keeping this off the UI thread means a busy window never lags typing.
/// </summary>
/// <remarks>
/// Windows runs the hook before it says which keyboard a key came from, and a key-down the hook swallows is never
/// reported at all. So when a key has a shortcut limited to one keyboard:
/// media/launch keys come with a separate HID signal naming the keyboard, so they're held until it arrives;
/// other keys are held back and their release is let through, because Windows does name the keyboard for the
/// release. Either way the shortcut then runs, or the key is replayed for the other keyboards.
/// While one of KeyLayer's own windows is active, Windows skips low-level keyboard hooks altogether (because this
/// process listens to raw keyboard input), so keys are then read from raw input instead; see
/// <see cref="AppInForeground"/>.
/// </remarks>
internal sealed unsafe partial class InputEngine : IDisposable
{
    const int WM_RUN_QUEUE = Native.WM_APP + 1;
    const nuint SettleTimer = 1, CaptureTimeoutTimer = 2, DeviceTimer = 3, ResolveTimer = 4, RepeatTimer = 5;
    const int MaxRepeatMs = 30000;        // a held signal repeats its shortcut for at most this long
    const int SettleMs = 350;             // after the first signal of a capture, wait for related ones (key + HID often both arrive)
    const int CaptureReleaseMs = 4000;    // how long a capture waits for the key's release to learn its keyboard
    const uint RepeatWindowMs = 1500;     // a key-down within this time of the last one for the same key is auto-repeat
    const int MediaWindowMs = 300;        // how long a media signal can name the keyboard of a key code
    const int DeferMs = 250;              // how long a held media key waits to learn its keyboard
    const int ReleaseMs = 500;            // how long a released key waits for Windows to name its keyboard
    const int ObservationMs = 1500;       // how long a Key monitor row waits for the keyboard's name
    const int HookSilentMs = 2000;        // real key presses the hook hasn't seen for this long: it was dropped
    const int RawBufferSize = 64 * 1024;

    enum KeyAction { Pass, Block, BlockUntilRelease }

    /// <summary>What a key's release does: nothing special, decide the shortcut then, pass it, or let go of a held key.</summary>
    enum KeyRelease { Normal, Decide, Pass, Unhold }

    /// <summary>
    /// What was decided when a key went down, so repeats and the release follow suit. <see cref="Fired"/> is the
    /// shortcut it ran: run again on auto-repeat if it repeats, or the key it holds down.
    /// </summary>
    readonly record struct HeldKey(uint Time, bool Blocked, KeyRelease Release, int Mods, bool WinOrAlt, Mapping? Fired = null);

    readonly SynchronizationContext ui;
    readonly ConcurrentQueue<Action> queue = new();
    readonly ManualResetEventSlim ready = new();
    readonly Dictionary<nint, HidDevice> devices = [];
    readonly Dictionary<nint, KeyboardInfo?> keyboardHandles = [];
    readonly Dictionary<string, KeyboardInfo> externalKeyboards = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> mouseIds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, HeldKey> held = [];
    readonly List<MediaSignal> recentMedia = [];
    readonly List<Deferred> deferred = [];
    readonly List<ReleaseWait> releaseWaits = [];
    readonly List<PendingObservation> observations = [];
    readonly List<SignalRow> signalRows = [];
    readonly HashSet<(ushort Page, ushort Usage)> registered = [];
    readonly HashSet<(nint Device, int Vk)> rawHeld = [];
    Thread? thread;
    uint threadId;
    MessageWindow? window;
    nint hook;
    Native.LowLevelKeyboardProc? hookProc;
    int lastHookTick, lastHookInstallTick, hookInstalls;
    Mapping? justFired; // the shortcut the current key-down ran, if any
    nint rawBuffer, drainBuffer;
    volatile Dictionary<string, Mapping> table = [];
    volatile HashSet<string> keyboardSpecific = [];
    volatile Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
    volatile HashSet<string> excludedApps = new(StringComparer.OrdinalIgnoreCase);
    volatile bool mappingsPerApp;
    nint frontWindow;
    uint frontProcess;
    string? frontExe;
    Capture? capture;
    bool builtInPresent;
    bool fnHeld, fnUsed, rawFnHeld;
    volatile bool appInForeground;
    string? lastActiveKeyboard;
    int lastActivityTick;

    public volatile bool Paused;
    public volatile bool MonitorEnabled;

    /// <summary>Key held as a home-made Fn key (0 = off).</summary>
    public volatile int CustomFnVk;

    /// <summary>
    /// Set while one of KeyLayer's windows is active. Windows doesn't call keyboard hooks then, so keys are taken
    /// from raw input: they still name their keyboard, but can't be switched off.
    /// </summary>
    public bool AppInForeground
    {
        get => appInForeground;
        set
        {
            appInForeground = value;
            Post(() =>
            {
                rawHeld.Clear();
                rawFnHeld = false;
                // The hook misses keys released while our window is in front: don't leave the Fn key, or keys
                // held for "act as another key", stuck down.
                fnHeld = false;
                held.Remove(CustomFnVk);
                UnholdAll();
            });
        }
    }

    /// <summary>Set while the shortcut editor is open: keys typed into KeyLayer itself then don't run shortcuts.</summary>
    public volatile bool Editing;

    public string? StartError { get; private set; }

    /// <summary>External keyboards, the built-in keyboard, then receivers (mouse dongles that can send keys).</summary>
    public IReadOnlyList<KeyboardInfo> Keyboards { get; private set; } = [];
    public IReadOnlyList<string> Listening { get; private set; } = [];

    /// <summary>Raised on the UI thread for every fresh key press / HID signal while <see cref="MonitorEnabled"/>.</summary>
    public event Action<ObservedInput>? Observed;
    public event Action<Mapping>? Fired;
    public event Action<Mapping, Exception>? ActionFailed;
    public event Action? KeyboardsChanged;

    /// <summary>Raised on the UI thread with a keyboard's ID when it's typed on (at most once a second per keyboard).</summary>
    public event Action<string>? KeyboardActivity;

    public InputEngine(SynchronizationContext ui) => this.ui = ui;

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "KeyLayer input" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void SetMappings(IEnumerable<Mapping> mappings)
    {
        var next = new Dictionary<string, Mapping>();
        var specific = new HashSet<string>();
        bool perApp = false;
        foreach (var m in mappings)
        {
            if (!m.Enabled) continue;
            next[m.Trigger.MatchKey] = m;
            perApp |= m.ExcludedApps.Count > 0;
            if (m.Trigger.Kind == TriggerKind.Key && m.Trigger.Keyboard is not null)
                specific.Add(Trigger.KeyMatch(m.Trigger.Vk, m.Trigger.Modifiers));
        }
        table = next;
        keyboardSpecific = specific;
        mappingsPerApp = perApp;
    }

    /// <summary>Apps where no shortcut runs, by program file name.</summary>
    public void SetExcludedApps(IEnumerable<AppRef> apps) =>
        excludedApps = new HashSet<string>(apps.Select(a => a.Exe), StringComparer.OrdinalIgnoreCase);

    public void SetKeyboardNames(IReadOnlyDictionary<string, string> userNames) =>
        names = new Dictionary<string, string>(userNames, StringComparer.OrdinalIgnoreCase);

    public string NameOf(KeyboardInfo keyboard) =>
        names.TryGetValue(keyboard.Id, out var name) && name.Length > 0 ? name
        : !keyboard.External ? "Built-in keyboard"
        : keyboard.Product.Length > 0 ? keyboard.Product
        : $"{keyboard.Connection} keyboard";

    public string NameOf(string id, string? fallback) =>
        names.TryGetValue(id, out var name) && name.Length > 0 ? name
        : Keyboards.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase)) is { } known ? NameOf(known)
        : id == RawDevices.BuiltInId ? "Built-in keyboard"
        : fallback ?? id;

    /// <summary>
    /// Waits for the next key or signal and reports it (null on timeout) on the UI thread.
    /// While capturing, shortcuts don't fire and captured keys are swallowed.
    /// </summary>
    public void BeginCapture(Action<Trigger?> done, uint timeoutMs = 15000) => Post(() =>
    {
        capture = new Capture(t => ui.Post(_ => done(t), null));
        Native.SetTimer(window!.Handle, CaptureTimeoutTimer, timeoutMs, 0);
    });

    public void CancelCapture() => Post(() =>
    {
        capture = null;
        StopCaptureTimers();
    });

    public void RunNow(Mapping m) => Fire(m, mask: false);

    public Task<string> DiagnosticsAsync()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(BuildDiagnostics()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        if (thread is null) return;
        Native.PostThreadMessage(threadId, Native.WM_QUIT, 0, 0);
        thread.Join(2000);
        thread = null;
    }

    void Post(Action action)
    {
        queue.Enqueue(action);
        if (window is not null) Native.PostMessage(window.Handle, WM_RUN_QUEUE, 0, 0);
    }

    void Raise(ObservedInput o) => ui.Post(_ => Observed?.Invoke(o), null);

    void Fire(Mapping m, bool mask)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (mask) InputSender.Mask();
                ActionRunner.Run(m.Action);
                ui.Post(_ => Fired?.Invoke(m), null);
            }
            catch (Exception ex)
            {
                ui.Post(_ => ActionFailed?.Invoke(m, ex), null);
            }
        });
    }

    /// <summary>Gives a swallowed key its normal job back.</summary>
    static void Replay(int vk) => ThreadPool.QueueUserWorkItem(_ =>
    {
        try { InputSender.Tap(vk); } catch { }
    });

    // ------------------------------------------------------------------ input thread

    void Run()
    {
        threadId = Native.GetCurrentThreadId();
        try
        {
            rawBuffer = Marshal.AllocHGlobal(RawBufferSize);
            drainBuffer = Marshal.AllocHGlobal(RawBufferSize);
            window = new MessageWindow(this);
            hookProc = HookCallback;
            InstallHook();
            hookInstalls = 0;
            if (hook == 0) StartError = $"Couldn't install the keyboard hook (error {Marshal.GetLastWin32Error()}).";
            RefreshDevices();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch (Exception ex)
        {
            StartError = ex.Message;
        }
        finally
        {
            ready.Set();
        }

        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        // Quitting while a key is held for "act as another key": let go of it, or it stays down.
        foreach (var h in held.Values.Where(h => h.Release == KeyRelease.Unhold))
            InputSender.Release(h.Fired!.Action.ShortcutMods, h.Fired.Action.ShortcutVk);
        if (hidHold is { Holding: true } hid) InputSender.Release(hid.Mapping.Action.ShortcutMods, hid.Mapping.Action.ShortcutVk);

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (hook != 0) Native.UnhookWindowsHookEx(hook);
        window?.DestroyHandle();
        foreach (var d in devices.Values) d.Dispose();
        devices.Clear();
        Marshal.FreeHGlobal(rawBuffer);
        Marshal.FreeHGlobal(drainBuffer);
    }

    void OnTimer(nuint id)
    {
        switch (id)
        {
            case SettleTimer:
                OnSettleTick();
                break;
            case CaptureTimeoutTimer:
                FinishCapture(timedOut: true);
                break;
            case DeviceTimer:
                Native.KillTimer(window!.Handle, DeviceTimer);
                RefreshDevices();
                break;
            case ResolveTimer:
                OnResolveTimer();
                break;
            case RepeatTimer:
                OnRepeatTick();
                break;
        }
    }

    string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"KeyLayer {typeof(InputEngine).Assembly.GetName().Version} on {Environment.OSVersion}");
        sb.AppendLine($"Keyboard hook: {(hook != 0 ? "installed" : "NOT installed")}, put back {hookInstalls} times {StartError}");
        sb.AppendLine($"KeyLayer Fn key: {(CustomFnVk == 0 ? "off" : KeyNames.Vk(CustomFnVk))}");
        sb.AppendLine($"Shortcuts off in: {(excludedApps.Count == 0 ? "no apps" : string.Join(", ", excludedApps))}");
        sb.AppendLine("Keyboards:");
        foreach (var k in Keyboards) sb.AppendLine($"  {k.Id,-10} {k.Connection,-13} {NameOf(k)}");
        sb.AppendLine("Keyboard devices:");
        foreach (var d in RawDevices.List().Where(d => d.dwType == Native.RIM_TYPEKEYBOARD))
        {
            string path = RawDevices.Name(d.hDevice);
            sb.AppendLine($"  {KeyboardFor(d.hDevice)?.Id ?? "?",-10} {RawDevices.Description(path),-28} {path}");
        }
        sb.AppendLine("HID collections:");
        foreach (var d in devices.Values.OrderBy(d => d.VidPid).ThenBy(d => d.UsagePage))
        {
            sb.AppendLine($"  {d.VidPid}  {d.KeyboardId,-10} page {d.UsagePage:X4} usage {d.Usage:X4}  \"{d.Product}\"  " +
                          $"input pages [{string.Join(",", d.InputPages.Select(p => p.ToString("X4")))}]  report IDs: {d.HasReportIds}");
        }
        sb.AppendLine("Listening for: " + string.Join(", ", Listening));
        return sb.ToString();
    }

    /// <summary>Message-only window that receives raw input, timers and queued work on the input thread.</summary>
    sealed class MessageWindow : NativeWindow
    {
        readonly InputEngine engine;

        public MessageWindow(InputEngine engine)
        {
            this.engine = engine;
            CreateHandle(new CreateParams { Caption = "KeyLayer.Input", Parent = new nint(-3) /* HWND_MESSAGE */ });
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                switch (m.Msg)
                {
                    case Native.WM_INPUT:
                        engine.OnRawInput(m.LParam);
                        break;
                    case Native.WM_INPUT_DEVICE_CHANGE:
                        Native.SetTimer(Handle, DeviceTimer, 700, 0); // debounce: one plug-in fires several of these
                        break;
                    case Native.WM_TIMER:
                        engine.OnTimer((nuint)m.WParam);
                        return;
                    case WM_RUN_QUEUE:
                        while (engine.queue.TryDequeue(out var action)) action();
                        return;
                }
            }
            catch
            {
                // Keep the input thread alive whatever happens.
            }
            base.WndProc(ref m);
        }
    }
}
