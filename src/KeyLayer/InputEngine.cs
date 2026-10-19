using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyLayer;

internal sealed record ObservedInput(DateTime Time, string Source, string Signal, string Meaning, string Device, Trigger Trigger);

/// <summary>A keyboard Windows knows about. <see cref="Product"/> is what the device calls itself.</summary>
internal sealed record KeyboardInfo(string Id, string Product, string Connection, bool External);

/// <summary>
/// Runs a dedicated input thread with a low-level keyboard hook (catches keys and can swallow them) and raw input
/// listeners (say which keyboard sent a key, and catch media/vendor signals Windows ignores). Matches what arrives
/// against the shortcut table and fires actions. Keeping this off the UI thread means a busy window never lags typing.
/// </summary>
/// <remarks>
/// Windows runs the hook before it says which keyboard a key came from, and a key the hook swallows is never
/// reported at all. So a shortcut limited to one keyboard works like this:
/// media/launch keys come with a separate HID signal naming the keyboard, so they're held until it arrives and then
/// either run the shortcut or are let through; ordinary keys run the shortcut once Windows names the keyboard, but
/// can't be switched off for just one keyboard.
/// </remarks>
internal sealed unsafe class InputEngine : IDisposable
{
    const int WM_RUN_QUEUE = Native.WM_APP + 1;
    const nuint SettleTimer = 1, CaptureTimeoutTimer = 2, DeviceTimer = 3, ResolveTimer = 4;
    const uint SettleMs = 350;            // after the first signal of a capture, wait for related ones (key + HID often both arrive)
    const uint RepeatWindowMs = 1500;     // a key-down within this time of the last one for the same key is auto-repeat
    const int MediaWindowMs = 300;        // how long a media signal can name the keyboard of a key code
    const int DeferMs = 250;              // how long a held media key waits to learn its keyboard
    const int CheckMs = 500;              // how long an ordinary key waits for Windows to name its keyboard
    const int ObservationMs = 300;        // how long a Key monitor row waits for the keyboard's name
    const int RawBufferSize = 64 * 1024;

    readonly SynchronizationContext ui;
    readonly ConcurrentQueue<Action> queue = new();
    readonly ManualResetEventSlim ready = new();
    readonly Dictionary<nint, HidDevice> devices = [];
    readonly Dictionary<nint, KeyboardInfo?> keyboardHandles = [];
    readonly Dictionary<string, KeyboardInfo> externalKeyboards = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, KeyboardInfo> usedBuiltIns = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, (uint Time, bool Blocked)> held = [];
    readonly List<MediaSignal> recentMedia = [];
    readonly List<Deferred> deferred = [];
    readonly List<PendingCheck> checks = [];
    readonly List<PendingObservation> observations = [];
    readonly HashSet<(ushort Page, ushort Usage)> registered = [];
    Thread? thread;
    uint threadId;
    MessageWindow? window;
    nint hook;
    Native.LowLevelKeyboardProc? hookProc;
    nint rawBuffer, drainBuffer;
    volatile Dictionary<string, Mapping> table = [];
    volatile HashSet<string> keyboardSpecific = [];
    volatile Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
    Capture? capture;
    bool fnHeld, fnUsed;
    string? lastActiveKeyboard;
    int lastActivityTick;

    public volatile bool Paused;
    public volatile bool MonitorEnabled;

    /// <summary>Key held as a home-made Fn key (0 = off).</summary>
    public volatile int CustomFnVk;

    public string? StartError { get; private set; }

