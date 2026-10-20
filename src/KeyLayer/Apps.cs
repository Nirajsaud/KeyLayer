using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KeyLayer;

/// <summary>An app with a window open, offered when picking apps.</summary>
internal sealed record RunningApp(AppRef App, ImageSource? Icon);

/// <summary>Finds the program behind a window, and lists the apps that have windows open.</summary>
internal static unsafe class Apps
{
    // Windows' own hosts: their windows belong to other apps or to the shell itself.
    static readonly HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe", "TextInputHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "LockApp.exe",
    };

    static readonly Dictionary<string, ImageSource?> icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Full path of a process's program file, or null if Windows won't say.</summary>
    public static string? ExePath(uint processId)
    {
        nint process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0) return null;
        try
        {
            const int Capacity = 1024;
            char* buffer = stackalloc char[Capacity];
            uint size = Capacity;
            return Native.QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    public static AppRef FromPath(string path) => new() { Exe = Path.GetFileName(path), Name = FriendlyName(path), Path = path };

    /// <summary>The name the program gives itself (e.g. "Adobe Premiere Pro"), else its file name.</summary>
    static string FriendlyName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (var name in (string?[])[info.FileDescription, info.ProductName])
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch
        {
            // No version info: fall back to the file name.
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Apps with a normal window open (not KeyLayer itself), by name.</summary>
    public static IReadOnlyList<RunningApp> Running()
    {
        var found = new Dictionary<string, AppRef>(StringComparer.OrdinalIgnoreCase);
        uint self = (uint)Environment.ProcessId;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!IsAppWindow(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == self || ExePath(processId) is not { } path) return true;
            string exe = Path.GetFileName(path);
            if (!hosts.Contains(exe) && !found.ContainsKey(exe)) found[exe] = FromPath(path);
            return true;
        }, 0);
        return [.. found.Values
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new RunningApp(a, Icon(a.Path)))];
    }

    static bool IsAppWindow(nint hwnd)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != 0 || Native.GetWindowTextLength(hwnd) == 0)
            return false;
        if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return false;
        int cloaked = 0; // hidden by Windows, e.g. on another virtual desktop or a suspended store app
        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return cloaked == 0;
    }

    /// <summary>The program's icon, or null when it can't be read (the app isn't installed here, say).</summary>
    public static ImageSource? Icon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (icons.TryGetValue(path, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            if (File.Exists(path) && System.Drawing.Icon.ExtractAssociatedIcon(path) is { } icon)
            {
                using (icon)
                    image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
            }
        }
        catch
        {
            image = null;
        }
        icons[path] = image;
        return image;
    }
}
