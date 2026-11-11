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
public sealed class KittenWindow : Window
{
    private const int ReassertTopmostEveryNFrames = 40;
    private const double JumpSeconds = 0.55;
    private const int TailGapBlocks = 2;  // by the tip of the big cat's tail
    private const int SideGapBlocks = 3;  // landing on the other side, it keeps some room from the big cat
    private const double KittenStrideFactor = 1.5;

    private enum Act { Sit, Crouch, Jump, Land }

    private readonly CatView _cat = new();
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private readonly Stopwatch _jumpClock = new();
    private CatConfig _cfg;
    private IntPtr _hwnd;
    private long _frames;
    private Color _tint;
    private bool _hidden = true;

    private bool _onTailSide = true;
    private Act _act;
    private long _nextActAt, _actUntil;
    private double _jumpT;
    private double _jumpFromX, _jumpToX, _jumpHeight; // body centers and arc height, physical pixels

    public KittenWindow(CatWindow mom, CatConfig cfg)
    {
        Mom = mom;
        _cfg = cfg;
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
        Closed += (_, _) =>
        {
            Mom.Moved -= OnMomMoved;
            _timer.Stop();
            CompositionTarget.Rendering -= OnJumpFrame;
            CompositionTarget.Rendering -= OnFollowFrame;
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

        // Whatever the big cat does (runs, peeks), it does too, a beat later.
        if (!_following && Mom.CurrentMove.Kind != CatWindow.MoveKind.Seated) StartFollowing();
        if (_following || Mom.Seated is not { } mom)
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
                if (!mom.Asleep && now >= _nextActAt) StartCrouch(now);
                break;
            case Act.Crouch:
                if (now >= _actUntil) StartJump(mom);
                break;
            case Act.Land:
                if (now >= _actUntil) Settle(now);
                break;
        }

        if (_act != Act.Jump) Place(mom);
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
    private void StartJump(CatWindow.SeatedPose mom)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _jumpFromX = BodyCenter(mom, _onTailSide, scale);
        _jumpToX = BodyCenter(mom, !_onTailSide, scale);
        // High enough that its feet clear the big cat's ears with a block to spare.
        _jumpHeight = mom.Floor - mom.Top + _cat.BlockSize * 2 * scale;
        _jumpT = 0;
        _act = Act.Jump;
        _cat.Unsquish();
        _cat.Flipped = _jumpToX > _jumpFromX; // leaps the way it faces
        _jumpClock.Restart();
        CompositionTarget.Rendering += OnJumpFrame;
    }

    private void OnJumpFrame(object? sender, EventArgs e)
    {
        if (Mom.Seated is not { } mom)
        {
            Vanish();
            return;
        }
        double dt = Math.Min(0.05, _jumpClock.Elapsed.TotalSeconds);
        _jumpClock.Restart();
        _jumpT = Math.Min(1, _jumpT + dt / JumpSeconds);

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // Mostly up first and mostly down at the end, so it clears the big cat rather than
        // stretching across it near the ground.
        double across = _jumpT * _jumpT * (3 - 2 * _jumpT);
        double center = _jumpFromX + (_jumpToX - _jumpFromX) * across;
        double lift = _jumpHeight * 4 * _jumpT * (1 - _jumpT);
        // Only stretched out once it's up past the big cat's shoulders: lower down, the wide leap
        // pose would cut across the big cat, so it goes up (and comes down) in its sitting pose.
        _cat.SetJumping(lift >= (mom.Floor - mom.Top) * 0.55);
        double middle = _cat.IsJumping ? _cat.RunSpriteLeft + _cat.RunSpriteWidth / 2 : _cat.BodyLeft + _cat.BodyWidth / 2;
        double x = center - middle * scale;
        double y = mom.Floor - ActualHeight * scale - lift;
        WindowPlacement.MoveTo(_hwnd, (int)Math.Round(x), (int)Math.Round(y));

        if (_jumpT < 1) return;
        StopJump();
        _onTailSide = !_onTailSide;
        _act = Act.Land;
        _actUntil = Environment.TickCount64 + CatView.SquishMs + 100;
        Place(mom);
        _cat.Squish(); // lands
    }

