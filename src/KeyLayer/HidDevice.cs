using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace KeyLayer;

internal sealed record HidSignal(string Key, string Code, string Meaning, IReadOnlyList<(ushort Page, ushort Id)> Usages);

/// <summary>One HID top-level collection seen through raw input, with what's needed to decode its reports.</summary>
internal sealed unsafe class HidDevice : IDisposable
{
    const int CapsSize = 72; // sizeof(HIDP_BUTTON_CAPS) == sizeof(HIDP_VALUE_CAPS)

    readonly Dictionary<byte, byte[]> lastReports = [];
    nint preparsed;

    public nint Handle { get; private init; }
    public string Path { get; private init; } = "";
    public int VendorId { get; private init; }
    public int ProductId { get; private init; }
    public ushort UsagePage { get; private init; }
    public ushort Usage { get; private init; }
    public string Product { get; private init; } = "";

    /// <summary>The keyboard this collection belongs to: "VID:PID" for external devices, or the built-in keyboard.</summary>
    public string KeyboardId { get; private init; } = "";
    public ushort[] InputPages { get; private set; } = [];
    public bool HasReportIds { get; private set; }

    public string VidPid => $"{VendorId:X4}:{ProductId:X4}";
    public string DisplayName => Product.Length > 0 ? $"{Product} ({VidPid})" : $"HID device {VidPid}";

    public static HidDevice? Open(nint handle)
    {
        Native.RID_DEVICE_INFO info = default;
        info.cbSize = (uint)sizeof(Native.RID_DEVICE_INFO);
        uint size = info.cbSize;
        if (Native.GetRawInputDeviceInfo(handle, Native.RIDI_DEVICEINFO, (nint)(&info), ref size) == uint.MaxValue) return null;
        if (info.dwType != Native.RIM_TYPEHID) return null;

        string path = RawDevices.Name(handle);
        var device = new HidDevice
        {
            Handle = handle,
            Path = path,
            VendorId = (int)info.hidVendorId,
            ProductId = (int)info.hidProductId,
            UsagePage = info.hidUsagePage,
            Usage = info.hidUsage,
            Product = RawDevices.Product(path),
            KeyboardId = RawDevices.KeyboardIdentity(path).Id,
        };
        device.LoadPreparsed();
        return device;
    }

