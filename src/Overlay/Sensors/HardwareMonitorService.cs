using System.Diagnostics;
using System.IO;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Overlay.Config;
using Overlay.Interop;

namespace Overlay.Sensors;

/// <summary>
/// Owns the LibreHardwareMonitor <see cref="Computer"/>. Sensor references are resolved once
/// (<see cref="Classify"/>) and each poll only updates the hardware that a visible metric needs.
/// Not thread-safe by design beyond the internal lock: call it from the poll thread.
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private const int FanEveryNTicks = 3;
    private const int DrivesEveryNTicks = 5;
    private const int TimingSampleTicks = 60;

    private long _cpuTicks, _gpuTicks;

    private static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency / TimingSampleTicks).ToString("0.00");

    private readonly Computer _computer = new();
    private readonly object _gate = new();
    private readonly NetworkCounters _net = new();
    private readonly DiskCounters _diskCounters = new();

    private WidgetConfig? _applied;
    private bool _opened;
    private long _tick;

    private CpuSensors? _cpu;
    private GpuSensors? _gpu;
    private NvmlGpu? _nvml;
    private List<IHardware> _boards = [];
    private List<ISensor> _fans = [];

    private IReadOnlyList<float> _lastFans = [];
    private IReadOnlyList<DriveSpace> _lastDrives = [];

    public void Open(WidgetConfig config)
    {
        lock (_gate)
        {
            EnableGroups(config);
            _computer.Open();
            _opened = true;
            Classify(config);
            _applied = config;
            Log.Info($"Hardware opened. CPU: {_cpu?.Hardware.Name ?? "-"}, GPU: {_gpu?.Hardware.Name ?? "-"}, board fans: {_fans.Count}");
        }
    }

    public Snapshot Sample(WidgetConfig config, IReadOnlySet<string> metrics)
    {
        lock (_gate)
        {
            if (!_opened) throw new InvalidOperationException("Open() first");
            if (!ReferenceEquals(config, _applied))
            {
                EnableGroups(config);
                Classify(config);
                _applied = config;
            }

            long tick = _tick++;

            long t0 = Stopwatch.GetTimestamp();
            CpuData? cpu = null;
            if (metrics.Contains("cpu") && _cpu is { } c)
            {
                c.Hardware.Update();
                cpu = c.Read();
            }

            long t1 = Stopwatch.GetTimestamp();
            GpuData? gpu = null;
            if (metrics.Contains("gpu") && _gpu is { } g)
            {
                if (_nvml is not null)
                {
                    gpu = _nvml.Read();
                }
                else
                {
                    g.Hardware.UpdateAll();
                    gpu = g.Read();
                }
            }
            long t2 = Stopwatch.GetTimestamp();
            _cpuTicks += t1 - t0;
            _gpuTicks += t2 - t1;
            if (tick == TimingSampleTicks)
                Log.Info($"Avg poll cost over {TimingSampleTicks} ticks: cpu {Ms(_cpuTicks)} ms, gpu {Ms(_gpuTicks)} ms");

            DiskData? disk = null;
            if (metrics.Contains("disk")) disk = _diskCounters.Sample();

            if (metrics.Contains("fans") && tick % FanEveryNTicks == 0)
            {
                foreach (var b in _boards) b.UpdateAll();
                var fans = _fans.Select(f => f.Read()).Where(v => v is > 0).Select(v => v!.Value).ToList();

                var nvmlFans = _nvml?.ReadFanRpm().Where(v => v > 0).ToList();
                if (nvmlFans is { Count: > 0 })
                {
                    fans.AddRange(nvmlFans);
                }
                else if (_gpu is not null)
                {
                    // LHM's GPU fan sensors hold the last GPU update; refresh unless the gpu row just did.
                    if (_nvml is not null || !metrics.Contains("gpu")) _gpu.Hardware.Update();
                    fans.AddRange(_gpu.Hardware.Sensors.Where(s => s.SensorType == SensorType.Fan)
                        .Select(s => s.Read()).Where(v => v is > 0).Select(v => v!.Value));
                }
                _lastFans = fans;
            }

            if (metrics.Contains("drives") && (tick % DrivesEveryNTicks == 0 || _lastDrives.Count == 0))
                _lastDrives = ReadDrives(config.Drives);

            return new Snapshot(
                cpu,
                gpu,
                metrics.Contains("ram") ? ReadMemory() : null,
                disk,
                metrics.Contains("drives") ? _lastDrives : [],
                metrics.Contains("net") ? _net.Sample() : null,
                metrics.Contains("fans") ? _lastFans : [],
                TimeSpan.FromMilliseconds(Environment.TickCount64));
        }
    }

    /// <summary>Every sensor LHM can see, for tuning sensor picks to a specific machine.</summary>
    public string Dump()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Selected CPU: {_cpu?.Hardware.Name ?? "-"}");
            sb.AppendLine($"Selected GPU: {_gpu?.Hardware.Name ?? "-"}");
            sb.AppendLine();
            foreach (var root in _computer.Hardware)
            {
                root.UpdateAll();
                foreach (var hw in root.WithSubHardware())
                {
                    sb.AppendLine($"{hw.HardwareType}: {hw.Name}");
                    foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
                        sb.AppendLine($"    [{s.SensorType,-12}] {s.Name,-32} = {s.Value?.ToString("0.###") ?? "null"}");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    private void EnableGroups(WidgetConfig config)
    {
        var all = config.Metrics.Concat(config.GameMode.Metrics).ToHashSet();
        _computer.IsCpuEnabled = all.Contains("cpu");
        _computer.IsGpuEnabled = all.Contains("gpu") || all.Contains("fans");
        // Never: LHM storage reads SMART every update, which wakes/ticks hard drives. See DiskCounters.
        _computer.IsStorageEnabled = false;
        _computer.IsMotherboardEnabled = all.Contains("fans");
        // RAM, disk, network, drive space and uptime come from cheaper OS APIs.
        _computer.IsMemoryEnabled = false;
        _computer.IsNetworkEnabled = false;
        _computer.IsControllerEnabled = false;
    }

    private void Classify(WidgetConfig config)
    {
        var hardware = _computer.Hardware.ToList();

        _cpu = hardware.Where(h => h.HardwareType == HardwareType.Cpu).Select(CpuSensors.From).FirstOrDefault();
        _gpu = PickGpu(hardware, config.Gpu);
        _boards = hardware.Where(h => h.HardwareType == HardwareType.Motherboard).ToList();

        _fans = _boards.SelectMany(b => b.WithSubHardware())
            .SelectMany(h => h.Sensors)
            .Where(s => s.SensorType == SensorType.Fan)
            .ToList();

        // Reopen NVML only when the selected card changes (config reloads re-run Classify).
        var nvidiaName = _gpu?.Hardware.HardwareType == HardwareType.GpuNvidia ? _gpu.Hardware.Name : null;
        if (_nvml?.Name != nvidiaName)
        {
            _nvml?.Dispose();
            _nvml = nvidiaName is null ? null : NvmlGpu.TryOpen(nvidiaName);
            if (nvidiaName is not null) Log.Info(_nvml is null ? "NVML unavailable; using LHM for the GPU" : "Using NVML for the GPU");
        }
    }

    private static GpuSensors? PickGpu(List<IHardware> hardware, string preference)
    {
        var gpus = hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToList();
        if (gpus.Count == 0) return null;

        if (!string.Equals(preference, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var named = gpus.FirstOrDefault(g => g.Name.Contains(preference, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return GpuSensors.From(named);
        }

        // Prefer discrete: NVIDIA, then AMD with the most VRAM (an AMD iGPU reports little/none), then Intel.
        foreach (var g in gpus) g.Update();
        var best = gpus
            .OrderByDescending(g => g.HardwareType switch { HardwareType.GpuNvidia => 3, HardwareType.GpuAmd => 2, _ => 1 })
            .ThenByDescending(g => GpuSensors.From(g).VramTotal.Read() ?? 0)
            .First();
        return GpuSensors.From(best);
    }

    private static float? Positive(float? v) => v > 0 ? v : null;

    private static MemData? ReadMemory()
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) return null;
        return new MemData(status.ullTotalPhys - status.ullAvailPhys, status.ullTotalPhys);
    }

    private static IReadOnlyList<DriveSpace> ReadDrives(List<string> wanted)
    {
        var result = new List<DriveSpace>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var letter = d.Name.TrimEnd('\\');
                if (wanted.Count > 0 && !wanted.Any(w => w.TrimEnd('\\').Equals(letter, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new DriveSpace(letter, d.AvailableFreeSpace, d.TotalSize));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_opened) _computer.Close();
            _diskCounters.Dispose();
            _nvml?.Dispose();
            _opened = false;
        }
    }

    // --- per-device sensor bundles ------------------------------------------------------------

    private sealed record CpuSensors(IHardware Hardware, ISensor? Load, ISensor? Temp, ISensor[] Clocks, ISensor? Power)
    {
        public static CpuSensors From(IHardware h) => new(
            h,
            h.Find(SensorType.Load, "CPU Total"),
            h.Find(SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core (Tdie)", "Core Average", "Core Max")
                ?? h.FirstOf(SensorType.Temperature),
            h.Sensors.Where(s => s.SensorType == SensorType.Clock
                                 && s.Name.StartsWith("Core #", StringComparison.Ordinal)
                                 && !s.Name.Contains("Effective", StringComparison.Ordinal)).ToArray(),
            h.Find(SensorType.Power, "Package", "CPU Package"));

        public CpuData Read()
        {
            float sum = 0;
            int n = 0;
            foreach (var c in Clocks)
                if (c.Read() is float v && v > 0) { sum += v; n++; }
            // Without driver access LHM leaves temp/power at 0 rather than null.
            return new CpuData(Load.Read(), Positive(Temp.Read()), n > 0 ? sum / n : null, Positive(Power.Read()));
        }
    }

    private sealed record GpuSensors(IHardware Hardware, ISensor? Load, ISensor? Temp, ISensor? VramUsed, ISensor? VramTotal, ISensor? Power)
    {
        public static GpuSensors From(IHardware h) => new(
            h,
            h.Find(SensorType.Load, "GPU Core"),
            h.Find(SensorType.Temperature, "GPU Core"),
            h.Find(SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
            h.Find(SensorType.SmallData, "GPU Memory Total"),
            h.Find(SensorType.Power, "GPU Package", "GPU Power", "GPU Core") ?? h.FirstOf(SensorType.Power));

        public GpuData Read() =>
            new(Hardware.Name, Load.Read(), Temp.Read(), VramUsed.Read(), VramTotal.Read(), Power.Read());
    }
}
