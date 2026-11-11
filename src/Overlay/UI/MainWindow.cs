using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Overlay.Config;
using Overlay.Interop;
using Overlay.Modes;

namespace Overlay.UI;

/// <summary>
/// The widget window. Built in code: a 4-column grid (label | value | sparkline | details) for the
/// desktop, a single line for the game overlay. Columns only ever grow so numbers don't jitter.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly Border _root = new();
    private readonly Dictionary<string, RowView> _rows = new();
    private Grid _grid = new();
    private StackPanel _compact = new();
    private WidgetConfig _cfg;
    private Theme _theme;
    private IntPtr _hwnd;

    /// <param name="screenName">Device name of the monitor this widget lives on.</param>
    /// <param name="hostsOverlay">Only one widget turns into the game overlay; the others hide meanwhile.</param>
    public MainWindow(WidgetConfig cfg, string screenName, bool hostsOverlay)
    {
        _cfg = cfg;
        ScreenName = screenName;
        HostsOverlay = hostsOverlay;
        _theme = Theme.From(cfg, cfg.Opacity);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        // Off-screen until the first layout pass positions us.
        Left = -32000;
        Top = -32000;
        Title = "Overlay";

        _root.Padding = new Thickness(6);
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowPlacement.MakeGhost(_hwnd);
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        };
        // Show() raises the window, so pin only once it's on screen.
        ContentRendered += (_, _) => ApplyMode(Mode);
        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => Reposition();

        Rebuild();
    }

    public DisplayMode Mode { get; private set; } = DisplayMode.Desktop;

    public string ScreenName { get; }

    public bool HostsOverlay { get; }

    public void ApplyConfig(WidgetConfig cfg)
    {
        _cfg = cfg;
        Rebuild();
        Reposition();
    }

    public void ApplyMode(DisplayMode mode)
    {
        Mode = mode;
        if (mode == DisplayMode.GameOverlay && !HostsOverlay)
        {
            Hide();
            return;
        }
        if (!IsVisible && _hwnd != IntPtr.Zero) Show();
        if (_hwnd != IntPtr.Zero)
        {
            if (mode == DisplayMode.Desktop) WindowPlacement.PinToDesktop(_hwnd);
            else WindowPlacement.MakeTopmost(_hwnd);
        }
        Rebuild();
        Reposition();
    }

    public void Render(IReadOnlyList<RowData> rows)
    {
        if (!IsVisible) return;
        if (Mode == DisplayMode.Desktop)
        {
            // Explorer restarts (and some shell transitions) replace the desktop window.
            if (_hwnd != IntPtr.Zero && !WindowPlacement.IsPinned(_hwnd)) WindowPlacement.PinToDesktop(_hwnd);

            foreach (var row in rows)
                if (_rows.TryGetValue(row.Id, out var view)) view.Update(row, _theme);
            LockColumnWidths();
        }
        else
        {
            RenderCompact(rows);
        }
    }

    /// <summary>In desktop mode, turn any attempt to raise us into "go to the bottom".</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WindowPlacement.WM_WINDOWPOSCHANGING && Mode == DisplayMode.Desktop)
            WindowPlacement.ForceBottom(lParam);
        return IntPtr.Zero;
    }

    private void Rebuild()
    {
        _theme = Theme.From(_cfg, Mode == DisplayMode.Desktop ? _cfg.Opacity : _cfg.GameMode.Opacity);
        FontFamily = _theme.Font;
        TextOptions.SetTextFormattingMode(this, _theme.FormattingMode);
        _root.Effect = _cfg.ShadowOpacity > 0
            ? new DropShadowEffect { ShadowDepth = 0, BlurRadius = 6, Color = Colors.Black, Opacity = Math.Clamp(_cfg.ShadowOpacity, 0, 1) }
            : null;

        _rows.Clear();
        // Fresh panels also reset the grow-only column widths and the window size.
        SizeToContent = SizeToContent.Manual;
        Width = Height = double.NaN;
        SizeToContent = SizeToContent.WidthAndHeight;

        if (Mode == DisplayMode.Desktop)
        {
            _grid = new Grid();
            for (int c = 0; c < 4; c++) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int r = 0;
            foreach (var id in _cfg.Metrics)
            {
                _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _rows[id] = new RowView(_grid, r++, _theme, _cfg);
            }
            _root.Child = _grid;
        }
        else
        {
            _compact = new StackPanel { Orientation = Orientation.Horizontal };
            _root.Child = _compact;
        }
    }

    private void LockColumnWidths()
    {
        _grid.UpdateLayout();
        foreach (var col in _grid.ColumnDefinitions)
            if (col.ActualWidth > col.MinWidth) col.MinWidth = Math.Ceiling(col.ActualWidth);
    }

    private void RenderCompact(IReadOnlyList<RowData> rows)
    {
        _compact.Children.Clear();
        foreach (var row in rows.Where(r => r.Visible))
        {
            var tb = new TextBlock { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Bottom };
            tb.Inlines.Add(new Run(row.Label + " ") { Foreground = _theme.Label, FontSize = _theme.LabelSize, FontWeight = _theme.SmallWeight });
            if (row.Primary is { } p)
                tb.Inlines.Add(new Run(p.Text) { Foreground = p.Hot ? _theme.Accent : _theme.Primary, FontSize = _theme.SmallSize * 1.15, FontWeight = _theme.Weight });
            if (row.Secondary.Count > 0)
            {
                var s = row.Secondary[0];
                tb.Inlines.Add(new Run((row.Primary is null ? "" : " ") + s.Text) { Foreground = s.Hot ? _theme.Accent : _theme.Secondary, FontSize = _theme.SmallSize, FontWeight = _theme.SmallWeight });
            }
            _compact.Children.Add(tb);
        }
    }

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool desktop = Mode == DisplayMode.Desktop;
        var position = desktop ? _cfg.Position : _cfg.GameMode.Position;
        int offX = desktop ? _cfg.OffsetX : _cfg.GameMode.OffsetX;
        int offY = desktop ? _cfg.OffsetY : _cfg.GameMode.OffsetY;

        var screen = Screens.ByName(ScreenName);
        // Games cover the taskbar, so the overlay anchors to the full monitor.
        var area = desktop ? screen.WorkingArea : screen.Bounds;

        var (w, h) = WindowPlacement.GetPixelSize(_hwnd);
        var dpi = VisualTreeHelper.GetDpi(this);
        int dx = (int)Math.Round(offX * dpi.DpiScaleX), dy = (int)Math.Round(offY * dpi.DpiScaleY);

        var (x, y) = Screens.Place(area, w, h, position, dx, dy);
        WindowPlacement.MoveTo(_hwnd, x, y);
    }

    /// <summary>The four cells of one desktop row.</summary>
    private sealed class RowView
    {
        private readonly TextBlock _label, _primary, _secondary;
        private readonly Sparkline? _spark;

        public RowView(Grid grid, int row, Theme t, WidgetConfig cfg)
        {
            var gap = new Thickness(0, 0, Math.Round(t.PrimarySize * 0.7), Math.Round(t.PrimarySize * 0.18));
            _label = Cell(grid, row, 0, new TextBlock
            {
                FontSize = t.LabelSize, FontWeight = t.SmallWeight, Foreground = t.Label,
                Margin = new Thickness(0, 0, Math.Round(t.PrimarySize * 0.9), gap.Bottom + Math.Round(t.PrimarySize * 0.04)),
            });
            _primary = Cell(grid, row, 1, new TextBlock
            {
                FontSize = t.PrimarySize, FontWeight = t.Weight, Foreground = t.Primary,
                TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = gap with { Bottom = 0 },
            });
            if (cfg.Sparklines)
            {
                _spark = Cell(grid, row, 2, new Sparkline
                {
                    Width = cfg.SparklineWidth, Height = Math.Round(t.PrimarySize * 0.62),
                    Margin = gap with { Bottom = Math.Round(t.PrimarySize * 0.28) },
                });
            }
            _secondary = Cell(grid, row, 3, new TextBlock
            {
                FontSize = t.SmallSize, FontWeight = t.SmallWeight, Foreground = t.Secondary,
                Margin = new Thickness(0, 0, 0, gap.Bottom),
            });
            SetVisible(false);
        }

        public void Update(RowData d, Theme t)
        {
            SetVisible(d.Visible);
            if (!d.Visible) return;

            _label.Text = d.Label;
            _primary.Text = d.Primary?.Text ?? "";
            _primary.Foreground = d.Primary?.Hot == true ? t.Accent : t.Primary;

            _secondary.Inlines.Clear();
            for (int i = 0; i < d.Secondary.Count; i++)
            {
                if (i > 0) _secondary.Inlines.Add(new Run("  ·  ") { Foreground = t.Separator });
                var p = d.Secondary[i];
                _secondary.Inlines.Add(p.Hot ? new Run(p.Text) { Foreground = t.Accent } : new Run(p.Text));
            }

            if (_spark is not null)
            {
                if (d.Spark is { } values)
                {
                    _spark.Visibility = Visibility.Visible;
                    _spark.Update(values, d.SparkCapacity, d.SparkHot ? t.SparkHotPen : t.SparkPen, d.SparkHot ? t.SparkHotFill : t.SparkFill);
                }
                else
                {
                    _spark.Visibility = Visibility.Hidden; // keep the column width
                }
            }
        }

        private void SetVisible(bool visible)
        {
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            _label.Visibility = _primary.Visibility = _secondary.Visibility = v;
            if (_spark is not null) _spark.Visibility = v;
        }

        private static T Cell<T>(Grid grid, int row, int col, T element) where T : FrameworkElement
        {
            element.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(element, row);
            Grid.SetColumn(element, col);
            grid.Children.Add(element);
            return element;
        }
    }
}
