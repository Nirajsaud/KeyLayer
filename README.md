# KeyLayer

Custom shortcuts for every keyboard on your Windows PC, including the Fn keys that never seemed to do anything.

Got a laptop keyboard and an external one? Each can have its own shortcuts. No Fn key? Pick a key to act as one.

## Features

### Shortcuts
- **Fn combos.** Many keyboards send signals like *Mail*, *Media Player* or *Browser Home* from their Fn row. Windows
  ignores most of them unless a default app is set up. KeyLayer catches them and runs your action instead.
- **Any key or combination** (a single key, or Ctrl / Shift / Alt / Win + a key), plus media, power and
  vendor-specific signals from external keyboards.
- **Your own Fn key.** Make Caps Lock, Right Ctrl, Right Alt, Menu, Scroll Lock, Pause or Insert an Fn key. Hold it and
  press another key to run a shortcut; tap it alone and it does its normal job.
- **Per keyboard.** Each shortcut works on every keyboard or just one, so the same key can do different things on your
  laptop and external keyboards.
- **Key capture.** Click the key box and press the key; KeyLayer records it and the keyboard it came from.
- **Per-shortcut options:** switch off the key's normal job or keep it, **repeat while held** (like holding a volume
  key), give it a name, turn it on or off, edit, delete, or "Try it now".

### Actions
- Open an app, file, folder or website (with arguments; `ms-settings:` links work too)
- Press a key combination (record it or build it from lists)
- **Act as another key:** the key is held down for as long as you hold yours, e.g. Caps Lock acting as Ctrl, or
  remapping keys for a game
- Type text, including new lines and tabs, in one of three ways: typed as characters (most apps), pressed key by key
  (games and remote desktop), or pasted (fast for long text; your clipboard is put back and the text stays out of
  clipboard history)
- Media and system controls:
  - play / pause, next, previous, stop · volume up / down, mute · screen brightness up / down
  - show desktop, Task View, screenshot, clipboard history, emoji panel
  - minimise or close the current window, previous / next virtual desktop
  - open File Explorer, Calculator, Task Manager or Windows Settings · lock the PC, sleep
- Run a command (with or without a window)
- Switch the key off

### Keyboards
- Every keyboard is found at startup, without a key press: the laptop's own keyboard (with its hotkeys) as one
  **Built-in keyboard**, external USB and Bluetooth keyboards, and mouse dongles that can send keys (**receivers**).
- Keyboards plugged in or out are picked up on the fly.
- A keyboard's card lights up ("Typing now") when you use it and shows its last key time.
- Rename keyboards, and filter the shortcut list by keyboard.

### Avoiding clashes with other apps
- **Clash check:** a key you pick is rated *Won't clash*, *Might clash* or *Windows uses this*, with the reason
  (e.g. "Almost every app uses Ctrl + S to save").
- **One-click fix:** "Use Caps Lock + S instead", which also sets up the Fn key if you don't have one yet.
- **Turn every shortcut off in chosen apps** (games, Premiere Pro …), or **one shortcut** in chosen apps. Pick from the
  apps you have open, with their icons, or browse for a program. Store apps (Calculator, Photos …) are recognised one by
  one.
- Warnings for a key that already has a shortcut, Fn shortcuts while the Fn key is off, and power keys Windows still
  acts on.

### Test keys
- A live list of every key and signal: which keyboard sent it, its code, and whether it's a key or a media signal.
  A media key and its signal show as one entry; keys sent by software show as "from Windows or another app".
- **Make shortcut** straight from an entry, or jump to the shortcut a key already has.

### App
- Runs in the system tray: left-click to open, right-click for Open, Pause shortcuts and Quit.
- **Pause all shortcuts** from the tray, Settings or the status button in the sidebar.
- **Start with Windows** (quietly, in the tray). Launching it again just opens the window.
- Windows 11 look: light, dark or system theme, your accent colour, keyboard and screen reader friendly.
- A notification when an action fails, and a "Ran: …" note for the last one.
- Help answers in Settings, **Copy diagnostics** for bug reports, and a button to open the settings folder.

