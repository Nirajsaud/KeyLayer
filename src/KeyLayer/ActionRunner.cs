using System.Diagnostics;
using System.Windows.Forms;

namespace KeyLayer;

internal enum BuiltInAction
{
    PlayPause, NextTrack, PreviousTrack, StopMedia, VolumeUp, VolumeDown, Mute,
    BrightnessUp, BrightnessDown,
    ShowDesktop, TaskView, Screenshot, ClipboardHistory, EmojiPanel, MinimizeWindow, CloseWindow,
    PreviousDesktop, NextDesktop,
    FileExplorer, Calculator, TaskManager, WindowsSettings,
    LockPc, Sleep,
}

/// <summary>Carries out a shortcut's action. Runs on a thread-pool thread, never on the hook thread.</summary>
internal static class ActionRunner
{
    public static readonly (BuiltInAction Action, string Name)[] BuiltIns =
    [
        (BuiltInAction.PlayPause, "Play / pause"),
        (BuiltInAction.NextTrack, "Next track"),
        (BuiltInAction.PreviousTrack, "Previous track"),
        (BuiltInAction.StopMedia, "Stop media"),
        (BuiltInAction.VolumeUp, "Volume up"),
        (BuiltInAction.VolumeDown, "Volume down"),
        (BuiltInAction.Mute, "Mute / unmute"),
        (BuiltInAction.BrightnessUp, "Screen brightness up"),
        (BuiltInAction.BrightnessDown, "Screen brightness down"),
        (BuiltInAction.ShowDesktop, "Show desktop"),
        (BuiltInAction.TaskView, "Task view (all windows)"),
        (BuiltInAction.Screenshot, "Screenshot (Snipping Tool)"),
        (BuiltInAction.ClipboardHistory, "Clipboard history"),
        (BuiltInAction.EmojiPanel, "Emoji panel"),
        (BuiltInAction.MinimizeWindow, "Minimize current window"),
        (BuiltInAction.CloseWindow, "Close current window"),
        (BuiltInAction.PreviousDesktop, "Previous virtual desktop"),
        (BuiltInAction.NextDesktop, "Next virtual desktop"),
        (BuiltInAction.FileExplorer, "Open File Explorer"),
        (BuiltInAction.Calculator, "Open Calculator"),
        (BuiltInAction.TaskManager, "Open Task Manager"),
        (BuiltInAction.WindowsSettings, "Open Windows Settings"),
        (BuiltInAction.LockPc, "Lock the PC"),
        (BuiltInAction.Sleep, "Put the PC to sleep"),
    ];

    public static string Name(BuiltInAction action) =>
        BuiltIns.FirstOrDefault(b => b.Action == action).Name ?? action.ToString();

    public static void Run(ActionSpec a)
    {
        switch (a.Type)
        {
            case ActionType.OpenTarget:
                Open(a.Target, a.Arguments);
                break;
            case ActionType.SendShortcut:
                if (a.ShortcutVk == 0) throw new InvalidOperationException("No shortcut has been set.");
                SendShortcut(a.ShortcutMods, a.ShortcutVk);
                break;
            case ActionType.TypeText:
                var lifted = InputSender.ReleaseHeldModifiers(0);
                try { InputSender.Text(a.Text ?? ""); }
                finally { InputSender.RestoreModifiers(lifted); }
                break;
            case ActionType.BuiltIn:
                RunBuiltIn(a.BuiltIn);
                break;
            case ActionType.RunCommand:
                RunCommand(a.Command, a.HideWindow);
                break;
            case ActionType.Disable:
                break;
        }
    }

    static void SendShortcut(int mods, int vk)
    {
        var lifted = InputSender.ReleaseHeldModifiers(mods);
        try { InputSender.Shortcut(mods, vk); }
        finally { InputSender.RestoreModifiers([.. lifted, .. InputSender.ModifierVks(mods)]); }
    }

