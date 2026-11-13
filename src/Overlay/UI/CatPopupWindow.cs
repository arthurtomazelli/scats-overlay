using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Overlay.Config;
using Overlay.Interop;

namespace Overlay.UI;

/// <summary>
/// What the big cat and the kitten share: sitting on the taskbar's top edge in a click-through
/// window, and reacting to notifications and flyouts that cover it (peeking over the top edge,
/// jumping between cards, or leaning away from one that covers only part of it).
/// </summary>
public abstract class CatPopupWindow : Window
{
    protected readonly CatView _cat = new();
    protected CatConfig _cfg;
    protected IntPtr _hwnd;

    protected CatPopupWindow(CatConfig cfg, string screenName)
    {
        _cfg = cfg;
        ScreenName = screenName;
    }

    public string ScreenName { get; protected set; }

    /// <summary>Window x (the view's own left edge) of its usual spot on its screen.</summary>
    protected abstract int HomeX(System.Drawing.Rectangle area, double scale);

    /// <summary>True while it's busy with something that rules out reacting to popups (running, asleep, away...).</summary>
    protected abstract bool PopupsBlocked { get; }

    /// <summary>Puts the window back in its usual spot, unless it is busy elsewhere.</summary>
    protected abstract void Reposition();

    /// <summary>It has just started something the kitten may need to react to.</summary>
    protected virtual void OnMoved() { }

    /// <summary>
    /// The view sits in a window with this much transparent room on each side (physical pixels),
    /// so it can tilt without being cut off and the window never has to change size when it
    /// does. Every x in here (<see cref="HomeX"/>, the run, the peek) is the view's own left
    /// edge; only the placement of the window itself subtracts this.
    /// </summary>
    protected int RoomPx => (int)Math.Round(LeanRoomBlocks * _cat.BlockSize * VisualTreeHelper.GetDpi(this).DpiScaleX);

    /// <summary>Window y that sits the cat on top of a bottom taskbar. Uses the WPF layout size,
    /// since on SizeChanged the HWND may not have been resized yet.</summary>
    protected int BottomY(System.Drawing.Rectangle area, double scale) =>
        area.Bottom - (int)Math.Round(ActualHeight * scale);

    // --- peeking over notifications -------------------------------------------------------
    //
    // Covered by a popup for a moment, the cat rises behind it until its head shows over the top
    // edge, then rests its paws there. It rides the edge if the popup slides down, and keeps going
    // down on its own if the popup vanishes on the way (or leaves any other way). Down on the
    // taskbar it stays in the pose a moment after the popup is gone, then gets up and sits.

    protected enum PeekPhase { None, Rising, Up, Landed, Falling, GettingUp, Jumping }

    protected const long PeekDelayMs = 1_000;      // covered this long before it peeks: a beat to notice
    protected const double PeekRiseSeconds = 0.18; // pops up from behind the edge
    protected const double FallGravityDip = 2600;  // DIPs/s² while dropping to the taskbar
    protected const long LandedPauseMs = CatView.SquishMs; // down on the taskbar: gets up as soon as its touchdown squish ends
    protected const long PeekHoldMs = 450;        // card gone: it holds on this long, in case another follows straight away
    protected const double GetUpSeconds = 0.2;     // from peeking over the taskbar up into its seat

    protected PeekPhase _peek;
    protected System.Drawing.Rectangle _card; // the card it peeks over, as last seen
    protected long _landedAt;       // landed: TickCount64 when the card last moved (or left)
    protected double _landedEdge;   // landed: the card's top when last seen; MaxValue once it has left
    protected IntPtr _ignoredPopup; // got up from peeking over this one; leave it be while it stays
    protected bool IsPeeking => _peek != PeekPhase.None;
    protected IntPtr _popupHwnd;    // what covers it (or what it peeks over)
    protected (int L, int T, int R, int B)? _cardInsets; // popup window rect -> its visible card; null = still measuring
    protected bool _cardTracked;    // its window has a card element we can watch (Windows toasts)
    protected bool _cardPresent;    // tracked: that card is on screen and at least half its width
    protected int _cardMinWidth;    // physical pixels
    protected bool _watching;       // a check of the card is under way
    protected long _goneSince;      // peeking: TickCount64 when its card went, or 0
    protected string _watchedName = ""; // what the card there now is called (a toast's text)
    protected string _peekCardName = ""; // the card it's peeking over, by that name
    protected readonly HashSet<IntPtr> _toastWindows = []; // windows seen with a toast card in them
    protected int _measureRetries;  // times the card was looked at and wasn't there (or was still sliding)
    protected long _coveredSince;   // TickCount64 when the current popup first covered it
    protected double _peekLift;     // rising: 0 = hidden behind the edge, 1 = all the way up
    protected int _peekX;           // window x while peeking, physical pixels
    protected double _peekY;        // window y while peeking or falling, physical pixels
    protected double _edge;         // screen y below which the body is hidden (the card's top edge)
    protected double _fallSpeed;    // DIPs per second
    protected double _getUpT;       // 0..1 through getting up
    protected double _cardSpeed;    // how fast the card's edge was moving down, DIPs per second
    protected readonly Stopwatch _peekClock = new();