### Under the hood
- **Lightweight:** about 1 MB of memory in the tray. The window is built when you open it and freed when you close it,
  and it's drawn without the GPU, so the graphics driver never loads into KeyLayer.
- **Self-repairing:** if Windows drops the keyboard hook (it can, silently), KeyLayer puts it back, also after sleep
  and unlocking.
- Modifiers you're still holding stay held after a shortcut types something, so you can run it again.
- Shortcuts don't run while you're typing into KeyLayer's own shortcut editor.

## Privacy

KeyLayer sees every key you press, because that's how it works, so here's exactly what it does with them:

- **Nothing leaves your PC.** There's no network code in KeyLayer at all: no updates check, no analytics, no
  telemetry. (Links you click, like "View on GitHub", or websites your shortcuts open, go through your browser.)
- **Key presses aren't saved.** They're matched against your shortcuts and forgotten. The Test keys list is only
  filled while that page is open, is never written to disk, and is gone when you close the window.
- The only files it writes are your settings (`%APPDATA%\KeyLayer\config.json`: shortcuts, keyboard names, app lists,
  theme) and, if you ask for it, a diagnostics report listing your keyboards and HID devices.
- It's open source, so you can check all of this in the code.

## Requirements

- Windows 10 or 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Getting started

1. Open **Test keys** and press your Fn combos one by one.
   - Anything that shows up there can be remapped.
   - Combos that show **nothing** are handled inside the keyboard itself (usually backlight or lighting controls),
     and no app can change them.
2. On **Shortcuts**, click **Add shortcut**, click the key box and press the key or combo.
3. Pick a keyboard (or *Any keyboard*) and choose what it should do.

To make your own Fn key, open **Settings** and pick a **Custom Fn key**. Then capture shortcuts while holding it.

## Avoiding clashes with other apps

A shortcut takes over its key in every app, and apps don't tell Windows which shortcuts they use. So:

- Prefer keys no app uses: your **custom Fn key** + any key, Fn **media and launch keys** (Mail, Calculator …) and
  F13–F24.
- Watch the clash check in the shortcut editor. For a risky key it offers the same key on your Fn key in one click.
- Use **Settings › Apps** to switch every shortcut off while certain apps are in front, or a shortcut's own
  **Turn this shortcut off in these apps** option for just that one. Apps are matched by their program file
  (e.g. `Adobe Premiere Pro.exe`).

## Good to know

- Windows never sees the Fn key itself; the keyboard handles it and sends a different signal instead. That's why only
  combos that show up on the Test keys page can be remapped.
- All keyboards are detected at startup. A laptop's own keyboard and its hotkey devices are grouped as one
  **Built-in keyboard**. Mouse dongles that can also send keys are listed as **receivers**.
- The same key can do different things on different keyboards. Windows only says which keyboard a key came from when
  the key is released. So:
  - A one-keyboard shortcut on an **ordinary key** runs when you let go of the key.
  - On the other keyboards that key keeps its normal job, also on release, and holding it won't auto-repeat.
  - **Media and launch keys** (volume, play/pause, Mail, Calculator, browser keys) carry their own signal naming the
    keyboard, so they react right away.
- While KeyLayer's own window is active, Windows doesn't let it switch keys off, so shortcuts still run but the key
  also does its normal job. Everywhere else they work as set.
- Shortcuts don't fire while an app running as administrator is in front, unless KeyLayer also runs as administrator.

## Limitations

- Two keyboards of the exact same model can't be told apart, so they share their shortcuts.
- A shortcut limited to one keyboard on an ordinary key runs when you let go (that's when Windows says which keyboard
  it was), so it can't repeat while held or act as a held key. On any keyboard, and for Fn and media keys, both work.
- The clash check knows the shortcuts of Windows and common apps, not every app's own shortcuts.
- Games with anti-cheat that ignore all simulated input won't see typed text or keys from KeyLayer, whichever typing
  method you pick.

## Building

```bash
dotnet publish src/KeyLayer/KeyLayer.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

`KeyLayer.exe --selftest out.txt` writes what the app can see (keyboards, HID devices, hook status) to a file. That's
handy when reporting a problem.

## License

[MIT](LICENSE)
