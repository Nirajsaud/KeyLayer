using Microsoft.Win32;

namespace KeyLayer;

/// <summary>Owns the tray icon, the config, the input engine and the (hide-on-close) main window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "KeyLayer";

    readonly ConfigStore store = new();
    readonly SynchronizationContext ui;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem pauseItem;
    readonly EventWaitHandle showSignal;
    readonly RegisteredWaitHandle showWait;
    MainForm? form;

    public AppConfig Config { get; }
    public InputEngine Engine { get; }
    public string ConfigPath => store.FilePath;
    public bool Quitting { get; private set; }
    public event Action? PausedChanged;

    public TrayApp(bool startHidden)
    {
        ui = SynchronizationContext.Current!;
        Config = store.Load();

        Engine = new InputEngine(ui);
        Engine.SetMappings(Config.Mappings);
        Engine.SetKeyboardNames(Config.KeyboardNames);
        Engine.CustomFnVk = Config.CustomFnVk;
        Engine.ActionFailed += (m, ex) => Notify($"“{m.Name}” didn't run", ex.Message, ToolTipIcon.Warning);
        Engine.Start();

        pauseItem = new ToolStripMenuItem("Pause shortcuts") { CheckOnClick = true };
        pauseItem.CheckedChanged += (_, _) => SetPaused(pauseItem.Checked);
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open KeyLayer", null, (_, _) => ShowMain()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));

        tray = new NotifyIcon { Icon = AppIcon.Tray(), Text = "KeyLayer", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };

        // A second launch signals this one to show its window instead of starting twice.
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => ui.Post(_ => ShowMain(), null), null, -1, false);

        if (StartupEnabled) SetStartup(true); // keep the path current if the exe was moved

        if (Engine.StartError is { } error)
            MessageBox.Show(error, "KeyLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        if (!startHidden) ShowMain();
    }

    public bool Paused => Engine.Paused;

    public void SetPaused(bool paused)
    {
        if (Engine.Paused == paused) return;
        Engine.Paused = paused;
        pauseItem.Checked = paused;
        tray.Text = paused ? "KeyLayer (paused)" : "KeyLayer";
        PausedChanged?.Invoke();
    }

    public void SaveConfig()
    {
        store.Save(Config);
        Engine.SetMappings(Config.Mappings);
        Engine.SetKeyboardNames(Config.KeyboardNames);
        Engine.CustomFnVk = Config.CustomFnVk;
    }

    public void ShowMain()
    {
        if (form is null || form.IsDisposed) form = new MainForm(this);
        form.Show();
        // Ask Windows directly: a window started minimized (by whoever launched us) isn't reflected in WindowState.
        if (form.WindowState == FormWindowState.Minimized || Native.IsIconic(form.Handle))
            Native.ShowWindow(form.Handle, 9 /* SW_RESTORE */);
        form.Activate();
    }

    public void Notify(string title, string text, ToolTipIcon icon) =>
        tray.ShowBalloonTip(5000, title, string.IsNullOrWhiteSpace(text) ? " " : text, icon);

    public void Quit()
    {
        Quitting = true;
        form?.Close();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        showWait.Unregister(null);
        Engine.Dispose();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            showSignal.Dispose();
        }
        base.Dispose(disposing);
    }

    public static bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
