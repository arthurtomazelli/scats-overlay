using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using static Overlay.Interop.Native;

namespace Overlay.Interop;

/// <summary>
/// Finds popups (notifications, tray flyouts, app toasts like Steam's) covering a spot on screen
/// by asking what's actually on top at points across it, so it also catches shell toasts, which
/// live in their own z-order band and don't show up as ordinary topmost windows. Big windows,
/// tooltips and click-through overlays don't count.
/// </summary>
internal static class PopupWindows
{
    private const int DWMWA_CLOAKED = 14;
    private const uint GW_HWNDNEXT = 2;
    private const double MaxScreenShare = 0.6; // bigger than this is a panel or an app, not a popup
    private const int SamplesX = 5, SamplesY = 4;
    private static readonly int OwnPid = Environment.ProcessId;
    private static IntPtr _lastRejected;

    private static readonly HashSet<string> IgnoredClasses =
        ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "tooltips_class32"];

    /// <summary>
    /// A popup above <paramref name="self"/> that covers any part of <paramref name="spot"/>
    /// (physical pixels), or zero. Its bounds come back in <paramref name="bounds"/>.
    /// </summary>
    public static IntPtr Covering(System.Drawing.Rectangle spot, IntPtr self,
        out System.Drawing.Rectangle bounds)
    {
        bounds = System.Drawing.Rectangle.Empty;
        var seen = new HashSet<IntPtr>();
        for (int iy = 0; iy < SamplesY; iy++)
        {
            for (int ix = 0; ix < SamplesX; ix++)
            {
                // Click-through windows (the cat itself included) are skipped by the hit test.
                var p = new POINT
                {
                    X = spot.Left + (2 * ix + 1) * spot.Width / (2 * SamplesX),
                    Y = spot.Top + (2 * iy + 1) * spot.Height / (2 * SamplesY),
                };
                var hit = WindowFromPoint(p);
                if (hit == IntPtr.Zero) continue;
                var root = GetAncestor(hit, GA_ROOT);
                if (root == IntPtr.Zero) root = hit;
                if (root == self || !seen.Add(root) || !IsAbove(root, self)) continue;
                if (IsPopup(root, out bounds, out var reason)) return root;
                if (root != _lastRejected)
                {
                    // Something is over the cat but doesn't count; say why, once per window.
                    _lastRejected = root;
                    Log.Info($"Ignoring {Native.ClassNameOf(root)} over the cat ({reason}): {bounds}");
                }
            }
        }
        bounds = System.Drawing.Rectangle.Empty;
        return IntPtr.Zero;
    }

    /// <summary>True while <paramref name="popup"/> is still on screen; its current bounds come back in <paramref name="bounds"/>.</summary>
    public static bool IsShowing(IntPtr popup, out System.Drawing.Rectangle bounds)
    {
        bounds = System.Drawing.Rectangle.Empty;
        if (!IsWindow(popup) || !IsWindowVisible(popup) || IsIconic(popup) || !GetWindowRect(popup, out var r)) return false;
        if (DwmGetWindowAttribute(popup, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        bounds = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return true;
    }

    /// <summary>
    /// Could <paramref name="hwnd"/> be a popup? Size isn't judged here: some popups (Steam's)
    /// sit in a much bigger transparent window, so that's checked on the measured card instead
    /// (<see cref="IsPopupSized"/>).
    /// </summary>
    private static bool IsPopup(IntPtr hwnd, out System.Drawing.Rectangle bounds, out string reason)
    {
        bounds = System.Drawing.Rectangle.Empty;
        reason = "hidden";
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || !GetWindowRect(hwnd, out var r)) return false;
        bounds = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

        reason = "own window";
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == OwnPid) return false;

        var className = new StringBuilder(64);
        GetClassName(hwnd, className, className.Capacity);
        reason = "shell window";
        if (IgnoredClasses.Contains(className.ToString())) return false;

        // Dismissed toasts and hidden flyouts can stay "visible" but cloaked.
        reason = "cloaked";
        return !(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0);
    }

    /// <summary>Small enough to be a popup rather than a panel or an app.</summary>
    public static bool IsPopupSized(System.Drawing.Rectangle card, System.Drawing.Rectangle monitor) =>
        card.Width <= monitor.Width * MaxScreenShare && card.Height <= monitor.Height * MaxScreenShare
        && card.Height > 0 && card.Width > 0;

    /// <summary>
    /// Whether <paramref name="other"/> is above <paramref name="self"/> in z-order: anything not
    /// below it. Shell toasts live in a band of their own that isn't in our z-order list at all,
    /// so "not found below" is the test that catches them too.
    /// </summary>
    private static bool IsAbove(IntPtr other, IntPtr self)
    {
        for (var h = GetWindow(self, GW_HWNDNEXT); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
            if (h == other) return false;
        return true;
    }

    /// <summary>
    /// The part of <paramref name="hwnd"/> that is actually drawn, found with UI Automation by
    /// following single, full-width children down from the window. Shell toasts sit inside a bigger, mostly
    /// transparent window (room for the slide-in), and the window's own rect would leave the cat
    /// floating above the card. Falls back to <paramref name="bounds"/>. Cross-process and
    /// slow-ish (~100-200 ms): call it off the UI thread.
    /// </summary>
    public static System.Drawing.Rectangle VisibleBounds(IntPtr hwnd, System.Drawing.Rectangle bounds)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var element = AutomationElement.FromHandle(hwnd);
            var best = bounds;
            for (int depth = 0; depth < 6; depth++)
            {
                var child = walker.GetFirstChild(element);
                if (child is null || walker.GetNextSibling(child) is not null) break; // stop where it branches out
                var r = child.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width < 1 || r.Height < 1) break;
                var rect = System.Drawing.Rectangle.Intersect(bounds,
                    System.Drawing.Rectangle.FromLTRB((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom));
                // A card fills (nearly) the width of what holds it; a lone label or button doesn't.
                if (rect.IsEmpty || rect.Width < best.Width * 0.75) break;
                best = rect;
                element = child;
            }
            return TrimTransparentTop(hwnd, best);
        }
        catch (Exception ex)
        {
            Log.Error("Could not measure a popup", ex);
            return bounds;
        }
    }

    /// <summary>
    /// Moves the top of <paramref name="rect"/> down to where <paramref name="hwnd"/> starts
    /// catching the mouse, scanning down its middle. Layered popups (like Steam's) let clicks
    /// through their fully transparent pixels, so that's where the drawn card begins.
    /// </summary>
    private static System.Drawing.Rectangle TrimTransparentTop(IntPtr hwnd, System.Drawing.Rectangle rect)
    {
        int x = (rect.Left + rect.Right) / 2;
        for (int y = rect.Top; y < rect.Bottom; y += 2)
        {
            var hit = WindowFromPoint(new POINT { X = x, Y = y });
            var root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);
            if (root == hwnd || hit == hwnd)
                return System.Drawing.Rectangle.FromLTRB(rect.Left, Math.Max(rect.Top, y - 1), rect.Right, rect.Bottom);
        }
        return rect; // never hit (covered by something else?): keep what we had
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

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
