using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Overlay.Config;

namespace Overlay.UI;

/// <summary>
/// A chunky pixel-art cat made of square blocks, near-flat monochrome with a faint shimmer. Eyes
/// are holes cut out of the face and blink now and then. Every so often a terminal-style thought
/// bubble appears and the cat "types" something into it. Each <see cref="Step"/> advances one
/// frame; the mood (from CPU load) sets how lively the tail and shimmer are.
/// The cat sits in the bottom-left of the element; the space above/right is reserved for the bubble.
/// </summary>
public sealed class CatView : FrameworkElement
{
    public enum Mood { Calm, Idle, Alert }

    // '#' body block, 'e' eye hole.
    private static readonly string[] Silhouette =
    [
        ".#.........#......",
        ".##.......##......",
        ".###########......",
        ".###########......",
        ".##e#####e##......",
        ".###########......",
        "..#########.......",
        "..#########.......",
        ".###########......",
        ".###########......",
        ".###########......",
        ".###########......",
        ".##.#####.##......",
    ];

    // Tail poses as (row, col) blocks, base to tip.
    private static readonly (int R, int C)[][] TailPoses =
    [
        [(12, 12), (12, 13), (12, 14), (11, 15), (10, 15), (9, 15)],           // curled
        [(12, 12), (12, 13), (12, 14), (12, 15), (11, 16), (10, 16)],          // out
        [(12, 12), (11, 13), (10, 13), (9, 13), (8, 13)],                      // up
    ];

    // Lying down, head on the left, body stretched out low behind it.
    // Rests on the same bottom row as the sitting pose.
    private static readonly string[] SleepingSilhouette =
    [
        "..................",
        "..................",
        "..................",
        "..................",
        "..................",
        "..................",
        "..#.....#.........",
        "..##...##.........",
        "..#######.........",
        "..#e###e######....",
        "..##############..",
        ".###############..",
        ".###############..",
    ];

    private static readonly (int R, int C)[][] SleepingTailPoses =
    [
        [(12, 16), (12, 17)], // flat
        [(12, 16), (11, 17)], // tip lifted
    ];

    // A gallop, side view facing left (it always runs left), modeled on a real cat's stride: the
    // front rears up, the cat launches and stretches out, the front paws land, the hind legs
    // gather under, and it rears again. The body tilts through the cycle instead of staying flat.
    // The head is turned three-quarters toward where it runs: both eyes sit toward the front.
    // All frames share the bottom row as the ground. 'e' = eye hole.
    private static readonly string[][] RunFrames =
    [
        [ // 1 rear up
            "........................",
            "..#.....#...............",
            "..##...##...............",
            "..#######...............",
            "..#######...............",
            "..#e##e##...............",
            "..#######...............",
            "...#######..............",
            "......######............",
            "......#######.....####..",
            "......#########.###.....",
            "......##########........",
            ".....##..######.........",
            "...........####.........",
            "............###.........",
            "............###.........",
            ".............###........",
        ],
        [ // 2 launch
            "........................",
            "........................",
            "..#.....#...............",
            "..##...##...............",
            "..#######...............",
            "..#######...............",
            "..#e##e##...............",
            "..#######...............",
            "...########.............",
            "......########..........",
            "......#########....####.",
            "......#############.....",
            "....###########.........",
            "..........#####.........",
            ".............###........",
            "..............#####.....",
            ".................###....",
        ],
        [ // 3 stretch
            "........................",
            "........................",
            "........................",
            "........................",
            ".#.....#................",
            ".##...##................",
            ".#######................",
            ".#######................",
            ".#e##e##................",
            ".##########.............",
            "..#############......##.",
            ".....################...",
            ".....##########.........",
            "....###########.........",
            "..####....########......",
            "...##..........######...",
            "..................###...",
        ],
        [ // 4 extended
            "........................",
            "........................",
            "........................",
            "........................",
            "........................",
            ".#.....#................",
            ".##...##................",
            ".#######................",
            ".#######................",
            ".#e##e##................",
            ".##############.........",
            "..#####################.",
            ".....##########.........",
            ".....##########.........",
            ".....############.......",
            "..#####........######...",
            ".####.............###...",
        ],
        [ // 5 front land
            "........................",
            "........................",
            "........................",
            "........................",
            "........................",
            ".#.....#................",
            ".##...##................",
            ".#######................",
            ".#######................",
            ".#e##e##................",
            ".##############....####.",
            "..#################.....",
            ".....##########.........",
            ".....##########.........",
            ".....#############......",
            ".....##.........####....",
            "....###.................",
        ],
        [ // 6 gather
            "........................",
            "........................",
            "........................",
            "........................",
            "........................",
            ".#.....#................",
            ".##...##................",
            ".#######................",
            ".#######................",
            ".#e##e##.............#..",
            ".##############....##...",
            "..#################.....",
            ".....##########.........",
            ".....##########.........",
            ".....##########.........",
            "......#.#.....##........",
            "......###.....##........",
        ],
        [ // 7 compact
            "........................",
            "........................",
            "........................",
            "........................",
            ".#.....#................",
            ".##...##................",
            ".#######................",
            ".#######................",
            ".#e##e##................",
            ".##########.........##..",
            "..#############...###...",
            ".....##############.....",
            ".....##########.........",
            ".....##########.........",
            "......###.#####.........",
            ".......##...###.........",
            "........##..##..........",
        ],
        [ // 8 front lift
            "........................",
            "........................",
            "..#.....#...............",
            "..##...##...............",
            "..#######...............",
            "..#######...............",
            "..#e##e##...............",
            "..#######...............",
            "...########.........##..",
            "......########....###...",
            "......#########.###.....",
            "......###########.......",
            "....###########.........",
            ".....#....#####.........",
            ".............##.........",
            ".............##.........",
            ".............##.........",
        ],

    ];

