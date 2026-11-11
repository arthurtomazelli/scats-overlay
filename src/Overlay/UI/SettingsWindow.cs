using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Overlay.Config;

namespace Overlay.UI;

/// <summary>
/// Settings editor opened from the tray. Edits a copy of the config and saves it shortly after
/// every change; the store's file watcher then applies it live, just like a hand edit of the JSON.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly (string Id, string Name)[] AllMetrics =
    [
        ("cpu", "CPU"), ("gpu", "GPU"), ("ram", "Memory"), ("disk", "Disk activity"),
        ("drives", "Free space"), ("net", "Network"), ("fans", "Fans"), ("uptime", "Uptime"),
    ];

    private static readonly string[] MonitorChoices = ["primary", "all"];

    /// <summary>A fresh config, for each setting's default value.</summary>
    private static readonly WidgetConfig Defaults = new();

    private readonly ConfigStore _store;
    private readonly Action _openJson;
    private readonly DispatcherTimer _save;
    private WidgetConfig _cfg;
    private int _tab;

    public SettingsWindow(ConfigStore store, Action openJson)
    {
        _store = store;
        _openJson = openJson;
        _cfg = store.CloneCurrent();

        Title = "Overlay settings";
        Width = 480;
        Height = 640;
        MinWidth = 400;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // Saving on every keystroke or slider tick would rebuild the widget constantly.
        _save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            _store.Save(_cfg);
        };
        Closing += (_, _) =>
        {
            if (!_save.IsEnabled) return;
            _save.Stop();
            _store.Save(_cfg);
        };

        Build();
    }

    private void Changed()
    {
        _save.Stop();
        _save.Start();
    }

    private void Build()
    {
        var tabs = new TabControl { Margin = new Thickness(10, 10, 10, 0) };
        tabs.Items.Add(Tab("Widget", WidgetTab()));
        tabs.Items.Add(Tab("Stats", StatsTab()));
        tabs.Items.Add(Tab("Cat", CatTab()));
        tabs.Items.Add(Tab("Advanced", AdvancedTab()));
        tabs.SelectedIndex = _tab;
        tabs.SelectionChanged += (_, e) => { if (e.Source == tabs) _tab = tabs.SelectedIndex; };

        var footer = new DockPanel { Margin = new Thickness(10), LastChildFill = false };
        var note = new TextBlock
        {
            Text = "Changes apply right away.", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center,
        };
        footer.Children.Add(note);
        var close = FooterButton("Close", (_, _) => Close());
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        var defaults = FooterButton("Restore defaults", (_, _) => RestoreDefaults());
        DockPanel.SetDock(defaults, Dock.Right);
        footer.Children.Add(defaults);
        var json = FooterButton("Open config.json", (_, _) => _openJson());
        DockPanel.SetDock(json, Dock.Right);
        footer.Children.Add(json);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;
    }

    private void RestoreDefaults()
    {
        var answer = MessageBox.Show(this, "Reset every setting to its default?", "Overlay settings",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        _cfg = new WidgetConfig();
        _store.Save(_cfg);
        Build();
    }

    // --- tabs ------------------------------------------------------------------------------

    private UIElement WidgetTab()
    {
        var p = Page();
        p.Children.Add(Check("Cat only", _cfg.CatOnly, v => _cfg.CatOnly = v));
        p.Children.Add(Hint("Just the cat: no stats, no admin prompt (from the next start), and only CPU load is read. Doesn't need PawnIO."));
        p.Children.Add(Check("Show the widget", _cfg.ShowWidget, v => _cfg.ShowWidget = v));

        p.Children.Add(Header("Placement"));
        p.Children.Add(Row("Position", Combo(Enum.GetValues<Position>().Select(v => v.ToString()), _cfg.Position.ToString(),
            v => _cfg.Position = Enum.Parse<Position>(v))));
        p.Children.Add(Row("Offset X", NumberBox(_cfg.OffsetX, v => _cfg.OffsetX = v)));
        p.Children.Add(Row("Offset Y", NumberBox(_cfg.OffsetY, v => _cfg.OffsetY = v)));
        p.Children.Add(Row("Monitors", EditableCombo(MonitorChoices, _cfg.Monitors, v => _cfg.Monitors = v)));
        p.Children.Add(Hint("\"primary\", \"all\", or monitor numbers like \"1,2\"."));

        p.Children.Add(Header("Look"));
        p.Children.Add(Row("Opacity", SliderBox(0.1, 1, _cfg.Opacity, 0.05, Percent, v => _cfg.Opacity = v)));
        p.Children.Add(Row("Text color", ColorBox(_cfg.TextColor, Defaults.TextColor, v => _cfg.TextColor = v)));
        p.Children.Add(Row("Accent color", ColorBox(_cfg.AccentColor, Defaults.AccentColor, v => _cfg.AccentColor = v)));
        p.Children.Add(Row("Font", EditableCombo(
            Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n), _cfg.FontFamily, v => _cfg.FontFamily = v)));
        p.Children.Add(Row("Font weight", Combo(["Thin", "ExtraLight", "Light", "Normal", "Medium", "SemiBold", "Bold"],
            _cfg.FontWeight, v => _cfg.FontWeight = v)));
        p.Children.Add(Row("Font size", SliderBox(10, 32, _cfg.FontSize, 1, v => v.ToString("0"), v => _cfg.FontSize = v)));
        p.Children.Add(Row("Shadow", SliderBox(0, 1, _cfg.ShadowOpacity, 0.05, Percent, v => _cfg.ShadowOpacity = v)));
        p.Children.Add(Check("Sparklines (tiny history graphs)", _cfg.Sparklines, v => _cfg.Sparklines = v));
        return Scroll(p);
    }

    private UIElement StatsTab()
    {
        var p = Page();
        p.Children.Add(Header("Rows"));
        p.Children.Add(Hint("Tick what to show; the arrows set the order. Rows your hardware doesn't report stay hidden."));
        var list = new StackPanel();
        p.Children.Add(list);

        // Shown rows in their current order, then the hidden ones.
        var order = _cfg.Metrics.Where(id => AllMetrics.Any(m => m.Id == id)).ToList();
        order.AddRange(AllMetrics.Select(m => m.Id).Where(id => !order.Contains(id)));
        var shown = _cfg.Metrics.ToHashSet();

        void Save() => _cfg.Metrics = order.Where(shown.Contains).ToList();
        void Fill()
        {
            list.Children.Clear();
            for (int i = 0; i < order.Count; i++)
            {
                int index = i;
                var id = order[i];
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var down = ArrowButton("▼", index < order.Count - 1, () => Move(index, index + 1));
                var up = ArrowButton("▲", index > 0, () => Move(index, index - 1));
                DockPanel.SetDock(down, Dock.Right);
                DockPanel.SetDock(up, Dock.Right);
                row.Children.Add(down);
                row.Children.Add(up);
                row.Children.Add(Check(AllMetrics.First(m => m.Id == id).Name, shown.Contains(id), v =>
                {
                    if (v) shown.Add(id); else shown.Remove(id);
                    Save();
                }, margin: 0));
                list.Children.Add(row);
            }
        }
        void Move(int from, int to)
        {
            (order[from], order[to]) = (order[to], order[from]);
            Save();
            Fill();
            Changed();
        }
        Fill();

        var t = _cfg.Thresholds;
        p.Children.Add(Header("Highlight in the accent color at"));
        p.Children.Add(Row("Load (%)", DecimalBox(t.Load, v => t.Load = v)));
        p.Children.Add(Row("CPU temp (°C)", DecimalBox(t.CpuTemp, v => t.CpuTemp = v)));
        p.Children.Add(Row("GPU temp (°C)", DecimalBox(t.GpuTemp, v => t.GpuTemp = v)));
        p.Children.Add(Row("RAM used (%)", DecimalBox(t.RamPercent, v => t.RamPercent = v)));
        p.Children.Add(Row("VRAM used (%)", DecimalBox(t.VramPercent, v => t.VramPercent = v)));
        p.Children.Add(Row("Drive free below (%)", DecimalBox(t.DriveFreePercent, v => t.DriveFreePercent = v)));
        return Scroll(p);
    }

    private UIElement CatTab()
    {
        var c = _cfg.Cat;
        var p = Page();
        p.Children.Add(Check("Show the cat", c.Enabled, v => c.Enabled = v));

        p.Children.Add(Header("Behavior"));
        p.Children.Add(Row("Pose", Combo(["auto", "sit", "sleep"], c.Pose, v => c.Pose = v)));
        p.Children.Add(Hint("\"auto\" sits and takes random naps."));
        p.Children.Add(Check("Thought bubble", c.Thoughts, v => c.Thoughts = v));
        p.Children.Add(Check("Squish when clicked", c.ClickReactions, v => c.ClickReactions = v));
        p.Children.Add(Check("Peek over notifications that cover it", c.PeekOverPopups, v => c.PeekOverPopups = v));
        p.Children.Add(Check("Mood follows CPU load", c.Moods, v => c.Moods = v));
        p.Children.Add(Check("Get out of the way of fullscreen apps", c.AvoidFullscreen, v => c.AvoidFullscreen = v));
        p.Children.Add(Check("Run when changing screens", c.RunBetweenScreens, v => c.RunBetweenScreens = v));
        p.Children.Add(Check("Stay above windows", c.Topmost, v => c.Topmost = v));

        p.Children.Add(Header("Look"));
        p.Children.Add(Row("Size", SliderBox(0.5, 3, c.Scale, 0.1, v => $"{v:0.0}×", v => c.Scale = v)));
        p.Children.Add(Check("Mirror (sit at the left end of the taskbar)", c.Flip, v => c.Flip = v));
        p.Children.Add(Row("From the edge (px)", NumberBox(c.OffsetX, v => c.OffsetX = v)));
        p.Children.Add(Row("Monitors", EditableCombo(MonitorChoices, c.Monitors, v => c.Monitors = v)));
        p.Children.Add(Row("Light color", ColorBox(c.LightColor, Defaults.Cat.LightColor, v => c.LightColor = v)));
        p.Children.Add(Check("Softer color over the desktop and terminals", c.AdaptToBackground, v => c.AdaptToBackground = v));

        p.Children.Add(Header("Kitten"));
        p.Children.Add(Check("A tiny second cat that keeps this one company", c.Kitten.Enabled, v => c.Kitten.Enabled = v));
        p.Children.Add(Row("Kitten size", SliderBox(0.4, 1, c.Kitten.Scale, 0.05, v => $"{v:0.00}×", v => c.Kitten.Scale = v)));
        p.Children.Add(Row("Leaps every (s)", RangeBox(c.Kitten.JumpEverySeconds, v => c.Kitten.JumpEverySeconds = v)));
        p.Children.Add(Hint("Now and then it leaps over the big cat to its other side."));
        return Scroll(p);
    }

    private UIElement AdvancedTab()
    {
        var p = Page();
        p.Children.Add(Header("Sensors"));
        p.Children.Add(Row("Update every (ms)", NumberBox(_cfg.PollIntervalMs, v => _cfg.PollIntervalMs = Math.Max(200, v))));
        p.Children.Add(Row("GPU", EditableCombo(["auto"], _cfg.Gpu, v => _cfg.Gpu = v)));
        p.Children.Add(Hint("\"auto\" prefers a discrete GPU; otherwise part of the GPU's name."));

        p.Children.Add(Header("Takes effect after a restart"));
        p.Children.Add(Check("Run as admin (CPU temps, clocks, power, fans)", _cfg.RequireAdmin, v => _cfg.RequireAdmin = v));
        p.Children.Add(Check("Software rendering (lowest GPU use)", _cfg.SoftwareRendering, v => _cfg.SoftwareRendering = v));
        p.Children.Add(Check("Tray icon", _cfg.TrayIcon, v => _cfg.TrayIcon = v));
        p.Children.Add(Hint("Without the tray icon this window can only be reached by editing config.json."));
        return Scroll(p);
    }

    // --- building blocks -------------------------------------------------------------------

    private static TabItem Tab(string header, UIElement content) => new() { Header = header, Content = content };

    private static StackPanel Page() => new() { Margin = new Thickness(14, 10, 14, 14) };

    private static ScrollViewer Scroll(UIElement content) =>
        new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 16, 0, 6),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
    };

    private static Grid Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static Button FooterButton(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        b.Click += click;
        return b;
    }

    private static Button ArrowButton(string glyph, bool enabled, Action click)
    {
        var b = new Button
        {
            Content = glyph, Width = 26, Margin = new Thickness(4, 0, 0, 0), IsEnabled = enabled,
            FontSize = 9, Padding = new Thickness(0, 2, 0, 2),
        };
        b.Click += (_, _) => click();
        return b;
    }

    private CheckBox Check(string text, bool value, Action<bool> set, double margin = 4)
    {
        var cb = new CheckBox
        {
            Content = text, IsChecked = value, Margin = new Thickness(0, margin, 0, margin),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cb.Checked += (_, _) => { set(true); Changed(); };
        cb.Unchecked += (_, _) => { set(false); Changed(); };
        return cb;
    }

    private ComboBox Combo(IEnumerable<string> items, string value, Action<string> set)
    {
        var list = items.ToList();
        var match = list.FirstOrDefault(i => string.Equals(i, value, StringComparison.OrdinalIgnoreCase));
        if (match is null) list.Add(match = value); // keep a hand-written value selectable
        var cb = new ComboBox { ItemsSource = list, SelectedItem = match };
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is string s) { set(s); Changed(); }
        };
        return cb;
    }

    private ComboBox EditableCombo(IEnumerable<string> items, string value, Action<string> set)
    {
        var cb = new ComboBox { ItemsSource = items.ToList(), IsEditable = true, Text = value };
        var last = value;
        // Also fires when the template first fills its text box; only real edits count.
        cb.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            var text = cb.Text.Trim();
            if (text.Length == 0 || text == last) return;
            last = text;
            set(text);
            Changed();
        }));
        return cb;
    }

    private UIElement SliderBox(double min, double max, double value, double step, Func<double, string> format, Action<double> set)
    {
        var label = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), TickFrequency = step,
            IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
        };
        label.Text = format(slider.Value);
        slider.ValueChanged += (_, e) =>
        {
            double v = Math.Round(e.NewValue, 2);
            label.Text = format(v);
            set(v);
            Changed();
        };
        var panel = new DockPanel();
        DockPanel.SetDock(label, Dock.Right);
        panel.Children.Add(label);
        panel.Children.Add(slider);
        return panel;
    }

    private TextBox NumberBox(int value, Action<int> set)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) { set(v); Changed(); }
        };
        return box;
    }

    private TextBox DecimalBox(double value, Action<double> set)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        box.TextChanged += (_, _) =>
        {
            var text = box.Text.Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                set(v);
                Changed();
            }
        };
        return box;
    }

    /// <summary>A "min to max" pair of whole numbers, saved as [min, max].</summary>
    private UIElement RangeBox(List<double> value, Action<List<double>> set)
    {
        int min = (int)(value.Count > 0 ? value[0] : 0), max = (int)(value.Count > 1 ? value[1] : min);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var from = NumberBox(min, v => { min = v; set([min, max]); });
        var to = NumberBox(max, v => { max = v; set([min, max]); });
        from.Width = to.Width = 60;
        panel.Children.Add(from);
        panel.Children.Add(new TextBlock { Text = "to", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(to);
        return panel;
    }

    /// <summary>
    /// A hex color field with a swatch; only valid colors are saved. Clicking the swatch opens a
    /// color picker that previews live and puts the old color back on Cancel.
    /// </summary>
    private UIElement ColorBox(string value, string defaultValue, Action<string> set)
    {
        var swatch = new Border
        {
            Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Pick a color",
        };
        var box = new TextBox { Text = value, Width = 110, VerticalContentAlignment = VerticalAlignment.Center };
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            var before = box.Text;
            var picker = new ColorPickerWindow(TryColor(before.Trim()) ?? Colors.White, TryColor(defaultValue) ?? Colors.White,
                c => box.Text = Hex(c)) { Owner = this };
            if (picker.ShowDialog() != true) box.Text = before;
        };
        void Paint()
        {
            var color = TryColor(box.Text.Trim());
            swatch.Background = color is { } c ? new SolidColorBrush(c) : Brushes.Transparent;
            box.Foreground = color is null ? Brushes.IndianRed : SystemColors.ControlTextBrush;
        }
        Paint();
        box.TextChanged += (_, _) =>
        {
            Paint();
            var text = box.Text.Trim();
            if (TryColor(text) is null) return;
            set(text);
            Changed();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(swatch);
        panel.Children.Add(box);
        return panel;
    }

    private static string Hex(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color? TryColor(string s)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch { return null; }
    }

    private static string Percent(double v) => $"{v * 100:0}%";
}
