param([string]$Exe, [string]$Dir)
# Simulated-key test of: repeat while held, act as another key (+ release when KeyLayer comes to the front),
# the three typing methods, and turning shortcuts off in a store app (Calculator).
if (Get-Process KeyLayer -ErrorAction SilentlyContinue) { "KeyLayer is already running - not testing over it."; exit 1 }

# ---- Calculator first, to learn the store app's real program name
$calcWasOpen = [bool](Get-Process CalculatorApp -ErrorAction SilentlyContinue)
if (-not $calcWasOpen) { Start-Process calc.exe; Start-Sleep -Seconds 3 }
$calcProc = Get-Process CalculatorApp -ErrorAction SilentlyContinue | Select-Object -First 1
$calcExe = if ($calcProc) { Split-Path $calcProc.Path -Leaf } else { 'CalculatorApp.exe' }

$fired = Join-Path $Dir 'fired-calc.txt'
$config = @"
{
  "Version": 1, "TrayTipShown": true,
  "ExcludedApps": [ { "Exe": "$calcExe", "Name": "Calculator" } ],
  "Mappings": [
    { "Name": "repeat", "Enabled": true, "BlockOriginal": true, "RepeatWhileHeld": true,
      "Trigger": { "Kind": "Key", "Vk": 135, "Modifiers": 0, "Description": "F24" },
      "Action": { "Type": "SendShortcut", "ShortcutVk": 124, "ShortcutMods": 0 } },
    { "Name": "no repeat", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 134, "Modifiers": 0, "Description": "F23" },
      "Action": { "Type": "SendShortcut", "ShortcutVk": 125, "ShortcutMods": 0 } },
    { "Name": "act as Shift", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 133, "Modifiers": 0, "Description": "F22" },
      "Action": { "Type": "HoldKey", "ShortcutVk": 160, "ShortcutMods": 0 } },
    { "Name": "act as Ctrl", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 132, "Modifiers": 0, "Description": "F21" },
      "Action": { "Type": "HoldKey", "ShortcutVk": 162, "ShortcutMods": 0 } },
    { "Name": "type normal", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 131, "Modifiers": 0, "Description": "F20" },
      "Action": { "Type": "TypeText", "Text": "Hi there!", "TypeMethod": "Normal" } },
    { "Name": "type keys", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 130, "Modifiers": 0, "Description": "F19" },
      "Action": { "Type": "TypeText", "Text": "Key@Test 1", "TypeMethod": "Keys" } },
    { "Name": "type paste", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 129, "Modifiers": 0, "Description": "F18" },
      "Action": { "Type": "TypeText", "Text": "Pasted \u00e9 text", "TypeMethod": "Paste" } },
    { "Name": "calc test", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 128, "Modifiers": 0, "Description": "F17" },
      "Action": { "Type": "RunCommand", "Command": "echo fired> \"$($fired -replace '\\','\\')\"" } }
  ]
}
"@
$configPath = Join-Path $Dir 'test-feature-config.json'
Set-Content $configPath $config -Encoding utf8

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
public static class FT {
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
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

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
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT))); Pump(60);
    }
    public static void Tap(ushort vk) { Send(vk, false); Send(vk, true); }
    public static void Front(IntPtr h) { Send(0xE8, false); Send(0xE8, true); SetForegroundWindow(h); Pump(400); }
    public static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
}
"@
function Count($p) { @([FT]::Seen | Where-Object { $_ -eq $p }).Count }
function Result($ok) { if ($ok) { 'PASS' } else { 'FAIL' } }

$userClip = Get-Clipboard -Raw
Set-Clipboard -Value 'ORIGINAL CLIPBOARD'
[FT]::Install()
$env:KEYLAYER_CONFIG = $configPath
$app = Start-Process $Exe -PassThru      # window shown: needed for the stuck-key check
Remove-Item Env:KEYLAYER_CONFIG
[FT]::Pump(3000); $app.Refresh()

$form = New-Object System.Windows.Forms.Form -Property @{ Text = 'KeyLayer feature test'; Width = 480; Height = 160 }
$box = New-Object System.Windows.Forms.TextBox -Property @{ Dock = 'Fill'; Multiline = $true }
$form.Controls.Add($box); $form.Show(); [FT]::Pump(300)
[FT]::Front($form.Handle); $box.Focus() | Out-Null

