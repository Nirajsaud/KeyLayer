## Download

| File | What it is |
|---|---|
| **KeyLayer-Setup.exe** | **Start here.** Installs KeyLayer just for you (no admin rights), with a Start menu shortcut and an uninstall entry in Settings › Apps. Nothing else to install. |
| KeyLayer-…-win-x64.zip | Portable: unzip it anywhere and run `KeyLayer.exe`. Nothing is installed. |
| SHA256SUMS.txt | Checksums, to check your download is the real one. |

**"Windows protected your PC"?** KeyLayer isn't code-signed yet, so Windows SmartScreen may warn the first time you
run it. Click **More info**, then **Run anyway**. KeyLayer is open source and sends nothing anywhere; see the
[README](https://github.com/Nirajsaud/KeyLayer#privacy) for what it does with your key presses.

## What's new in 1.2.2

Your keyboard's own Fn key never reaches Windows (the keyboard handles it), so Fn + H arrives as just H. KeyLayer now
makes that clear and points you to the fix:

- **First start:** a card explains this and offers to make **Caps Lock** your **KeyLayer Fn key** in one click
  (or pick another key).
- **"Did you hold Fn?"** If you capture a plain key, the editor says what happened and offers *Use Caps Lock + H
  instead*.
- A tip in the key box before you press, clearer Test keys and Help text, and the setting is now called
  **KeyLayer Fn key**.
- Fix: trying a second copy of KeyLayer (like the portable zip) no longer takes over *Start with Windows* from your
  installed copy.

Updating from 1.2.1: run the new `KeyLayer-Setup.exe`. Your shortcuts are kept.

## What KeyLayer does

Custom shortcuts for every keyboard on your PC, including the Fn keys.

- Remap Fn and media keys (Mail, Calculator, Browser Home …) that Windows ignores, or any key or combination
- Different shortcuts per keyboard (laptop and external), and a KeyLayer Fn key (Caps Lock, Right Ctrl …)
- Actions: open apps, files and websites · press a key combination · act as another key while held · type text
  (typed, key by key, or pasted) · media, volume and brightness · window and desktop controls · run a command
- Repeat while held, a clash check that warns about shortcuts Windows and apps already use, and lists of apps
  (store apps included) where shortcuts stay off
- Test keys page that shows what every key sends and from which keyboard
- About 1 MB of memory while it sits in the tray

See the [README](https://github.com/Nirajsaud/KeyLayer#readme) for everything it does and its known limitations.
