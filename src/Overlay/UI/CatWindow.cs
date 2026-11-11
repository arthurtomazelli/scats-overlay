using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Overlay.Config;
using Overlay.Interop;
using Overlay.Sensors;

namespace Overlay.UI;

/// <summary>Click-through window that keeps a <see cref="CatView"/> sitting on the taskbar's top edge.</summary>
public sealed class CatWindow : Window
{
    private const int ReassertTopmostEveryNFrames = 40;

    private readonly CatView _cat = new();
    private readonly DispatcherTimer _timer;
    private CatConfig _cfg;

    // Desktop (Progman/WorkerW), classic console (cmd/PowerShell), and Windows Terminal.
    private static readonly HashSet<string> ShellOrTerminalClasses =
        ["Progman", "WorkerW", "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow"];

    private Color _tint, _lightTint = Colors.White;
    private Color _currentColor, _targetColor;
    private IntPtr _hwnd;
    private long _frames;
    private readonly Random _rng = new();
    private bool _napping;
    private long _napEndsAt;
    private long _nextNapAt;
    private bool _fullscreen;   // the focused window is a fullscreen app
    private long _fullscreenNapAt;
    private MouseClicks? _clicks;

    public CatWindow(CatConfig cfg, Color tint, string screenName)
    {
        _cfg = cfg;
        _tint = tint;
        ScreenName = screenName;
        _nextNapAt = Environment.TickCount64 + RandomMs(cfg.AwakeMinutes, 3, 12);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = Top = -32000;
        Title = "Overlay cat";
        Content = _cat;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FrameInterval(cfg) };
        _timer.Tick += (_, _) => Tick();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowPlacement.MakeGhost(_hwnd);
            ApplyConfig(_cfg, _tint);
            _timer.Start();
            // Still click-through: the click goes on to whatever is beneath, the cat just reacts.
            _clicks = new MouseClicks();
            _clicks.LeftDown += (x, y) => Dispatcher.BeginInvoke(() => OnClick(x, y));
        };
        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => ApplyConfig(_cfg, _tint);
        Closed += (_, _) =>
        {
            _clicks?.Dispose();
            _timer.Stop();
            CompositionTarget.Rendering -= OnRunFrame;
        };
    }

    public string ScreenName { get; private set; }

    /// <summary>
    /// While a fullscreen app has focus the cat settles down and naps a few seconds later, staying
    /// asleep until the app closes or loses focus (hovering still wakes it briefly).
    /// </summary>
    public void SetFullscreenActive(bool active)
    {
        if (active == _fullscreen) return;
        _fullscreen = active;
        long now = Environment.TickCount64;
        if (active)
        {
            _fullscreenNapAt = now + FullscreenSettleMs();
        }
        else if (_napping)
        {
            // The game/video closed or lost focus: doze on for a moment, then wake up and go back
            // to the usual random naps (UpdateNap handles the wake-up).
            _napEndsAt = now + _rng.Next(1_000, 2_001);
        }
    }

    private long FullscreenSettleMs() => _rng.Next(8_000, 15_001);

    /// <summary>True while it has run off its screen (see <see cref="RunAway"/>).</summary>
    public bool IsAway => _away;

    /// <summary>Moves the cat to the same spot on another monitor, or brings it back if it's away.</summary>
    public void MoveToScreen(string screenName)
    {
        if (_running)
        {
            _runDest = screenName; // picked up when it leaves this screen, or right after arriving
            return;
        }
        if (_away)
        {
            _away = false;
            ScreenName = screenName;
            if (_cfg.RunBetweenScreens && _hwnd != IntPtr.Zero && IsVisible) StartRun(screenName, entering: true);
            else Reposition();
            return;
        }
        if (screenName == ScreenName) return;
        if (_cfg.RunBetweenScreens && _hwnd != IntPtr.Zero && IsVisible)
        {
            StartRun(screenName);
            return;
        }
        ScreenName = screenName;
        Reposition();
    }

    /// <summary>
    /// Runs off the left edge of its screen and stays out of sight until <see cref="MoveToScreen"/>
    /// brings it back (it then runs in from the right edge). For when there's no free screen to go to.
    /// </summary>
    public void RunAway()
    {
        if (_running)
        {
            _runDest = null; // leaves the screen and stays out, rather than coming in somewhere
            return;
        }
        if (_away) return;
        if (_cfg.RunBetweenScreens && _hwnd != IntPtr.Zero && IsVisible)
        {
            StartRun(null);
            return;
        }
        _away = true;
        Reposition();
    }

    /// <summary>
    /// The cat's brightest color, always opaque. By default ("match") it's the tone the widget's
    /// text shows on a dark wallpaper (text color dimmed by the widget's opacity), so the cat shares
    /// the stats' color without their see-through look; otherwise the configured color.
    /// </summary>
    public static Color TintFor(WidgetConfig cfg)
    {
        bool match = string.Equals(cfg.Cat.Color, "match", StringComparison.OrdinalIgnoreCase);
        var c = Parse(match ? cfg.TextColor : cfg.Cat.Color, Colors.White);
        if (!match) return Color.FromRgb(c.R, c.G, c.B);
        double k = Math.Clamp(cfg.Opacity, 0, 1);
        return Color.FromRgb((byte)Math.Round(c.R * k), (byte)Math.Round(c.G * k), (byte)Math.Round(c.B * k));
    }

    public void ApplyConfig(CatConfig cfg, Color tint)
    {
        _cfg = cfg;
        _tint = tint;
        var light = Parse(cfg.LightColor, Color.FromRgb(0xE6, 0xE6, 0xE6));
        _lightTint = Color.FromRgb(light.R, light.G, light.B);
        _timer.Interval = FrameInterval(cfg);
        // Start from the soft tint; UpdateTone moves to the right color within a couple of seconds.
        _currentColor = _targetColor = tint;
        _cat.Configure(cfg, VisualTreeHelper.GetDpi(this).PixelsPerDip, tint);
        ApplyZOrder();
        Reposition();
    }

    /// <summary>Mood from CPU load (unknown = idle), and live stats for the thought bubble.</summary>
    public void Update(Snapshot s)
    {
        var mood = !_cfg.Moods || s.Cpu?.Load is not float l ? CatView.Mood.Idle
            : l < 8 ? CatView.Mood.Calm
            : l > 70 ? CatView.Mood.Alert
            : CatView.Mood.Idle;
        _cat.SetMood(mood);

        var live = new List<string>(4);
        var c = CultureInfo.InvariantCulture;
        if (s.Cpu?.Temp is float ct) live.Add($"cpu {ct.ToString("0", c)}°");
        if (s.Cpu?.Load is float cl) live.Add($"cpu {cl.ToString("0", c)}%");
        if (s.Gpu?.Temp is float gt) live.Add($"gpu {gt.ToString("0", c)}°");
        if (s.Uptime.TotalHours >= 1) live.Add($"up {Fmt.Uptime(s.Uptime)}");
        _cat.SetLivePhrases(live);
    }

    public void Pause(bool paused)
    {
        if (paused) { _timer.Stop(); Hide(); }
        else { Show(); ApplyZOrder(); _timer.Start(); }
    }

    private void Tick()
    {
        if (_running) return; // the run timer animates it meanwhile
        if (_frames % 4 == 0) UpdateNap();
        UpdateTone();
        _cat.Step();
        // Other topmost windows (and Explorer restarts) can push us down; quietly climb back.
        if (++_frames % ReassertTopmostEveryNFrames == 0) ApplyZOrder();
    }

    /// <summary>
    /// Random naps: awake for a while, asleep for a while. Hovering the mouse over a napping cat
    /// wakes it early (it's startled); nothing else does.
    /// </summary>
    private void UpdateNap()
    {
        switch (_cfg.Pose.ToLowerInvariant())
        {
            case "sleep": _cat.SetAsleep(true); return;
            case "sit": _cat.SetAsleep(false); return;
        }

        long now = Environment.TickCount64;
        if (!_napping && _fullscreen && now >= _fullscreenNapAt)
        {
            // Settle in for the whole fullscreen session.
            _napping = true;
            _napEndsAt = long.MaxValue;
        }
        else if (!_napping && now >= _nextNapAt)
        {
            _napping = true;
            _napEndsAt = now + RandomMs(_cfg.NapMinutes, 2, 8);
        }
        else if (_napping && (now >= _napEndsAt || IsHovered()))
        {
            bool startled = now < _napEndsAt;
            _napping = false;
            _nextNapAt = now + RandomMs(_cfg.AwakeMinutes, 3, 12);
            if (_fullscreen) _fullscreenNapAt = now + FullscreenSettleMs();
            _cat.SetAsleep(false);
            if (startled) _cat.Say("?!");
        }
        _cat.SetAsleep(_napping);
    }

    /// <summary>
    /// Every ~1.5 s picks a target color from the window the cat is sitting on: the softer tint
    /// over the desktop and terminals, the normal light color over everything else (even dark-mode
    /// apps). Each frame eases the current color toward the target.
    /// </summary>
    private void UpdateTone()
    {
        if (_frames % 12 == 0) _targetColor = PickColor();
        if (_currentColor == _targetColor) return;

        var next = Lerp(_currentColor, _targetColor, 0.35);
        if (ColorDistance(next, _targetColor) < 3) next = _targetColor;
        _currentColor = next;
        _cat.SetTint(next);
    }

    private Color PickColor() =>
        _cfg.AdaptToBackground && IsOverShellOrTerminal() ? _tint : _lightTint;

    /// <summary>True when the window under the cat is the desktop or a console/terminal window.</summary>
    private bool IsOverShellOrTerminal()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r)) return true;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // Middle of the cat's body. Our window is click-through, so hit-testing finds what's beneath.
        var p = new Native.POINT
        {
            X = r.Left + (int)(_cat.CatWidth * scale * 0.5),
            Y = r.Bottom - (int)(_cat.CatHeight * scale * 0.4),
        };
        var under = Native.WindowFromPoint(p);
        if (under == IntPtr.Zero) return true;
        var root = Native.GetAncestor(under, Native.GA_ROOT);
        return ShellOrTerminalClasses.Contains(Native.ClassNameOf(root == IntPtr.Zero ? under : root));
    }

    private static int ColorDistance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) + Math.Abs(a.A - b.A);

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>A click on the cat itself (not the bubble room above it) squishes it.</summary>
    private void OnClick(int x, int y)
    {
        if (_hwnd == IntPtr.Zero || _running || _away || !IsVisible || !Native.GetWindowRect(_hwnd, out var r)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (x >= r.Left && x < r.Left + _cat.CatWidth * scale && y >= r.Bottom - _cat.CatHeight * scale && y < r.Bottom)
            _cat.Squish();
    }

    private bool IsHovered() =>
        _hwnd != IntPtr.Zero && Native.GetCursorPos(out var p) && Native.GetWindowRect(_hwnd, out var r)
        && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    private long RandomMs(List<double> range, double defaultMin, double defaultMax)
    {
        double min = range.Count > 0 ? range[0] : defaultMin;
        double max = range.Count > 1 ? range[1] : min;
        if (max < min) (min, max) = (max, min);
        return (long)((min + _rng.NextDouble() * (max - min)) * 60_000);
    }

    private void ApplyZOrder()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_cfg.Topmost) WindowPlacement.MakeTopmost(_hwnd);
        else WindowPlacement.PinToDesktop(_hwnd);
    }

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero || _running) return; // the run positions the window itself
        if (_away) { WindowPlacement.MoveTo(_hwnd, -32000, -32000); return; }
        var area = Screens.ByName(ScreenName).WorkingArea;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        WindowPlacement.MoveTo(_hwnd, HomeX(area, scale), BottomY(area, scale));
    }

    /// <summary>Window x that puts the cat at its spot (the extra room for the bubble extends to its right).</summary>
    private int HomeX(System.Drawing.Rectangle area, double scale) =>
        area.Right - (int)Math.Round(_cat.CatWidth * scale) - (int)Math.Round(_cfg.OffsetX * scale);

    /// <summary>Window y that sits the cat on top of a bottom taskbar. Uses the WPF layout size,
    /// since on SizeChanged the HWND may not have been resized yet.</summary>
    private int BottomY(System.Drawing.Rectangle area, double scale) =>
        area.Bottom - (int)Math.Round(ActualHeight * scale);

    // --- running between screens ------------------------------------------------------------

    private const double RunTopSpeedDip = 700;  // DIPs per second
    private const double RunAccelSeconds = 0.3; // time to reach top speed from a standstill
    private const double RunArriveSpeedDip = 120; // slowest it gets while easing into its spot

    private bool _running;
    private bool _entering; // false: running off the old screen; true: coming in on the new one
    private string? _runDest; // null: run off and stay away
    private bool _away;     // ran off its screen and is parked out of sight
    private double _runX;   // window x, physical pixels
    private double _runSpeed; // DIPs per second
    private double _strideLeft; // DIPs until the next gallop step
    private readonly Stopwatch _runClock = new();

    /// <summary>
    /// Always runs left: off the left edge of the current screen, then in from the right edge of the
    /// next (<paramref name="dest"/> null: it stays off screen). <paramref name="entering"/> skips
    /// the first half, coming straight in on <paramref name="dest"/>.
    /// </summary>
    private void StartRun(string? dest, bool entering = false)
    {
        if (entering)
        {
            _runX = Screens.ByName(dest!).WorkingArea.Right - _cat.RunSpriteLeft * VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        else if (Native.GetWindowRect(_hwnd, out var r)) _runX = r.Left;
        else
        {
            if (dest is null) _away = true;
            else ScreenName = dest;
            Reposition();
            return;
        }
        _running = true;
        _entering = entering;
        _runDest = dest;
        _runSpeed = 0;
        _strideLeft = _cat.RunStrideDip;
        _napping = false;
        _cat.SetAsleep(false);
        _cat.SetRunning(true);
        _runClock.Restart();
        // Once per displayed frame, so motion lines up with the screen's refresh.
        CompositionTarget.Rendering += OnRunFrame;
    }

    private void OnRunFrame(object? sender, EventArgs e)
    {
        // Don't leap forward after a hiccup (e.g. the machine was busy).
        double dt = Math.Min(0.05, _runClock.Elapsed.TotalSeconds);
        _runClock.Restart();
        if (dt <= 0) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = Screens.ByName(ScreenName).WorkingArea;

        // Ease in from a standstill; when arriving, ease out so it settles into its spot.
        _runSpeed = Math.Min(RunTopSpeedDip, _runSpeed + RunTopSpeedDip / RunAccelSeconds * dt);
        if (_entering)
        {
            double remainingDip = Math.Max(0, (_runX - HomeX(area, scale)) / scale);
            double decel = RunTopSpeedDip / RunAccelSeconds;
            _runSpeed = Math.Min(_runSpeed, Math.Max(RunArriveSpeedDip, Math.Sqrt(2 * decel * remainingDip)));
        }
        double moveDip = _runSpeed * dt;
        _runX -= moveDip * scale;

        // Legs advance by distance travelled, so they never look like they're sliding.
        _strideLeft -= moveDip;
        if (_strideLeft <= 0)
        {
            _strideLeft += _cat.RunStrideDip;
            _cat.StepRun();
        }

        if (!_entering)
        {
            // Fully off the left edge: hop to just past the right edge of the destination.
            double spriteRight = _runX + (_cat.RunSpriteLeft + _cat.RunSpriteWidth) * scale;
            if (spriteRight <= area.Left)
            {
                if (_runDest is null)
                {
                    _away = true;
                    FinishRun();
                    return;
                }
                ScreenName = _runDest ?? ScreenName;
                area = Screens.ByName(ScreenName).WorkingArea;
                _runX = area.Right - _cat.RunSpriteLeft * scale;
                _entering = true;
            }
        }
        else if (_runX <= HomeX(area, scale))
        {
            FinishRun();
            return;
        }

        WindowPlacement.MoveTo(_hwnd, (int)Math.Round(_runX), BottomY(area, scale));
        // Only draw the part that's on this screen, so it never peeks out onto a neighbor.
        double x0 = Math.Max(0, (area.Left - _runX) / scale), x1 = (area.Right - _runX) / scale;
        _cat.Clip = new RectangleGeometry(new Rect(x0, 0, Math.Max(0, x1 - x0), Math.Max(0, ActualHeight)));
    }

    private void FinishRun()
    {
        CompositionTarget.Rendering -= OnRunFrame;
        _runClock.Stop();
        _running = false;
        _cat.Clip = null;
        _cat.SetRunning(false);
        Reposition();
        if (_away) return;
        // Asked to go somewhere else (or away) mid-run? Head there now.
        if (_runDest is null) RunAway();
        else if (_runDest != ScreenName) MoveToScreen(_runDest);
    }

    private static Color Parse(string s, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch { return fallback; }
    }

    private static TimeSpan FrameInterval(CatConfig cfg) => TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(cfg.Fps, 1, 30));
}
