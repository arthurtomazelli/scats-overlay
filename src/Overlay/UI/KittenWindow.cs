using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Overlay.Config;
using Overlay.Interop;

namespace Overlay.UI;

/// <summary>
/// A tiny second cat that keeps a big one (its "mom") company, sitting next to it on the taskbar.
/// Every so often it crouches and leaps over the big cat to its other side. It naps when the big
/// cat naps, and keeps out of sight while the big cat runs, peeks or is away.
/// </summary>
public sealed class KittenWindow : CatPopupWindow
{
    private const int ReassertTopmostEveryNFrames = 40;
    private const double JumpSeconds = 0.55;
    private const int TailGapBlocks = 2;  // by the tip of the big cat's tail
    private const int SideGapBlocks = 3;  // landing on the other side, it keeps some room from the big cat
    private const double KittenStrideFactor = 1.5;
    private const double OffsetEaseDip = 250; // DIPs per second it drops back to the big cat's tail side

    private enum Act { Sit, Crouch, Leap, Land }

    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private readonly Stopwatch _leapClock = new();
    private long _frames;
    private Color _tint;
    private bool _hidden = true;

    private bool _onTailSide = true;
    private Act _act;
    private long _nextActAt, _actUntil;
    private double _leapT;
    private double _leapFromX, _leapToX, _leapHeight; // body centers and arc height, physical pixels

