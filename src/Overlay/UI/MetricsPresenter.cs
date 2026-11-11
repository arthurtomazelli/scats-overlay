using System.Globalization;
using Overlay.Config;
using Overlay.Sensors;

namespace Overlay.UI;

/// <summary>A piece of text; <see cref="Hot"/> parts get the accent color.</summary>
public sealed record Part(string Text, bool Hot = false);

/// <summary>One metric, ready to draw. Layout-agnostic so the desktop and compact views share it.</summary>
public sealed class RowData
{
    public required string Id { get; init; }
    public string Label { get; init; } = "";
    public bool Visible { get; init; } = true;
    public Part? Primary { get; init; }
    public IReadOnlyList<Part> Secondary { get; init; } = [];
    /// <summary>Normalized 0..1, oldest first. Null = no sparkline for this row.</summary>
    public double[]? Spark { get; init; }
    public int SparkCapacity { get; init; }
    public bool SparkHot { get; init; }

    public static RowData Hidden(string id) => new() { Id = id, Visible = false };
}

/// <summary>Turns snapshots into rows: formatting, thresholds, and sparkline history.</summary>
public sealed class MetricsPresenter
{
    private const double NetSparkFloorBps = 256 * 1024;

    private readonly Dictionary<string, History> _history = new();

    public IReadOnlyList<RowData> Build(Snapshot s, IEnumerable<string> metrics, WidgetConfig cfg)
    {
        var rows = new List<RowData>();
        foreach (var id in metrics)
        {
            rows.Add(id switch
            {
                "cpu" => Cpu(s.Cpu, cfg),
                "gpu" => Gpu(s.Gpu, cfg),
                "ram" => Ram(s.Mem, cfg),
                "disk" => Disk(s.Disk, cfg),
                "drives" => Drives(s.Drives, cfg),
                "net" => Net(s.Net, cfg),
                "fans" => Fans(s.Fans),
                "uptime" => new RowData { Id = id, Label = "up", Secondary = [new(Fmt.Uptime(s.Uptime))] },
                _ => RowData.Hidden(id),
            });
        }
        return rows;
    }

    private RowData Cpu(CpuData? c, WidgetConfig cfg)
    {
        if (c is null || (c.Load is null && c.Temp is null)) return RowData.Hidden("cpu");
        var t = cfg.Thresholds;
        var secondary = new List<Part>();
        if (c.Temp is float temp) secondary.Add(new(Fmt.Temp(temp), temp >= t.CpuTemp));
        if (c.ClockMhz is float mhz) secondary.Add(new(Fmt.Clock(mhz)));
        if (c.PowerW is float w) secondary.Add(new(Fmt.Watts(w)));
        bool hot = c.Load >= t.Load;
        return WithSpark(new RowData
        {
            Id = "cpu", Label = "cpu",
            Primary = c.Load is float l ? new(Fmt.Pct(l), hot) : null,
            Secondary = secondary,
            SparkHot = hot,
        }, c.Load / 100.0, cfg);
    }

    private RowData Gpu(GpuData? g, WidgetConfig cfg)
    {
        if (g is null || (g.Load is null && g.Temp is null)) return RowData.Hidden("gpu");
        var t = cfg.Thresholds;
        var secondary = new List<Part>();
        if (g.Temp is float temp) secondary.Add(new(Fmt.Temp(temp), temp >= t.GpuTemp));
        if (g.VramUsedMb is float used && g.VramTotalMb is float total && total > 0)
            secondary.Add(new($"{Fmt.Gb(used / 1024.0)} / {total / 1024.0:0} GB", used / total * 100 >= t.VramPercent));
        else if (g.VramUsedMb is float onlyUsed)
            secondary.Add(new($"{Fmt.Gb(onlyUsed / 1024.0)} GB"));
        if (g.PowerW is float w) secondary.Add(new(Fmt.Watts(w)));
        bool hot = g.Load >= t.Load;
        return WithSpark(new RowData
        {
            Id = "gpu", Label = "gpu",
            Primary = g.Load is float l ? new(Fmt.Pct(l), hot) : null,
            Secondary = secondary,
            SparkHot = hot,
        }, g.Load / 100.0, cfg);
    }

    private RowData Ram(MemData? m, WidgetConfig cfg)
    {
        if (m is null || m.TotalBytes == 0) return RowData.Hidden("ram");
        double pct = m.UsedBytes * 100.0 / m.TotalBytes;
        bool hot = pct >= cfg.Thresholds.RamPercent;
        return WithSpark(new RowData
        {
            Id = "ram", Label = "ram",
            Primary = new(Fmt.Pct(pct), hot),
            Secondary = [new($"{Fmt.Gb(m.UsedBytes / Fmt.GiB)} / {Math.Round(m.TotalBytes / Fmt.GiB):0} GB")],
            SparkHot = hot,
        }, pct / 100.0, cfg);
    }