    /// <summary>
    /// Watches for a notification or flyout covering the cat's spot, and starts peeking once that
    /// has lasted <see cref="PeekDelayMs"/>. Once peeking, <see cref="OnPeekFrame"/> takes over.
    /// </summary>
    protected void UpdatePeek()
    {
        // A sleeping cat ignores popups (no peeking, no leaning): only a hover wakes it.
        bool allowed = _cfg.PeekOverPopups && _cfg.Topmost && IsVisible && !PopupsBlocked
            && !_cfg.Pose.Equals("sleep", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
        {
            EndPeekOrLeanNow();
            _popupHwnd = IntPtr.Zero;
            return;
        }
        WatchCard();
        if (IsPeeking) return;
        if (_leaning)
        {
            UpdateLean();
            return;
        }

        if (!FindPopup(out var hwnd, out var bounds, out var body) || hwnd != _ignoredPopup) _ignoredPopup = IntPtr.Zero;
        if (hwnd == IntPtr.Zero || hwnd == _ignoredPopup)
        {
            _popupHwnd = IntPtr.Zero;
            _measureRetries = 0;
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
        {
            // Only part of it under the card: it stays on the floor and leans away from the card,
            // rather than sliding over to peek over the top.
            if (LeanSide(card) is int side and not 0) StartLean(side, card);
            else StartPeek();
        }
    }

    /// <summary>A popup over the cat's usual spot (<paramref name="body"/>).</summary>
    protected bool FindPopup(out IntPtr hwnd, out System.Drawing.Rectangle bounds, out System.Drawing.Rectangle body)
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

    /// <summary>
    /// Keeps <see cref="_cardPresent"/> up to date while it peeks or leans over a toast. Checking
    /// takes a moment (another process), so it's asked in the background, one check at a time:
    /// called every frame, it re-checks as soon as the last answer is in.
    /// </summary>
    protected void WatchCard()
    {
        if (!(IsPeeking || _leaning) || !_cardTracked || _watching || _popupHwnd == IntPtr.Zero) return;
        _watching = true;
        var target = _popupHwnd;
        Task.Run(() => PopupWindows.CardWidth(target)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _watching = false;
            if (target != _popupHwnd || t.IsFaulted) return;
            _cardPresent = t.Result.Width >= _cardMinWidth;
            // Only a card that's really there counts, not one on its way out.
            if (_cardPresent && t.Result.Name.Length > 0) _watchedName = t.Result.Name;
        }), TaskScheduler.Default);
    }

