param([string]$Log)
# Fn listening test: shows a window that walks the user through pressing Fn on each keyboard, while it records
# everything that arrives: every key at the lowest level (keyboard hook), raw keyboard input with scan codes, every
# HID channel (media, system, wireless, vendor), and ASUS hotkey events from WMI. Writes all of it to $Log.
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Management -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public class FnProbe : Form
{
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint header);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, StringBuilder data, ref uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, IntPtr data, ref uint size);
    delegate IntPtr HookProc(int n, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);

    static readonly string[][] Phases = {
        new[] { "6",  "baseline",    "Don't touch the keyboards", "Measuring background noise first." },
        new[] { "14", "laptop-tap",  "LAPTOP keyboard: press Fn by itself, 4 times", "Slowly: press, let go, wait a second." },
        new[] { "4",  "rest",        "Hands off", "" },
        new[] { "8",  "laptop-hold", "LAPTOP keyboard: hold Fn down for 3 seconds", "Then let go." },
        new[] { "4",  "rest",        "Hands off", "" },
        new[] { "14", "havit-tap",   "HAVIT keyboard: press Fn by itself, 4 times", "Slowly: press, let go, wait a second." },
        new[] { "4",  "rest",        "Hands off", "" },
        new[] { "8",  "havit-hold",  "HAVIT keyboard: hold Fn down for 3 seconds", "Then let go." },
        new[] { "4",  "rest",        "Hands off", "" },
        new[] { "12", "control",     "Check: change the volume once on each keyboard", "Use each keyboard's volume key (with Fn if it needs it)." },
    };

    readonly string logPath;
    readonly StreamWriter log;
    readonly Label title = new Label(), detail = new Label(), clock = new Label();
    readonly ListBox feed = new ListBox();
    readonly Button start = new Button();
    readonly Timer timer = new Timer { Interval = 100 };
    readonly Dictionary<IntPtr, string> names = new Dictionary<IntPtr, string>();
    readonly Dictionary<IntPtr, string> usages = new Dictionary<IntPtr, string>();
    readonly DateTime t0 = DateTime.Now;
    HookProc hookProc; IntPtr hook; ManagementEventWatcher wmi;
    int phase = -1; DateTime phaseEnd;
    string PhaseTag { get { return phase < 0 ? "ready" : phase >= Phases.Length ? "done" : Phases[phase][1]; } }

    public FnProbe(string logPath)
    {
        this.logPath = logPath;
        log = new StreamWriter(logPath, false, Encoding.UTF8) { AutoFlush = true };
        Text = "KeyLayer Fn test"; Width = 760; Height = 430; TopMost = true; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; BackColor = Color.White;
        title.SetBounds(24, 20, 700, 70); title.Font = new Font("Segoe UI", 20, FontStyle.Bold);
        title.Text = "Fn listening test";
        detail.SetBounds(24, 92, 700, 50); detail.Font = new Font("Segoe UI", 12);
        detail.Text = "Click Start, then follow the instructions. It takes about 1.5 minutes. Don't touch the touchpad or screen.";
        clock.SetBounds(600, 24, 120, 40); clock.Font = new Font("Segoe UI", 18); clock.TextAlign = ContentAlignment.TopRight;
        feed.SetBounds(24, 150, 700, 170); feed.Font = new Font("Consolas", 9);
        start.SetBounds(24, 332, 140, 40); start.Text = "Start"; start.Font = new Font("Segoe UI", 12);
        start.Click += delegate { start.Enabled = false; NextPhase(); timer.Start(); };
        timer.Tick += delegate { Tick(); };
        Controls.AddRange(new Control[] { title, detail, clock, feed, start });
        Load += delegate { Listen(); };
        FormClosed += delegate { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); if (wmi != null) try { wmi.Stop(); } catch { } log.Dispose(); };
    }

    void Listen()
    {
        // Everything a keyboard could send, except touch/pen (page 0D), sensors (page 20) and mice.
        var wanted = new List<RAWINPUTDEVICE>();
        Action<ushort, ushort, uint> add = (p, u, f) => wanted.Add(new RAWINPUTDEVICE { Page = p, Usage = u, Flags = 0x100 | f, Target = Handle });
        add(0x01, 0x06, 0); add(0x01, 0x80, 0); add(0x01, 0x0C, 0); add(0x01, 0x0D, 0); add(0x01, 0x07, 0);
        add(0x0C, 0, 0x20); // consumer page, all usages (PAGEONLY)
        for (int p = 0xFF00; p <= 0xFF0F; p++) add((ushort)p, 0, 0x20);
        add(0xFF31, 0, 0x20); add(0xFF60, 0, 0x20); add(0xFFC0, 0, 0x20);
        int ok = 0;
        foreach (var d in wanted)
            if (RegisterRawInputDevices(new[] { d }, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) ok++;
            else Write("setup", string.Format("couldn't listen to {0:X4}/{1:X4}", d.Page, d.Usage));
        Write("setup", string.Format("raw input: listening to {0} of {1} channels", ok, wanted.Count));

        hookProc = HookCallback;
        hook = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0);
        Write("setup", "keyboard hook: " + (hook != IntPtr.Zero));

        try
        {
            wmi = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new WqlEventQuery("SELECT * FROM AsusAtkWmiEvent"));
            wmi.EventArrived += (s, e) =>
            {
                string id = "";
                foreach (PropertyData p in e.NewEvent.Properties) id += p.Name + "=" + Convert.ToString(p.Value) + " ";
                BeginInvoke(new Action(() => Write("ASUS-WMI", id.Trim())));
            };
            wmi.Start();
            Write("setup", "ASUS hotkey events (WMI): listening");
        }
        catch (Exception ex) { Write("setup", "ASUS hotkey events (WMI): " + ex.Message); }
    }

    void NextPhase()
    {
        phase++;
        if (phase >= Phases.Length)
        {
            timer.Stop();
            title.Text = "Done, thank you!"; detail.Text = "You can close this window."; clock.Text = "";
            Write("phase", "done");
            var close = new Timer { Interval = 4000 }; close.Tick += delegate { Close(); }; close.Start();
            return;
        }
        var p = Phases[phase];
        phaseEnd = DateTime.Now.AddSeconds(int.Parse(p[0]));
        title.Text = p[2]; detail.Text = p[3];
        title.ForeColor = p[1] == "rest" || p[1] == "baseline" ? Color.DimGray : Color.FromArgb(0, 102, 204);
        Write("phase", p[1]);
    }

    void Tick()
    {
        double left = (phaseEnd - DateTime.Now).TotalSeconds;
        clock.Text = Math.Max(0, Math.Ceiling(left)).ToString() + " s";
        if (left <= 0) NextPhase();
    }

    void Write(string kind, string text)
    {
        string line = string.Format("{0,7:F2}  {1,-11}  {2,-9}  {3}", (DateTime.Now - t0).TotalSeconds, PhaseTag, kind, text);
        log.WriteLine(line);
        if (kind != "setup" && kind != "phase")
        {
            feed.Items.Insert(0, string.Format("{0,-11} {1,-9} {2}", PhaseTag, kind, text));
            if (feed.Items.Count > 200) feed.Items.RemoveAt(feed.Items.Count - 1);
        }
    }

    IntPtr HookCallback(int n, IntPtr w, IntPtr l)
    {
        if (n >= 0)
        {
            int vk = Marshal.ReadInt32(l), scan = Marshal.ReadInt32(l, 4), flags = Marshal.ReadInt32(l, 8);
            int msg = w.ToInt32();
            string dir = msg == 0x100 || msg == 0x104 ? "down" : "up";
            Write("hook", string.Format("vk=0x{0:X2} scan=0x{1:X2} flags=0x{2:X2} {3}{4}", vk, scan, flags, dir, (flags & 0x10) != 0 ? " (injected)" : ""));
        }
        return CallNextHookEx(hook, n, w, l);
    }

    string NameOf(IntPtr device)
    {
        string name;
        if (names.TryGetValue(device, out name)) return name;
        uint len = 0; GetRawInputDeviceInfo(device, 0x20000007, (StringBuilder)null, ref len);
        var sb = new StringBuilder((int)len + 1); GetRawInputDeviceInfo(device, 0x20000007, sb, ref len);
        string path = sb.ToString();
        var parts = path.TrimStart('\\', '?').Split('#');
        name = device == IntPtr.Zero ? "(software)" : parts.Length > 1 ? parts[1] : path;
        names[device] = name;
        return name;
    }

    string UsageOf(IntPtr device)
    {
        string usage;
        if (usages.TryGetValue(device, out usage)) return usage;
        uint size = 32; IntPtr buf = Marshal.AllocHGlobal(32); Marshal.WriteInt32(buf, 32);
        usage = "";
        if (GetRawInputDeviceInfo(device, 0x2000000b, buf, ref size) != uint.MaxValue && Marshal.ReadInt32(buf, 4) == 2)
            usage = string.Format("{0:X4}/{1:X4}", (ushort)Marshal.ReadInt16(buf, 20), (ushort)Marshal.ReadInt16(buf, 22));
        Marshal.FreeHGlobal(buf);
        usages[device] = usage;
        return usage;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x00FF) // WM_INPUT
        {
            uint size = 0, header = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            GetRawInputData(m.LParam, 0x10000003, IntPtr.Zero, ref size, header);
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(m.LParam, 0x10000003, buf, ref size, header) == size)
                {
                    var h = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                    IntPtr body = IntPtr.Add(buf, (int)header);
                    if (h.Type == 1)
                    {
                        ushort make = (ushort)Marshal.ReadInt16(body), flags = (ushort)Marshal.ReadInt16(body, 2), vkey = (ushort)Marshal.ReadInt16(body, 6);
                        Write("rawkey", string.Format("{0}  make=0x{1:X2} flags=0x{2:X2} vk=0x{3:X2}", NameOf(h.Device), make, flags, vkey));
                    }
                    else if (h.Type == 2)
                    {
                        int each = Marshal.ReadInt32(body), count = Marshal.ReadInt32(body, 4);
                        var bytes = new byte[each * count];
                        Marshal.Copy(IntPtr.Add(body, 8), bytes, 0, bytes.Length);
                        Write("hid", string.Format("{0} [{1}]  {2}", NameOf(h.Device), UsageOf(h.Device), BitConverter.ToString(bytes).Replace("-", " ")));
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        base.WndProc(ref m);
    }
}
"@
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::Run((New-Object FnProbe $Log))
"log written to $Log"