    void LoadPreparsed()
    {
        uint size = 0;
        Native.GetRawInputDeviceInfo(Handle, Native.RIDI_PREPARSEDDATA, 0, ref size);
        if (size == 0) return;
        preparsed = Marshal.AllocHGlobal((int)size);
        if (Native.GetRawInputDeviceInfo(Handle, Native.RIDI_PREPARSEDDATA, preparsed, ref size) == uint.MaxValue
            || Native.HidP_GetCaps(preparsed, out var caps) != Native.HIDP_STATUS_SUCCESS)
        {
            Marshal.FreeHGlobal(preparsed);
            preparsed = 0;
            return;
        }

        var pages = new HashSet<ushort>();
        if (caps.NumberInputButtonCaps > 0)
        {
            ushort n = caps.NumberInputButtonCaps;
            var buffer = new byte[n * CapsSize];
            if (Native.HidP_GetButtonCaps(Native.HidP_Input, buffer, ref n, preparsed) == Native.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    pages.Add(BitConverter.ToUInt16(buffer, i * CapsSize));
                    if (buffer[i * CapsSize + 2] != 0) HasReportIds = true;
                }
            }
        }
        if (caps.NumberInputValueCaps > 0)
        {
            ushort n = caps.NumberInputValueCaps;
            var buffer = new byte[n * CapsSize];
            if (Native.HidP_GetValueCaps(Native.HidP_Input, buffer, ref n, preparsed) == Native.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                    if (buffer[i * CapsSize + 2] != 0) HasReportIds = true;
            }
        }
        InputPages = [.. pages];
    }

    /// <summary>False when the report repeats the previous one with the same report ID.</summary>
    public bool IsNewState(byte[] report)
    {
        byte id = HasReportIds && report.Length > 0 ? report[0] : (byte)0;
        if (lastReports.TryGetValue(id, out var last) && last.AsSpan().SequenceEqual(report)) return false;
        lastReports[id] = report;
        return true;
    }

    /// <summary>Turns a report into a signal, or null when nothing is pressed (a release).</summary>
    public HidSignal? Decode(byte[] report)
    {
        var usages = new List<(ushort Page, ushort Id)>();
        if (preparsed != 0)
        {
            var buffer = new ushort[64];
            foreach (var page in InputPages)
            {
                uint count = (uint)buffer.Length;
                if (Native.HidP_GetUsages(Native.HidP_Input, page, 0, buffer, ref count, preparsed, report, (uint)report.Length)
                    != Native.HIDP_STATUS_SUCCESS) continue;
                for (int i = 0; i < count; i++)
                    if (buffer[i] != 0) usages.Add((page, buffer[i]));
            }
        }

        if (usages.Count > 0)
        {
            return new HidSignal(
                $"{VidPid}|U|" + string.Join(",", usages.Select(u => $"{u.Page:X4}.{u.Id:X4}")),
                string.Join(" + ", usages.Select(u => $"{KeyNames.PageName(u.Page)} 0x{u.Id:X3}")),
                string.Join(" + ", usages.Select(u => KeyNames.HidUsage(u.Page, u.Id))),
                usages);
        }

        // Not described as buttons: fall back to the raw bytes.
        int start = HasReportIds ? 1 : 0;
        int end = report.Length;
        while (end > start && report[end - 1] == 0) end--;
        if (end <= start) return null;

        string spaced = string.Join(" ", report.Take(end).Select(b => b.ToString("X2")));
        return new HidSignal(
            $"{VidPid}|R|{UsagePage:X4}.{Usage:X4}|{Convert.ToHexString(report, 0, end)}",
            $"Report {spaced}",
            UsagePage >= 0xFF00 ? "Vendor-specific signal" : $"{KeyNames.PageName(UsagePage)} signal",
            []);
    }

    public void Dispose()
    {
        if (preparsed != 0) Marshal.FreeHGlobal(preparsed);
        preparsed = 0;
    }
}

/// <summary>Raw input device enumeration helpers.</summary>
internal static unsafe partial class RawDevices
{
    static readonly Dictionary<string, string> productCache = new(StringComparer.OrdinalIgnoreCase);

    public static Native.RAWINPUTDEVICELIST[] List()
    {
        uint itemSize = (uint)sizeof(Native.RAWINPUTDEVICELIST);
        uint count = 0;
        if (Native.GetRawInputDeviceList(null, ref count, itemSize) == uint.MaxValue || count == 0) return [];
        var items = new Native.RAWINPUTDEVICELIST[count + 8]; // devices can appear between the two calls
        count = (uint)items.Length;
        uint got = Native.GetRawInputDeviceList(items, ref count, itemSize);
        return got == uint.MaxValue ? [] : items[..(int)got];
    }