    static void RunBuiltIn(BuiltInAction action)
    {
        switch (action)
        {
            case BuiltInAction.PlayPause: InputSender.Tap(0xB3); break;
            case BuiltInAction.NextTrack: InputSender.Tap(0xB0); break;
            case BuiltInAction.PreviousTrack: InputSender.Tap(0xB1); break;
            case BuiltInAction.StopMedia: InputSender.Tap(0xB2); break;
            case BuiltInAction.VolumeUp: InputSender.Tap(0xAF); break;
            case BuiltInAction.VolumeDown: InputSender.Tap(0xAE); break;
            case BuiltInAction.Mute: InputSender.Tap(0xAD); break;
            case BuiltInAction.BrightnessUp: Brightness.Step(+10); break;
            case BuiltInAction.BrightnessDown: Brightness.Step(-10); break;
            case BuiltInAction.ShowDesktop: SendShortcut(KeyNames.Win, 'D'); break;
            case BuiltInAction.TaskView: SendShortcut(KeyNames.Win, 0x09); break;
            case BuiltInAction.Screenshot: SendShortcut(KeyNames.Win | KeyNames.Shift, 'S'); break;
            case BuiltInAction.ClipboardHistory: SendShortcut(KeyNames.Win, 'V'); break;
            case BuiltInAction.EmojiPanel: SendShortcut(KeyNames.Win, 0xBE); break;
            case BuiltInAction.MinimizeWindow: Native.ShowWindow(Native.GetForegroundWindow(), 6 /* SW_MINIMIZE */); break;
            case BuiltInAction.CloseWindow: SendShortcut(KeyNames.Alt, 0x73); break;
            case BuiltInAction.PreviousDesktop: SendShortcut(KeyNames.Ctrl | KeyNames.Win, 0x25); break;
            case BuiltInAction.NextDesktop: SendShortcut(KeyNames.Ctrl | KeyNames.Win, 0x27); break;
            case BuiltInAction.FileExplorer: Open("explorer.exe", null); break;
            case BuiltInAction.Calculator: Open("calc.exe", null); break;
            case BuiltInAction.TaskManager: SendShortcut(KeyNames.Ctrl | KeyNames.Shift, 0x1B); break;
            case BuiltInAction.WindowsSettings: Open("ms-settings:", null); break;
            case BuiltInAction.LockPc: Native.LockWorkStation(); break;
            case BuiltInAction.Sleep: Application.SetSuspendState(PowerState.Suspend, false, false); break;
        }
    }

    static void Open(string? target, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("No program, file or address has been set.");
        target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        var psi = new ProcessStartInfo(target) { UseShellExecute = true, Arguments = arguments ?? "" };
        if (File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target)!;
        AllowLaunchedAppToComeForward();
        Process.Start(psi)?.Dispose();
    }

    static void RunCommand(string? command, bool hide)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("No command has been set.");
        var psi = new ProcessStartInfo("cmd.exe", $"/s /c \"{command}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = hide,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        AllowLaunchedAppToComeForward();
        Process.Start(psi)?.Dispose();
    }

    /// <summary>
    /// A background app may not normally bring windows to the front. Sending a harmless keystroke makes this
    /// process the last input provider, which lets it hand foreground rights to the app it launches.
    /// </summary>
    static void AllowLaunchedAppToComeForward()
    {
        try { InputSender.Mask(); } catch { }
        Native.AllowSetForegroundWindow(-1 /* ASFW_ANY */);
    }
}

/// <summary>Built-in laptop screen brightness through WMI (external monitors usually don't support it).</summary>
internal static class Brightness
{
    public static int Read()
    {
        dynamic wmi = Connect();
        dynamic set = wmi.ExecQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
        if ((int)set.Count == 0) throw new NotSupportedException("This screen doesn't support brightness control.");
        dynamic item = set.ItemIndex(0);
        return Convert.ToInt32(item.Properties_.Item("CurrentBrightness").Value);
    }

    public static void Step(int delta)
    {
        int target = Math.Clamp(Read() + delta, 0, 100);
        dynamic wmi = Connect();
        dynamic set = wmi.ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
        if ((int)set.Count == 0) throw new NotSupportedException("This screen doesn't support brightness control.");
        dynamic monitor = set.ItemIndex(0);
        dynamic input = monitor.Methods_.Item("WmiSetBrightness").InParameters.SpawnInstance_();
        input.Properties_.Item("Timeout").Value = 1;
        input.Properties_.Item("Brightness").Value = (byte)target;
        monitor.ExecMethod_("WmiSetBrightness", input);
    }

    static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator")
            ?? throw new NotSupportedException("WMI isn't available on this PC.");
        dynamic locator = Activator.CreateInstance(type)!;
        return locator.ConnectServer(".", @"root\wmi");
    }
}
