param([string]$Exe, [string]$Dir)
# Simulated-key test of: normal shortcut, custom Fn layer, per-keyboard shortcuts (ordinary + held special key).
if (Get-Process KeyLayer -ErrorAction SilentlyContinue) { "KeyLayer is already running - not testing over it."; exit 1 }

$files = 'a','b','c','d' | ForEach-Object { Join-Path $Dir "fired-$_.txt" }
$files | ForEach-Object { Remove-Item $_ -ErrorAction SilentlyContinue }
function Cmd($f) { "echo fired> \`"$($f -replace '\\','\\')\`"" }
$config = @"
{
  "Version": 1, "TrayTipShown": true, "CustomFnVk": 145,
  "Mappings": [
    { "Name": "a F24 any", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 135, "Modifiers": 0, "Description": "F24" },
      "Action": { "Type": "RunCommand", "Command": "$(Cmd $files[0])" } },
    { "Name": "b CustomFn+F22", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 133, "Modifiers": 16, "Description": "Custom Fn + F22" },
      "Action": { "Type": "RunCommand", "Command": "$(Cmd $files[1])" } },
    { "Name": "c F21 on Havit only", "Enabled": true, "BlockOriginal": false,
      "Trigger": { "Kind": "Key", "Vk": 132, "Modifiers": 0, "Description": "F21", "Keyboard": "1A2C:90FE", "KeyboardName": "USB Gaming Keyboard" },
      "Action": { "Type": "RunCommand", "Command": "$(Cmd $files[2])" } },
    { "Name": "d F20 on Havit only", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 131, "Modifiers": 0, "Description": "F20", "Keyboard": "1A2C:90FE", "KeyboardName": "USB Gaming Keyboard" },
      "Action": { "Type": "RunCommand", "Command": "$(Cmd $files[3])" } }
  ]
}
"@
$configPath = Join-Path $Dir 'test-config2.json'
Set-Content $configPath $config -Encoding utf8

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
public static class HookTest {
    delegate IntPtr Proc(int n, IntPtr w, IntPtr l);
    static Proc proc; static IntPtr hook; static int t0;
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
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    public static void Install() { proc = Callback; hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0); t0 = Environment.TickCount; }
    public static void Remove() { UnhookWindowsHookEx(hook); }
    public static void Mark(string s) { Seen.Add(string.Format("-- +{0,5}ms {1}", Environment.TickCount - t0, s)); }
    static IntPtr Callback(int n, IntPtr w, IntPtr l) {
        if (n >= 0) {
            int vk = Marshal.ReadInt32(l); int msg = w.ToInt32();
            if (vk != 0xE8) Seen.Add(string.Format("   +{0,5}ms 0x{1:X2} {2}", Environment.TickCount - t0, vk, msg == 0x100 || msg == 0x104 ? "down" : "up"));
        }
        return CallNextHookEx(hook, n, w, l);
    }
    public static void Pump(int ms) {
        int end = Environment.TickCount + ms; MSG m;
        while (Environment.TickCount < end) { while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); } Thread.Sleep(5); }
    }
    static void Send(ushort vk, bool up) {
        INPUT[] i = new INPUT[1]; i[0].type = 1; i[0].ki.vk = vk; if (up) i[0].ki.flags = 2;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Down(ushort vk) { Send(vk, false); Pump(60); }
    public static void Up(ushort vk) { Send(vk, true); Pump(60); }
    public static void Tap(ushort vk) { Down(vk); Up(vk); }
    public static bool ScrollLockOn() { return (GetKeyState(0x91) & 1) != 0; }
}
"@

$scrollBefore = [HookTest]::ScrollLockOn()
[HookTest]::Install()
$env:KEYLAYER_CONFIG = $configPath
$app = Start-Process $Exe -ArgumentList '--tray' -PassThru
[HookTest]::Pump(2500)
[HookTest]::Mark("0. F19 (no shortcut) control"); [HookTest]::Tap(0x82); [HookTest]::Pump(500)
[HookTest]::Mark("0b. F19 again"); [HookTest]::Tap(0x82); [HookTest]::Pump(500)
[HookTest]::Mark("1. F24 (any keyboard, switched off)"); [HookTest]::Tap(0x87); [HookTest]::Pump(500)
[HookTest]::Mark("2. hold Scroll Lock (custom Fn) + F22"); [HookTest]::Down(0x91); [HookTest]::Tap(0x85); [HookTest]::Up(0x91); [HookTest]::Pump(500)
[HookTest]::Mark("3. F21 (Havit-only shortcut, ordinary key, from simulated input)"); [HookTest]::Tap(0x84); [HookTest]::Pump(800)
[HookTest]::Mark("3b. F21 again"); [HookTest]::Tap(0x84); [HookTest]::Pump(800)
[HookTest]::Mark("4. F20 (Havit-only shortcut, switched off, from simulated input)"); [HookTest]::Tap(0x83); [HookTest]::Pump(800)
[HookTest]::Mark("5. tap Scroll Lock alone, twice"); [HookTest]::Tap(0x91); [HookTest]::Pump(300); [HookTest]::Tap(0x91); [HookTest]::Pump(600)
[HookTest]::Remove()
Stop-Process -Id $app.Id -ErrorAction SilentlyContinue

"keys that reached other apps:"
[HookTest]::Seen
""
"actions run:  a=$(Test-Path $files[0])  b=$(Test-Path $files[1])  c=$(Test-Path $files[2])  d=$(Test-Path $files[3])"
"Scroll Lock state unchanged: $($scrollBefore -eq [HookTest]::ScrollLockOn())"
$ok = (Test-Path $files[0]) -and (Test-Path $files[1]) -and -not (Test-Path $files[2]) -and -not (Test-Path $files[3]) -and ($scrollBefore -eq [HookTest]::ScrollLockOn())
"any-keyboard and Fn-key shortcuts ran, one-keyboard shortcuts left simulated keys alone => $(if ($ok) { 'PASS' } else { 'FAIL' })"
