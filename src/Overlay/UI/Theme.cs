using System.Windows;
using System.Windows.Media;
using Overlay.Config;

namespace Overlay.UI;

/// <summary>Frozen brushes/pens/fonts derived from config. Rebuilt on config reload or mode change.</summary>
public sealed class Theme
{
    // Relative emphasis of each text role, multiplied by the configured opacity.
    private const double LabelAlpha = 0.6;
    private const double SecondaryAlpha = 0.8;
    private const double SeparatorAlpha = 0.45;
    private const double SparkAlpha = 0.55;
    private const double SparkFillAlpha = 0.08;

    public required FontFamily Font { get; init; }
    public required FontWeight Weight { get; init; }
    public required FontWeight SmallWeight { get; init; }
    public required double PrimarySize { get; init; }
    public required double SmallSize { get; init; }
    public required double LabelSize { get; init; }
    public required Brush Primary { get; init; }
    public required Brush Secondary { get; init; }
    public required Brush Label { get; init; }
    public required Brush Separator { get; init; }
    public required Brush Accent { get; init; }
    public required Pen SparkPen { get; init; }
    public required Pen SparkHotPen { get; init; }
    public required Brush SparkFill { get; init; }
    public required Brush SparkHotFill { get; init; }
    public required TextFormattingMode FormattingMode { get; init; }

    public static Theme From(WidgetConfig cfg, double opacity)
    {
        var text = ParseColor(cfg.TextColor, Colors.White);
        var accent = ParseColor(cfg.AccentColor, Color.FromRgb(0xFF, 0x7A, 0x59));
        opacity = Math.Clamp(opacity, 0.05, 1);
        double accentOpacity = Math.Clamp(Math.Max(opacity, cfg.AccentOpacity), 0, 1);

        return new Theme
        {
            Font = new FontFamily(cfg.FontFamily),
            Weight = ParseWeight(cfg.FontWeight, FontWeights.Light),
            SmallWeight = ParseWeight(cfg.SmallFontWeight, FontWeights.Normal),
            PrimarySize = cfg.FontSize,
            SmallSize = Math.Round(cfg.FontSize * 0.72, 1),
            LabelSize = Math.Round(cfg.FontSize * 0.66, 1),
            Primary = Solid(text, opacity),
            Secondary = Solid(text, opacity * SecondaryAlpha),
            Label = Solid(text, opacity * LabelAlpha),
            Separator = Solid(text, opacity * SeparatorAlpha),
            Accent = Solid(accent, accentOpacity),
            SparkPen = Pen(Solid(text, opacity * SparkAlpha)),
            SparkHotPen = Pen(Solid(accent, accentOpacity)),
            SparkFill = Solid(text, opacity * SparkFillAlpha),
            SparkHotFill = Solid(accent, accentOpacity * SparkFillAlpha * 1.5),
            FormattingMode = string.Equals(cfg.TextRendering, "Display", StringComparison.OrdinalIgnoreCase)
                ? TextFormattingMode.Display : TextFormattingMode.Ideal,
        };
    }

    private static Brush Solid(Color c, double alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static Pen Pen(Brush b)
    {
        var p = new Pen(b, 1) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    private static Color ParseColor(string s, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch { return fallback; }
    }

    private static FontWeight ParseWeight(string s, FontWeight fallback)
    {
        try { return (FontWeight)new FontWeightConverter().ConvertFromInvariantString(s)!; }
        catch { return fallback; }
    }
}