# ---- repeat while held: one press + 4 auto-repeats
[FT]::Seen.Clear()
[FT]::Send(0x87, $false); for ($i = 0; $i -lt 4; $i++) { [FT]::Send(0x87, $false) }; [FT]::Send(0x87, $true)
[FT]::Send(0x86, $false); for ($i = 0; $i -lt 4; $i++) { [FT]::Send(0x86, $false) }; [FT]::Send(0x86, $true)
[FT]::Pump(600)
$rep = Count '7Cv*'; $norep = Count '7Dv*'
"Repeat while held      : held key ran it {0} times (want 5), without the option {1} time (want 1)  => {2}" -f $rep, $norep, (Result ($rep -eq 5 -and $norep -eq 1))

# ---- act as another key (F22 acts as Left Shift)
[FT]::Seen.Clear(); $box.Clear()
[FT]::Send(0x85, $false); [FT]::Pump(150)
$shiftHeld = [FT]::Down(0x10)
[FT]::Tap(0x41)                                   # 'a' while held -> 'A'
[FT]::Send(0x85, $false); [FT]::Send(0x85, $false) # auto-repeat of the trigger
[FT]::Send(0x85, $true); [FT]::Pump(200)
$shiftAfter = [FT]::Down(0x10)
$leaked = Count '85v'
"Act as another key     : Shift held while F22 held={0}, typed '{1}' (want A), Shift released after={2}, F22 reached app {3}x  => {4}" -f $shiftHeld, $box.Text, (-not $shiftAfter), $leaked, (Result ($shiftHeld -and $box.Text -eq 'A' -and -not $shiftAfter -and $leaked -eq 0))

# ---- held key let go when KeyLayer's window comes to the front (F21 acts as Ctrl)
[FT]::Send(0x84, $false); [FT]::Pump(150)
$ctrlHeld = [FT]::Down(0x11)
[FT]::Front($app.MainWindowHandle)
$ctrlAfterSwitch = [FT]::Down(0x11)
[FT]::Send(0x84, $true)
[FT]::Front($form.Handle); $box.Focus() | Out-Null
"Stuck-key safety       : Ctrl held={0}, released when KeyLayer came to the front={1}  => {2}" -f $ctrlHeld, (-not $ctrlAfterSwitch), (Result ($ctrlHeld -and -not $ctrlAfterSwitch))

# ---- typing methods
$box.Clear(); [FT]::Tap(0x83); [FT]::Pump(800); $t1 = $box.Text
"Type: normal           : '{0}'  => {1}" -f $t1, (Result ($t1 -eq 'Hi there!'))
$box.Clear(); [FT]::Tap(0x82); [FT]::Pump(1500); $t2 = $box.Text
"Type: key presses      : '{0}'  => {1}" -f $t2, (Result ($t2 -eq 'Key@Test 1'))
$box.Clear(); [FT]::Tap(0x81); [FT]::Pump(2500); $t3 = $box.Text; $clip = Get-Clipboard
"Type: paste            : '{0}', clipboard put back='{1}'  => {2}" -f $t3, $clip, (Result ($t3 -eq "Pasted $([char]0xe9) text" -and $clip -eq 'ORIGINAL CLIPBOARD'))

# ---- store app: shortcuts off in Calculator
$calcWin = [FT]::FindWindow([NullString]::Value, 'Calculator')
Remove-Item $fired -ErrorAction SilentlyContinue
[FT]::Front($calcWin)
$fg = [FT]::GetForegroundWindow(); $fgPid = 0; [void][FT]::GetWindowThreadProcessId($fg, [ref]$fgPid)
"  calc window 0x{0:X}, in front 0x{1:X} ({2})" -f $calcWin.ToInt64(), $fg.ToInt64(), (Get-Process -Id $fgPid).ProcessName
[FT]::Seen.Clear(); [FT]::Tap(0x80); [FT]::Pump(1500)
$ranInCalc = Test-Path $fired; $reachedCalc = (Count '80v') -gt 0
Remove-Item $fired -ErrorAction SilentlyContinue
[FT]::Front($form.Handle); [FT]::Tap(0x80); [FT]::Pump(1500)
$ranElsewhere = Test-Path $fired
"Store app ($calcExe): ran in Calculator={0} (want False), key reached it={1}, ran elsewhere={2}  => {3}" -f $ranInCalc, $reachedCalc, $ranElsewhere, (Result (-not $ranInCalc -and $reachedCalc -and $ranElsewhere))

$form.Close()
Stop-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $calcWasOpen) { Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process }
[FT]::Remove()
if ($userClip) { Set-Clipboard -Value $userClip } else { Set-Clipboard -Value $null }
"keys still down after test: Shift={0} Ctrl={1}" -f [FT]::Down(0x10), [FT]::Down(0x11)
