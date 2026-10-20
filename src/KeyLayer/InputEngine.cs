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
internal sealed unsafe class InputEngine : IDisposable
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

    /// <summary>
    /// Program file of the window in front (e.g. "Photoshop.exe") when a shortcut depends on it, else null.
    /// Asked on every key press, so the answer is kept until another window comes to the front.
    /// </summary>
    string? AppInFront()
    {
        if (excludedApps.Count == 0 && !mappingsPerApp) return null;
        nint hwnd = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(hwnd, out uint processId);
        // A store app still starting up shows only its frame for a moment: look again until the app itself is there.
        if (hwnd != frontWindow || processId != frontProcess || Apps.IsFrameHost(frontExe))
        {
            frontWindow = hwnd;
            frontProcess = processId;
            frontExe = Apps.ExePathOf(hwnd) is { } path ? Path.GetFileName(path) : null;
        }
        return frontExe;
    }

    /// <summary>True when every shortcut is off in this app.</summary>
    bool OffIn(string? app) => app is not null && excludedApps.Contains(app);

    /// <summary>The shortcut, unless it's switched off in this app.</summary>
    static Mapping? RunsIn(Mapping? m, string? app) =>
        m is null || app is null || !m.ExcludedApps.Exists(a => a.Is(app)) ? m : null;

    bool RunsInFront(Mapping m)
    {
        string? app = AppInFront();
        return !OffIn(app) && RunsIn(m, app) is not null;
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

    /// <summary>What the Key monitor shows for keys that come from software rather than a keyboard.</summary>
    const string FromSoftware = "Windows or another app";

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

    // ------------------------------------------------------------------ devices

    HidDevice? GetDevice(nint handle)
    {
        if (devices.TryGetValue(handle, out var device)) return device;
        device = HidDevice.Open(handle);
        if (device is not null) devices[handle] = device;
        return device;
    }

    KeyboardInfo? KeyboardFor(nint handle)
    {
        if (handle == 0) return null;
        if (keyboardHandles.TryGetValue(handle, out var known)) return known;

        KeyboardInfo? keyboard = null;
        string path = RawDevices.Name(handle);
        if (path.Length > 0)
        {
            var (id, connection, external) = RawDevices.KeyboardIdentity(path);
            if (!external)
            {
                keyboard = KeyboardInfo.BuiltIn;
            }
            else
            {
                string product = RawDevices.Product(path);
                if (product.Length == 0) product = RawDevices.Description(path);
                // A dongle that is also a mouse: usually a mouse (or combo) receiver.
                if (mouseIds.Contains(id)) connection += " receiver";
                keyboard = new KeyboardInfo(id, product, connection, true);
            }
        }
        keyboardHandles[handle] = keyboard;
        return keyboard;
    }

    void NoteActivity(KeyboardInfo keyboard)
    {
        bool added = keyboard.External ? externalKeyboards.TryAdd(keyboard.Id, keyboard) : !builtInPresent;
        builtInPresent |= !keyboard.External;
        if (added) PublishKeyboards();

        int now = Environment.TickCount;
        if (keyboard.Id == lastActiveKeyboard && now - lastActivityTick < 1000) return;
        lastActiveKeyboard = keyboard.Id;
        lastActivityTick = now;
        string id = keyboard.Id;
        ui.Post(_ => KeyboardActivity?.Invoke(id), null);
    }

    void PublishKeyboards()
    {
        var external = externalKeyboards.Values.OrderBy(k => k.IsReceiver).ThenBy(k => k.Product).ToList();
        IReadOnlyList<KeyboardInfo> next =
        [
            .. external.Where(k => !k.IsReceiver),
            .. (builtInPresent ? new[] { KeyboardInfo.BuiltIn } : Array.Empty<KeyboardInfo>()),
            .. external.Where(k => k.IsReceiver),
        ];
        if (next.SequenceEqual(Keyboards)) return;
        Keyboards = next;
        ui.Post(_ => KeyboardsChanged?.Invoke(), null);
    }

    void RefreshDevices()
    {
        var list = RawDevices.List();
        var present = list.Select(d => d.hDevice).ToHashSet();
        foreach (var gone in devices.Keys.Where(h => !present.Contains(h)).ToList())
        {
            devices[gone].Dispose();
            devices.Remove(gone);
        }

        mouseIds.Clear();
        foreach (var d in list.Where(d => d.dwType == Native.RIM_TYPEMOUSE))
            if (RawDevices.KeyboardIdentity(RawDevices.Name(d.hDevice)) is { External: true } mouse)
                mouseIds.Add(mouse.Id);

        keyboardHandles.Clear(); // handles can be reused after a device is unplugged
        externalKeyboards.Clear();
        builtInPresent = false;
        foreach (var d in list.Where(d => d.dwType == Native.RIM_TYPEKEYBOARD))
        {
            if (KeyboardFor(d.hDevice) is not { } keyboard) continue;
            if (keyboard.External) externalKeyboards.TryAdd(keyboard.Id, keyboard);
            else builtInPresent = true;
        }

        // Keyboards (to learn which one sent a key), media/consumer keys and system power keys from any device,
        // plus the vendor channels of external keyboards.
        var wanted = new List<(ushort Page, ushort Usage)> { (0x01, 0x06), (0x0C, 0x01), (0x01, 0x80) };
        foreach (var d in list.Where(d => d.dwType == Native.RIM_TYPEHID))
        {
            var device = GetDevice(d.hDevice);
            if (device is { UsagePage: >= 0xFF00 } && externalKeyboards.ContainsKey(device.KeyboardId) && !wanted.Contains((device.UsagePage, device.Usage)))
                wanted.Add((device.UsagePage, device.Usage));
        }

        // Register each usage once: every registration makes Windows announce all present devices again,
        // and those announcements trigger this refresh.
        foreach (var (page, usage) in wanted.Where(w => !registered.Contains(w)))
        {
            Native.RAWINPUTDEVICE[] registration =
            [
                new() { usUsagePage = page, usUsage = usage, dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY, hwndTarget = window!.Handle },
            ];
            if (Native.RegisterRawInputDevices(registration, 1, (uint)sizeof(Native.RAWINPUTDEVICE)))
                registered.Add((page, usage));
        }

        Listening = [.. registered.Select(r => $"{r.Page:X4}/{r.Usage:X4}")];
        PublishKeyboards();
    }

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

    sealed record MediaSignal(string Keyboard, string Name, int Vk, int Tick);

    /// <summary>A media signal's Key monitor row, waiting to be merged with the key press Windows makes from it.</summary>
    sealed record SignalRow(int Vk, int Tick, ObservedInput Row);

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

    sealed class PendingObservation(int vk, int tick, DateTime time, string signal, Trigger trigger)
    {
        public int Vk { get; } = vk;
        public int Tick { get; } = tick;
        public Trigger Trigger { get; } = trigger;
        public ObservedInput ToObserved(string device) => new(time, "Key", signal, Trigger.Description, device, Trigger);
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
