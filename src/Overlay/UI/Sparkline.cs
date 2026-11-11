using System.Windows;
using System.Windows.Media;

namespace Overlay.UI;

/// <summary>A hairline sparkline with a faint fill. Fills from the right as history accumulates.</summary>
public sealed class Sparkline : FrameworkElement
{
    private double[] _values = [];
    private int _capacity = 2;
    private Pen? _pen;
    private Brush? _fill;

    public void Update(double[] values, int capacity, Pen pen, Brush fill)
    {
        _values = values;
        _capacity = Math.Max(2, capacity);
        _pen = pen;
        _fill = fill;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_values.Length < 2 || _pen is null) return;

        double w = ActualWidth, h = ActualHeight;
        double step = w / (_capacity - 1);
        double x0 = w - step * (_values.Length - 1);
        // Keep the 1px line inside the box at 0% and 100%.
        double Y(double v) => 0.5 + (1 - Math.Clamp(v, 0, 1)) * (h - 1);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(x0, Y(_values[0])), false, false);
            for (int i = 1; i < _values.Length; i++) ctx.LineTo(new Point(x0 + i * step, Y(_values[i])), true, true);
        }
        line.Freeze();

        if (_fill is not null)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(x0, h), true, true);
                for (int i = 0; i < _values.Length; i++) ctx.LineTo(new Point(x0 + i * step, Y(_values[i])), false, false);
                ctx.LineTo(new Point(w, h), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(_fill, null, area);
        }

        dc.DrawGeometry(null, _pen, line);
    }
}