    public static string Name(nint handle)
    {
        uint chars = 0;
        Native.GetRawInputDeviceInfo(handle, Native.RIDI_DEVICENAME, 0, ref chars);
        if (chars == 0) return "";
        var buffer = new char[chars + 1];
        fixed (char* p = buffer)
        {
            if (Native.GetRawInputDeviceInfo(handle, Native.RIDI_DEVICENAME, (nint)p, ref chars) == uint.MaxValue) return "";
        }
        int end = Array.IndexOf(buffer, '\0');
        string name = new(buffer, 0, end < 0 ? buffer.Length : end);
        return name.StartsWith(@"\??\") ? @"\\?\" + name[4..] : name;
    }

    /// <summary>The product string the device reports over USB/Bluetooth, e.g. "USB Gaming Keyboard".</summary>
    public static string Product(string path)
    {
        if (path.Length == 0) return "";
        if (productCache.TryGetValue(path, out var cached)) return cached;
        string product = "";
        using (var handle = Native.CreateFile(path, 0, 3 /* share read|write */, 0, 3 /* OPEN_EXISTING */, 0, 0))
        {
            if (!handle.IsInvalid)
            {
                var buffer = new byte[256];
                if (Native.HidD_GetProductString(handle, buffer, (uint)buffer.Length))
                    product = Encoding.Unicode.GetString(buffer).Split('\0')[0].Trim();
            }
        }
        productCache[path] = product;
        return product;
    }

    /// <summary>"1A2C:90FE" from a device path (USB and Bluetooth formats), or null for built-in devices.</summary>
    public static string? VidPid(string path)
    {
        var m = VidPidRegex().Match(path);
        return m.Success ? $"{m.Groups[1].Value}:{m.Groups[2].Value}".ToUpperInvariant() : null;
    }

    /// <summary>ID shared by everything built into the PC: the laptop keyboard and its hotkey devices.</summary>
    public const string BuiltInId = "BUILTIN";

    static readonly Dictionary<string, bool> builtInCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A keyboard's stable ID and how it's connected. External keyboards are identified by "VID:PID"
    /// (all of a keyboard's collections share it); everything built into the PC shares <see cref="BuiltInId"/>.
    /// </summary>
    public static (string Id, string Connection, bool External) KeyboardIdentity(string path)
    {
        if (IsBuiltIn(path)) return (BuiltInId, "Built-in", false);
        bool bluetooth = path.Contains("{00001124", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("{00001812", StringComparison.OrdinalIgnoreCase);
        var parts = path.TrimStart('\\', '?').Split('#');
        string id = VidPid(path) ?? (parts.Length >= 2 ? $"{parts[0]}#{parts[1]}".ToUpperInvariant() : path);
        return (id, bluetooth ? "Bluetooth" : "USB", true);
    }

    /// <summary>
    /// Whether Windows counts the device as part of the computer itself (it shares the PC's device container).
    /// That holds for laptop keyboards however they're wired, including internal USB ones.
    /// </summary>
    public static bool IsBuiltIn(string path)
    {
        if (builtInCache.TryGetValue(path, out bool cached)) return cached;
        bool builtIn = VidPid(path) is null; // fallback: devices without a vendor ID sit on the motherboard
        if (InstanceId(path) is { } instance && Native.CM_Locate_DevNode(out uint node, instance, 0) == 0)
        {
            var key = Native.DEVPKEY_Device_InLocalMachineContainer;
            var buffer = new byte[4];
            uint size = (uint)buffer.Length;
            if (Native.CM_Get_DevNode_Property(node, ref key, out uint type, buffer, ref size, 0) == 0 && type == Native.DEVPROP_TYPE_BOOLEAN)
                builtIn = buffer[0] != 0;
        }
        builtInCache[path] = builtIn;
        return builtIn;
    }

    /// <summary>\\?\HID#ASUE140D&amp;Col04#5&amp;2dea9dfa&amp;0&amp;0003#{guid}  ->  HID\ASUE140D&amp;Col04\5&amp;2dea9dfa&amp;0&amp;0003</summary>
    static string? InstanceId(string path)
    {
        var parts = path.TrimStart('\\', '?').Split('#');
        return parts.Length < 3 ? null : string.Join('\\', parts.Take(3));
    }

    /// <summary>The device's name as Device Manager shows it, e.g. "ASUS Keyboard".</summary>
    public static string Description(string path)
    {
        if (InstanceId(path) is not { } instance || Native.CM_Locate_DevNode(out uint node, instance, 0) != 0) return "";
        foreach (uint property in (uint[])[Native.CM_DRP_FRIENDLYNAME, Native.CM_DRP_DEVICEDESC])
        {
            uint length = 512;
            var buffer = new byte[length];
            if (Native.CM_Get_DevNode_Registry_Property(node, property, out _, buffer, ref length, 0) == 0)
                return Encoding.Unicode.GetString(buffer, 0, (int)length).TrimEnd('\0').Trim();
        }
        return "";
    }

    // USB "VID_1A2C&PID_90FE", Bluetooth LE "VID&02046D_PID&B35B", classic Bluetooth "VID&0002046D_PID&B342":
    // the Bluetooth forms put a 2- or 4-digit vendor ID source in front of the vendor ID.
    [GeneratedRegex(@"VID[_&](?:[0-9A-F]{2}){0,2}?([0-9A-F]{4})[_&]PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();
}