    private void StopJump()
    {
        CompositionTarget.Rendering -= OnJumpFrame;
        _jumpClock.Stop();
        _cat.SetJumping(false);
    }

    // --- following the big cat -----------------------------------------------------------
    //
    // While the big cat isn't sitting in its spot, the kitten records what it does every frame and
    // replays it a beat later (a fresh 150-350 ms each time, so it never looks mechanical),
    // shifted to its own side: it runs after the big cat along the same path, ending up in its own
    // spot when the big cat reaches its; and it peeks over the same edge next to it, riding it
    // down, landing and getting up just after it does.

    private bool _following;
    private readonly List<(long T, CatWindow.Move Move)> _trail = [];
    private readonly Stopwatch _followClock = new();
    private long _delayMs;
    private double _offset;        // its body's center minus the big cat's, when both sit
    private double _peekOffset;    // same while peeking, chosen so it's over the card; NaN = not peeking
    private string _screen = "";   // the screen it's on (or last left)
    private double _x;             // its body's center, screen pixels
    private bool _onScreen;        // some of it is showing on _screen
    private int _runDir;
    private double _runSpeed;      // pixels per second, for carrying on off-screen by itself
    private double _strideLeft;    // DIPs until the next gallop step
    private bool _wasLanded;

    private void OnMomMoved()
    {
        if (!_following && _hwnd != IntPtr.Zero) StartFollowing();
    }

    private void StartFollowing()
    {
        if (_act == Act.Jump) StopJump();
        Settle(Environment.TickCount64);
        _cat.SetAsleep(false);

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var home = Mom.HomeSeat;
        double momCenter = (home.BodyLeft + home.BodyRight) / 2.0;
        _offset = BodyCenter(home, _onTailSide, scale) - momCenter;
        _x = Native.GetWindowRect(_hwnd, out var r) && !_hidden
            ? r.Left + (_cat.BodyLeft + _cat.BodyWidth / 2) * scale
            : momCenter + _offset;
        _screen = Mom.ScreenName;
        _onScreen = !_hidden;
        _runSpeed = 0;
        _peekOffset = double.NaN;
        _wasLanded = false;
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
            case CatWindow.MoveKind.Peeking:
                FollowPeek(move, live, scale);
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
                // Caught up: back in its spot, and once the big cat has stayed put, done.
                if (_trail.All(t => t.Move.Kind == CatWindow.MoveKind.Seated)) EndFollowing();
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
        _peekOffset = double.NaN;
        _cat.SetPeeking(false);
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
        WindowPlacement.MoveTo(_hwnd, (int)Math.Round(left), (int)Math.Round(y));
        _hidden = false;
        // Only the part on this screen shows, like the big cat.
        double x0 = Math.Max(0, (area.Left - left) / scale), x1 = (area.Right - left) / scale;
        _cat.Clip = new RectangleGeometry(new Rect(x0, 0, Math.Max(0, x1 - x0), Math.Max(0, ActualHeight)));
        double spriteLeft = left + _cat.RunSpriteLeft * scale, spriteRight = spriteLeft + _cat.RunSpriteWidth * scale;
        _onScreen = spriteRight > area.Left && spriteLeft < area.Right;
    }

