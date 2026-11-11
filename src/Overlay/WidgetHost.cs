using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Overlay.Interop;
using Overlay.Config;
using Overlay.Modes;
using Overlay.Sensors;
using Overlay.UI;

namespace Overlay;

/// <summary>
/// Wires config, sensors, windows (one widget and optionally one cat per selected monitor), tray
/// and mode switching together. Acts as the single <see cref="IWidgetSurface"/> for all windows.
/// </summary>
internal sealed class WidgetHost : IWidgetSurface, IDisposable
{
    private readonly ConfigStore _config;
    private readonly HardwareMonitorService _hardware = new();
    private readonly MetricsPresenter _presenter = new();
    private readonly List<MainWindow> _widgets = [];
    private readonly List<CatWindow> _cats = [];
    private SensorPoller? _poller;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;
    private ModeController? _modes;
    private string _widgetScreens = "", _catScreens = "";
    private string? _catHome;
    private const long ReturnHomeDelayMs = 60_000;
    private long _lastFullscreenFocus; // TickCount64 when a fullscreen app last had focus
    private long _singleReturnDelayMs = 2_500; // re-rolled within 2-3 s each time a fullscreen app has focus
    // Window events trigger placement checks immediately; the timer is only a safety net.
    private readonly DispatcherTimer _catPlacement = new() { Interval = TimeSpan.FromSeconds(2) };
    private WindowEvents? _windowEvents;
    private readonly DispatcherTimer _returnCheck = new(); // one-shot, for the single-screen return

    // Read by the poll thread; swapped atomically on the UI thread.
    private volatile IReadOnlySet<string> _activeMetrics = new HashSet<string>();
    private int _renderPending;

    public WidgetHost(ConfigStore config) => _config = config;

    public DisplayMode Mode { get; private set; } = DisplayMode.Desktop;

    private WidgetConfig Cfg => _config.Current;

    public void Start()
    {
        SyncWindows();

        _modes = new ModeController(this, new NullGameDetector(), Cfg.GameMode.Enabled);

        if (Cfg.TrayIcon)
            _tray = new TrayIcon(OpenSettings, OpenConfig, _config.Reload, DumpSensors, on => _modes.Preview = on, () => Application.Current.Shutdown());

        var dispatcher = Application.Current.Dispatcher;
        _config.Changed += () => dispatcher.BeginInvoke(OnConfigChanged);
        _config.StartWatching();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _catPlacement.Tick += (_, _) => PlaceCat();
        _catPlacement.Start();
        _returnCheck.Tick += (_, _) => { _returnCheck.Stop(); PlaceCat(); };
        _windowEvents = new WindowEvents(TimeSpan.FromMilliseconds(50));
        _windowEvents.Changed += PlaceCat;

        _poller = new SensorPoller(_hardware, () => (Cfg, _activeMetrics), OnSnapshot);
        _poller.Start();
    }

    /// <summary>
    /// Keeps a single cat off screens running a fullscreen app: home screen if it's free, else
    /// another free screen, else the secondary screen. With only one screen it runs off it instead.
    /// </summary>
    private void PlaceCat()
    {
        if (_cats.Count != 1 || _catHome is null) return;
        var cat = _cats[0];
        var screens = System.Windows.Forms.Screen.AllScreens;
        var bounds = screens.Select(s => s.Bounds).ToList();
        var busy = FullscreenWindows.Find(bounds);
        // It naps while you're focused on a game or video; switching to another app wakes it.
        bool focusedFullscreen = FullscreenWindows.IsForegroundFullscreen(bounds);
        if (focusedFullscreen)
        {
            _lastFullscreenFocus = Environment.TickCount64;
            _singleReturnDelayMs = Random.Shared.Next(2_000, 3_001);
        }
        cat.SetFullscreenActive(focusedFullscreen);

        if (!Cfg.Cat.AvoidFullscreen)
        {
            cat.MoveToScreen(_catHome);
            return;
        }

        // With one screen, a fullscreen app behind other windows isn't in the way; only the focused one is.
        bool single = screens.Length < 2;
        bool Free(System.Windows.Forms.Screen s) => single ? !focusedFullscreen : !busy.Contains(s.Bounds);

        var home = Screens.ByName(_catHome);
        var secondary = screens.FirstOrDefault(s => !s.Primary) ?? home;
        var target = Free(home) ? home
            : screens.FirstOrDefault(s => s.DeviceName != home.DeviceName && Free(s)) ?? (single ? null : secondary);

        if (target is null)
        {
            cat.RunAway();
            return;
        }

        // Having moved away for a game/video, it lingers a while after that loses focus before
        // heading home, so it doesn't dash back and forth when you briefly switch apps.
        // With one screen it's been out of sight, so it comes back sooner.
        bool goingHome = target.DeviceName == home.DeviceName && (cat.ScreenName != home.DeviceName || cat.IsAway);
        long wait = _lastFullscreenFocus + (single ? _singleReturnDelayMs : ReturnHomeDelayMs) - Environment.TickCount64;
        if (goingHome && wait > 0)
        {
            // The 2 s safety timer is too coarse for the short delay: check again right when it's up.
            if (single && !_returnCheck.IsEnabled)
            {
                _returnCheck.Interval = TimeSpan.FromMilliseconds(wait);
                _returnCheck.Start();
            }
            return;
        }

        cat.MoveToScreen(target.DeviceName);
    }

