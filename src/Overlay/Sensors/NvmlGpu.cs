using System.Runtime.InteropServices;

namespace Overlay.Sensors;

/// <summary>
/// Fast path for NVIDIA cards through NVML (nvml.dll ships with the driver). LibreHardwareMonitor's
/// NVIDIA update also walks NVAPI and D3D statistics and costs ~100 ms per poll; NVML answers the
/// handful of values we show in well under a millisecond.
/// </summary>
internal sealed class NvmlGpu : IDisposable
{
    private const int NVML_SUCCESS = 0;
    private const int NVML_TEMPERATURE_GPU = 0;

    private readonly IntPtr _device;
    private readonly uint _fanCount;
    private bool _fanRpmSupported = true;

    private NvmlGpu(IntPtr device, string name)
    {
        _device = device;
        Name = name;
        if (nvmlDeviceGetNumFans(device, out uint fans) == NVML_SUCCESS) _fanCount = fans;
    }

    public string Name { get; }

    /// <summary>Opens the NVIDIA device whose name matches <paramref name="name"/>, or null if NVML isn't usable.</summary>
    public static NvmlGpu? TryOpen(string name)
    {
        try
        {
            if (nvmlInit_v2() != NVML_SUCCESS) return null;
            if (nvmlDeviceGetCount_v2(out uint count) == NVML_SUCCESS)
            {
                var buffer = new byte[96];
                for (uint i = 0; i < count; i++)
                {
                    if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != NVML_SUCCESS) continue;
                    if (nvmlDeviceGetName(device, buffer, (uint)buffer.Length) != NVML_SUCCESS) continue;
                    var deviceName = System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
                    // LHM prefixes "NVIDIA "; NVML may or may not.
                    if (name.Contains(deviceName, StringComparison.OrdinalIgnoreCase) || deviceName.Contains(name, StringComparison.OrdinalIgnoreCase))
                        return new NvmlGpu(device, name);
                }
            }
            nvmlShutdown();
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return null;
    }

    public GpuData Read()
    {
        float? load = nvmlDeviceGetUtilizationRates(_device, out var util) == NVML_SUCCESS ? util.Gpu : null;
        float? temp = nvmlDeviceGetTemperature(_device, NVML_TEMPERATURE_GPU, out uint t) == NVML_SUCCESS ? t : null;
        float? power = nvmlDeviceGetPowerUsage(_device, out uint mw) == NVML_SUCCESS ? mw / 1000f : null;
        float? used = null, total = null;
        if (nvmlDeviceGetMemoryInfo(_device, out var mem) == NVML_SUCCESS)
        {
            used = mem.Used / (1024f * 1024f);
            total = mem.Total / (1024f * 1024f);
        }
        return new GpuData(Name, load, temp, used, total, power);
    }

    /// <summary>Fan speeds in RPM (drivers 535+). Empty if the driver can't report RPM.</summary>
    public IEnumerable<float> ReadFanRpm()
    {
        if (!_fanRpmSupported) yield break;
        for (uint i = 0; i < _fanCount; i++)
        {
            var info = new FanSpeedInfo { Version = (uint)Marshal.SizeOf<FanSpeedInfo>() | (1u << 24), Fan = i };
            int result;
            try { result = nvmlDeviceGetFanSpeedRPM(_device, ref info); }
            catch (EntryPointNotFoundException) { _fanRpmSupported = false; yield break; }
            if (result == NVML_SUCCESS) yield return info.Speed;
        }
    }

    public void Dispose()
    {
        try { nvmlShutdown(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu, Memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory { public ulong Total, Free, Used; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FanSpeedInfo { public uint Version, Fan, Speed; }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetNumFans(IntPtr device, out uint fans);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetFanSpeedRPM(IntPtr device, ref FanSpeedInfo info);
}
