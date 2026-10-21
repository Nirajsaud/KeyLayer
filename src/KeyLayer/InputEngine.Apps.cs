using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyLayer;

// Apps where shortcuts are off: which app is in front and whether a shortcut runs there.
internal sealed unsafe partial class InputEngine
{
    /// <summary>
    /// Program file of the window in front (e.g. "Photoshop.exe") when a shortcut depends on it, else null.
    /// Asked on every key press, so the answer is kept until another window comes to the front.
    /// </summary>
    string? AppInFront()
    {
        if (excludedApps.Count == 0 && !mappingsPerApp) return null;
        nint hwnd = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(hwnd, out uint processId);
        // A store app still starting up shows only its frame for a moment: look again until the app itself is there.
        if (hwnd != frontWindow || processId != frontProcess || Apps.IsFrameHost(frontExe))
        {
            frontWindow = hwnd;
            frontProcess = processId;
            frontExe = Apps.ExePathOf(hwnd) is { } path ? Path.GetFileName(path) : null;
        }
        return frontExe;
    }

    /// <summary>True when every shortcut is off in this app.</summary>
    bool OffIn(string? app) => app is not null && excludedApps.Contains(app);

    /// <summary>The shortcut, unless it's switched off in this app.</summary>
    static Mapping? RunsIn(Mapping? m, string? app) =>
        m is null || app is null || !m.ExcludedApps.Exists(a => a.Is(app)) ? m : null;

    bool RunsInFront(Mapping m)
    {
        string? app = AppInFront();
        return !OffIn(app) && RunsIn(m, app) is not null;
    }
}