    public void ApplyMode(DisplayMode mode)
    {
        Mode = mode;
        foreach (var w in _widgets) w.ApplyMode(mode);
        foreach (var c in _cats) c.Pause(mode == DisplayMode.GameOverlay);
        RefreshActiveMetrics();
    }

    private void OnSnapshot(Snapshot snapshot)
    {
        // Never queue more than one render if the UI thread is busy.
        if (Interlocked.Exchange(ref _renderPending, 1) == 1) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref _renderPending, 0);
            var rows = _presenter.Build(snapshot, ShownMetrics(), Cfg);
            foreach (var w in _widgets) w.Render(rows);
            foreach (var c in _cats) c.Update(snapshot);
        });
    }

    private void OnConfigChanged()
    {
        SyncWindows();
        foreach (var w in _widgets) w.ApplyConfig(Cfg);
        foreach (var c in _cats) c.ApplyConfig(Cfg.Cat, CatWindow.TintFor(Cfg));
        _modes!.SetEnabled(Cfg.GameMode.Enabled);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // Monitor set or numbering may have changed: rebuild from scratch.
            _widgetScreens = _catScreens = "";
            SyncWindows();
        });

    /// <summary>Creates/closes windows so there is one per monitor the config asks for.</summary>
    private void SyncWindows()
    {
        var widgetScreens = Cfg.ShowWidget ? Screens.Resolve(Cfg.Monitors) : [];
        var widgetKey = string.Join('|', widgetScreens);
        if (widgetKey != _widgetScreens)
        {
            _widgetScreens = widgetKey;
            foreach (var w in _widgets) w.Close();
            _widgets.Clear();
            var primary = System.Windows.Forms.Screen.PrimaryScreen!.DeviceName;
            var overlayHost = widgetScreens.Contains(primary) ? primary : widgetScreens.FirstOrDefault();
            foreach (var name in widgetScreens)
            {
                var w = new MainWindow(Cfg, name, hostsOverlay: name == overlayHost);
                w.Show();
                if (Mode != DisplayMode.Desktop) w.ApplyMode(Mode);
                _widgets.Add(w);
            }
        }

        var catScreens = Cfg.Cat.Enabled ? Screens.Resolve(Cfg.Cat.Monitors) : [];
        var catKey = string.Join('|', catScreens);
        if (catKey != _catScreens)
        {
            _catScreens = catKey;
            foreach (var c in _cats) c.Close();
            _cats.Clear();
            foreach (var name in catScreens)
            {
                var c = new CatWindow(Cfg.Cat, CatWindow.TintFor(Cfg), name);
                c.Show();
                if (Mode == DisplayMode.GameOverlay) c.Pause(true);
                _cats.Add(c);
            }
            _catHome = catScreens.Count == 1 ? catScreens[0] : null;
            PlaceCat();
        }

        RefreshActiveMetrics();
    }

    private IReadOnlyList<string> ShownMetrics() => Mode == DisplayMode.Desktop ? Cfg.Metrics : Cfg.GameMode.Metrics;

    private void RefreshActiveMetrics()
    {
        var set = ShownMetrics().ToHashSet();
        if (_cats.Count > 0 && Cfg.Cat.Moods) set.Add("cpu"); // the cat's mood follows CPU load
        _activeMetrics = set;
    }

    private void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_config, OpenConfig);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    private void OpenConfig()
    {
        try { Process.Start(new ProcessStartInfo(_config.FilePath) { UseShellExecute = true }); }
        catch
        {
            try { Process.Start("notepad.exe", $"\"{_config.FilePath}\""); }
            catch (Exception ex) { Log.Error("Could not open config", ex); }
        }
    }

    private void DumpSensors()
    {
        Task.Run(() =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "sensors.txt");
            try
            {
                File.WriteAllText(path, _hardware.Dump());
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("Sensor dump failed", ex); }
        });
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _catPlacement.Stop();
        _returnCheck.Stop();
        _windowEvents?.Dispose();
        _poller?.Dispose();
        _modes?.Dispose();
        _tray?.Dispose();
        _hardware.Dispose();
        _config.Dispose();
    }
}