    /// <summary>Finds the popup's visible card in the background (it takes a moment), as insets from its window.</summary>
    protected void MeasureCard(IntPtr hwnd, System.Drawing.Rectangle bounds)
    {
        Task.Run(() =>
        {
            var v = PopupWindows.VisibleBounds(hwnd, bounds);
            return (Bounds: v, Card: PopupWindows.CardWidth(hwnd));
        }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (hwnd != _popupHwnd) return;
            var v = t.Result.Bounds;
            // A toast's window stays open between one toast and the next, and the card slides in
            // and out of it. Measured then, there'd be no card or a half-slid one: look again in a
            // moment (the delay before it peeks starts over), for a couple of seconds at most.
            var (hasCard, cardWidth, cardName) = t.Result.Card;
            // Only windows that have shown a toast card are treated this way: a tray flyout has
            // panes too, but waiting for a card there would just keep the cat from reacting.
            if (hasCard) _toastWindows.Add(hwnd);
            if (_toastWindows.Contains(hwnd) && cardWidth < v.Width * 0.9 && _measureRetries++ < 12)
            {
                _popupHwnd = IntPtr.Zero;
                return;
            }
            _measureRetries = 0;
            // If its card can be watched, it is (see CurrentCard).
            _cardTracked = _toastWindows.Contains(hwnd) && cardWidth > 0;
            _cardPresent = true;
            _watchedName = cardName;
            _cardMinWidth = (int)(v.Width * 0.85); // gone once it has slid a little way out
            _cardInsets = (v.Left - bounds.Left, v.Top - bounds.Top, bounds.Right - v.Right, bounds.Bottom - v.Bottom);
            Log.Info($"Popup over the cat: {Native.ClassNameOf(hwnd)} window {bounds}, card {v}, card watched: {_cardTracked}");
        }), TaskScheduler.Default);
    }

    protected static System.Drawing.Rectangle Card(System.Drawing.Rectangle window, (int L, int T, int R, int B) inset) =>
        System.Drawing.Rectangle.FromLTRB(window.Left + inset.L, window.Top + inset.T, window.Right - inset.R, window.Bottom - inset.B);

    /// <summary>Where the popup's card is now, or null once it's gone (hidden, closed, cloaked, faded out).</summary>
    protected System.Drawing.Rectangle? CurrentCard()
    {
        if (_cardInsets is not { } inset || !PopupWindows.IsShowing(_popupHwnd, out var bounds)) return null;
        // Its window can stay open, empty, between one toast and the next (or while the last one
        // slides out): no card then, even though the window is still there.
        if (_cardTracked && !_cardPresent) return null;
        return Card(bounds, inset);
    }

    /// <summary>Window x (the view's left edge) that has the cat over the card rather than past its side.</summary>
    protected int PeekXFor(System.Drawing.Rectangle card, System.Drawing.Rectangle area, double scale)
    {
        double px = _cat.BlockSize * scale;
        double bodyOff = _cat.BodyLeft * scale, bodyW = _cat.BodyWidth * scale;
        double minX = card.Left + px - bodyOff, maxX = card.Right - px - bodyW - bodyOff;
        return (int)Math.Round(maxX < minX ? (card.Left + card.Right - bodyW) / 2.0 - bodyOff : Math.Clamp(HomeX(area, scale), minX, maxX));
    }

    protected void StartPeek()
    {
        if (CurrentCard() is not { } card) return;
        _card = card;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = Screens.ByName(ScreenName).WorkingArea;
        _peekX = PeekXFor(card, area, scale);
        _peekCardName = _watchedName;
        _goneSince = 0;

        _peek = PeekPhase.Rising;
        _peekLift = 0;
        _cardSpeed = 0;
        _cat.SetPeeking(true);
        _peekClock.Restart();
        CompositionTarget.Rendering += OnPeekFrame;
        OnMoved();
    }

    /// <summary>Every displayed frame while peeking: follows the card, or drops when it's gone.</summary>
    protected void OnPeekFrame(object? sender, EventArgs e)
    {
        WatchCard();
        double dt = Math.Min(0.05, _peekClock.Elapsed.TotalSeconds);
        _peekClock.Restart();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = Screens.ByName(ScreenName).WorkingArea;
        double px = _cat.BlockSize * scale;
        double homeY = BottomY(area, scale);
        // The floor: still peeking, paws resting on the taskbar, the rest of it behind it.
        double floorY = area.Bottom - (_cat.GridTop + (CatView.PeekEdgeRow + 1) * _cat.BlockSize) * scale;

        if (_peek == PeekPhase.Jumping)
        {
            Jump(dt, scale, area, floorY);
            return;
        }
        // Another card turns up while it's still in the peek, on its way down or getting up: it
        // jumps to the new one instead of carrying on down.
        if ((_peek == PeekPhase.GettingUp || _peek == PeekPhase.Falling) && CurrentCard() is { } fresh
            && fresh.Top < area.Bottom && PeekHeight(fresh.Top) > 0)
        {
            StartJump(fresh, scale, area);
            Jump(dt, scale, area, floorY);
            return;
        }
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
            // The card is gone, but toasts often come in a quick run: it holds on for a moment, and
            // if another card turns up it jumps straight to it instead of dropping to the floor and
            // climbing back. (Not when it's just that the card moved off to the side.)
            if (current is null)
            {
                if (_goneSince == 0) _goneSince = now;
                if (now - _goneSince < PeekHoldMs)
                {
                    Place(scale);
                    return;
                }
            }
            _goneSince = 0;
            StartFall(); // let go and drop
            return;
        }
        bool returned = _goneSince != 0; // a card is back after it had gone: that's the next one
        _goneSince = 0;
        var card = current!.Value;
        _card = card;
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
            if (upY < floorY)
            {
                if (_peek == PeekPhase.Landed)
                {
                    // Down on the taskbar and a card is there again: jump up to it.
                    StartJump(card, scale, area);
                    Jump(dt, scale, area, floorY);
                    return;
                }
                _peek = PeekPhase.Up;
                // A new card in the same place, replacing the one it's peeking over (or turning up
                // right after it went): hop onto it.
                if (returned || (_cardTracked && _cardPresent && _peekCardName.Length > 0 && _watchedName != _peekCardName))
                {
                    StartJump(card, scale, area);
                    Jump(dt, scale, area, floorY);
                    return;
                }
            }
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

    // --- jumping to a new card --------------------------------------------------------------

    protected const double JumpRiseSeconds = 0.30; // from below, up to the new edge
    protected const double JumpHopSeconds = 0.34;  // a hop in place, onto a card that took the old one's place
    protected const double JumpOvershootPx = 2;    // goes this far past the edge, then settles onto it

    protected double _jumpT, _jumpDur, _jumpFromY;
    protected int _jumpFromX, _jumpToX;
    protected bool _jumpRising;

    protected static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
    protected static double EaseIn(double t) => Math.Clamp(t, 0, 1) * Math.Clamp(t, 0, 1);
    protected static double EaseInOut(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>
    /// Jumps from wherever it is up to a card that has just turned up: the peek pose with its paws
    /// out, sliding across to the new card as it goes.
    /// </summary>
    protected void StartJump(System.Drawing.Rectangle card, double scale, System.Drawing.Rectangle area)
    {
        _card = card;
        double paws = (_cat.GridTop + (CatView.PeekEdgeRow + 1) * _cat.BlockSize) * scale;
        _jumpFromX = _peekX;
        _jumpFromY = _peekY;
        _jumpToX = PeekXFor(card, area, scale);
        _jumpRising = card.Top - paws - _peekY < -6 * scale;
        _jumpDur = _jumpRising ? JumpRiseSeconds : JumpHopSeconds;
        _jumpT = 0;
        _peekCardName = _watchedName;
        _peekLift = 1;
        _cardSpeed = 0;
        _peek = PeekPhase.Jumping;
        _cat.Unsquish();
        _cat.SetPeeking(true);
        _cat.SetPeekPaws(true);
    }

    /// <summary>
    /// Window y at <paramref name="s"/> (0..1) through a jump from <paramref name="y0"/> to the
    /// resting spot <paramref name="y1"/>. Rising, it goes a little past the edge and settles back
    /// onto it. Level, it hops up and lands a little past the edge before settling.
    /// </summary>
    protected double JumpY(double s, double y0, double y1, double scale)
    {
        double ov = JumpOvershootPx * scale;
        if (_jumpRising)
        {
            if (s < 0.75) return y0 + (y1 - ov - y0) * EaseOut(s / 0.75);
            return y1 - ov + ov * EaseInOut((s - 0.75) / 0.25);
        }
        double peak = Math.Min(y0, y1) - 9 * scale;
        if (s < 0.4) return y0 + (peak - y0) * EaseOut(s / 0.4);
        if (s < 0.8) return peak + (y1 + ov - peak) * EaseIn((s - 0.4) / 0.4);
        return y1 + ov - ov * EaseInOut((s - 0.8) / 0.2);
    }

    protected void Jump(double dt, double scale, System.Drawing.Rectangle area, double floorY)
    {
        // The card went away again before it got there: back to dropping.
        if (CurrentCard() is not { } card)
        {
            StartFall();
            return;
        }
        _card = card;
        double paws = (_cat.GridTop + (CatView.PeekEdgeRow + 1) * _cat.BlockSize) * scale;
        double y1 = Math.Min(card.Top - paws, floorY);
        _jumpT = Math.Min(1, _jumpT + dt / _jumpDur);
        _peekX = (int)Math.Round(_jumpFromX + (_jumpToX - _jumpFromX) * EaseOut(_jumpT / 0.75));
        _peekY = Math.Min(JumpY(_jumpT, _jumpFromY, y1, scale), floorY);
        // In the air there's nothing to hide behind: the edge follows its paws.
        _edge = _peekY + paws;
        if (_jumpT >= 1)
        {
            _peek = PeekPhase.Up;
            _peekY = y1;
            _edge = card.Top;
        }
        Place(scale);
    }

    /// <summary>Down on the floor: paws on the taskbar, everything below them hidden behind it.</summary>
    protected void Land(long now, double floorY, System.Drawing.Rectangle area, double scale)
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
    protected void StartGetUp()
    {
        _peek = PeekPhase.GettingUp;
        _getUpT = 0;
        _cat.Unsquish(); // the touchdown squish is over: up it gets
        _cat.SetPeeking(false);
    }

    protected void GetUp(double dt, double scale, System.Drawing.Rectangle area, double homeY, double floorY)
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
    protected double PeekHeight(double edge)
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
    protected void StartFall()
    {
        _peek = PeekPhase.Falling;
        _fallSpeed = Math.Max(0, _cardSpeed);
    }

    /// <summary>Down to the floor, speeding up as it goes; then a moment there and it gets up.</summary>
    protected void Fall(double dt, double scale, System.Drawing.Rectangle area, double floorY)
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
    protected void Place(double scale)
    {
        int y = (int)Math.Round(_peekY);
        WindowPlacement.MoveTo(_hwnd, _peekX - RoomPx, y);
        _cat.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, (_edge - y) / scale)));
    }

    /// <summary>Back to sitting in its usual spot, right away.</summary>
    protected void EndPeekNow()
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

    // --- leaning away from a notification that covers part of it -----------------------------------
    //
    // Covered on one side only, it stays on the floor and tilts as one piece (pivoting on its
    // base) away from the card, shifting that way at the same time, so the half that isn't covered
    // peeks out diagonally. Both ease out: fast at the start, settling slowly. When the card goes
    // (or no longer covers it) it eases back upright the same way.

    protected const double LeanDegrees = 35;
    protected const double LeanShiftBlocks = 2;   // slides this far (in blocks) away from the card while it tilts
    protected const double LeanSeconds = 0.25;
    protected const double LeanRoomBlocks = 6;    // transparent room on both sides of the view, so the tilted ears aren't cut off

    protected bool _leaning;
    protected int _leanSide;          // -1: leans left (card covers its right side), +1: leans right
    protected double _leanFrom, _leanTo, _leanT, _leanS; // amount of lean: 0 upright .. 1 fully leaned
    protected int _leanHomeX, _leanWinY; // where the view's own origin is on screen, physical pixels
    protected RotateTransform _leanRotate = new();
    protected TranslateTransform _leanMove = new();
    protected readonly Stopwatch _leanClock = new();

    /// <summary>The cat's body where it usually sits, in physical pixels.</summary>
    protected System.Drawing.Rectangle HomeBodyRect()
    {
        var area = Screens.ByName(ScreenName).WorkingArea;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int height = (int)Math.Round(_cat.CatHeight * scale);
        return new System.Drawing.Rectangle(
            HomeX(area, scale) + (int)Math.Round(_cat.BodyLeft * scale), area.Bottom - height,
            (int)Math.Round(_cat.BodyWidth * scale), height);
    }

    /// <summary>
    /// -1 / +1: the card covers only its right / left part, so it leans left / right. 0 when the
    /// card covers all of it (tail included), sits inside its width, or leaves less than two blocks uncovered:
    /// those peek over the top as before.
    /// </summary>
    protected int LeanSide(System.Drawing.Rectangle card)
    {
        // The whole cat counts, tail included: a card that ends just past its body but short of
        // its tail still leaves a side to peek out of.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int home = HomeX(Screens.ByName(ScreenName).WorkingArea, scale);
        double left = home + Math.Min(_cat.BodyLeft, _cat.TailTipX) * scale;
        double right = home + Math.Max(_cat.BodyLeft + _cat.BodyWidth, _cat.TailTipX) * scale;
        bool coversLeft = card.Left <= left, coversRight = card.Right >= right;
        if (coversLeft == coversRight) return 0;
        double uncovered = coversRight ? card.Left - left : right - card.Right;
        if (uncovered < 2 * _cat.BlockSize * scale) return 0;
        return coversRight ? -1 : 1;
    }

    protected void StartLean(int side, System.Drawing.Rectangle card)
    {
        if (_hwnd == IntPtr.Zero) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = Screens.ByName(ScreenName).WorkingArea;
        _leanSide = side;
        _leaning = true;
        _leanFrom = _leanS = 0;
        _leanTo = 1;
        _leanT = 0;
        _cat.SetLeaning(true);
        _cat.SetSmoothTilt(true);

        // The window already has room for the tilt and stays exactly where it is: nothing about it
        // changes, so there's nothing to flicker.
        _leanHomeX = HomeX(area, scale);
        _leanWinY = BottomY(area, scale);

        _leanRotate = new RotateTransform(0, _cat.BodyLeft + _cat.BodyWidth / 2, _cat.ActualHeight);
        _leanMove = new TranslateTransform();
        _cat.RenderTransform = new TransformGroup { Children = { _leanRotate, _leanMove } };
        _leanClock.Restart();
        CompositionTarget.Rendering += OnLeanFrame;
        Log.Info($"Leaning {(side < 0 ? "left" : "right")} away from the popup over the cat: card {card}");
    }

    /// <summary>While leaning: stays leaned while the card still covers it, eases back once it doesn't.</summary>
    protected void UpdateLean()
    {
        bool covered = CurrentCard() is { } card && card.IntersectsWith(HomeBodyRect());
        double target = covered ? 1 : 0;
        if (target != _leanTo)
        {
            _leanFrom = _leanS;
            _leanTo = target;
            _leanT = 0;
        }
    }

    protected void OnLeanFrame(object? sender, EventArgs e)
    {
        WatchCard();
        double dt = Math.Min(0.05, _leanClock.Elapsed.TotalSeconds);
        _leanClock.Restart();
        _leanT = Math.Min(1, _leanT + dt / LeanSeconds);
        double eased = 1 - Math.Pow(1 - _leanT, 3); // fast start, slow end
        _leanS = _leanFrom + (_leanTo - _leanFrom) * eased;
        ApplyLean();
        if (_leanT >= 1 && _leanTo == 0) EndLeanNow();
    }

    protected void ApplyLean()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _leanRotate.Angle = _leanSide * LeanDegrees * _leanS;
        _leanMove.X = _leanSide * LeanShiftBlocks * _cat.BlockSize * _leanS;

        // Only what's on this screen shows. What's behind the card isn't cut off: the card is above
        // the cat, so it hides it (or shows it through, when the card is see-through). The clip
        // lives in the view's own (untilted) space, so it's the wanted region carried back
        // through the tilt.
        var s = Screens.ByName(ScreenName).Bounds;
        Geometry visible = new RectangleGeometry(
            new Rect((s.Left - _leanHomeX) / scale, (s.Top - _leanWinY) / scale, s.Width / scale, s.Height / scale));
        var m = _cat.RenderTransform.Value;
        if (m.HasInverse)
        {
            m.Invert();
            visible.Transform = new MatrixTransform(m);
        }
        _cat.Clip = visible;
    }

    /// <summary>Upright again right away, window back to its normal size.</summary>
    protected void EndLeanNow()
    {
        if (!_leaning) return;
        CompositionTarget.Rendering -= OnLeanFrame;
        _leanClock.Stop();
        _leaning = false;
        _popupHwnd = IntPtr.Zero;
        _cardInsets = null;
        _cat.RenderTransform = Transform.Identity;
        _cat.Clip = null;
        _cat.SetSmoothTilt(false);
        _cat.SetLeaning(false);
        Reposition();
    }

    protected void EndPeekOrLeanNow()
    {
        if (IsPeeking) EndPeekNow();
        EndLeanNow();
    }

}
