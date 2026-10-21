param([string]$Exe, [string]$Drive, [switch]$KeepRunning)
# Measures KeyLayer's memory: in the tray, with the window open on every page, and after closing the window.
if (Get-Process KeyLayer -ErrorAction SilentlyContinue) { Get-Process KeyLayer | Stop-Process; Start-Sleep -Milliseconds 800 }

function Mem($label) {
    $p = Get-Process KeyLayer -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { "{0,-36} not running" -f $label; return }
    $p.Refresh()
    $priv = (Get-Counter "\Process(KeyLayer)\Working Set - Private" -ErrorAction SilentlyContinue).CounterSamples[0].CookedValue
    "{0,-36} Task Manager {1,6:N1} MB | working set {2,6:N1} MB | private bytes {3,6:N1} MB | threads {4,3} | modules {5}" -f $label, ($priv / 1MB), ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB), $p.Threads.Count, $p.Modules.Count
}

$app = Start-Process $Exe -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds 6
Mem "1. In the tray (window never opened)"

Start-Process $Exe            # a second launch shows the window
Start-Sleep -Seconds 3
foreach ($page in 'Keyboards', 'Test keys', 'Settings', 'Shortcuts') {
    & $Drive -ProcessId $app.Id -Window "KeyLayer" -Action select -Name $page | Out-Null
    Start-Sleep -Milliseconds 700
}
Start-Sleep -Seconds 2
Mem "2. Window open, all pages visited"

Add-Type -Namespace M -Name W -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);'
$app.Refresh()
[M.W]::PostMessage($app.MainWindowHandle, 0x0010 <# WM_CLOSE #>, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Start-Sleep -Seconds 8
Mem "3. Window closed back to the tray"
Start-Sleep -Seconds 20
Mem "4. Tray, 20 s later"

if (-not $KeepRunning) { Stop-Process -Id $app.Id }