    public KittenWindow(CatWindow mom, CatConfig cfg) : base(cfg, mom.ScreenName)
    {
        Mom = mom;
        _nextActAt = Environment.TickCount64 + NextJumpWaitMs();

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
        Title = "Overlay kitten";
        Content = _cat;

        // Normal rather than Background: it has to keep ticking while the big cat animates every frame.
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(125) };
        _timer.Tick += (_, _) => Tick();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowPlacement.MakeGhost(_hwnd);
            ApplyConfig(_cfg);
            _timer.Start();
        };
        DpiChanged += (_, _) => ApplyConfig(_cfg);
        Mom.Moved += OnMomMoved;
        Mom.Clicked += OnMomClicked;
        Closed += (_, _) =>
        {
            Mom.Moved -= OnMomMoved;
            Mom.Clicked -= OnMomClicked;
            _timer.Stop();
            CompositionTarget.Rendering -= OnLeapFrame;
            CompositionTarget.Rendering -= OnFollowFrame;
            CompositionTarget.Rendering -= OnPeekFrame;
            CompositionTarget.Rendering -= OnLeanFrame;
        };
    }

    /// <summary>The big cat it stays with.</summary>
    public CatWindow Mom { get; }

    public void ApplyConfig(CatConfig cfg)
    {
        _cfg = cfg;
        _tint = Mom.CurrentColor;
        var look = new CatConfig { Scale = cfg.Kitten.Scale, Thoughts = false, Moods = false, BubbleFont = cfg.BubbleFont };
        _cat.Configure(look, VisualTreeHelper.GetDpi(this).PixelsPerDip, _tint);
        // Room around the view so it can tilt (lean) without being cut off, like the big cat.
        double room = LeanRoomBlocks * _cat.BlockSize;
        _cat.Margin = new Thickness(room, 0, room, 0);
        // A changed range applies to the wait in progress, not just the one after it.
        if (_act == Act.Sit) _nextActAt = Environment.TickCount64 + NextJumpWaitMs();
        ApplyZOrder();
    }

    private void Tick()
    {
        if (++_frames % ReassertTopmostEveryNFrames == 0) ApplyZOrder();
        if (Mom.CurrentColor != _tint)
        {
            _tint = Mom.CurrentColor;
            _cat.SetTint(_tint);
        }

        // When the big cat runs off (or is out of sight) it runs after it, a beat later.
        var kind = Mom.CurrentMove.Kind;
        if (!_following && kind is CatWindow.MoveKind.Running or CatWindow.MoveKind.Away or CatWindow.MoveKind.Hidden) StartFollowing();
        if (_following)
        {
            _cat.Step();
            return;
        }

        // Otherwise it stays by the big cat's spot (even while the big cat peeks over a popup) and
        // reacts to popups over itself on its own, just like the big cat does.
        ScreenName = Mom.ScreenName;
        var seat = kind == CatWindow.MoveKind.Peeking ? Mom.HomeSeat : Mom.Seated;
        if (seat is not { } mom)
        {
            _cat.Step();
            return;
        }
        UpdatePeek();
        if (IsPeeking || _leaning)
        {
            _cat.Step();
            return;
        }

        long now = Environment.TickCount64;
        switch (_act)
        {
            case Act.Sit:
                // Naps along with the big cat; otherwise, now and then, leaps over it.
                _cat.SetAsleep(mom.Asleep);
                if (!mom.Asleep && kind == CatWindow.MoveKind.Seated && now >= _nextActAt) StartCrouch(now);
                break;
            case Act.Crouch:
                if (now >= _actUntil) StartLeap(mom);
                break;
            case Act.Land:
                if (now >= _actUntil) Settle(now);
                break;
        }

        if (_act != Act.Leap) Sit(mom);
        _cat.Step();
    }

    // --- leaping over the big cat ---------------------------------------------------------

    private void StartCrouch(long now)
    {
        _act = Act.Crouch;
        _cat.Squish(); // gathers itself
        _actUntil = now + CatView.SquishMs;
    }

    /// <summary>Over the big cat's head in an arc, to its other side.</summary>
    private void StartLeap(CatWindow.SeatedPose mom)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _leapFromX = BodyCenter(mom, _onTailSide, scale);
        _leapToX = BodyCenter(mom, !_onTailSide, scale);
        // High enough that its feet clear the big cat's ears with a block to spare.
        _leapHeight = mom.Floor - mom.Top + _cat.BlockSize * 2 * scale;
        _leapT = 0;
        _act = Act.Leap;
        _cat.Unsquish();
        _cat.Flipped = _leapToX > _leapFromX; // leaps the way it faces
        _leapClock.Restart();
        CompositionTarget.Rendering += OnLeapFrame;
    }

    private void OnLeapFrame(object? sender, EventArgs e)
    {
        if (Mom.Seated is not { } mom)
        {
            Vanish();
            return;
        }
        double dt = Math.Min(0.05, _leapClock.Elapsed.TotalSeconds);
        _leapClock.Restart();
        _leapT = Math.Min(1, _leapT + dt / JumpSeconds);

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // Mostly up first and mostly down at the end, so it clears the big cat rather than
        // stretching across it near the ground.
        double across = _leapT * _leapT * (3 - 2 * _leapT);
        double center = _leapFromX + (_leapToX - _leapFromX) * across;
        double lift = _leapHeight * 4 * _leapT * (1 - _leapT);
        // Only stretched out once it's up past the big cat's shoulders: lower down, the wide leap
        // pose would cut across the big cat, so it goes up (and comes down) in its sitting pose.
        _cat.SetJumping(lift >= (mom.Floor - mom.Top) * 0.55);
        double middle = _cat.IsJumping ? _cat.RunSpriteLeft + _cat.RunSpriteWidth / 2 : _cat.BodyLeft + _cat.BodyWidth / 2;
        double x = center - middle * scale;
        double y = mom.Floor - ActualHeight * scale - lift;
        WindowPlacement.MoveTo(_hwnd, (int)Math.Round(x) - RoomPx, (int)Math.Round(y));

        if (_leapT < 1) return;
        StopLeap();
        _onTailSide = !_onTailSide;
        _act = Act.Land;
        _actUntil = Environment.TickCount64 + CatView.SquishMs + 100;
        Sit(mom);
        _cat.Squish(); // lands
    }

    private void StopLeap()
    {
        CompositionTarget.Rendering -= OnLeapFrame;
        _leapClock.Stop();
        _cat.SetJumping(false);
    }

    // --- following the big cat -----------------------------------------------------------
    //
    // While the big cat runs off (or is out of sight), the kitten records what it does every frame
    // and replays it a beat later (a fresh 150-350 ms each time, so it never looks mechanical): it
    // runs after the big cat along the same path, always behind it (on its tail side, whichever side
    // it was sitting on: from the far side it waits for the big cat to run past first), ending up in
    // its spot when the big cat reaches its. Popups are the kitten's own business: it reacts to them like the big cat does.

    private bool _following;
    private readonly List<(long T, CatWindow.Move Move)> _trail = [];
    private readonly Stopwatch _followClock = new();
    private long _delayMs;
    private double _offset;        // its body's center minus the big cat's, while following
    private double _offsetGoal;    // where that settles: on the tail side, behind the big cat
    private bool _waitForMom;      // it sits ahead of the big cat on its way: waits in place until the big cat has gone past
    private string _screen = "";   // the screen it's on (or last left)
    private double _x;             // its body's center, screen pixels
    private bool _onScreen;        // some of it is showing on _screen
    private int _runDir;
    private double _runSpeed;      // pixels per second, for carrying on off-screen by itself
    private double _strideLeft;    // DIPs until the next gallop step

    private void OnMomMoved()
    {
        if (!_following && _hwnd != IntPtr.Zero && Mom.CurrentMove.Kind is CatWindow.MoveKind.Running or CatWindow.MoveKind.Away or CatWindow.MoveKind.Hidden)
            StartFollowing();
    }

    /// <summary>A click on the kitten itself squishes it, like the big cat.</summary>
    private void OnMomClicked(int x, int y)
    {
        if (_hwnd == IntPtr.Zero || _hidden || _following || _act == Act.Leap || IsPeeking || _leaning
            || !Native.GetWindowRect(_hwnd, out var r)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int left = r.Left + RoomPx;
        if (x >= left && x < left + _cat.CatWidth * scale && y >= r.Bottom - _cat.CatHeight * scale && y < r.Bottom)
            _cat.Squish();
    }

    private void StartFollowing()
    {
        EndPeekOrLeanNow();
        if (_act == Act.Leap) StopLeap();
        Settle(Environment.TickCount64);
        _cat.SetAsleep(false);

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var home = Mom.HomeSeat;
        double momCenter = (home.BodyLeft + home.BodyRight) / 2.0;
        _offset = BodyCenter(home, _onTailSide, scale) - momCenter;
        // The big cat runs the way it faces, so its tail side is always the way back: the kitten
        // runs there, even from the other side (where it would run ahead of it).
        _offsetGoal = BodyCenter(home, true, scale) - momCenter;
        // Ahead of the big cat on its way (the other side from where it runs to)? Then it stays put
        // and lets the big cat run past before it starts after it.
        _waitForMom = !_hidden && _offset * Mom.CurrentMove.Dir > 0;
        _onTailSide = true;
        _x = Native.GetWindowRect(_hwnd, out var r) && !_hidden
            ? r.Left + RoomPx + (_cat.BodyLeft + _cat.BodyWidth / 2) * scale
            : momCenter + _offset;
        _screen = Mom.ScreenName;
        _onScreen = !_hidden;
        _runSpeed = 0;
        _delayMs = _rng.Next(150, 351);
        _trail.Clear();
        _following = true;
        _followClock.Restart();
        CompositionTarget.Rendering += OnFollowFrame;
    }

    private void OnFollowFrame(object? sender, EventArgs e)
    {
        double dt = Math.Min(0.05, _followClock.Elapsed.TotalSeconds);
        _followClock.Restart();
        long now = Environment.TickCount64;
        var live = Mom.CurrentMove;
        _trail.Add((now, live));

        // The move from _delayMs ago: drop older ones, keeping the latest at or before that time.
        long then = now - _delayMs;
        while (_trail.Count > 1 && _trail[1].T <= then) _trail.RemoveAt(0);
        if (_trail[0].T > then) return; // not long enough yet: stays as it is

        var move = _trail[0].Move;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        switch (move.Kind)
        {
            case CatWindow.MoveKind.Running:
                FollowRun(move, dt, scale);
                break;
            case CatWindow.MoveKind.Away:
                // Off it goes too, on its own if the big cat is already out of sight.
                if (_onScreen && _runSpeed > 0) RunOn(dt, scale);
                else HideFollowing();
                break;
            case CatWindow.MoveKind.Hidden:
                HideFollowing();
                break;
            case CatWindow.MoveKind.Seated:
            case CatWindow.MoveKind.Peeking:
                // Caught up: back in its spot, and once the big cat has stayed put, done.
                if (_trail.All(t => t.Move.Kind is CatWindow.MoveKind.Seated or CatWindow.MoveKind.Peeking)) EndFollowing();
                break;
        }
    }

    /// <summary>
    /// Runs along the big cat's path, shifted to its own side, so it ends in its spot as the big
    /// cat reaches its own. If the big cat has already hopped to another screen (or out of sight)
    /// while the kitten is still in view, it first runs off this screen by itself.
    /// </summary>
    private void FollowRun(CatWindow.Move move, double dt, double scale)
    {
        _runDir = move.Dir;
        if (move.Screen != _screen)
        {
            if (_onScreen && _runSpeed > 0)
            {
                RunOn(dt, scale);
                return;
            }
            _screen = move.Screen;
        }
        if (_waitForMom)
        {
            // Sitting ahead of the big cat: holds still until the big cat's center is past its own,
            // then runs after it from where it sat.
            if ((move.CenterX - _x) * move.Dir <= _cat.BodyWidth / 2 * scale) return;
            _waitForMom = false;
            _offset = _x - move.CenterX;
        }
        if (_offset != _offsetGoal)
        {
            // Settling onto the trailing side, only ever forward: it never slides back while the
            // big cat gets going.
            double step = OffsetEaseDip * scale * dt;
            _offset += Math.Clamp(_offsetGoal - _offset, -step, step);
            if ((move.CenterX + _offset - _x) * move.Dir < 0) _offset = _x - move.CenterX;
        }
        double x = move.CenterX + _offset;
        double moved = Math.Abs(x - _x);
        if (dt > 0 && moved < 400 * scale) _runSpeed = moved / dt; // ignore hops between screens
        ShowRunning(x, moved < 400 * scale ? moved : 0, scale);
    }

    /// <summary>Keeps running the way it was going, at its last speed, until it's off this screen.</summary>
    private void RunOn(double dt, double scale)
    {
        double step = Math.Max(_runSpeed, 300 * scale) * dt;
        ShowRunning(_x + _runDir * step, step, scale);
    }

    private void ShowRunning(double x, double movedPx, double scale)
    {
        _x = x;
        _cat.SetRunning(true);
        _cat.Flipped = _runDir > 0;
        _strideLeft -= movedPx / scale; // legs keep pace with the distance covered
        if (_strideLeft <= 0)
        {
            // Longer strides than its size would give: it keeps pace with the big cat, and at its
            // own stride length its legs would blur.
            _strideLeft += _cat.RunStrideDip * KittenStrideFactor;
            _cat.StepRun();
        }

        var area = Screens.ByName(_screen).WorkingArea;
        double left = x - (_cat.BodyLeft + _cat.BodyWidth / 2) * scale;
        double y = area.Bottom - ActualHeight * scale;
        WindowPlacement.MoveTo(_hwnd, (int)Math.Round(left) - RoomPx, (int)Math.Round(y));
        _hidden = false;
        // Only the part on this screen shows, like the big cat.
        double x0 = Math.Max(0, (area.Left - left) / scale), x1 = (area.Right - left) / scale;
        _cat.Clip = new RectangleGeometry(new Rect(x0, 0, Math.Max(0, x1 - x0), Math.Max(0, ActualHeight)));
        double spriteLeft = left + _cat.RunSpriteLeft * scale, spriteRight = spriteLeft + _cat.RunSpriteWidth * scale;
        _onScreen = spriteRight > area.Left && spriteLeft < area.Right;
    }

    private void HideFollowing()
    {
        _runSpeed = 0;
        _onScreen = false;
        if (_hidden || _hwnd == IntPtr.Zero) return;
        WindowPlacement.MoveTo(_hwnd, -32000, -32000);
        _hidden = true;
    }

    /// <summary>Back to its own life next to the big cat (on the side it ended up on).</summary>
    private void EndFollowing()
    {
        CompositionTarget.Rendering -= OnFollowFrame;
        _followClock.Stop();
        _following = false;
        _trail.Clear();
        _cat.SetRunning(false);
        _cat.SetPeeking(false);
        _cat.Clip = null;
        Settle(Environment.TickCount64);
        if (Mom.Seated is { } mom) Sit(mom);
    }

    // --- placement ------------------------------------------------------------------------

    /// <summary>Back to sitting, with the next leap a while off.</summary>
    private void Settle(long now)
    {
        _act = Act.Sit;
        _nextActAt = now + NextJumpWaitMs();
    }

    /// <summary>A random wait within <see cref="KittenConfig.JumpEverySeconds"/>, in milliseconds.</summary>
    private long NextJumpWaitMs()
    {
        var range = _cfg.Kitten.JumpEverySeconds;
        double min = range.Count > 0 ? range[0] : 35;
        double max = range.Count > 1 ? range[1] : min;
        if (max < min) (min, max) = (max, min);
        min = Math.Max(1, min);
        max = Math.Max(min, max);
        return (long)((min + _rng.NextDouble() * (max - min)) * 1000);
    }

    /// <summary>
    /// Sits next to the big cat: by the tip of its tail, or at its other flank. Either way it
    /// faces the big cat (unmirrored it faces left, so on the right it's unmirrored).
    /// </summary>
    private void Sit(CatWindow.SeatedPose mom)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        bool onRight = OnRight(mom, _onTailSide);
        _cat.Flipped = !onRight;
        double x = BodyCenter(mom, _onTailSide, scale) - (_cat.BodyLeft + _cat.BodyWidth / 2) * scale;
        double y = mom.Floor - ActualHeight * scale;
        WindowPlacement.MoveTo(_hwnd, (int)Math.Round(x) - RoomPx, (int)Math.Round(y));
        _hidden = false;
    }

    /// <summary>The tail is on the big cat's right unless it's mirrored.</summary>
    private static bool OnRight(CatWindow.SeatedPose mom, bool tailSide) => tailSide == !mom.Flipped;

    /// <summary>Screen x of its body's center when sitting on the given side of the big cat.</summary>
    private double BodyCenter(CatWindow.SeatedPose mom, bool tailSide, double scale)
    {
        double half = _cat.BodyWidth / 2 * scale;
        double gap = (tailSide ? TailGapBlocks : SideGapBlocks) * _cat.BlockSize * scale;
        return OnRight(mom, tailSide)
            ? (tailSide ? mom.TailTip : mom.BodyRight) + gap + half
            : (tailSide ? mom.TailTip : mom.BodyLeft) - gap - half;
    }

    /// <summary>Out of sight while the big cat isn't sitting in its spot; whatever it was doing stops.</summary>
    private void Vanish()
    {
        if (_act == Act.Leap) StopLeap();
        if (_act != Act.Sit) Settle(Environment.TickCount64);
        if (_hidden || _hwnd == IntPtr.Zero) return;
        WindowPlacement.MoveTo(_hwnd, -32000, -32000);
        _hidden = true;
    }

    protected override int HomeX(System.Drawing.Rectangle area, double scale) =>
        (int)Math.Round(BodyCenter(Mom.HomeSeat, _onTailSide, scale) - (_cat.BodyLeft + _cat.BodyWidth / 2) * scale);

    protected override bool PopupsBlocked => _following || _hidden || _act != Act.Sit || _cat.Asleep;

    protected override void Reposition()
    {
        if (_hwnd == IntPtr.Zero || _following || _act == Act.Leap || IsPeeking || _leaning) return;
        Sit(Mom.HomeSeat);
    }

    private void ApplyZOrder()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_cfg.Topmost) WindowPlacement.MakeTopmost(_hwnd);
        else WindowPlacement.PinToDesktop(_hwnd);
    }
}
