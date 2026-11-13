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
public sealed class CatWindow : CatPopupWindow
{
    private const int ReassertTopmostEveryNFrames = 40;

    private readonly DispatcherTimer _timer;

    // Desktop (Progman/WorkerW), classic console (cmd/PowerShell), and Windows Terminal.
    private static readonly HashSet<string> ShellOrTerminalClasses =
        ["Progman", "WorkerW", "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow"];

    private Color _tint, _lightTint = Colors.White;
    private Color _currentColor, _targetColor;
    private long _frames;
    private readonly Random _rng = new();
    private bool _napping;
    private long _napEndsAt;
    private long _nextNapAt;
    private bool _fullscreen;   // the focused window is a fullscreen app
    private long _fullscreenNapAt;
    private MouseClicks? _clicks;

    public CatWindow(CatConfig cfg, Color tint, string screenName) : base(cfg, screenName)
    {
        _tint = tint;
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
            CompositionTarget.Rendering -= OnLeanFrame;
        };
    }

    /// <summary>Where a seated cat is on screen, in physical pixels (all x's are screen x's).</summary>
    public readonly record struct SeatedPose(int BodyLeft, int BodyRight, int TailTip, int Top, int Floor, bool Flipped, bool Asleep);

    /// <summary>
    /// Where it sits, while it's sitting in its spot; null while it runs, is away, peeks or is
    /// hidden. What the kitten keeps itself next to.
    /// </summary>
    public SeatedPose? Seated
    {
        get
        {
            if (_hwnd == IntPtr.Zero || _running || _away || IsPeeking || !IsVisible || !Native.GetWindowRect(_hwnd, out var r))
                return null;
            if (_leaning) return HomeSeat; // its feet haven't moved, whatever it's leaning
            return PoseAt(r.Left + RoomPx, r.Bottom);
        }
    }

    /// <summary>Where it sits (or will sit) in its spot on its current screen, whatever it's doing now.</summary>
    public SeatedPose HomeSeat
    {
        get
        {
            var area = Screens.ByName(ScreenName).WorkingArea;
            return PoseAt(HomeX(area, VisualTreeHelper.GetDpi(this).DpiScaleX), area.Bottom);
        }
    }

    private SeatedPose PoseAt(int left, int bottom)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int X(double dip) => left + (int)Math.Round(dip * scale);
        return new SeatedPose(X(_cat.BodyLeft), X(_cat.BodyLeft + _cat.BodyWidth), X(_cat.TailTipX),
            bottom - (int)Math.Round(_cat.CatHeight * scale), bottom, _cfg.Flip, _cat.Asleep);
    }

    /// <summary>
    /// Raised the moment it starts to run or peek, so the kitten can react right away: during a
    /// run the UI thread is busy every frame and slow timers don't get a turn.
    /// </summary>
    public event Action? Moved;

    /// <summary>A left click anywhere on screen (physical pixels), for the kitten to react to too.</summary>
    public event Action<int, int>? Clicked;

    protected override void OnMoved() => Moved?.Invoke();

    protected override bool PopupsBlocked => _running || _away || _napping;

    public enum MoveKind { Seated, Running, Peeking, Away, Hidden }

    /// <summary>
    /// What the cat is doing at one moment, in screen pixels, for the kitten to copy a moment
    /// later. CenterX is its body's center (or where it would be, mid-run); while peeking,
    /// PawsLine is the bottom of its paws, Edge where it's cut off, and GetUp (0..1, else -1) how
    /// far it has risen from the taskbar back into its seat.
    /// </summary>
    public readonly record struct Move(MoveKind Kind, string Screen, double CenterX, int Dir,
        double PawsLine, double Edge, bool Paws, bool Landed, double GetUp, double CardLeft, double CardRight);

    public Move CurrentMove
    {
        get
        {
            if (_hwnd == IntPtr.Zero || !IsVisible) return new(MoveKind.Hidden, ScreenName, 0, RunDir, 0, 0, false, false, -1, 0, 0);
            if (_away) return new(MoveKind.Away, ScreenName, 0, RunDir, 0, 0, false, false, -1, 0, 0);
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double mid = (_cat.BodyLeft + _cat.BodyWidth / 2) * scale;
            if (_running) return new(MoveKind.Running, ScreenName, _runX + mid, RunDir, 0, 0, false, false, -1, 0, 0);
            if (!IsPeeking) return new(MoveKind.Seated, ScreenName, 0, RunDir, 0, 0, false, false, -1, 0, 0);

            var area = Screens.ByName(ScreenName).WorkingArea;
            double paws = (_cat.GridTop + (CatView.PeekEdgeRow + 1) * _cat.BlockSize) * scale;
            double floorY = area.Bottom - paws, homeY = BottomY(area, scale);
            double getUp = _peek == PeekPhase.GettingUp && floorY > homeY ? (floorY - _peekY) / (floorY - homeY) : -1;
            return new(MoveKind.Peeking, ScreenName, _peekX + mid, RunDir, _peekY + paws, _edge,
                _cat.PeekPawsOut, _peek == PeekPhase.Landed, getUp, _card.Left, _card.Right);
        }
    }

    /// <summary>The color it's showing right now (it shifts with what's beneath it).</summary>
    public Color CurrentColor => _currentColor;
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
        EndPeekOrLeanNow();
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
        EndPeekOrLeanNow();
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
        _cat.Flipped = cfg.Flip;
        _cat.Configure(cfg, VisualTreeHelper.GetDpi(this).PixelsPerDip, tint);
        double room = LeanRoomBlocks * _cat.BlockSize;
        _cat.Margin = new Thickness(room, 0, room, 0);
        // Still click-through: the click goes on to whatever is beneath, the cat just reacts.
        if (cfg.ClickReactions && _clicks is null && _hwnd != IntPtr.Zero)
        {
            _clicks = new MouseClicks();
            _clicks.LeftDown += (x, y) => Dispatcher.BeginInvoke(() => { OnClick(x, y); Clicked?.Invoke(x, y); });
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
            EndPeekOrLeanNow();
            _timer.Stop();
            Hide();
        }
        else { Show(); ApplyZOrder(); _timer.Start(); }
    }

    private void Tick()
    {
        if (_running) return; // the run timer animates it meanwhile
        if (_frames % 2 == 0) UpdatePeek();
        if (_frames % 4 == 0 && !IsPeeking && !_leaning) UpdateNap();
        // Off screen there's nothing beneath it to match: keep the color it left with.
        if (!_away) UpdateTone();
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
            X = r.Left + RoomPx + (int)(_cat.CatWidth * scale * 0.5),
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
        if (_hwnd == IntPtr.Zero || _running || _away || IsPeeking || _leaning || !IsVisible || !Native.GetWindowRect(_hwnd, out var r)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (x >= r.Left + RoomPx && x < r.Left + RoomPx + _cat.CatWidth * scale && y >= r.Bottom - _cat.CatHeight * scale && y < r.Bottom)
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

    protected override void Reposition()
    {
        if (_hwnd == IntPtr.Zero || _running) return; // the run positions the window itself
        if (_away) { WindowPlacement.MoveTo(_hwnd, -32000, -32000); return; }
        if (IsPeeking) return; // OnPeekFrame places it
        if (_leaning) return;  // placed (padded) when the lean started
        var area = Screens.ByName(ScreenName).WorkingArea;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        WindowPlacement.MoveTo(_hwnd, HomeX(area, scale) - RoomPx, BottomY(area, scale));
    }

    /// <summary>
    /// Window x that puts the cat at its spot: <see cref="CatConfig.OffsetX"/> in from the right
    /// edge, or from the left edge when mirrored (the extra room for the bubble extends to its right).
    /// </summary>
    protected override int HomeX(System.Drawing.Rectangle area, double scale) => _cfg.Flip
        ? area.Left + (int)Math.Round((_cfg.OffsetX - _cat.GridLeft) * scale)
        : area.Right - (int)Math.Round(_cat.CatWidth * scale) - (int)Math.Round(_cfg.OffsetX * scale);

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

    /// <summary>+1 when it runs right (mirrored), -1 when it runs left.</summary>
    private int RunDir => _cfg.Flip ? 1 : -1;

    /// <summary>Window x that has the running sprite just out of sight past the edge it comes in from.</summary>
    private double EntryX(System.Drawing.Rectangle area, double scale) => RunDir < 0
        ? area.Right - _cat.RunSpriteLeft * scale
        : area.Left - (_cat.RunSpriteLeft + _cat.RunSpriteWidth) * scale;

    /// <summary>
    /// Always runs the way it faces (left, or right when mirrored): off that edge of the current
    /// screen, then in from the opposite edge of the next (<paramref name="dest"/> null: it stays
    /// off screen). <paramref name="entering"/> skips the first half, coming straight in on
    /// <paramref name="dest"/>.
    /// </summary>
    private void StartRun(string? dest, bool entering = false)
    {
        EndPeekOrLeanNow();
        if (entering)
        {
            _runX = EntryX(Screens.ByName(dest!).WorkingArea, VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
        else if (Native.GetWindowRect(_hwnd, out var r)) _runX = r.Left + RoomPx;
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
        Moved?.Invoke();
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
            double remainingDip = Math.Max(0, (HomeX(area, scale) - _runX) * RunDir / scale);
            double decel = RunTopSpeedDip / RunAccelSeconds;
            _runSpeed = Math.Min(_runSpeed, Math.Max(RunArriveSpeedDip, Math.Sqrt(2 * decel * remainingDip)));
        }
        double moveDip = _runSpeed * dt;
        _runX += RunDir * moveDip * scale;

        // Legs advance by distance travelled, so they never look like they're sliding.
        _strideLeft -= moveDip;
        if (_strideLeft <= 0)
        {
            _strideLeft += _cat.RunStrideDip;
            _cat.StepRun();
        }

        if (!_entering)
        {
            // Fully off this screen: hop to just past the far edge of the destination.
            double spriteLeft = _runX + _cat.RunSpriteLeft * scale;
            double spriteRight = spriteLeft + _cat.RunSpriteWidth * scale;
            if (RunDir < 0 ? spriteRight <= area.Left : spriteLeft >= area.Right)
            {
                if (_runDest is null)
                {
                    _away = true;
                    FinishRun();
                    return;
                }
                ScreenName = _runDest ?? ScreenName;
                area = Screens.ByName(ScreenName).WorkingArea;
                _runX = EntryX(area, scale);
                _entering = true;
            }
        }
        else if ((HomeX(area, scale) - _runX) * RunDir <= 0)
        {
            FinishRun();
            return;
        }

        WindowPlacement.MoveTo(_hwnd, (int)Math.Round(_runX) - RoomPx, BottomY(area, scale));
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
