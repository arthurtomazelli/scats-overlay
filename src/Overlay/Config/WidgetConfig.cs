namespace Overlay.Config;

/// <summary>Where on its monitor a window anchors. Offsets push inward from the anchored edges (and nudge right/down for centered axes).</summary>
public enum Position { TopLeft, TopCenter, TopRight, CenterLeft, Center, CenterRight, BottomLeft, BottomCenter, BottomRight }

/// <summary>Everything in config.json. Edits are picked up live (see <see cref="ConfigStore"/>).</summary>
public sealed class WidgetConfig
{
    // Placement
    public Position Position { get; set; } = Position.TopRight;
    public int OffsetX { get; set; } = 48;
    public int OffsetY { get; set; } = 48;
    /// <summary>"primary", "all", or 1-based monitor numbers like "1,2". One widget per monitor.</summary>
    public string Monitors { get; set; } = "primary";

    // Look
    public double Opacity { get; set; } = 0.55;
    public double AccentOpacity { get; set; } = 0.95;
    public string TextColor { get; set; } = "#FFFFFF";
    public string AccentColor { get; set; } = "#FF7A59";
    public string FontFamily { get; set; } = "Segoe UI";
    public string FontWeight { get; set; } = "Light";
    public string SmallFontWeight { get; set; } = "Normal";
    public double FontSize { get; set; } = 16;
    public bool Sparklines { get; set; } = true;
    public int SparklineWidth { get; set; } = 44;
    public int HistoryLength { get; set; } = 60;
    /// <summary>0 disables the shadow. A faint one helps on bright wallpapers.</summary>
    public double ShadowOpacity { get; set; } = 0;
    /// <summary>"Ideal" (smoother, better for thin fonts) or "Display" (crisper at small sizes).</summary>
    public string TextRendering { get; set; } = "Ideal";

    // Content
    public List<string> Metrics { get; set; } = ["cpu", "gpu", "ram", "disk", "drives", "net", "fans", "uptime"];
    /// <summary>Drive letters for the "drives" row, e.g. ["C:", "D:"]. Empty = all fixed drives.</summary>
    public List<string> Drives { get; set; } = [];
    /// <summary>"auto" prefers a discrete GPU; otherwise a substring of the GPU name.</summary>
    public string Gpu { get; set; } = "auto";
    public Thresholds Thresholds { get; set; } = new();

    // Behaviour
    public int PollIntervalMs { get; set; } = 1000;
    public bool RequireAdmin { get; set; } = true;
    public bool SoftwareRendering { get; set; } = true;
    public bool TrayIcon { get; set; } = true;

    public GameModeConfig GameMode { get; set; } = new();
    public CatConfig Cat { get; set; } = new();
}

/// <summary>The little code-glyph cat that sits on the taskbar.</summary>
public sealed class CatConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>"primary", "all", or 1-based monitor numbers like "1,2".</summary>
    public string Monitors { get; set; } = "primary";
    /// <summary>Distance from the right edge of the screen, in DIPs.</summary>
    public int OffsetX { get; set; } = 90;
    public double Scale { get; set; } = 1.0;
    /// <summary>
    /// Brightest block color. "match" = the widget's text color at the widget's opacity, so the cat
    /// shares the tone of the stats; otherwise any color like "#E6E6E6".
    /// </summary>
    public string Color { get; set; } = "match";
    /// <summary>
    /// The cat is normally <see cref="LightColor"/>; over the desktop and terminals (cmd,
    /// PowerShell, Windows Terminal) it softens to <see cref="Color"/>. Other apps keep the light
    /// color even in dark mode. Off = always <see cref="LightColor"/>.
    /// </summary>
    public bool AdaptToBackground { get; set; } = true;
    public string LightColor { get; set; } = "#E6E6E6";
    /// <summary>Monospace font for the thought bubble.</summary>
    public string BubbleFont { get; set; } = "Cascadia Mono, Consolas";
    /// <summary>Stay above app windows (it only covers a small strip just above the taskbar).</summary>
    public bool Topmost { get; set; } = true;
    public int Fps { get; set; } = 8;
    /// <summary>"auto" (sits, takes random naps), or pin it to "sit" or "sleep".</summary>
    public string Pose { get; set; } = "auto";
    /// <summary>In "auto" pose, stays awake for a random time in this range [min, max] before napping.</summary>
    public List<double> AwakeMinutes { get; set; } = [3, 12];
    /// <summary>Naps last a random time in this range [min, max]; hovering the mouse over it wakes it early.</summary>
    public List<double> NapMinutes { get; set; } = [2, 8];
    /// <summary>
    /// With a single cat: when a fullscreen app covers its screen, hop to the same spot on another
    /// screen; if every screen has one, stay on the secondary screen. With a single screen, run off
    /// it while the fullscreen app has focus.
    /// </summary>
    public bool AvoidFullscreen { get; set; } = true;
    /// <summary>Switch screens by running: off the left edge of one, in from the right edge of the other.</summary>
    public bool RunBetweenScreens { get; set; } = true;
    /// <summary>React to CPU load: calmer tail when idle, livelier tail and shimmer when busy.</summary>
    public bool Moods { get; set; } = true;
}

/// <summary>Values at or past these get the accent color.</summary>
public sealed class Thresholds
{
    public double Load { get; set; } = 95;
    public double CpuTemp { get; set; } = 85;
    public double GpuTemp { get; set; } = 80;
    public double RamPercent { get; set; } = 90;
    public double VramPercent { get; set; } = 95;
    public double DriveFreePercent { get; set; } = 10;
}

/// <summary>
/// Settings for the compact always-on-top overlay. Detection isn't implemented yet
/// (see <see cref="Modes.IGameDetector"/>); the tray's "Preview game overlay" shows it.
/// </summary>
public sealed class GameModeConfig
{
    public bool Enabled { get; set; } = false;
    public Position Position { get; set; } = Position.TopLeft;
    public int OffsetX { get; set; } = 16;
    public int OffsetY { get; set; } = 12;
    public double Opacity { get; set; } = 0.8;
    public List<string> Metrics { get; set; } = ["cpu", "gpu", "ram"];
}
