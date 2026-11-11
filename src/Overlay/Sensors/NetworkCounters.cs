using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Overlay.Sensors;

/// <summary>
/// Network throughput from the OS interface counters. .NET's NetworkInterface API rebuilds the
/// whole adapter list on every call (~3% of a core at 1 Hz), so instead we scan the table rarely
/// and read only the chosen interfaces with GetIfEntry2 on each tick.
/// Counts physical, connected interfaces only: no loopback, VPN tunnels, Hyper-V switches or
/// filter-driver duplicates.
/// </summary>
internal sealed class NetworkCounters
{
    private const int RescanEveryNTicks = 30;

    // MIB_IF_ROW2 layout (x64). Only the fields we read.
    private const int RowSize = 1352;
    private const int OffIndex = 8;
    private const int OffType = 1128;
    private const int OffFlags = 1152; // InterfaceAndOperStatusFlags: bit0 HardwareInterface, bit1 FilterInterface
    private const int OffOperStatus = 1156;
    private const int OffInOctets = 1208;
    private const int OffOutOctets = 1280;
    private const int IfTypeLoopback = 24;
    private const int IfOperStatusUp = 1;

    private readonly IntPtr _row = Marshal.AllocHGlobal(RowSize);
    private uint[] _indices = [];
    private long _tick;
    private ulong _rx, _tx;
    private long _stamp;

    public NetData? Sample()
    {
        if (_tick++ % RescanEveryNTicks == 0) _indices = ScanInterfaces();

        ulong rx = 0, tx = 0;
        foreach (var index in _indices)
        {
            // GetIfEntry2 looks up by LUID, or by index when the LUID is zero.
            Marshal.WriteInt64(_row, 0, 0);
            Marshal.WriteInt32(_row, OffIndex, (int)index);
            if (GetIfEntry2(_row) != 0) continue;
            rx += (ulong)Marshal.ReadInt64(_row, OffInOctets);
            tx += (ulong)Marshal.ReadInt64(_row, OffOutOctets);
        }

        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _stamp) / (double)Stopwatch.Frequency;
        // First sample, or counters went backwards (adapter reset / list changed): report idle once.
        var result = _stamp == 0 || seconds <= 0 || rx < _rx || tx < _tx
            ? new NetData(0, 0)
            : new NetData((rx - _rx) / seconds, (tx - _tx) / seconds);
        (_rx, _tx, _stamp) = (rx, tx, now);
        return result;
    }

    private static uint[] ScanInterfaces()
    {
        if (GetIfTable2(out var table) != 0) return [];
        try
        {
            int count = Marshal.ReadInt32(table);
            var result = new List<uint>();
            for (int i = 0; i < count; i++)
            {
                var row = table + 8 + i * RowSize; // NumEntries is padded to 8 bytes
                byte flags = Marshal.ReadByte(row, OffFlags);
                bool hardware = (flags & 1) != 0, filter = (flags & 2) != 0;
                if (!hardware || filter) continue;
                if (Marshal.ReadInt32(row, OffType) == IfTypeLoopback) continue;
                if (Marshal.ReadInt32(row, OffOperStatus) != IfOperStatusUp) continue;
                result.Add((uint)Marshal.ReadInt32(row, OffIndex));
            }
            return result.ToArray();
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfEntry2(IntPtr row);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);
}