    // Things the cat "types" in its thought bubble. Keep within MaxBubbleChars.
    private static readonly string[] Phrases =
    [
        "meow", ":3", "purr()", "ls ~/fish", "sudo pet", "rm -rf mice", "nap --long", "cat food.txt",
        "ping human", "git push yarn", "chmod +nap", "0xCAFE", "mew --help", "while(1) purr",
    ];

    private const int MaxBubbleChars = 14;
    private const string Prompt = "> ";
    private const int BubbleLeftCol = 5;   // bubble's left edge, in cat blocks
    private const int FadeFrames = 4;

    // Two body tones plus the scanline's brighter one.
    private const int Shades = 3;
    private const double BasePixelDip = 7;
    // The lying pose has fewer rows, so it's drawn with bigger blocks to stay close to the sitting size.
    private const double SleepScale = 1.3;
    // The lying pose sits this many (sitting-size) blocks further left; the view keeps that much margin.
    private const int SleepShiftBlocks = 4;

    private static readonly int Rows = Silhouette.Length;
    private static readonly int Cols = Silhouette[0].Length;

    private readonly Random _rng = new();
    private readonly int[,] _shade = new int[Rows, Cols];

    private Brush[] _brushes = [];
    private Brush _bubbleText = Brushes.White, _bubbleLine = Brushes.Gray;
    private Typeface _typeface = new("Consolas");
    private double _px = BasePixelDip; // block size in DIPs, snapped to whole device pixels
    private double _sleepPx = BasePixelDip * SleepScale;
    private double _pixelsPerDip = 1;

    // Bubble geometry, derived in Configure.
    private double _fontSize, _charW, _line, _pad, _bubbleH, _top, _width;

    private Mood _mood = Mood.Idle;
    private long _frame;
    private int _blinkFrames, _nextBlink = 30;
    private int _tailPose, _nextTailMove = 16;
    private int _scanRow = -1, _nextScan = 40;

    private bool _asleep;
    private bool _running;
    private int _runFrame;
    private readonly List<(double X, double Y, int Age)> _zs = new();

    // Thought bubble state: hidden (counting down) → typing → holding → fading.
    private IReadOnlyList<string> _livePhrases = [];
    private string? _phrase;
    private int _typed, _holdFrames, _nextBubble = 24;

