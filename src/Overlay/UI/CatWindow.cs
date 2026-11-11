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
        };
        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => ApplyConfig(_cfg, _tint);
        Closed += (_, _) =>
        {
            _clicks?.Dispose();
            _timer.Stop();
            CompositionTarget.Rendering -= OnRunFrame;
            CompositionTarget.Rendering -= OnPeekFrame;
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
        if (IsPeeking) EndPeekNow();
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
        if (IsPeeking) EndPeekNow();
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
        // Still click-through: the click goes on to whatever is beneath, the cat just reacts.
        if (cfg.ClickReactions && _clicks is null && _hwnd != IntPtr.Zero)
        {
            _clicks = new MouseClicks();
            _clicks.LeftDown += (x, y) => Dispatcher.BeginInvoke(() => OnClick(x, y));
        }
        else if (!cfg.ClickReactions && _clicks is not null)
        {
            _clicks.Dispose();
            _clicks = null;
        }
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
        if (paused)
        {
            if (IsPeeking) EndPeekNow();
            _timer.Stop();
            Hide();
        }
        else { Show(); ApplyZOrder(); _timer.Start(); }
    }

    private void Tick()
    {
        if (_running) return; // the run timer animates it meanwhile
        if (_frames % 2 == 0) UpdatePeek();
        if (_frames % 4 == 0 && !IsPeeking) UpdateNap();
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
        if (_hwnd == IntPtr.Zero || _running || _away || IsPeeking || !IsVisible || !Native.GetWindowRect(_hwnd, out var r)) return;
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
        if (IsPeeking) return; // OnPeekFrame places it
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
        if (IsPeeking) EndPeekNow();
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

    // --- peeking over notifications -------------------------------------------------------
    //
    // Covered by a popup for a moment, the cat rises behind it until its head shows over the top
    // edge, then rests its paws there. It rides the edge if the popup slides down, and keeps going
    // down on its own if the popup vanishes on the way (or leaves any other way). Down on the
    // taskbar it stays in the pose a moment after the popup is gone, then gets up and sits.

    private enum PeekPhase { None, Rising, Up, Landed, Falling, GettingUp }

    private const long PeekDelayMs = 1_000;      // covered this long before it peeks: a beat to notice
    private const double PeekRiseSeconds = 0.18; // pops up from behind the edge
    private const double FallGravityDip = 2600;  // DIPs/s² while dropping to the taskbar
    private const long LandedPauseMs = CatView.SquishMs; // down on the taskbar: gets up as soon as its touchdown squish ends
    private const double GetUpSeconds = 0.2;     // from peeking over the taskbar up into its seat

    private PeekPhase _peek;
    private long _landedAt;       // landed: TickCount64 when the card last moved (or left)
    private double _landedEdge;   // landed: the card's top when last seen; MaxValue once it has left
    private IntPtr _ignoredPopup; // got up from peeking over this one; leave it be while it stays
    private bool IsPeeking => _peek != PeekPhase.None;
    private IntPtr _popupHwnd;    // what covers it (or what it peeks over)
    private (int L, int T, int R, int B)? _cardInsets; // popup window rect -> its visible card; null = still measuring
    private long _coveredSince;   // TickCount64 when the current popup first covered it
    private double _peekLift;     // rising: 0 = hidden behind the edge, 1 = all the way up
    private int _peekX;           // window x while peeking, physical pixels
    private double _peekY;        // window y while peeking or falling, physical pixels
    private double _edge;         // screen y below which the body is hidden (the card's top edge)
    private double _fallSpeed;    // DIPs per second
    private double _getUpT;       // 0..1 through getting up
    private double _cardSpeed;    // how fast the card's edge was moving down, DIPs per second
    private readonly Stopwatch _peekClock = new();

    /// <summary>
    /// Watches for a notification or flyout covering the cat's spot, and starts peeking once that
    /// has lasted <see cref="PeekDelayMs"/>. Once peeking, <see cref="OnPeekFrame"/> takes over.
    /// </summary>
    private void UpdatePeek()
    {
        bool allowed = _cfg.PeekOverPopups && _cfg.Topmost && !_running && !_away && IsVisible
            && !_cfg.Pose.Equals("sleep", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
        {
            if (IsPeeking) EndPeekNow();
            _popupHwnd = IntPtr.Zero;
            return;
        }
        if (IsPeeking) return;

        if (!FindPopup(out var hwnd, out var bounds, out var body) || hwnd != _ignoredPopup) _ignoredPopup = IntPtr.Zero;
        if (hwnd == IntPtr.Zero || hwnd == _ignoredPopup)
        {
            _popupHwnd = IntPtr.Zero;
            return;
        }
        long now = Environment.TickCount64;
        if (hwnd != _popupHwnd)
        {
            _popupHwnd = hwnd;
            _coveredSince = now;
            _cardInsets = null;
            MeasureCard(hwnd, bounds);
            return;
        }
        if (_cardInsets is not { } inset) return; // still measuring

        // Only the card counts: a toast's transparent margin over the cat doesn't, nor does
        // something too big to be a popup. The delay runs from when the card itself covers it.
        var card = Card(bounds, inset);
        if (!card.IntersectsWith(body) || !PopupWindows.IsPopupSized(card, Screens.ByName(ScreenName).Bounds)
            || PeekHeight(card.Top) <= 0) // low enough that its head is already above it
            _coveredSince = now;
        else if (now - _coveredSince >= PeekDelayMs)
            StartPeek();
    }

    /// <summary>A popup over the cat's usual spot (<paramref name="body"/>).</summary>
    private bool FindPopup(out IntPtr hwnd, out System.Drawing.Rectangle bounds, out System.Drawing.Rectangle body)
    {
        hwnd = IntPtr.Zero;
        bounds = body = System.Drawing.Rectangle.Empty;
        if (_hwnd == IntPtr.Zero) return false;
        var screen = Screens.ByName(ScreenName);
        var area = screen.WorkingArea;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int height = (int)Math.Round(_cat.CatHeight * scale);
        body = new System.Drawing.Rectangle(
            HomeX(area, scale) + (int)Math.Round(_cat.BodyLeft * scale), area.Bottom - height,
            (int)Math.Round(_cat.BodyWidth * scale), height);
        hwnd = PopupWindows.Covering(body, _hwnd, out bounds);
        return hwnd != IntPtr.Zero;
    }

    /// <summary>Finds the popup's visible card in the background (it takes a moment), as insets from its window.</summary>
    private void MeasureCard(IntPtr hwnd, System.Drawing.Rectangle bounds)
    {
        Task.Run(() => PopupWindows.VisibleBounds(hwnd, bounds)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (hwnd != _popupHwnd) return;
            var v = t.Result;
            _cardInsets = (v.Left - bounds.Left, v.Top - bounds.Top, bounds.Right - v.Right, bounds.Bottom - v.Bottom);
            Log.Info($"Popup over the cat: {Native.ClassNameOf(hwnd)} window {bounds}, card {v}");
        }), TaskScheduler.Default);
    }

    private static System.Drawing.Rectangle Card(System.Drawing.Rectangle window, (int L, int T, int R, int B) inset) =>
        System.Drawing.Rectangle.FromLTRB(window.Left + inset.L, window.Top + inset.T, window.Right - inset.R, window.Bottom - inset.B);

    /// <summary>Where the popup's card is now, or null once it's gone (hidden, closed, cloaked, faded out).</summary>
    private System.Drawing.Rectangle? CurrentCard() =>
        _cardInsets is { } inset && PopupWindows.IsShowing(_popupHwnd, out var bounds) ? Card(bounds, inset) : null;

    private void StartPeek()
    {
        if (CurrentCard() is not { } card) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = Screens.ByName(ScreenName).WorkingArea;
        double px = _cat.BlockSize * scale;
        double bodyOff = _cat.BodyLeft * scale, bodyW = _cat.BodyWidth * scale;
        // Slid sideways as needed so it peeks over the card rather than past its side.
        double minX = card.Left + px - bodyOff, maxX = card.Right - px - bodyW - bodyOff;
        _peekX = (int)Math.Round(maxX < minX ? (card.Left + card.Right - bodyW) / 2.0 - bodyOff : Math.Clamp(HomeX(area, scale), minX, maxX));

        _peek = PeekPhase.Rising;
        _peekLift = 0;
        _cardSpeed = 0;
        // Something showed up: it wakes from a nap to have a look.
        _napping = false;
        _nextNapAt = Environment.TickCount64 + RandomMs(_cfg.AwakeMinutes, 3, 12);
        _cat.SetAsleep(false);
        _cat.SetPeeking(true);
        _peekClock.Restart();
        CompositionTarget.Rendering += OnPeekFrame;
    }

    /// <summary>Every displayed frame while peeking: follows the card, or drops when it's gone.</summary>
    private void OnPeekFrame(object? sender, EventArgs e)
    {
        double dt = Math.Min(0.05, _peekClock.Elapsed.TotalSeconds);
        _peekClock.Restart();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = Screens.ByName(ScreenName).WorkingArea;
        double px = _cat.BlockSize * scale;
        double homeY = BottomY(area, scale);
        // The floor: still peeking, paws resting on the taskbar, the rest of it behind it.
        double floorY = area.Bottom - (_cat.GridTop + (CatView.PeekEdgeRow + 1) * _cat.BlockSize) * scale;

        if (_peek == PeekPhase.GettingUp)
        {
            GetUp(dt, scale, area, homeY, floorY);
            return;
        }
        if (_peek == PeekPhase.Falling)
        {
            Fall(dt, scale, area, floorY);
            return;
        }

        long now = Environment.TickCount64;
        double centerX = _peekX + (_cat.BodyLeft + _cat.BodyWidth / 2) * scale;
        var current = CurrentCard();
        bool gone = current is not { } c || centerX < c.Left || centerX > c.Right;
        bool slidAway = current is { } below && below.Top >= area.Bottom; // down behind the taskbar
        if (gone || slidAway)
        {
            // Down on the taskbar already (or the card slid out of sight under it): it stays put
            // a moment after the card has gone, then gets up.
            if (_peek == PeekPhase.Landed || slidAway)
            {
                if (_peek != PeekPhase.Landed) Land(now, floorY, area, scale);
                if (_landedEdge != double.MaxValue)
                {
                    _landedEdge = double.MaxValue;
                    _landedAt = now; // the moment starts when the card has left
                }
                if (now - _landedAt >= LandedPauseMs) StartGetUp();
                return;
            }
            StartFall(); // let go and drop
            return;
        }
        var card = current!.Value;
        if (dt > 0 && _peek != PeekPhase.Rising) _cardSpeed = (card.Top - _edge) / scale / dt;
        _edge = card.Top;

        if (_peek == PeekPhase.Rising)
        {
            _peekLift = Math.Min(1, _peekLift + dt / PeekRiseSeconds);
            if (_peekLift >= 1)
            {
                _peek = PeekPhase.Up;
                _cat.SetPeekPaws(true); // up: now it rests its paws on the edge
            }
        }

        // Up: the paw row's bottom on the edge. Down: even the ear tips are below it. It rides
        // the edge down, but never below the floor.
        double upY = _edge - (_cat.GridTop + (CatView.PeekEdgeRow + 1) * _cat.BlockSize) * scale;
        double downY = upY + (CatView.PeekEdgeRow + 1) * px;
        double eased = 1 - (1 - _peekLift) * (1 - _peekLift);
        _peekY = Math.Min(downY + (upY - downY) * eased, floorY);

        // Ridden down to the taskbar, it stays in the peek pose while the card slides on past.
        // If the card stops there instead of leaving, it gets up a moment after it stops.
        if (_peek != PeekPhase.Rising)
        {
            if (upY < floorY) _peek = PeekPhase.Up;
            else if (_peek != PeekPhase.Landed) Land(now, floorY, area, scale);
            else if (card.Top != _landedEdge)
            {
                _landedEdge = card.Top; // still moving
                _landedAt = now;
            }
            else if (now - _landedAt >= LandedPauseMs)
            {
                _ignoredPopup = _popupHwnd; // don't peek right back at it while it stays put
                StartGetUp();
                return;
            }
            if (_peek == PeekPhase.Landed) return;
        }
        Place(scale);
    }

    /// <summary>Down on the floor: paws on the taskbar, everything below them hidden behind it.</summary>
    private void Land(long now, double floorY, System.Drawing.Rectangle area, double scale)
    {
        _peek = PeekPhase.Landed;
        _landedAt = now;
        _landedEdge = double.NaN;
        _peekY = floorY;
        _edge = area.Bottom;
        Place(scale);
        _cat.Squish(); // touchdown
    }

    /// <summary>Gets up from the floor: the whole cat rises from behind the taskbar into its seat.</summary>
    private void StartGetUp()
    {
        _peek = PeekPhase.GettingUp;
        _getUpT = 0;
        _cat.Unsquish(); // the touchdown squish is over: up it gets
        _cat.SetPeeking(false);
    }

    private void GetUp(double dt, double scale, System.Drawing.Rectangle area, double homeY, double floorY)
    {
        _getUpT = Math.Min(1, _getUpT + dt / GetUpSeconds);
        double eased = 1 - (1 - _getUpT) * (1 - _getUpT);
        _peekY = floorY + (homeY - floorY) * eased;
        _edge = area.Bottom;
        if (_getUpT >= 1)
        {
            EndPeekNow();
            return;
        }
        Place(scale);
    }

    /// <summary>
    /// How far (DIPs) above its seat it would be, peeking over an edge at screen y
    /// <paramref name="edge"/>. 0 or less: sitting, its head is above that edge already.
    /// </summary>
    private double PeekHeight(double edge)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double pawsBottom = BottomY(Screens.ByName(ScreenName).WorkingArea, scale)
            + (_cat.GridTop + (CatView.PeekEdgeRow + 1) * _cat.BlockSize) * scale;
        return (pawsBottom - edge) / scale;
    }

    /// <summary>
    /// The card left before it got down to the taskbar (popups often vanish mid-slide): it keeps
    /// going down on its own, still in the peek pose, from the card's speed.
    /// </summary>
    private void StartFall()
    {
        _peek = PeekPhase.Falling;
        _fallSpeed = Math.Max(0, _cardSpeed);
    }

    /// <summary>Down to the floor, speeding up as it goes; then a moment there and it gets up.</summary>
    private void Fall(double dt, double scale, System.Drawing.Rectangle area, double floorY)
    {
        _fallSpeed += FallGravityDip * dt;
        _peekY = Math.Min(floorY, _peekY + _fallSpeed * dt * scale);
        // Nothing to hide behind any more: the edge just follows its paws.
        _edge = _peekY + (_cat.GridTop + (CatView.PeekEdgeRow + 1) * _cat.BlockSize) * scale;
        if (_peekY >= floorY)
        {
            Land(Environment.TickCount64, floorY, area, scale);
            _landedEdge = double.MaxValue; // the card is gone: the moment starts now
            return;
        }
        Place(scale);
    }

    /// <summary>Moves the window to the peek position and hides everything below the edge.</summary>
    private void Place(double scale)
    {
        int y = (int)Math.Round(_peekY);
        WindowPlacement.MoveTo(_hwnd, _peekX, y);
        _cat.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, (_edge - y) / scale)));
    }

    /// <summary>Back to sitting in its usual spot, right away.</summary>
    private void EndPeekNow()
    {
        if (IsPeeking) CompositionTarget.Rendering -= OnPeekFrame;
        _peek = PeekPhase.None;
        _peekClock.Stop();
        _popupHwnd = IntPtr.Zero;
        _cardInsets = null;
        _cat.Clip = null;
        _cat.SetPeeking(false);
        Reposition();
    }

    private static Color Parse(string s, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch { return fallback; }
    }

    private static TimeSpan FrameInterval(CatConfig cfg) => TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(cfg.Fps, 1, 30));
}
