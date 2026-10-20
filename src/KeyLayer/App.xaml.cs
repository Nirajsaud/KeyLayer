using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace KeyLayer;

/// <summary>Owns the tray icon, the config, the input engine and the (hide-on-close) main window.</summary>
internal partial class App : Application
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "KeyLayer";

    readonly ConfigStore store = new();
    Forms.NotifyIcon? tray;
    Forms.ToolStripMenuItem? pauseItem;
    EventWaitHandle? showSignal;
    RegisteredWaitHandle? showWait;
    MainWindow? window;

    public AppConfig Config { get; private set; } = new();
    public InputEngine Engine { get; private set; } = null!;
    public string ConfigPath => store.FilePath;
    public bool Quitting { get; private set; }
    public bool StartHidden { get; init; }
    public bool Paused => Engine.Paused;
    public event Action? PausedChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Forms.Application.EnableVisualStyles();
        DispatcherUnhandledException += OnUnhandledException;

        // Draw without the GPU. The window is a few cards and buttons, and using the GPU loads the graphics
        // driver into the process, which costs well over 100 MB of memory.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        Config = store.Load();
        KeyNames.CustomFnVk = Config.CustomFnVk;
        ApplyTheme(Config.Theme);

        Engine = new InputEngine(SynchronizationContext.Current!);
        Engine.SetMappings(Config.Mappings);
        Engine.SetExcludedApps(Config.ExcludedApps);
        Engine.SetKeyboardNames(Config.KeyboardNames);
        Engine.CustomFnVk = Config.CustomFnVk;
        Engine.ActionFailed += (m, ex) => Notify($"“{m.Name}” didn't run", ex.Message, Forms.ToolTipIcon.Warning);
        Engine.Start();
        // Windows skips keyboard hooks while our own window is active; the engine reads raw input then.
        Activated += (_, _) => Engine.AppInForeground = true;
        Deactivated += (_, _) => Engine.AppInForeground = false;

        pauseItem = new Forms.ToolStripMenuItem("Pause shortcuts") { CheckOnClick = true };
        pauseItem.CheckedChanged += (_, _) => SetPaused(pauseItem.Checked);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Open KeyLayer", null, (_, _) => ShowMain())
        {
            Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold),
        });
        menu.Items.Add(pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit", null, (_, _) => Quit()));

        tray = new Forms.NotifyIcon { Icon = TrayIcon(), Text = "KeyLayer", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, me) => { if (me.Button == Forms.MouseButtons.Left) ShowMain(); };

        // A second launch signals this one to show its window instead of starting twice.
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        var dispatcher = Dispatcher;
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => dispatcher.BeginInvoke(new Action(ShowMain)), null, -1, false);

        if (StartupEnabled) SetStartup(true); // keep the path current if the exe was moved

        if (Engine.StartError is { } error)
            MessageBox.Show(error, "KeyLayer", MessageBoxButton.OK, MessageBoxImage.Warning);

        if (!StartHidden) ShowMain();
        else TrimMemorySoon();
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "KeyLayer", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    public void ApplyTheme(string theme) => ThemeMode = theme switch
    {
        "Light" => ThemeMode.Light,
        "Dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    public void SetPaused(bool paused)
    {
        if (Engine.Paused == paused) return;
        Engine.Paused = paused;
        if (pauseItem is not null) pauseItem.Checked = paused;
        if (tray is not null) tray.Text = paused ? "KeyLayer (paused)" : "KeyLayer";
        PausedChanged?.Invoke();
    }

    public void SaveConfig()
    {
        store.Save(Config);
        Engine.SetMappings(Config.Mappings);
        Engine.SetExcludedApps(Config.ExcludedApps);
        Engine.SetKeyboardNames(Config.KeyboardNames);
        Engine.CustomFnVk = KeyNames.CustomFnVk = Config.CustomFnVk;
    }

    public void ShowMain()
    {
        if (window is null)
        {
            // The window is built when it's opened and let go when it's closed, so the tray app stays small.
            window = new MainWindow(this);
            window.Closed += (_, _) =>
            {
                window = null;
                Engine.AppInForeground = false;
                if (!Quitting) TrimMemorySoon();
            };
        }
        window.Show();
        // Ask Windows directly: a window started minimized (by whoever launched us) isn't reflected in WindowState.
        var handle = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Minimized || Native.IsIconic(handle))
        {
            window.WindowState = WindowState.Normal;
            Native.ShowWindow(handle, 9 /* SW_RESTORE */);
        }
        window.Activate();
    }

    /// <summary>
    /// Shortly after the window closes (or a start in the tray), frees what it used and hands unused memory back to
    /// Windows. Pages KeyLayer still needs come straight back the next time they're used.
    /// </summary>
    void TrimMemorySoon()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (window is not null) return; // opened again meanwhile
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Native.SetProcessWorkingSetSize(-1 /* this process */, -1, -1);
        };
        timer.Start();
    }

    public void Notify(string title, string text, Forms.ToolTipIcon icon) =>
        tray?.ShowBalloonTip(5000, title, string.IsNullOrWhiteSpace(text) ? " " : text, icon);

    public void Quit()
    {
        Quitting = true;
        window?.Close();
        if (tray is not null)
        {
            tray.Visible = false;
            tray.Dispose();
        }
        showWait?.Unregister(null);
        showSignal?.Dispose();
        Engine.Dispose();
        Shutdown();
    }

    static System.Drawing.Icon TrayIcon()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("app.ico")
            ?? throw new InvalidOperationException("Icon resource missing.");
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
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