    /// <summary>External keyboards, plus built-in ones once they've been typed on.</summary>
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
        foreach (var m in mappings)
        {
            if (!m.Enabled) continue;
            next[m.Trigger.MatchKey] = m;
            if (m.Trigger.Kind == TriggerKind.Key && m.Trigger.Keyboard is not null)
                specific.Add(Trigger.KeyMatch(m.Trigger.Vk, m.Trigger.Modifiers));
        }
        table = next;
        keyboardSpecific = specific;
    }

    public void SetKeyboardNames(IReadOnlyDictionary<string, string> userNames) =>
        names = new Dictionary<string, string>(userNames, StringComparer.OrdinalIgnoreCase);

    public string NameOf(KeyboardInfo keyboard) =>
        names.TryGetValue(keyboard.Id, out var name) && name.Length > 0 ? name
        : keyboard.External ? (keyboard.Product.Length > 0 ? keyboard.Product : $"{keyboard.Connection} keyboard")
        : keyboard.Product.Length > 0 ? $"Built-in keyboard ({keyboard.Product})" : "Built-in keyboard";

    public string NameOf(string id, string? fallback) =>
        names.TryGetValue(id, out var name) && name.Length > 0 ? name
        : Keyboards.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase)) is { } known ? NameOf(known)
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
            hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(null), 0);
            if (hook == 0) StartError = $"Couldn't install the keyboard hook (error {Marshal.GetLastWin32Error()}).";
            RefreshDevices();
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

        if (hook != 0) Native.UnhookWindowsHookEx(hook);
        window?.DestroyHandle();
        foreach (var d in devices.Values) d.Dispose();
        devices.Clear();
        Marshal.FreeHGlobal(rawBuffer);
        Marshal.FreeHGlobal(drainBuffer);
    }

    nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var k = *(Native.KBDLLHOOKSTRUCT*)lParam;
                if (k.dwExtraInfo != InputSender.Marker)
                {
                    bool down = wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN;
                    if (OnKey(k, down)) return 1;
                }
            }
            catch
            {
                // Never let an exception escape into the system hook chain.
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    /// <summary>Returns true to swallow the key event.</summary>
    bool OnKey(in Native.KBDLLHOOKSTRUCT k, bool down)
    {
        int vk = (int)k.vkCode;
        int fnVk = CustomFnVk;

        if (!down)
        {
            // A modifier pressed and released on its own during capture is the captured key itself.
            if (capture is { Key: null, Consumer: null, Other: null } c && c.ModifiersPressed.Contains(vk) && vk != fnVk)
            {
                c.Key = Trigger.ForKey(vk, 0);
                ArmCapture();
            }
            bool wasBlocked = held.Remove(vk, out var h) && h.Blocked;
            if (vk == fnVk && fnHeld)
            {
                fnHeld = false;
                if (!fnUsed && wasBlocked) Replay(vk); // tapped on its own: e.g. Caps Lock still toggles
            }
            return wasBlocked;
        }

        if (held.TryGetValue(vk, out var previous) && unchecked(k.time - previous.Time) < RepeatWindowMs)
        {
            held[vk] = (k.time, previous.Blocked); // auto-repeat: same decision as the first press
            return previous.Blocked;
        }

        DrainRawInput(); // media signals that name the keyboard may already be waiting

        if (vk == fnVk && fnVk != 0)
        {
            // Home-made Fn key. Modifiers pass through so e.g. Right Ctrl + C still copies.
            fnHeld = true;
            fnUsed = false;
            bool swallow = !KeyNames.IsModifier(vk);
            held[vk] = (k.time, swallow);
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

        bool block = false;
        if (capture is { } cap)
        {
            if (KeyNames.IsModifier(vk))
            {
                cap.ModifiersPressed.Add(vk);
            }
            else
            {
                cap.Key ??= trigger;
                ArmCapture();
                block = true;
            }
        }
        else if (!Paused)
        {
            block = Decide(vk, mods, trigger.Keyboard, winOrAltDown);
        }

        held[vk] = (k.time, block);
        return block;
    }

    /// <summary>Picks and fires the shortcut for a key press. Returns true to swallow the key.</summary>
    bool Decide(int vk, int mods, string? keyboard, bool winOrAltDown)
    {
        var t = table;
        var anyKeyboard = t.GetValueOrDefault(Trigger.KeyMatch(vk, mods));

        if (keyboard is not null)
            return FireAndBlock(t.GetValueOrDefault(Trigger.KeyMatch(vk, mods, keyboard)) ?? anyKeyboard, winOrAltDown);

        if (!keyboardSpecific.Contains(Trigger.KeyMatch(vk, mods)))
            return FireAndBlock(anyKeyboard, winOrAltDown);

        // A shortcut on this key is limited to one keyboard, but Windows hasn't said which keyboard this is yet.
        int now = Environment.TickCount;
        if (KeyNames.HasMediaSignal(vk))
        {
            // Media/launch keys come with a HID signal naming the keyboard: hold the key until it arrives.
            deferred.Add(new Deferred(vk, mods, now));
            StartResolveTimer();
            return true;
        }

        // Ordinary keys are only named after they've been delivered, so they can't be switched off per keyboard.
        if (anyKeyboard is { BlockOriginal: true }) return FireAndBlock(anyKeyboard, winOrAltDown);
        checks.Add(new PendingCheck(vk, mods, now));
        StartResolveTimer();
        return false;
    }

    bool FireAndBlock(Mapping? m, bool winOrAltDown)
    {
        if (m is null) return false;
        // Swallowing a key while Win/Alt is held would make Windows open Start / a menu bar on release.
        Fire(m, mask: m.BlockOriginal && winOrAltDown);
        return m.BlockOriginal;
    }

    Mapping? Lookup(int vk, int mods, string? keyboard)
    {
        var t = table;
        return (keyboard is null ? null : t.GetValueOrDefault(Trigger.KeyMatch(vk, mods, keyboard)))
               ?? t.GetValueOrDefault(Trigger.KeyMatch(vk, mods));
    }

    void ResolveDeferred(Deferred d, string? keyboard)
    {
        deferred.Remove(d);
        var m = Lookup(d.Vk, d.Mods, keyboard);
        if (m is not null) Fire(m, mask: false);
        if (m is null || !m.BlockOriginal) Replay(d.Vk);
    }

    void ResolveCheck(PendingCheck c, string? keyboard)
    {
        checks.Remove(c);
        if (Lookup(c.Vk, c.Mods, keyboard) is { } m) Fire(m, mask: false);
    }

    void Observe(in Native.KBDLLHOOKSTRUCT k, Trigger trigger)
    {
        bool extended = (k.flags & Native.LLKHF_EXTENDED) != 0;
        var row = new PendingObservation(KeyNames.Normalize((int)k.vkCode), Environment.TickCount, DateTime.Now,
            $"Key code 0x{k.vkCode:X2}  (scan {k.scanCode:X2}{(extended ? ", ext" : "")})", trigger);
        if (trigger.KeyboardName is { } named)
        {
            Raise(row.ToObserved(named));
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
            ushort flags = *(ushort*)(body + 2);
            ushort vkey = *(ushort*)(body + 6);
            uint extra = *(uint*)(body + 12);
            if (extra == (uint)InputSender.Marker) return; // our own keystrokes
            OnRawKey(header.hDevice, vkey, (flags & Native.RI_KEY_BREAK) == 0);
        }
        else if (header.dwType == Native.RIM_TYPEHID)
        {
            var device = GetDevice(header.hDevice);
            if (device is null) return;
            // Vendor channels are only interesting on keyboards (other devices can be chatty).
            if (device.UsagePage >= 0xFF00 && !externalKeyboards.ContainsKey(device.VidPid)) return;

            int reportSize = *(int*)body;
            int count = *(int*)(body + 4);
            byte* reports = body + 8;
            for (int i = 0; i < count; i++)
                OnHidReport(device, new ReadOnlySpan<byte>(reports + i * reportSize, reportSize).ToArray());
        }
    }

    void OnRawKey(nint handle, int rawVk, bool down)
    {
        var keyboard = KeyboardFor(handle);
        if (keyboard is not null) NoteActivity(keyboard);
        if (!down) return;

        int vk = KeyNames.Normalize(rawVk);
        string name = keyboard is not null ? NameOf(keyboard) : handle == 0 ? "Simulated input" : "Unknown keyboard";

        if (observations.FirstOrDefault(o => o.Vk == vk) is { } row)
        {
            observations.Remove(row);
            row.Trigger.Keyboard ??= keyboard?.Id;
            row.Trigger.KeyboardName ??= keyboard is null ? null : name;
            Raise(row.ToObserved(name));
        }
        if (checks.FirstOrDefault(c => KeyNames.Normalize(c.Vk) == vk) is { } check)
            ResolveCheck(check, keyboard?.Id);
    }

    void OnHidReport(HidDevice device, byte[] report)
    {
        if (!device.IsNewState(report)) return;
        var signal = device.Decode(report);
        if (signal is null) return; // released

        string keyboardName = NameOf(device.VidPid, device.DisplayName);
        var trigger = new Trigger
        {
            Kind = TriggerKind.Hid,
            HidKey = signal.Key,
            UsagePage = device.UsagePage,
            Description = signal.Meaning,
            Signal = signal.Code,
            Device = device.DisplayName,
            Keyboard = device.VidPid,
            KeyboardName = keyboardName,
        };

        foreach (var (page, usage) in signal.Usages)
            if (page == 0x0C && KeyNames.ConsumerToVk.TryGetValue(usage, out int vk))
                OnMediaSignal(device.VidPid, keyboardName, vk);

        if (MonitorEnabled) Raise(new ObservedInput(DateTime.Now, "Signal", signal.Code, signal.Meaning, keyboardName, trigger));

        if (capture is { } cap)
        {
            if (device.UsagePage == 0x0C) cap.Consumer ??= trigger;
            else cap.Other ??= trigger;
            ArmCapture();
        }
        else if (!Paused && table.TryGetValue(trigger.MatchKey, out var m))
        {
            Fire(m, mask: false);
        }
    }

    /// <summary>
    /// A media signal names the keyboard behind the key code Windows makes from it. The key code may already be
    /// waiting (held key, unshown monitor row, pending check) or may arrive just after.
    /// </summary>
    void OnMediaSignal(string keyboard, string name, int vk)
    {
        bool used = false;
        if (deferred.FirstOrDefault(d => d.Vk == vk) is { } waiting)
        {
            ResolveDeferred(waiting, keyboard);
            used = true;
        }
        if (checks.FirstOrDefault(c => c.Vk == vk) is { } check)
        {
            ResolveCheck(check, keyboard);
            used = true;
        }
        if (observations.FirstOrDefault(o => o.Vk == vk) is { } row)
        {
            observations.Remove(row);
            row.Trigger.Keyboard = keyboard;
            row.Trigger.KeyboardName = name;
            Raise(row.ToObserved(name));
            used = true;
        }
        if (used) return;

        int now = Environment.TickCount;
        recentMedia.RemoveAll(r => now - r.Tick > 1000);
        recentMedia.Add(new MediaSignal(keyboard, name, vk, now));
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
            string product = external ? RawDevices.Product(path) : "";
            if (product.Length == 0) product = RawDevices.Description(path);
            keyboard = new KeyboardInfo(id, product, connection, external);
        }
        keyboardHandles[handle] = keyboard;
        return keyboard;
    }

    void NoteActivity(KeyboardInfo keyboard)
    {
        if (keyboard.External ? externalKeyboards.TryAdd(keyboard.Id, keyboard) : usedBuiltIns.TryAdd(keyboard.Id, keyboard))
            PublishKeyboards();

        int now = Environment.TickCount;
        if (keyboard.Id == lastActiveKeyboard && now - lastActivityTick < 1000) return;
        lastActiveKeyboard = keyboard.Id;
        lastActivityTick = now;
        string id = keyboard.Id;
        ui.Post(_ => KeyboardActivity?.Invoke(id), null);
    }

    void PublishKeyboards()
    {
        IReadOnlyList<KeyboardInfo> next = [.. externalKeyboards.Values.OrderBy(k => k.Product), .. usedBuiltIns.Values];
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

        keyboardHandles.Clear(); // handles can be reused after a device is unplugged
        externalKeyboards.Clear();
        foreach (var d in list.Where(d => d.dwType == Native.RIM_TYPEKEYBOARD))
            if (KeyboardFor(d.hDevice) is { External: true } keyboard)
                externalKeyboards.TryAdd(keyboard.Id, keyboard);

        // Keyboards (to learn which one sent a key), media/consumer keys and system power keys from any device,
        // plus the vendor channels of external keyboards.
        var wanted = new List<(ushort Page, ushort Usage)> { (0x01, 0x06), (0x0C, 0x01), (0x01, 0x80) };
        foreach (var d in list.Where(d => d.dwType == Native.RIM_TYPEHID))
        {
            var device = GetDevice(d.hDevice);
            if (device is { UsagePage: >= 0xFF00 } && externalKeyboards.ContainsKey(device.VidPid) && !wanted.Contains((device.UsagePage, device.Usage)))
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
        if (capture is null || capture.Armed) return;
        capture.Armed = true;
        Native.SetTimer(window!.Handle, SettleTimer, SettleMs, 0);
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
        if (chosen == c.Key && c.Key is { Keyboard: null } key && TakeMediaSignal(key.Vk, (int)SettleMs + 200) is { } media)
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
        foreach (var c in checks.Where(c => now - c.Tick >= CheckMs).ToList()) ResolveCheck(c, null);
        foreach (var row in observations.Where(o => now - o.Tick >= ObservationMs).ToList())
        {
            observations.Remove(row);
            Raise(row.ToObserved("Unknown keyboard"));
        }
        if (deferred.Count == 0 && checks.Count == 0 && observations.Count == 0)
            Native.KillTimer(window!.Handle, ResolveTimer);
    }

    void OnTimer(nuint id)
    {
        switch (id)
        {
            case SettleTimer:
                FinishCapture(timedOut: false);
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
        }
    }

    string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"KeyLayer {typeof(InputEngine).Assembly.GetName().Version} on {Environment.OSVersion}");
        sb.AppendLine($"Keyboard hook: {(hook != 0 ? "installed" : "NOT installed")} {StartError}");
        sb.AppendLine($"Custom Fn key: {(CustomFnVk == 0 ? "off" : KeyNames.Vk(CustomFnVk))}");
        sb.AppendLine("Keyboards:");
        foreach (var k in Keyboards) sb.AppendLine($"  {k.Id}  {k.Connection}  {NameOf(k)}");
        sb.AppendLine("HID collections:");
        foreach (var d in devices.Values.OrderBy(d => d.VidPid).ThenBy(d => d.UsagePage))
        {
            sb.AppendLine($"  {d.VidPid}  page {d.UsagePage:X4} usage {d.Usage:X4}  \"{d.Product}\"  " +
                          $"input pages [{string.Join(",", d.InputPages.Select(p => p.ToString("X4")))}]  report IDs: {d.HasReportIds}");
        }
        sb.AppendLine("Listening for: " + string.Join(", ", Listening));
        return sb.ToString();
    }

    sealed record MediaSignal(string Keyboard, string Name, int Vk, int Tick);

    /// <summary>A swallowed media/launch key waiting to learn which keyboard sent it.</summary>
    sealed class Deferred(int vk, int mods, int tick)
    {
        public int Vk { get; } = vk;
        public int Mods { get; } = mods;
        public int Tick { get; } = tick;
    }

    /// <summary>An ordinary key (let through) waiting to learn which keyboard sent it.</summary>
    sealed class PendingCheck(int vk, int mods, int tick)
    {
        public int Vk { get; } = vk;
        public int Mods { get; } = mods;
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
        public bool Armed { get; set; }
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
