namespace Overlay.Sensors;

// Raw readings for one poll. Anything the hardware doesn't report is null / empty,
// and the presenter hides it.

public sealed record CpuData(float? Load, float? Temp, float? ClockMhz, float? PowerW);

public sealed record GpuData(string Name, float? Load, float? Temp, float? VramUsedMb, float? VramTotalMb, float? PowerW);

public sealed record MemData(ulong UsedBytes, ulong TotalBytes);

public sealed record DiskData(float? Activity, double? ReadBps, double? WriteBps);

public sealed record DriveSpace(string Name, long FreeBytes, long TotalBytes);

public sealed record NetData(double DownBps, double UpBps);

public sealed record Snapshot(
    CpuData? Cpu,
    GpuData? Gpu,
    MemData? Mem,
    DiskData? Disk,
    IReadOnlyList<DriveSpace> Drives,
    NetData? Net,
    IReadOnlyList<float> Fans,
    TimeSpan Uptime);
