using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KeyLayer;

/// <summary>Win32 declarations for the keyboard hook, raw input and HID report parsing.</summary>
internal static unsafe class Native
{
    public const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    public const int WM_INPUT_DEVICE_CHANGE = 0x00FE, WM_INPUT = 0x00FF, WM_TIMER = 0x0113, WM_QUIT = 0x0012, WM_APP = 0x8000;

    // ---- Low-level keyboard hook ----

    public const int WH_KEYBOARD_LL = 13;
    public const uint LLKHF_EXTENDED = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public nuint dwExtraInfo;
    }

    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---- Message loop / misc ----

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int ptX, ptY, lPrivate;
    }

    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint idThread, int msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);
    [DllImport("user32.dll")] public static extern bool KillTimer(nint hWnd, nuint uIDEvent);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hWnd);

    // ---- Windows and the apps they belong to ----

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint GW_OWNER = 4;
    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, DWMWA_CLOAKED = 14;

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(nint hWnd, int index);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hWnd, int attribute, void* value, int size);
    [DllImport("kernel32.dll")] public static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    // ---- Raw input ----

    public const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b;
    public const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_DEVNOTIFY = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType, dwSize;
        public nint hDevice, wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST
    {
        public nint hDevice;
        public uint dwType;
    }

    /// <summary>RID_DEVICE_INFO with only the HID branch of the union spelled out.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public uint hidVendorId;
        [FieldOffset(12)] public uint hidProductId;
        [FieldOffset(16)] public uint hidVersion;
        [FieldOffset(20)] public ushort hidUsagePage;
        [FieldOffset(22)] public ushort hidUsage;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(nint hRawInput, uint uiCommand, nint pData, ref uint pcbSize, uint cbSizeHeader);

    /// <summary>Reads (and removes) the raw input already queued for this thread, without dispatching other messages.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetRawInputBuffer(nint pData, ref uint pcbSize, uint cbSizeHeader);

    public const ushort RI_KEY_BREAK = 0x01, RI_KEY_E0 = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    public static extern uint GetRawInputDeviceInfo(nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);

    // ---- HID parsing ----

    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public const int HidP_Input = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(nint preparsedData, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    public static extern int HidP_GetButtonCaps(int reportType, [Out] byte[] buttonCaps, ref ushort length, nint preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(int reportType, [Out] byte[] valueCaps, ref ushort length, nint preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, nint preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern bool HidD_GetProductString(SafeFileHandle device, [Out] byte[] buffer, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    // ---- Device descriptions (Device Manager names) ----

    public const uint CM_DRP_DEVICEDESC = 0x01, CM_DRP_FRIENDLYNAME = 0x0D;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    public static extern int CM_Get_DevNode_Registry_Property(uint devInst, uint property, out uint regType,
        [Out] byte[]? buffer, ref uint length, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    public const uint DEVPROP_TYPE_BOOLEAN = 0x11;

    /// <summary>True when the device is part of the computer itself rather than plugged in.</summary>
    public static readonly DEVPROPKEY DEVPKEY_Device_InLocalMachineContainer =
        new() { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 4 };

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    public static extern int CM_Get_DevNode_Property(uint devInst, ref DEVPROPKEY key, out uint propertyType,
        [Out] byte[] buffer, ref uint size, uint flags);
}