    /// <summary>
    /// Peeks over the same edge next to the big cat. Deciding to come up and to get back up
    /// happen a beat after the big cat (<paramref name="move"/>); but once its paws are on the
    /// edge it goes with the edge as it is now (<paramref name="live"/>): riding it down, dropping
    /// when it vanishes and landing at the same moment as the big cat, since they're on the same card.
    /// </summary>
    private void FollowPeek(CatWindow.Move move, CatWindow.Move live, double scale)
    {
        _runSpeed = 0;
        _screen = move.Screen;
        _cat.SetRunning(false);

        // Its own side if that's over the card too, else the big cat's other side, else it sits it out.
        if (double.IsNaN(_peekOffset))
        {
            var home = Mom.HomeSeat;
            double momCenter = (home.BodyLeft + home.BodyRight) / 2.0;
            double half = _cat.BodyWidth / 2 * scale;
            bool Fits(double offset) => move.CenterX + offset - half >= move.CardLeft && move.CenterX + offset + half <= move.CardRight;
            double other = BodyCenter(home, !_onTailSide, scale) - momCenter;
            if (Fits(_offset)) _peekOffset = _offset;
            else if (Fits(other)) _peekOffset = other;
            else _peekOffset = double.PositiveInfinity;
        }
        if (double.IsInfinity(_peekOffset))
        {
            HideFollowing();
            return;
        }

        _x = move.CenterX + _peekOffset;
        _cat.Flipped = _peekOffset < 0; // on the big cat's left it faces right, toward it
        var area = Screens.ByName(_screen).WorkingArea;
        double paws = (_cat.GridTop + (CatView.PeekEdgeRow + 1) * _cat.BlockSize) * scale;
        double floorY = area.Bottom - paws, homeY = area.Bottom - ActualHeight * scale;
        double y, edge;
        if (move.GetUp >= 0)
        {
            _cat.SetPeeking(false); // whole again, rising from behind the taskbar
            y = floorY - move.GetUp * (floorY - homeY);
            edge = move.Edge;
        }
        else
        {
            _cat.SetPeeking(true);
            _cat.SetPeekPaws(move.Paws);
            bool up = move.Paws || _wasLanded;
            if (!up)
            {
                // Still coming up, a beat behind.
                y = Math.Min(move.PawsLine - paws, floorY);
                edge = move.Edge;
            }
            else if (live.Kind == CatWindow.MoveKind.Peeking && live.GetUp < 0)
            {
                // Hanging on the same edge as the big cat, right now.
                y = Math.Min(live.PawsLine - paws, floorY);
                edge = live.Edge;
                if (live.Landed && !_wasLanded) _cat.Squish(); // touchdown, together
                _wasLanded = live.Landed;
            }
            else
            {
                // The big cat is already getting up; this one stays down a beat longer.
                y = floorY;
                edge = area.Bottom;
                if (!_wasLanded) _cat.Squish();
                _wasLanded = true;
            }
        }

        double left = _x - (_cat.BodyLeft + _cat.BodyWidth / 2) * scale;
        WindowPlacement.MoveTo(_hwnd, (int)Math.Round(left), (int)Math.Round(y));
        _hidden = false;
        _onScreen = true;
        _cat.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, (edge - y) / scale)));
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
        if (!double.IsNaN(_peekOffset) && !double.IsInfinity(_peekOffset)) _onTailSide = Math.Abs(_peekOffset - _offset) < 1;
        Settle(Environment.TickCount64);
        if (Mom.Seated is { } mom) Place(mom);
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
    private void Place(CatWindow.SeatedPose mom)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        bool onRight = OnRight(mom, _onTailSide);
        _cat.Flipped = !onRight;
        double x = BodyCenter(mom, _onTailSide, scale) - (_cat.BodyLeft + _cat.BodyWidth / 2) * scale;
        double y = mom.Floor - ActualHeight * scale;
        WindowPlacement.MoveTo(_hwnd, (int)Math.Round(x), (int)Math.Round(y));
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
        if (_act == Act.Jump) StopJump();
        if (_act != Act.Sit) Settle(Environment.TickCount64);
        if (_hidden || _hwnd == IntPtr.Zero) return;
        WindowPlacement.MoveTo(_hwnd, -32000, -32000);
        _hidden = true;
    }

    private void ApplyZOrder()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_cfg.Topmost) WindowPlacement.MakeTopmost(_hwnd);
        else WindowPlacement.PinToDesktop(_hwnd);
    }
}
