using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyLayer;

// Finding keyboards: devices, the built-in keyboard, receivers, and which one was typed on.
internal sealed unsafe partial class InputEngine
{
    // ------------------------------------------------------------------ devices

    HidDevice? GetDevice(nint handle)
    {
        if (devices.TryGetValue(handle, out var device)) return device;
        device = HidDevice.Open(handle);
        if (device is not null) devices[handle] = device;
        return device;
    }

    KeyboardInfo? KeyboardFor(nint handle)
    {
        if (handle == 0) return null;
        if (keyboardHandles.TryGetValue(handle, out var known)) return known;

        KeyboardInfo? keyboard = null;
        string path = RawDevices.Name(handle);
        if (path.Length > 0)
        {
            var (id, connection, external) = RawDevices.KeyboardIdentity(path);
            if (!external)
            {
                keyboard = KeyboardInfo.BuiltIn;
            }
            else
            {
                string product = RawDevices.Product(path);
                if (product.Length == 0) product = RawDevices.Description(path);
                // A dongle that is also a mouse: usually a mouse (or combo) receiver.
                if (mouseIds.Contains(id)) connection += " receiver";
                keyboard = new KeyboardInfo(id, product, connection, true);
            }
        }
        keyboardHandles[handle] = keyboard;
        return keyboard;
    }

    void NoteActivity(KeyboardInfo keyboard)
    {
        bool added = keyboard.External ? externalKeyboards.TryAdd(keyboard.Id, keyboard) : !builtInPresent;
        builtInPresent |= !keyboard.External;
        if (added) PublishKeyboards();

        int now = Environment.TickCount;
        if (keyboard.Id == lastActiveKeyboard && now - lastActivityTick < 1000) return;
        lastActiveKeyboard = keyboard.Id;
        lastActivityTick = now;
        string id = keyboard.Id;
        ui.Post(_ => KeyboardActivity?.Invoke(id), null);
    }

    void PublishKeyboards()
    {
        var external = externalKeyboards.Values.OrderBy(k => k.IsReceiver).ThenBy(k => k.Product).ToList();
        IReadOnlyList<KeyboardInfo> next =
        [
            .. external.Where(k => !k.IsReceiver),
            .. (builtInPresent ? new[] { KeyboardInfo.BuiltIn } : Array.Empty<KeyboardInfo>()),
            .. external.Where(k => k.IsReceiver),
        ];
        if (next.SequenceEqual(Keyboards)) return;
        Keyboards = next;
        ui.Post(_ => KeyboardsChanged?.Invoke(), null);
    }

    void RefreshDevices()
    {
        var list = RawDevices.List();
        var present = list.Select(d => d.hDevice).ToHashSet();
        foreach (var gone in devices.Keys.Where(h => !present.Contains(h)).ToList())
        {
            devices[gone].Dispose();
            devices.Remove(gone);
        }

        mouseIds.Clear();
        foreach (var d in list.Where(d => d.dwType == Native.RIM_TYPEMOUSE))
            if (RawDevices.KeyboardIdentity(RawDevices.Name(d.hDevice)) is { External: true } mouse)
                mouseIds.Add(mouse.Id);

        keyboardHandles.Clear(); // handles can be reused after a device is unplugged
        externalKeyboards.Clear();
        builtInPresent = false;
        foreach (var d in list.Where(d => d.dwType == Native.RIM_TYPEKEYBOARD))
        {
            if (KeyboardFor(d.hDevice) is not { } keyboard) continue;
            if (keyboard.External) externalKeyboards.TryAdd(keyboard.Id, keyboard);
            else builtInPresent = true;
        }

        // Keyboards (to learn which one sent a key), media/consumer keys and system power keys from any device,
        // plus the vendor channels of external keyboards.
        var wanted = new List<(ushort Page, ushort Usage)> { (0x01, 0x06), (0x0C, 0x01), (0x01, 0x80) };
        foreach (var d in list.Where(d => d.dwType == Native.RIM_TYPEHID))
        {
            var device = GetDevice(d.hDevice);
            if (device is { UsagePage: >= 0xFF00 } && externalKeyboards.ContainsKey(device.KeyboardId) && !wanted.Contains((device.UsagePage, device.Usage)))
                wanted.Add((device.UsagePage, device.Usage));
        }

        // Register each usage once: every registration makes Windows announce all present devices again,
        // and those announcements trigger this refresh.
        foreach (var (page, usage) in wanted.Where(w => !registered.Contains(w)))
        {
            Native.RAWINPUTDEVICE[] registration =
            [
                new() { usUsagePage = page, usUsage = usage, dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY, hwndTarget = window!.Handle },
            ];
            if (Native.RegisterRawInputDevices(registration, 1, (uint)sizeof(Native.RAWINPUTDEVICE)))
                registered.Add((page, usage));
        }

        Listening = [.. registered.Select(r => $"{r.Page:X4}/{r.Usage:X4}")];
        PublishKeyboards();
    }
}
