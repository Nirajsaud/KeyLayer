param([string]$Exe, [string]$Dir)
# Checks three suspected engine bugs with simulated keys:
#  A  holding Ctrl and pressing a Ctrl+F24 shortcut twice (action presses F13): does the 2nd press still run it?
#  B  custom Fn key (Scroll Lock) while an excluded app (Character Map) is in front: does the app get the key-down?
#  C  Fn key released while KeyLayer's own window is in front: is Fn stuck afterwards (plain F22 runs the Fn+F22 shortcut)?
if (Get-Process KeyLayer -ErrorAction SilentlyContinue) { "KeyLayer is already running - not testing over it."; exit 1 }

$config = @"
{
  "Version": 1, "TrayTipShown": true, "CustomFnVk": 145,
  "ExcludedApps": [ { "Exe": "charmap.exe", "Name": "Character Map" } ],
  "Mappings": [
    { "Name": "Ctrl+F24", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 135, "Modifiers": 1, "Description": "Ctrl + F24" },
      "Action": { "Type": "SendShortcut", "ShortcutVk": 124, "ShortcutMods": 0 } },
    { "Name": "Fn+F22", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 133, "Modifiers": 16, "Description": "Fn + F22" },
      "Action": { "Type": "SendShortcut", "ShortcutVk": 125, "ShortcutMods": 0 } }
  ]
}
"@
$configPath = Join-Path $Dir 'test-bug-config.json'
Set-Content $configPath $config -Encoding utf8

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
public static class BugTest {
    delegate IntPtr Proc(int n, IntPtr w, IntPtr l);
    static Proc proc; static IntPtr hook;
    public static List<string> Seen = new List<string>();
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, Proc p, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint msg; public IntPtr w, l; public uint time; public int x, y; }
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint a, uint b, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KI ki; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    public static void Install() { proc = Callback; hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0); }
    public static void Remove() { UnhookWindowsHookEx(hook); }
    static IntPtr Callback(int n, IntPtr w, IntPtr l) {
        if (n >= 0) {
            int vk = Marshal.ReadInt32(l); long extra = Marshal.ReadInt64(l, 16); int msg = w.ToInt32();
            if (vk != 0xE8) Seen.Add(string.Format("{0:X2}{1}{2}", vk, (msg == 0x100 || msg == 0x104) ? "v" : "^", extra == 0x4B4C5952 ? "*" : ""));
        }
        return CallNextHookEx(hook, n, w, l);
    }
    public static void Pump(int ms) {
        int end = Environment.TickCount + ms; MSG m;
        while (Environment.TickCount < end) { while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); } Thread.Sleep(5); }
    }
    public static void Send(ushort vk, bool up) {
        INPUT[] i = new INPUT[1]; i[0].type = 1; i[0].ki.vk = vk; if (up) i[0].ki.flags = 2;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT))); Pump(80);
    }
    public static void Tap(ushort vk) { Send(vk, false); Send(vk, true); }
    public static void Front(IntPtr h) { Send(0xE8, false); Send(0xE8, true); SetForegroundWindow(h); Pump(400); }
    public static bool ScrollLockOn() { return (GetKeyState(0x91) & 1) != 0; }
}
"@

function Count($pattern) { @([BugTest]::Seen | Where-Object { $_ -eq $pattern }).Count }
$scrollBefore = [BugTest]::ScrollLockOn()

[BugTest]::Install()
$env:KEYLAYER_CONFIG = $configPath
$app = Start-Process $Exe -PassThru   # window shown: needed for test C
Remove-Item Env:KEYLAYER_CONFIG
[BugTest]::Pump(3000)
$app.Refresh()
$form = New-Object System.Windows.Forms.Form -Property @{ Text = 'KeyLayer bug test'; Width = 360; Height = 160 }
$form.Show(); [BugTest]::Pump(300)

# ---- A
[BugTest]::Front($form.Handle); [BugTest]::Seen.Clear()
[BugTest]::Send(0xA2, $false); [BugTest]::Tap(0x87); [BugTest]::Pump(300); [BugTest]::Tap(0x87); [BugTest]::Pump(300); [BugTest]::Send(0xA2, $true)
[BugTest]::Pump(500)
$ranA = Count '7Cv*'; $passedA = Count '87v'
"A  Ctrl held, Ctrl+F24 pressed twice : shortcut ran $ranA of 2 times, plain F24 leaked to the app $passedA times  => $(if ($ranA -eq 2 -and $passedA -eq 0) { 'OK' } else { 'BUG' })"
"   events: $([BugTest]::Seen -join ' ')"

# ---- B
$charmap = Start-Process charmap.exe -PassThru
for ($i = 0; $i -lt 40 -and $charmap.MainWindowHandle -eq 0; $i++) { [BugTest]::Pump(150); $charmap.Refresh() }
[BugTest]::Front($charmap.MainWindowHandle); [BugTest]::Seen.Clear()
[BugTest]::Send(0x91, $false); [BugTest]::Pump(400)
$downReached = (Count '91v') -gt 0
[BugTest]::Send(0x91, $true); [BugTest]::Pump(300)
"B  Fn key held in an excluded app  : the app got the real key-down while held = $downReached  => $(if ($downReached) { 'OK' } else { 'BUG' })"
"   events: $([BugTest]::Seen -join ' ')"
Stop-Process -Id $charmap.Id -ErrorAction SilentlyContinue

# ---- C
[BugTest]::Front($form.Handle); [BugTest]::Seen.Clear()
[BugTest]::Send(0x91, $false)
[BugTest]::Front($app.MainWindowHandle)
[BugTest]::Send(0x91, $true)
[BugTest]::Front($form.Handle)
[BugTest]::Tap(0x85); [BugTest]::Pump(500)
$stuck = (Count '7Dv*') -gt 0
"C  Fn released inside KeyLayer      : plain F22 afterwards ran the Fn+F22 shortcut = $stuck  => $(if (-not $stuck) { 'OK' } else { 'BUG' })"
"   events: $([BugTest]::Seen -join ' ')"

$form.Close()
Stop-Process -Id $app.Id -ErrorAction SilentlyContinue
[BugTest]::Pump(300)
if ([BugTest]::ScrollLockOn() -ne $scrollBefore) { [BugTest]::Tap(0x91) }
"Scroll Lock restored: $([BugTest]::ScrollLockOn() -eq $scrollBefore)"
[BugTest]::Remove()
