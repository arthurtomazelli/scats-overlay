using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Overlay.UI;

/// <summary>
/// A Photoshop-style color picker: a saturation/brightness square, a hue bar, the new color next to
/// the current one, and HSB / RGB / hex fields that all stay in sync. Every change is reported
/// through the preview callback so the widget updates live; Cancel reports the original color again.
/// </summary>
public sealed class ColorPickerWindow : Window
{
    private const double SquareSize = 256, BarWidth = 22;

    private readonly Color _original;
    private readonly Color _default;
    private readonly Action<Color> _preview;
    private double _h, _s, _v; // hue 0..360, saturation and brightness 0..1
    private bool _syncing;     // set while fields are being filled from the color, so they don't echo back

    private readonly Border _square = new() { Width = SquareSize, Height = SquareSize, ClipToBounds = true };
    private readonly Ellipse _squareMark = new()
    {
        Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 2, ShadowDepth = 0, Opacity = 0.8 },
    };
    private readonly Canvas _squareCanvas = new() { Width = SquareSize, Height = SquareSize };
    private readonly Canvas _barCanvas = new() { Width = BarWidth + 10, Height = SquareSize };
    private readonly Polygon _barMark = new()
    {
        Points = [new Point(0, -5), new Point(5, 0), new Point(0, 5)], Fill = Brushes.Black, IsHitTestVisible = false,
    };
    private readonly Polygon _barMarkRight = new()
    {
        Points = [new Point(0, -5), new Point(-5, 0), new Point(0, 5)], Fill = Brushes.Black, IsHitTestVisible = false,
    };
    private readonly Border _newSwatch = new() { Height = 34 };
    private readonly TextBox _hBox = Field(), _sBox = Field(), _bBox = Field();
    private readonly TextBox _rBox = Field(), _gBox = Field(), _blBox = Field();
    private readonly TextBox _hexBox = new() { Width = 80, VerticalContentAlignment = VerticalAlignment.Center };

    public ColorPickerWindow(Color initial, Color defaultColor, Action<Color> preview)
    {
        _original = initial;
        _default = defaultColor;
        _preview = preview;
        (_h, _s, _v) = ToHsv(initial);

        Title = "Pick a color";
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        Content = Build();
        Sync(fromField: null);

        // Esc cancels, Enter accepts (via the buttons' IsCancel / IsDefault).
        Closed += (_, _) => { if (DialogResult != true) _preview(_original); };
    }

    /// <summary>The color picked (meaningful when ShowDialog returned true).</summary>
    public Color Color => FromHsv(_h, _s, _v, _original.A);

    private UIElement Build()
    {
        // Saturation left to right (white to the hue), brightness top to bottom (to black).
        var whiteToHue = new Border
        {
            Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
        };
        var clearToBlack = new Border
        {
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
        };
        var layers = new Grid();
        layers.Children.Add(whiteToHue);
        layers.Children.Add(clearToBlack);
        _square.Child = layers;
        _squareCanvas.Children.Add(_square);
        _squareCanvas.Children.Add(_squareMark);
        _squareCanvas.Cursor = Cursors.Cross;
        _squareCanvas.Background = Brushes.Transparent;
        Drag(_squareCanvas, p =>
        {
            _s = Math.Clamp(p.X / SquareSize, 0, 1);
            _v = 1 - Math.Clamp(p.Y / SquareSize, 0, 1);
            Sync(fromField: null);
        });

        // Hue bar: red at the top, round the wheel, red again at the bottom.
        var hues = new GradientStopCollection();
        for (int i = 0; i <= 6; i++) hues.Add(new GradientStop(FromHsv(i * 60, 1, 1, 255), i / 6.0));
        var bar = new Border
        {
            Width = BarWidth, Height = SquareSize, Background = new LinearGradientBrush(hues, 90),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
        };
        Canvas.SetLeft(bar, 5);
        _barCanvas.Children.Add(bar);
        _barCanvas.Children.Add(_barMark);
        _barCanvas.Children.Add(_barMarkRight);
        _barCanvas.Background = Brushes.Transparent;
        Drag(_barCanvas, p =>
        {
            _h = Math.Clamp(p.Y / SquareSize, 0, 1) * 360;
            if (_h >= 360) _h = 359.999;
            Sync(fromField: null);
        });

        // New over current, like Photoshop; clicking "current" goes back to it.
        var current = new Border { Height = 34, Background = new SolidColorBrush(_original), Cursor = Cursors.Hand, ToolTip = "Back to the current color" };
        current.MouseLeftButtonDown += (_, _) =>
        {
            (_h, _s, _v) = ToHsv(_original);
            Sync(fromField: null);
        };
        var compare = new StackPanel { Width = 80 };
        compare.Children.Add(Caption("new"));
        compare.Children.Add(new Border
        {
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { _newSwatch, current } },
        });
        compare.Children.Add(Caption("current"));

        var fields = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddField(fields, 0, "H:", _hBox, "°");
        AddField(fields, 1, "S:", _sBox, "%");
        AddField(fields, 2, "B:", _bBox, "%");
        AddField(fields, 3, "R:", _rBox, "", top: 10);
        AddField(fields, 4, "G:", _gBox, "");
        AddField(fields, 5, "B:", _blBox, "");
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        hexRow.Children.Add(new TextBlock { Text = "#", Width = 22, VerticalAlignment = VerticalAlignment.Center });
        _hexBox.Text = "";
        hexRow.Children.Add(_hexBox);

        _hBox.TextChanged += (_, _) => FromHsbFields(_hBox);
        _sBox.TextChanged += (_, _) => FromHsbFields(_sBox);
        _bBox.TextChanged += (_, _) => FromHsbFields(_bBox);
        _rBox.TextChanged += (_, _) => FromRgbFields(_rBox);
        _gBox.TextChanged += (_, _) => FromRgbFields(_gBox);
        _blBox.TextChanged += (_, _) => FromRgbFields(_blBox);
        _hexBox.TextChanged += (_, _) => FromHex();

        var side = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        side.Children.Add(compare);
        side.Children.Add(fields);
        side.Children.Add(hexRow);

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(0, 3, 0, 3) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(0, 3, 0, 3) };
        // Picks this setting's default color; OK still applies it, Cancel still undoes it.
        var reset = new Button { Content = "Default", MinWidth = 80, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 3, 0, 3) };
        reset.Click += (_, _) =>
        {
            (_h, _s, _v) = ToHsv(_default);
            Sync(fromField: null);
        };
        var buttons = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        buttons.Children.Add(reset);

        var squareFrame = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _squareCanvas, VerticalAlignment = VerticalAlignment.Top };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14) };
        row.Children.Add(squareFrame);
        row.Children.Add(new Border { Margin = new Thickness(8, 0, 0, 0), Child = _barCanvas, VerticalAlignment = VerticalAlignment.Top });
        row.Children.Add(side);
        row.Children.Add(buttons);
        return row;
    }

    /// <summary>Puts the color everywhere (except the field being typed in) and previews it.</summary>
    private void Sync(TextBox? fromField)
    {
        var color = Color;
        _syncing = true;
        _square.Background = new SolidColorBrush(FromHsv(_h, 1, 1, 255));
        Canvas.SetLeft(_squareMark, _s * SquareSize - 6);
        Canvas.SetTop(_squareMark, (1 - _v) * SquareSize - 6);
        // Light marker on dark colors, dark on light ones.
        _squareMark.Stroke = _v > 0.6 && _s < 0.4 ? Brushes.Black : Brushes.White;
        double y = _h / 360 * SquareSize;
        Canvas.SetLeft(_barMark, 0);
        Canvas.SetTop(_barMark, y);
        Canvas.SetLeft(_barMarkRight, BarWidth + 10);
        Canvas.SetTop(_barMarkRight, y);
        _newSwatch.Background = new SolidColorBrush(color);

        SetText(_hBox, fromField, Math.Round(_h) % 360);
        SetText(_sBox, fromField, Math.Round(_s * 100));
        SetText(_bBox, fromField, Math.Round(_v * 100));
        SetText(_rBox, fromField, color.R);
        SetText(_gBox, fromField, color.G);
        SetText(_blBox, fromField, color.B);
        if (fromField != _hexBox) _hexBox.Text = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        _syncing = false;

        _preview(color);
    }

    private static void SetText(TextBox box, TextBox? except, double value)
    {
        if (box != except) box.Text = value.ToString(CultureInfo.InvariantCulture);
    }

    private void FromHsbFields(TextBox changed)
    {
        if (_syncing || !Number(_hBox, 0, 360, out var h) || !Number(_sBox, 0, 100, out var s) || !Number(_bBox, 0, 100, out var b)) return;
        (_h, _s, _v) = (h % 360, s / 100, b / 100);
        Sync(changed);
    }

    private void FromRgbFields(TextBox changed)
    {
        if (_syncing || !Number(_rBox, 0, 255, out var r) || !Number(_gBox, 0, 255, out var g) || !Number(_blBox, 0, 255, out var b)) return;
        (_h, _s, _v) = ToHsv(Color.FromRgb((byte)r, (byte)g, (byte)b));
        Sync(changed);
    }

    private void FromHex()
    {
        if (_syncing) return;
        var text = _hexBox.Text.Trim().TrimStart('#');
        if (text.Length == 3) text = string.Concat(text.Select(ch => $"{ch}{ch}")); // #abc shorthand
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return;
        (_h, _s, _v) = ToHsv(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        Sync(_hexBox);
    }

    private static bool Number(TextBox box, double min, double max, out double value) =>
        double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    /// <summary>Click or drag inside <paramref name="target"/>; the mouse is captured while the button is held.</summary>
    private static void Drag(FrameworkElement target, Action<Point> moved)
    {
        target.MouseLeftButtonDown += (_, e) =>
        {
            target.CaptureMouse();
            moved(e.GetPosition(target));
        };
        target.MouseMove += (_, e) => { if (target.IsMouseCaptured) moved(e.GetPosition(target)); };
        target.MouseLeftButtonUp += (_, _) => target.ReleaseMouseCapture();
    }

    private static void AddField(Grid grid, int row, string label, TextBox box, string unit, double top = 0)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, Width = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, top + 2, 0, 2) };
        box.Margin = new Thickness(0, top + 2, 0, 2);
        var u = new TextBlock { Text = unit, Margin = new Thickness(4, top + 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(l, row);
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        Grid.SetRow(u, row);
        Grid.SetColumn(u, 2);
        grid.Children.Add(l);
        grid.Children.Add(box);
        grid.Children.Add(u);
    }

    private static TextBox Field() => new() { Width = 46, VerticalContentAlignment = VerticalAlignment.Center };

    private static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 2),
    };

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v, byte alpha)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb(alpha, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
