# Tests

These scripts check KeyLayer the way a person would use it: they start a real KeyLayer, press keys with simulated
input, bring real windows to the front (a small test window, Character Map, Calculator) and check what happened.
Each test uses its own throwaway settings file (`KEYLAYER_CONFIG`), so your own shortcuts and settings are never
touched.

They need Windows PowerShell (built into Windows) and a desktop session. Don't touch the keyboard or mouse while they
run: they take a few minutes and move windows to the front.

## Running them

Build KeyLayer, quit any running copy (tray icon > Quit), then:

```powershell
dotnet publish src/KeyLayer/KeyLayer.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-all.ps1
```

It ends with `All 6 test groups passed.` or shows the full output of whatever failed.

## What each one checks

| Script | Checks |
|---|---|
| `test-features.ps1` | Repeat while held · act as another key (and that it's let go when KeyLayer comes to the front) · the three typing methods, including putting your clipboard back after pasting · shortcuts off in a store app (Calculator) |
| `test-modifiers-and-fn.ps1` | Holding Ctrl and running a shortcut twice · the KeyLayer Fn key behaving like a normal key in an app where shortcuts are off · the Fn key never getting stuck when KeyLayer's window comes to the front |
| `test-app-exclusions.ps1` | Shortcuts off in every app on the list, or for one shortcut only, and still running everywhere else (run with and without `-NoGlobal`) |
| `test-editor.ps1` | Shortcuts don't run while you type in the shortcut editor, but do in the main window and in other apps |
| `test-fn-and-keyboards.ps1` | Any-keyboard shortcuts and KeyLayer Fn key shortcuts run; one-keyboard shortcuts leave keys from other sources alone; Scroll Lock tapped alone still toggles |

## Measuring and checking

| Script | What it's for |
|---|---|
| `measure-memory.ps1` | Memory in the tray, with the window open on every page, and after closing it |
| `measure-cpu.ps1` | CPU while idle and per key press, memory and GPU use of the running KeyLayer |
| `check-installer-screens.ps1` | Screenshots of each installer page (cancelling before anything is installed) and the uninstaller's goodbye window |
| `tools/fn-listening-test.ps1` | Listens to every channel a keyboard can use while you press Fn, to see whether a keyboard reports its Fn key at all |
| `tools/drive.ps1`, `tools/capture.ps1` | Helpers: click buttons in KeyLayer's windows, and take a screenshot of a window |

`test-fn-and-keyboards.ps1` uses a keyboard ID (`1A2C:90FE`) in its settings. Simulated keys come from no keyboard,
so shortcuts limited to that keyboard must *not* run; that's what the test checks.
