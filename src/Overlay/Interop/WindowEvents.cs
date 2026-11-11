using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Overlay.Interop;

/// <summary>
/// Raises <see cref="Changed"/> shortly after any top-level window is shown, hidden, moved/resized
/// (which includes entering or leaving fullscreen), minimized/restored, or activated. Bursts of
/// events are coalesced into a single notification. Must be created on a thread with a message
/// loop (the UI thread); the event is raised there too.
/// </summary>
internal sealed class WindowEvents : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const uint GA_ROOT = 2;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private readonly WinEventProc _proc; // kept alive: the OS holds only a raw pointer to it
    private readonly List<IntPtr> _hooks = [];
    private readonly DispatcherTimer _settle;

    public WindowEvents(TimeSpan settle)
    {
        _settle = new DispatcherTimer { Interval = settle };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Changed?.Invoke();
        };

        _proc = OnEvent;
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE);
    }

    public event Action? Changed;

    private void Hook(uint min, uint max)
    {
        var h = SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Only whole top-level windows; skip carets, cursors, and child controls.
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return;

        // Restart the settle timer so a burst (e.g. dragging a window) yields one check.
        _settle.Stop();
        _settle.Start();
    }

    public void Dispose()
    {
        _settle.Stop();
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
