using System.Runtime.InteropServices;
using static Overlay.Interop.Native;

namespace Overlay.Interop;

/// <summary>
/// The Win32 side of the widget's window behaviour: click-through/no-focus styles, and the two
/// z-order modes (pinned to the desktop vs. topmost overlay).
/// </summary>
internal static class WindowPlacement
{
    /// <summary>
    /// Click-through, never activated, absent from the taskbar and Alt+Tab, and not hidden when
    /// Alt+Tab or the taskbar "peeks" at another window.
    /// </summary>
    public static void MakeGhost(IntPtr hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        ex &= ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)ex);

        int excluded = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_EXCLUDED_FROM_PEEK, ref excluded, sizeof(int));
    }

    /// <summary>
    /// Makes the desktop's icon host window our owner. Owned windows always sit directly above
    /// their owner, so we stay behind every app, and when "show desktop" raises the desktop we
    /// come up with it instead of being hidden.
    /// </summary>
    public static void PinToDesktop(IntPtr hwnd)
    {
        var host = FindDesktopHost();
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, host);
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>True while our owner is still the live desktop host (Explorer restarts replace it).</summary>
    public static bool IsPinned(IntPtr hwnd)
    {
        var owner = GetWindowLongPtr(hwnd, GWLP_HWNDPARENT);
        return owner != IntPtr.Zero && IsWindow(owner) && FindWindowEx(owner, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
    }

    public const int WM_WINDOWPOSCHANGING = 0x0046;

    /// <summary>Rewrites a pending WINDOWPOS (lParam of WM_WINDOWPOSCHANGING) to sit at the bottom.</summary>
    public static void ForceBottom(IntPtr windowPos)
    {
        var pos = Marshal.PtrToStructure<WINDOWPOS>(windowPos);
        if ((pos.flags & SWP_NOZORDER) != 0) return;
        pos.hwndInsertAfter = HWND_BOTTOM;
        pos.flags |= SWP_NOOWNERZORDER;
        Marshal.StructureToPtr(pos, windowPos, false);
    }

    public static void MakeTopmost(IntPtr hwnd)
    {
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, IntPtr.Zero);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static void MoveTo(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    public static (int Width, int Height) GetPixelSize(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);

    /// <summary>
    /// The window holding SHELLDLL_DefView (the icons): normally Progman, but after the
    /// wallpaper/"show desktop" machinery has run it can be one of the WorkerW windows.
    /// </summary>
    private static IntPtr FindDesktopHost()
    {
        var progman = FindWindow("Progman", null);
        if (FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return progman;

        IntPtr host = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            host = h;
            return false;
        }, IntPtr.Zero);
        return host != IntPtr.Zero ? host : progman;
    }
}
