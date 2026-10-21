param([string]$Setup, [string]$Out)
# Walks through the installer's pages taking screenshots, then cancels before anything is installed.
# Then opens the uninstaller's goodbye window in preview mode, takes a screenshot and closes it.
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class SW {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    public struct RECT { public int L, T, R, B; }
    static string Text(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
    public static IntPtr Top(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => { if (IsWindowVisible(h) && Text(h) == title) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }
    // Clicks a visible button whose caption (without the & accelerator) matches
    public static bool Click(IntPtr top, string caption) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(top, (h, l) => {
            var c = new StringBuilder(64); GetClassName(h, c, 64);
            if (IsWindowVisible(h) && c.ToString().Contains("Button") && Text(h).Replace("&", "") == caption) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return false;
        PostMessage(found, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
        return true;
    }
}
"@
[SW]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null

function WaitFor([string]$title, [int]$seconds = 20) {
    for ($i = 0; $i -lt $seconds * 4; $i++) { $h = [SW]::Top($title); if ($h -ne [IntPtr]::Zero) { return $h }; Start-Sleep -Milliseconds 250 }
    throw "No window titled '$title'"
}
function Shot([IntPtr]$h, [string]$name) {
    Start-Sleep -Milliseconds 700
    $r = New-Object SW+RECT; [SW]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [SW]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $g.Dispose()
    $bmp.Save((Join-Path $Out "$name.png")); $bmp.Dispose()
    "  saved $name"
}

New-Item -ItemType Directory $Out -Force | Out-Null
"--- installer"
Start-Process $Setup
$w = WaitFor 'Setup - KeyLayer'
Shot $w '1-welcome'
[SW]::Click($w, 'Next') | Out-Null; Shot $w '2-tasks'
[SW]::Click($w, 'Next') | Out-Null; Shot $w '3-fn-key'
[SW]::Click($w, 'Next') | Out-Null; Shot $w '4-ready'
[SW]::Click($w, 'Cancel') | Out-Null
$confirm = WaitFor 'Exit Setup' 10
[SW]::Click($confirm, 'Yes') | Out-Null
Start-Sleep -Seconds 2
"  cancelled before installing: installer still open = $([SW]::Top('Setup - KeyLayer') -ne [IntPtr]::Zero)"

"--- goodbye window (preview)"
Start-Process $Setup -ArgumentList '/PREVIEWGOODBYE=1'
$bye = WaitFor 'Uninstall KeyLayer'
Shot $bye '5-goodbye'
[SW]::Click($bye, 'Keep my shortcuts') | Out-Null
Start-Sleep -Seconds 1
"  goodbye window closed: $([SW]::Top('Uninstall KeyLayer') -eq [IntPtr]::Zero)"
