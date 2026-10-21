param([string]$Exe, [string]$Dir, [string]$Drive)
# Shortcuts while the shortcut editor is open: off when typing into the editor, on in other apps.
if (Get-Process KeyLayer -ErrorAction SilentlyContinue) { "KeyLayer is already running - not testing over it."; exit 1 }

$fired = Join-Path $Dir 'fired-editor.txt'
$config = @"
{
  "Version": 1, "TrayTipShown": true,
  "Mappings": [
    { "Name": "F24", "Enabled": true, "BlockOriginal": true,
      "Trigger": { "Kind": "Key", "Vk": 135, "Modifiers": 0, "Description": "F24" },
      "Action": { "Type": "RunCommand", "Command": "echo fired> \"$($fired -replace '\\','\\')\"" } }
  ]
}
"@
$configPath = Join-Path $Dir 'test-editor-config.json'
Set-Content $configPath $config -Encoding utf8

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class EdTest {
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint msg; public IntPtr w, l; public uint time; public int x, y; }
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint a, uint b, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KI ki; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static string FrontInfo() { IntPtr h = GetForegroundWindow(); var s = new System.Text.StringBuilder(200); GetWindowText(h, s, 200); uint pid; GetWindowThreadProcessId(h, out pid); return pid + " \"" + s + "\""; }
    public static void Pump(int ms) {
        int end = Environment.TickCount + ms; MSG m;
        while (Environment.TickCount < end) { while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); } Thread.Sleep(5); }
    }
    static void Send(ushort vk, bool up) { INPUT[] i = new INPUT[1]; i[0].type = 1; i[0].ki.vk = vk; if (up) i[0].ki.flags = 2; SendInput(1, i, Marshal.SizeOf(typeof(INPUT))); Pump(70); }
    public static void Tap(ushort vk) { Send(vk, false); Send(vk, true); }
    public static void Front(IntPtr h) { Tap(0xE8); SetForegroundWindow(h); Pump(500); }
}
"@

function Check($name, [IntPtr]$hwnd, $expect) {
    Remove-Item $fired -ErrorAction SilentlyContinue
    [EdTest]::Front($hwnd)
    $inFront = [EdTest]::FrontInfo()
    [EdTest]::Tap(0x87); [EdTest]::Pump(1500)
    $ran = Test-Path $fired
    "{0,-40} front={1,-32} shortcut ran={2,-5} => {3}" -f $name, $inFront, $ran, $(if ($ran -eq $expect) { 'PASS' } else { 'FAIL' })
}

$env:KEYLAYER_CONFIG = $configPath
$app = Start-Process $Exe -PassThru
Remove-Item Env:KEYLAYER_CONFIG
[EdTest]::Pump(3000); $app.Refresh()
$form = New-Object System.Windows.Forms.Form -Property @{ Text = 'KeyLayer editor test'; Width = 360; Height = 160 }
$form.Show(); [EdTest]::Pump(300)

Check "KeyLayer main window (still runs)" $app.MainWindowHandle $true
[EdTest]::Front($app.MainWindowHandle)
& $Drive -ProcessId $app.Id -Window "KeyLayer" -Action invoke -Name "Add shortcut" | Out-Null
[EdTest]::Pump(1500)
$editor = [EdTest]::FindWindow($null, "Add shortcut")
"editor hwnd: $editor  KeyLayer pid: $($app.Id)"
Check "Shortcut editor open and in front" $editor $false
Check "Another app while the editor is open" $form.Handle $true

$form.Close()
Stop-Process -Id $app.Id -ErrorAction SilentlyContinue
