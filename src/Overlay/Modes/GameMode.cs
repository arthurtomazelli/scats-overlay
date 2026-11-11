namespace Overlay.Modes;

public enum DisplayMode
{
    /// <summary>Pinned behind all windows, full layout.</summary>
    Desktop,
    /// <summary>Topmost, compact single line, for fullscreen games.</summary>
    GameOverlay,
}

/// <summary>Anything that can show the widget in a given mode (the host, fanning out to its windows).</summary>
public interface IWidgetSurface
{
    DisplayMode Mode { get; }
    void ApplyMode(DisplayMode mode);
}

/// <summary>
/// Reports whether a fullscreen game is running. Planned implementation: every ~2 s check
/// SHQueryUserNotificationState (QUNS_RUNNING_D3D_FULL_SCREEN / QUNS_BUSY), and fall back to
/// "foreground window covers its whole monitor and isn't the shell". Raise only on changes.
/// </summary>
public interface IGameDetector : IDisposable
{
    /// <summary>May be raised on any thread.</summary>
    event Action<bool>? GameRunningChanged;
    void Start();
}

/// <summary>Placeholder until detection is written: never reports a game.</summary>
public sealed class NullGameDetector : IGameDetector
{
    public event Action<bool>? GameRunningChanged { add { } remove { } }
    public void Start() { }
    public void Dispose() { }
}

/// <summary>
/// Decides the display mode from the detector (when game mode is enabled) and a manual preview
/// toggle, and applies it to the surface. All public members must be called on the UI thread.
/// </summary>
public sealed class ModeController : IDisposable
{
    private readonly IWidgetSurface _surface;
    private readonly IGameDetector _detector;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private bool _gameRunning;
    private bool _preview;
    private bool _enabled;

    public ModeController(IWidgetSurface surface, IGameDetector detector, bool enabled)
    {
        _surface = surface;
        _detector = detector;
        _enabled = enabled;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _detector.GameRunningChanged += running => _dispatcher.BeginInvoke(() =>
        {
            _gameRunning = running;
            Apply();
        });
        _detector.Start();
    }

    public bool Preview
    {
        get => _preview;
        set { _preview = value; Apply(); }
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Apply();
    }

    private void Apply()
    {
        var mode = _preview || (_enabled && _gameRunning) ? DisplayMode.GameOverlay : DisplayMode.Desktop;
        if (mode != _surface.Mode) _surface.ApplyMode(mode);
    }

    public void Dispose() => _detector.Dispose();
}
