# KeyLayer

Custom shortcuts for every keyboard on your Windows PC, including the Fn keys that never seemed to do anything.

Got a laptop keyboard and an external one? Each can have its own shortcuts. No Fn key? Pick a key to act as one.

## What it does

- **Remap Fn combos.** Many keyboards send signals like *Mail*, *Media Player* or *Browser Home* from their Fn row.
  Windows ignores most of them unless a default app is set up. KeyLayer catches them and runs your action instead.
- **Per-keyboard shortcuts.** Every connected keyboard is listed, built-in included, and each shortcut can apply to all
  keyboards or just one.
- **Your own Fn key.** Choose Caps Lock, Right Ctrl, Menu or similar. Hold it and press another key to run a shortcut;
  tap it alone and it works as normal. No app uses these combinations, so they never clash with app shortcuts.
- **Clash check.** When you pick a key, KeyLayer tells you if Windows or common apps already use it (Ctrl + S, Win + E,
  F5 …) and offers the same key on your Fn key instead.
- **Turn shortcuts off in chosen apps.** List apps (games, Premiere Pro, Photoshop …) where every key does its normal
  job, or turn off a single shortcut in some apps.
- **Actions:** open an app, file, folder or website · press a key combination · type text · media and volume ·
  screen brightness · lock / sleep · screenshot, show desktop, task view and more · run a command · switch a key off.
- **Test keys** page that shows exactly what each key press sends, and from which keyboard.

It runs quietly in the system tray and uses only a few MB of memory while its window is closed. The window follows the
Windows 11 look and your light/dark and accent settings.
Settings are saved in `%APPDATA%\KeyLayer\config.json`.

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

## Building

```bash
dotnet publish src/KeyLayer/KeyLayer.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

`KeyLayer.exe --selftest out.txt` writes what the app can see (keyboards, HID devices, hook status) to a file. That's
handy when reporting a problem.

## License

[MIT](LICENSE)
