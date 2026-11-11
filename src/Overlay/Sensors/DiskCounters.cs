using System.Runtime.InteropServices;
using static Overlay.Interop.Native;

namespace Overlay.Sensors;

/// <summary>
/// Disk activity and throughput from Windows' PhysicalDisk performance counters (what Task
/// Manager shows). The disk driver keeps these in memory, so sampling never sends a command to
/// the drive: no SMART reads, nothing that wakes or seeks a hard drive. (LibreHardwareMonitor's
/// storage module did, and made an HDD tick every second.)
/// </summary>
internal sealed class DiskCounters : IDisposable
{
    private readonly IntPtr _query;
    private readonly IntPtr _idle, _read, _write;
    private readonly bool _ok;

    public DiskCounters()
    {
        _ok = PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
              && PdhAddEnglishCounter(_query, @"\PhysicalDisk(*)\% Idle Time", IntPtr.Zero, out _idle) == 0
              && PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _read) == 0
              && PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _write) == 0;
        if (_ok) PdhCollectQueryData(_query); // rate counters need a baseline sample
        else Log.Error("PhysicalDisk performance counters unavailable; disk row hidden");
    }

    public DiskData? Sample()
    {
        if (!_ok || PdhCollectQueryData(_query) != 0) return null;
        return new DiskData(BusiestDiskActivity(), Value(_read), Value(_write));
    }

    private static double? Value(IntPtr counter) =>
        PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) == 0 && v.CStatus == 0
            ? v.DoubleValue : null;

    /// <summary>Activity of the busiest physical disk (100 − idle), like Task Manager's per-disk graph.</summary>
    private float? BusiestDiskActivity()
    {
        uint size = 0;
        if (PdhGetFormattedCounterArray(_idle, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_idle, PDH_FMT_DOUBLE, ref size, out uint count, buffer) != 0) return null;

            float? busiest = null;
            int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * stride);
                if (item.FmtValue.CStatus != 0 || Marshal.PtrToStringUni(item.szName) == "_Total") continue;
                float activity = (float)Math.Clamp(100 - item.FmtValue.DoubleValue, 0, 100);
                busiest = Math.Max(busiest ?? 0, activity);
            }
            return busiest;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
    }
}