    public CatView()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased); // crisp block edges
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _shade[r, c] = _rng.Next(0, Shades - 1);
    }

    /// <summary>Width of the cat's own grid (excluding bubble room), for anchoring it on screen.</summary>
    public double CatWidth => LeftMargin + Cols * _px;

    /// <summary>Height of the cat's own grid (excluding bubble room above it).</summary>
    public double CatHeight => Rows * _px;

    /// <summary>Horizontal extent of the running sprite inside the view, in DIPs.</summary>
    public double RunSpriteLeft => LeftMargin;
    public double RunSpriteWidth => RunFrames[0][0].Length * _px;

    /// <summary>
    /// How far (DIPs) the cat travels per gallop frame, so the stride keeps pace with the speed:
    /// at top speed that's ~60 ms a frame, about two body lengths per full eight-frame stride.
    /// </summary>
    public double RunStrideDip => _px * 6;

    /// <summary>Switches to the running sprite (and drops the bubble/z's), or back.</summary>
    public void SetRunning(bool running)
    {
        _running = running;
        _runFrame = 0;
        _phrase = null;
        _zs.Clear();
        InvalidateVisual();
    }

    /// <summary>Advances the gallop by one step.</summary>
    public void StepRun()
    {
        _runFrame++;
        InvalidateVisual();
    }

    private double LeftMargin => SleepShiftBlocks * _px;

    /// <param name="tint">The brightest block color, alpha included; every other tone derives from it.</param>
    public void Configure(CatConfig cfg, double pixelsPerDip, Color tint)
    {
        _pixelsPerDip = pixelsPerDip;
        _px = Snap(BasePixelDip * Math.Clamp(cfg.Scale, 0.4, 6));
        _sleepPx = Snap(_px * SleepScale);

        SetTint(tint);

        _typeface = new Typeface(new FontFamily(cfg.BubbleFont), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _fontSize = Math.Round(_px * 1.6);
        var probe = Text("M");
        _charW = probe.WidthIncludingTrailingWhitespace;
        _line = Snap(Math.Max(1, _px / 3.5));
        _pad = Snap(_px * 0.9);
        _bubbleH = Snap(probe.Height + 2 * _pad);
        // Bubble, then two thought dots stepping down to the head.
        _top = _bubbleH + Snap(_px * 2);
        _width = LeftMargin + Math.Max(Math.Max(Cols * _sleepPx, RunFrames[0][0].Length * _px),
            BubbleLeftCol * _px + BubbleWidth(new string('M', MaxBubbleChars)));
        InvalidateMeasure();
    }

    /// <summary>Recolors the cat (and bubble) from its brightest color; cheap enough to call per frame.</summary>
    public void SetTint(Color tint)
    {
        // A very narrow band of shades: reads as a flat fill with only a hint of shimmer.
        _brushes = Enumerable.Range(0, Shades)
            .Select(i => Freeze(new SolidColorBrush(WithAlpha(tint, (237 + i * (18 / (Shades - 1))) / 255.0))))
            .ToArray();
        _bubbleText = Freeze(new SolidColorBrush(WithAlpha(tint, 235 / 255.0)));
        _bubbleLine = Freeze(new SolidColorBrush(WithAlpha(tint, 170 / 255.0)));
        InvalidateVisual();
    }

    public void SetMood(Mood mood) => _mood = mood;

    /// <summary>Asleep: lies down, eyes nearly shut, z's instead of the thought bubble.</summary>
    public void SetAsleep(bool asleep)
    {
        if (asleep == _asleep) return;
        _asleep = asleep;
        _tailPose = 0;
        _nextTailMove = 20;
        _phrase = null;
        _nextBubble = _rng.Next(40, 120); // a little pause after waking before it "thinks" again
        _zs.Clear();
    }

    /// <summary>Types <paramref name="phrase"/> into the thought bubble right away.</summary>
    public void Say(string phrase)
    {
        _phrase = phrase.Length > MaxBubbleChars ? phrase[..MaxBubbleChars] : phrase;
        _typed = 0;
        _holdFrames = _rng.Next(16, 24);
    }

    /// <summary>Live readouts (e.g. "cpu 45°") the bubble sometimes shows instead of a canned phrase.</summary>
    public void SetLivePhrases(IReadOnlyList<string> phrases) => _livePhrases = phrases;

    public void Step()
    {
        _frame++;

        // Shimmer: nudge a few blocks' shade each frame, more when busy.
        int nudges = _mood switch { Mood.Alert => 6, Mood.Calm => 1, _ => 2 };
        for (int i = 0; i < nudges; i++)
        {
            int r = _rng.Next(Rows), c = _rng.Next(Cols);
            _shade[r, c] = Math.Clamp(_shade[r, c] + (_rng.Next(2) == 0 ? -1 : 1), 0, Shades - 2);
        }

        // Eyes stay open; a quick blink every few seconds, sometimes a double blink.
        if (_blinkFrames > 0) _blinkFrames--;
        else if (--_nextBlink <= 0)
        {
            _blinkFrames = 1;
            _nextBlink = _rng.Next(5) == 0 ? 3 : _rng.Next(24, 56);
        }

        if (--_nextTailMove <= 0)
        {
            var poses = _asleep ? SleepingTailPoses : TailPoses;
            _tailPose = _tailPose == 0 ? _rng.Next(1, poses.Length) : 0;
            _nextTailMove = _asleep ? (_tailPose == 0 ? _rng.Next(80, 200) : 8)
                : _mood switch
                {
                    Mood.Alert => _rng.Next(2, 4),
                    Mood.Calm => _tailPose == 0 ? _rng.Next(50, 100) : 6,
                    _ => _tailPose == 0 ? _rng.Next(12, 40) : _rng.Next(3, 6),
                };
        }

        // Sleep z's rise from just above the head and drift right.
        if (_asleep && _frame % 14 == 0) _zs.Add((8.5, 5.5, 0));
        for (int i = _zs.Count - 1; i >= 0; i--)
        {
            var z = _zs[i];
            z = (z.X + 0.16, z.Y - 0.18, z.Age + 1);
            if (z.Age > 30) _zs.RemoveAt(i);
            else _zs[i] = z;
        }

        // A slightly brighter scanline sweeps down now and then.
        if (_scanRow >= 0 && ++_scanRow >= Rows) _scanRow = -1;
        if (_scanRow < 0 && --_nextScan <= 0)
        {
            _scanRow = 0;
            _nextScan = _rng.Next(60, 140);
        }

        if (!_asleep) StepBubble();
        InvalidateVisual();
    }

    private void StepBubble()
    {
        if (_phrase is null)
        {
            if (--_nextBubble > 0) return;
            _phrase = PickPhrase();
            _typed = 0;
            _holdFrames = _rng.Next(24, 40);
            return;
        }

        if (_typed < _phrase.Length)
        {
            // Uneven typing rhythm: mostly a char per frame, with small hesitations.
            if (_rng.Next(4) != 0) _typed++;
            return;
        }

        if (--_holdFrames <= -FadeFrames)
        {
            _phrase = null;
            _nextBubble = _rng.Next(160, 480); // 20–60 s at 8 fps
        }
    }

    private string PickPhrase()
    {
        var pool = _livePhrases.Count > 0 && _rng.Next(3) == 0 ? _livePhrases : Phrases;
        var p = pool[_rng.Next(pool.Count)];
        return p.Length > MaxBubbleChars ? p[..MaxBubbleChars] : p;
    }

    protected override Size MeasureOverride(Size availableSize) => new(_width, _top + Rows * _px);

    protected override void OnRender(DrawingContext dc)
    {
        if (_brushes.Length == 0) return;
        if (_running)
        {
            DrawRun(dc);
            return;
        }

        if (_phrase is { } phrase) DrawBubble(dc, phrase);

        var silhouette = _asleep ? SleepingSilhouette : Silhouette;
        var tail = (_asleep ? SleepingTailPoses : TailPoses)[_tailPose];
        bool squint = _asleep || _blinkFrames > 0;
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                char cell = silhouette[r][c];
                if (cell == '#') Block(dc, r, c);
                else if (cell == 'e' && squint)
                {
                    // Fill the eye hole except a slit: thin mid-blink, thinner still when asleep.
                    var brush = Shade(r, c);
                    double s = CellSize;
                    double slit = Snap(Math.Max(1, s * (_asleep ? 0.15 : 0.3)));
                    double y = CellY(r);
                    dc.DrawRectangle(brush, null, new Rect(CellX(c), y, s, (s - slit) / 2));
                    dc.DrawRectangle(brush, null, new Rect(CellX(c), y + (s + slit) / 2, s, (s - slit) / 2));
                }
            }
        }

        foreach (var (r, c) in tail)
        {
            if (_asleep) TailBlock(dc, silhouette, r, c);
            else Block(dc, r, c);
        }

        foreach (var (x, y, age) in _zs)
        {
            // Small z's growing as they rise, fading out at the end.
            double size = CellSize * (1.3 + age / 30.0);
            double alpha = age > 22 ? (30 - age) / 8.0 : 1;
            var z = new FormattedText(age < 12 ? "z" : "Z", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _typeface, size, _bubbleText, _pixelsPerDip);
            dc.PushOpacity(alpha);
            dc.DrawText(z, new Point(CellX(x), CellY(y) - z.Height));
            dc.Pop();
        }
    }

    /// <summary>The current gallop frame, bottom-aligned like the other poses, at the sitting block size.</summary>
    private void DrawRun(DrawingContext dc)
    {
        var frame = RunFrames[_runFrame % RunFrames.Length];
        double s = _px, bottom = _top + Rows * _px;
        for (int r = 0; r < frame.Length; r++)
        {
            double y = bottom - (frame.Length - r) * s;
            for (int c = 0; c < frame[r].Length; c++)
                if (frame[r][c] == '#')
                    dc.DrawRectangle(_brushes[_shade[r % Rows, c % Cols]], null, new Rect(RunSpriteLeft + c * s, y, s, s));
        }
    }

    private void DrawBubble(DrawingContext dc, string phrase)
    {
        double fade = _holdFrames < 0 ? 1 + _holdFrames / (double)FadeFrames : 1;
        dc.PushOpacity(Math.Clamp(fade, 0, 1));

        double x = LeftMargin + BubbleLeftCol * _px, w = BubbleWidth(phrase), h = _bubbleH, t = _line;
        // Pixel-style box: four edges with the corners notched out.
        dc.DrawRectangle(_bubbleLine, null, new Rect(x + t, 0, w - 2 * t, t));
        dc.DrawRectangle(_bubbleLine, null, new Rect(x + t, h - t, w - 2 * t, t));
        dc.DrawRectangle(_bubbleLine, null, new Rect(x, t, t, h - 2 * t));
        dc.DrawRectangle(_bubbleLine, null, new Rect(x + w - t, t, t, h - 2 * t));

        // Thought dots stepping down toward the head.
        double big = Snap(_px * 0.6), small = Snap(_px * 0.4);
        dc.DrawRectangle(_bubbleLine, null, new Rect(x + _px, h + Snap(_px * 0.35), big, big));
        dc.DrawRectangle(_bubbleLine, null, new Rect(x + _px * 0.3, h + Snap(_px * 1.3), small, small));

        var text = Text(Prompt + phrase[.._typed]);
        double tx = x + t + _pad, ty = (h - text.Height) / 2;
        dc.DrawText(text, new Point(tx, ty));

        // Block cursor: solid while typing, blinking once done.
        bool typing = _typed < phrase.Length;
        if (typing || _frame / 3 % 2 == 0)
        {
            double cx = tx + text.WidthIncludingTrailingWhitespace;
            dc.DrawRectangle(_bubbleText, null, new Rect(cx, ty + text.Height * 0.15, Snap(_charW * 0.8), Snap(text.Height * 0.7)));
        }

        dc.Pop();
    }

    /// <summary>Box width for the finished phrase: text, room for the cursor, padding and borders.</summary>
    private double BubbleWidth(string phrase) =>
        Snap(Text(Prompt + phrase).WidthIncludingTrailingWhitespace + _charW * 1.6 + 2 * (_pad + _line));

    /// <summary>A tail block; where it lies against the body, a thin seam keeps the two apart.</summary>
    private void TailBlock(DrawingContext dc, string[] silhouette, int r, int c)
    {
        bool bodyAbove = r > 0 && silhouette[r - 1][c] == '#';
        double s = CellSize;
        double seam = bodyAbove ? Snap(Math.Max(1, s * 0.2)) : 0;
        dc.DrawRectangle(Shade(r, c), null, new Rect(CellX(c), CellY(r) + seam, s, s - seam));
    }

    private void Block(DrawingContext dc, int r, int c) =>
        dc.DrawRectangle(Shade(r, c), null, new Rect(CellX(c), CellY(r), CellSize, CellSize));

    // Block geometry for the current pose. Rows are anchored to the bottom so the cat always rests
    // on the taskbar, whatever its block size.
    private double CellSize => _asleep ? _sleepPx : _px;
    private double CellX(double c) => LeftMargin - (_asleep ? LeftMargin : 0) + c * CellSize;
    private double CellY(double r) => _top + Rows * _px - (Rows - r) * CellSize;

    private Brush Shade(int r, int c) => _brushes[r == _scanRow ? Shades - 1 : _shade[r, c]];

    /// <summary>Rounds a DIP length to whole device pixels so blocks stay crisp.</summary>
    private double Snap(double dip) => Math.Max(1, Math.Round(dip * _pixelsPerDip)) / _pixelsPerDip;

    private FormattedText Text(string s) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, _bubbleText, _pixelsPerDip);

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary><paramref name="c"/> with its alpha scaled by <paramref name="factor"/>.</summary>
    private static Color WithAlpha(Color c, double factor) =>
        Color.FromArgb((byte)Math.Round(c.A * Math.Clamp(factor, 0, 1)), c.R, c.G, c.B);
}
