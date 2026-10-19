using System.Text;

namespace KeyLayer;

internal static class Program
{
    const string MutexName = "KeyLayer.SingleInstance";
    internal const string ShowEventName = "KeyLayer.ShowWindow";

    [STAThread]
    static int Main(string[] args)
    {
        int selfTest = Array.IndexOf(args, "--selftest");
        if (selfTest >= 0) return SelfTest(args.Length > selfTest + 1 ? args[selfTest + 1] : "selftest.txt");

        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch { }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "KeyLayer", MessageBoxButtons.OK, MessageBoxIcon.Error);

        using var app = new TrayApp(startHidden: args.Contains("--tray"));
        Application.Run(app);
        return 0;
    }

    /// <summary>Writes what the app can see (keyboards, HID channels, hook status) to a file and exits.</summary>
    static int SelfTest(string outFile)
    {
        var sb = new StringBuilder();
        using (var engine = new InputEngine(new SynchronizationContext()))
        {
            engine.Start();
            sb.AppendLine(engine.DiagnosticsAsync().GetAwaiter().GetResult());
        }
        try { sb.AppendLine($"Screen brightness: {Brightness.Read()}%"); }
        catch (Exception ex) { sb.AppendLine($"Screen brightness: unavailable ({ex.Message})"); }
        File.WriteAllText(outFile, sb.ToString());
        return 0;
    }
}
