using System.Runtime.InteropServices;
using System.Text;
using static Overlay.Interop.Native;

namespace Overlay.Interop;

/// <summary>
/// Finds monitors that currently have a fullscreen app on them: a visible window covering the
/// whole monitor, taskbar included (games, fullscreen video, presentations). Maximized windows
/// don't qualify, since they leave the taskbar showing. A natural source for the future game-mode
/// detector as well.
/// </summary>
internal static class FullscreenWindows
{
    private const int DWMWA_CLOAKED = 14;
    private static readonly int OwnPid = Environment.ProcessId;

    // Shell windows that span the screen but aren't apps.
    private static readonly HashSet<string> ShellClasses =
        ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"];

    /// <summary>Bounds (physical pixels) of every monitor that has a fullscreen app on it.</summary>
    public static List<System.Drawing.Rectangle> Find(IReadOnlyList<System.Drawing.Rectangle> monitors)
    {
        var hits = new List<System.Drawing.Rectangle>();
        EnumWindows((hwnd, _) =>
        {
            if (hits.Count == monitors.Count) return false;
            var cover = FullscreenOn(hwnd, monitors);
            if (!cover.IsEmpty && !hits.Contains(cover)) hits.Add(cover);
            return true;
        }, IntPtr.Zero);
        return hits;
    }

    /// <summary>True when the focused window is a fullscreen app.</summary>
    public static bool IsForegroundFullscreen(IReadOnlyList<System.Drawing.Rectangle> monitors) =>
        !FullscreenOn(GetForegroundWindow(), monitors).IsEmpty;

    /// <summary>The monitor <paramref name="hwnd"/> fills as a fullscreen app, or empty if it isn't one.</summary>
    private static System.Drawing.Rectangle FullscreenOn(IntPtr hwnd, IReadOnlyList<System.Drawing.Rectangle> monitors)
    {
        var none = System.Drawing.Rectangle.Empty;
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return none;
        if (!GetWindowRect(hwnd, out var r)) return none;

        var cover = monitors.FirstOrDefault(m =>
            r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom);
        if (cover.IsEmpty) return none;

        // Click-through overlays (ours, GPU overlays, recorders) are not apps in the way.
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) != 0) return none;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == OwnPid) return none;

        var className = new StringBuilder(64);
        GetClassName(hwnd, className, className.Capacity);
        if (ShellClasses.Contains(className.ToString())) return none;

        // Windows on other virtual desktops / suspended UWP apps are "visible" but cloaked.
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return none;

        return cover;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