    private RowData Disk(DiskData? d, WidgetConfig cfg)
    {
        if (d is null || (d.Activity is null && d.ReadBps is null && d.WriteBps is null)) return RowData.Hidden("disk");
        var secondary = new List<Part>();
        if (d.ReadBps is double r) secondary.Add(new($"r {Fmt.Rate(r)}"));
        if (d.WriteBps is double w) secondary.Add(new($"w {Fmt.Rate(w)}"));
        bool hot = d.Activity >= cfg.Thresholds.Load;
        return WithSpark(new RowData
        {
            Id = "disk", Label = "disk",
            Primary = d.Activity is float a ? new(Fmt.Pct(a), hot) : null,
            Secondary = secondary,
            SparkHot = hot,
        }, d.Activity / 100.0, cfg);
    }

    private static RowData Drives(IReadOnlyList<DriveSpace> drives, WidgetConfig cfg)
    {
        if (drives.Count == 0) return RowData.Hidden("drives");
        return new RowData
        {
            Id = "drives", Label = "free",
            Secondary = drives.Select(d => new Part(
                $"{d.Name.TrimEnd(':').ToLowerInvariant()} {Fmt.Size(d.FreeBytes)}",
                d.TotalBytes > 0 && d.FreeBytes * 100.0 / d.TotalBytes <= cfg.Thresholds.DriveFreePercent)).ToList(),
        };
    }

    private RowData Net(NetData? n, WidgetConfig cfg)
    {
        if (n is null) return RowData.Hidden("net");
        var row = new RowData
        {
            Id = "net", Label = "net",
            Secondary = [new($"↓ {Fmt.Rate(n.DownBps)}"), new($"↑ {Fmt.Rate(n.UpBps)}")],
        };
        // Autoscaled to the recent peak, with a floor so idle trickle stays flat.
        var h = Track("net", n.DownBps + n.UpBps, cfg);
        if (h is null) return row;
        double max = Math.Max(NetSparkFloorBps, h.Max());
        return new RowData
        {
            Id = row.Id, Label = row.Label, Secondary = row.Secondary,
            Spark = h.ToArray(v => v / max), SparkCapacity = h.Capacity,
        };
    }

    private static RowData Fans(IReadOnlyList<float> fans)
    {
        if (fans.Count == 0) return RowData.Hidden("fans");
        var parts = fans.Select(f => new Part(f.ToString("0", CultureInfo.InvariantCulture))).ToList();
        parts[^1] = parts[^1] with { Text = parts[^1].Text + " rpm" };
        return new RowData { Id = "fans", Label = "fans", Secondary = parts };
    }

    private RowData WithSpark(RowData row, double? normalized, WidgetConfig cfg)
    {
        var h = Track(row.Id, normalized is double v ? Math.Clamp(v, 0, 1) : null, cfg);
        if (h is null) return row;
        return new RowData
        {
            Id = row.Id, Label = row.Label, Visible = row.Visible, Primary = row.Primary,
            Secondary = row.Secondary, SparkHot = row.SparkHot,
            Spark = h.ToArray(x => x), SparkCapacity = h.Capacity,
        };
    }

    private History? Track(string id, double? value, WidgetConfig cfg)
    {
        if (!cfg.Sparklines || value is not double v) return null;
        int capacity = Math.Clamp(cfg.HistoryLength, 2, 600);
        if (!_history.TryGetValue(id, out var h) || h.Capacity != capacity)
            _history[id] = h = new History(capacity);
        h.Add(v);
        return h;
    }

    private sealed class History(int capacity)
    {
        private readonly double[] _buf = new double[capacity];
        private int _start, _count;

        public int Capacity => _buf.Length;

        public void Add(double v)
        {
            if (_count < _buf.Length) _buf[(_start + _count++) % _buf.Length] = v;
            else { _buf[_start] = v; _start = (_start + 1) % _buf.Length; }
        }

        public double Max()
        {
            double m = 0;
            for (int i = 0; i < _count; i++) m = Math.Max(m, _buf[(_start + i) % _buf.Length]);
            return m;
        }

        public double[] ToArray(Func<double, double> map)
        {
            var a = new double[_count];
            for (int i = 0; i < _count; i++) a[i] = map(_buf[(_start + i) % _buf.Length]);
            return a;
        }
    }
}

internal static class Fmt
{
    public const double GiB = 1024.0 * 1024 * 1024;
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Pct(double v) => $"{Math.Round(v).ToString("0", C)}%";
    public static string Temp(double v) => $"{v.ToString("0", C)}°";
    public static string Clock(double mhz) => $"{(mhz / 1000).ToString("0.00", C)} GHz";
    public static string Watts(double w) => $"{w.ToString("0", C)} W";
    public static string Gb(double gb) => gb.ToString(gb < 10 ? "0.0" : "0", C);

    public static string Rate(double bps) => bps switch
    {
        < 1024 * 1024 => $"{(bps / 1024).ToString("0", C)} KB/s",
        < 100 * 1024 * 1024 => $"{(bps / 1024 / 1024).ToString("0.0", C)} MB/s",
        < 1024L * 1024 * 1024 => $"{(bps / 1024 / 1024).ToString("0", C)} MB/s",
        _ => $"{(bps / GiB).ToString("0.0", C)} GB/s",
    };

    public static string Size(long bytes)
    {
        double gb = bytes / GiB;
        return gb >= 1000 ? $"{(gb / 1024).ToString("0.0", C)} TB" : $"{gb.ToString("0", C)} GB";
    }

    public static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
        : $"{t.Minutes}m";
}
