# Measures the running KeyLayer: CPU while idle, CPU per key press, memory, and any GPU use.
# Key presses are F24 (does nothing in apps) sent to a small test window of our own.
$p = Get-Process KeyLayer -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { "KeyLayer isn't running"; exit 1 }
"KeyLayer $($p.MainModule.FileVersionInfo.FileVersion) at $($p.Path)"

function PrivateMB { (Get-Counter "\Process(KeyLayer)\Working Set - Private").CounterSamples[0].CookedValue / 1MB }
function Gpu {
    $s = (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction SilentlyContinue).CounterSamples |
        Where-Object { $_.InstanceName -like "pid_$($p.Id)_*" }
    if (-not $s) { 'no GPU engine in use' } else { '{0:N2}% GPU' -f ($s | Measure-Object CookedValue -Sum).Sum }
}

# ---- idle
$p.Refresh(); $c0 = $p.TotalProcessorTime; $t0 = Get-Date
Start-Sleep -Seconds 20
$p.Refresh(); $idle = ($p.TotalProcessorTime - $c0).TotalMilliseconds; $secs = ((Get-Date) - $t0).TotalSeconds
"Idle for {0:N0} s     : {1:N0} ms of CPU in total ({2:N3}% of one core), {3:N1} MB memory, {4}" -f $secs, $idle, ($idle / ($secs * 10)), (PrivateMB), (Gpu)

# ---- key presses
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices; using System.Threading;
public static class LT {
    [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KI ki; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static void Taps(int count, int gapMs) {
        for (int k = 0; k < count; k++) foreach (bool up in new[] { false, true }) {
            INPUT[] i = new INPUT[1]; i[0].type = 1; i[0].ki.vk = 0x87; if (up) i[0].ki.flags = 2;
            SendInput(1, i, Marshal.SizeOf(typeof(INPUT))); Thread.Sleep(gapMs);
        }
    }
}
"@
$form = New-Object System.Windows.Forms.Form -Property @{ Text = 'KeyLayer load test'; Width = 300; Height = 120; TopMost = $true }
$form.Show(); [System.Windows.Forms.Application]::DoEvents(); [LT]::SetForegroundWindow($form.Handle) | Out-Null
Start-Sleep -Milliseconds 300
[LT]::Taps(600, 4); Start-Sleep -Seconds 2   # warm up first, like after a few minutes of normal typing
$p.Refresh(); $c0 = $p.TotalProcessorTime; $t0 = Get-Date
[LT]::Taps(500, 8)   # 500 presses in about 8 seconds: much faster than anyone types
$p.Refresh(); $busy = ($p.TotalProcessorTime - $c0).TotalMilliseconds; $secs = ((Get-Date) - $t0).TotalSeconds
$form.Close()
"500 key presses ({0:N0} s): {1:N0} ms of CPU in total = {2:N3} ms per key press ({3:N2}% of one core), {4:N1} MB memory, {5}" -f $secs, $busy, ($busy / 500), ($busy / ($secs * 10)), (PrivateMB), (Gpu)
"threads: $($p.Threads.Count)"
