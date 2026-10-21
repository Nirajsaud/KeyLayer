param([string]$Exe = "$PSScriptRoot\..\dist\KeyLayer.exe")
# Runs every functional test against a KeyLayer build and prints a summary. Each test starts its own KeyLayer with a
# throwaway settings file, so your own shortcuts and settings are never touched.
$Exe = (Resolve-Path $Exe).Path
if (Get-Process KeyLayer -ErrorAction SilentlyContinue) {
    "KeyLayer is running. Quit it first (right-click the tray icon > Quit), then run the tests again."
    exit 1
}
$work = Join-Path $env:TEMP 'keylayer-tests'
New-Item -ItemType Directory $work -Force | Out-Null
$drive = "$PSScriptRoot\tools\drive.ps1"

$tests = [ordered]@{
    'Repeat, act as another key, typing methods, store apps' = { & "$PSScriptRoot\test-features.ps1" -Exe $Exe -Dir $work }
    'Held modifiers and the KeyLayer Fn key'                 = { & "$PSScriptRoot\test-modifiers-and-fn.ps1" -Exe $Exe -Dir $work }
    'Shortcuts off in chosen apps'                           = { & "$PSScriptRoot\test-app-exclusions.ps1" -Exe $Exe -Dir $work }
    'Shortcuts off in chosen apps (no app list)'             = { & "$PSScriptRoot\test-app-exclusions.ps1" -Exe $Exe -Dir $work -NoGlobal }
    'Shortcuts while the editor is open'                     = { & "$PSScriptRoot\test-editor.ps1" -Exe $Exe -Dir $work -Drive $drive }
    'KeyLayer Fn key and one-keyboard shortcuts'             = { & "$PSScriptRoot\test-fn-and-keyboards.ps1" -Exe $Exe -Dir $work }
}

$failed = 0
foreach ($name in $tests.Keys) {
    "=== $name"
    $out = & $tests[$name] 2>&1 | ForEach-Object { "$_" }
    $out | Where-Object { $_ -match 'PASS|FAIL|=> OK|=> BUG' } | ForEach-Object { "  $_" }
    $bad = @($out | Where-Object { $_ -match 'FAIL|=> BUG' }).Count
    if ($bad -gt 0 -or -not ($out -match 'PASS|=> OK')) { $failed++; "  ^ problem, full output:"; $out | ForEach-Object { "    $_" } }
    Start-Sleep -Seconds 1
}
""
if ($failed -eq 0) { "All $($tests.Count) test groups passed." } else { "$failed of $($tests.Count) test groups had problems."; exit 1 }
