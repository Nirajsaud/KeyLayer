param([string]$Exe, [string]$Dir, [switch]$NoGlobal)
# Simulated-key test of app exclusions:
#   F24 shortcut is off in powershell.exe (this script's own test window)
#   F23 shortcut has no exclusions, but every shortcut is off in charmap.exe (Settings list)
if (Get-Process KeyLayer -ErrorAction SilentlyContinue) { "KeyLayer is already running - not testing over it."; exit 1 }

$fa = Join-Path $Dir 'fired-f24.txt'; $fb = Join-Path $Dir 'fired-f23.txt'
function Cmd($f) { "echo fired> \`"$($f -replace '\\','\\')\`"" }
$config = @"
{
  "Version": 1, "TrayTipShown": true,
  "ExcludedApps": $(if ($NoGlobal) { '[]' } else { '[ { "Exe": "charmap.exe", "Name": "Character Map" } ]' }),
  "Mappings": [
    { "Name": "F24", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 135, "Modifiers": 0, "Description": "F24" },
      "Action": { "Type": "RunCommand", "Command": "$(Cmd $fa)" },
      "ExcludedApps": [ { "Exe": "powershell.exe", "Name": "Windows PowerShell" } ] },
    { "Name": "F23", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 134, "Modifiers": 0, "Description": "F23" },
      "Action": { "Type": "RunCommand", "Command": "$(Cmd $fb)" } }
  ]
}
"@
$configPath = Join-Path $Dir 'test-apps-config.json'
Set-Content $configPath $config -Encoding utf8

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
public static class AppTest {
    delegate IntPtr Proc(int n, IntPtr w, IntPtr l);
    static Proc proc; static IntPtr hook;
    public static List<int> Seen = new List<int>();
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
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public static void Install() { proc = Callback; hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0); }
    public static void Remove() { UnhookWindowsHookEx(hook); }
    static IntPtr Callback(int n, IntPtr w, IntPtr l) {
        if (n >= 0) { int vk = Marshal.ReadInt32(l); int msg = w.ToInt32(); if (msg == 0x100 || msg == 0x104) Seen.Add(vk); }
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
    public static void Tap(ushort vk) { Send(vk, false); Pump(60); Send(vk, true); Pump(60); }
    public static void Front(IntPtr h) { Tap(0xE8); SetForegroundWindow(h); Pump(400); }
    public static uint FrontPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
}
"@

$results = @()
function Phase($name, [IntPtr]$hwnd, $expectF24, $expectF23) {
    Remove-Item $fa, $fb -ErrorAction SilentlyContinue
    [AppTest]::Front($hwnd)
    $front = (Get-Process -Id ([AppTest]::FrontPid())).ProcessName
    [AppTest]::Seen.Clear()
    [AppTest]::Tap(0x87); [AppTest]::Tap(0x86)
    [AppTest]::Pump(1500)
    $f24 = Test-Path $fa; $f23 = Test-Path $fb
    $seen24 = [AppTest]::Seen.Contains(0x87); $seen23 = [AppTest]::Seen.Contains(0x86)
    $ok = ($f24 -eq $expectF24) -and ($f23 -eq $expectF23) -and ($seen24 -ne $f24) -and ($seen23 -ne $f23)
    "{0,-34} front={1,-11} F24 fired={2,-5} reached app={3,-5} | F23 fired={4,-5} reached app={5,-5} => {6}" -f $name, $front, $f24, $seen24, $f23, $seen23, $(if ($ok) { 'PASS' } else { 'FAIL' })
}

[AppTest]::Install()
$env:KEYLAYER_CONFIG = $configPath
$app = Start-Process $Exe -ArgumentList '--tray' -PassThru
Remove-Item Env:KEYLAYER_CONFIG
[AppTest]::Pump(3000)

$form = New-Object System.Windows.Forms.Form -Property @{ Text = 'KeyLayer app test'; Width = 360; Height = 160; TopMost = $true }
$form.Show(); [AppTest]::Pump(300)
$charmap = Start-Process charmap.exe -PassThru
for ($i = 0; $i -lt 40 -and $charmap.MainWindowHandle -eq 0; $i++) { [AppTest]::Pump(150); $charmap.Refresh() }

if ($NoGlobal) { Phase "Character Map (nothing off here)" $charmap.MainWindowHandle $true $true; Phase "Test window (F24 off here)" $form.Handle $false $true }
else {
Phase "Test window (F24 off here)" $form.Handle $false $true
Phase "Character Map (all off here)" $charmap.MainWindowHandle $false $false
Phase "Test window again" $form.Handle $false $true
}

$form.Close()
Stop-Process -Id $charmap.Id -ErrorAction SilentlyContinue
Stop-Process -Id $app.Id -ErrorAction SilentlyContinue
[AppTest]::Remove()
