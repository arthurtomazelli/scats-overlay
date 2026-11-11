using System.Diagnostics;
using Overlay.Config;

namespace Overlay.Sensors;

/// <summary>Background loop: opens the hardware once, then samples every PollIntervalMs.</summary>
public sealed class SensorPoller : IDisposable
{
    private readonly HardwareMonitorService _hardware;
    private readonly Func<(WidgetConfig Config, IReadOnlySet<string> Metrics)> _state;
    private readonly Action<Snapshot> _onSnapshot;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public SensorPoller(HardwareMonitorService hardware, Func<(WidgetConfig, IReadOnlySet<string>)> state, Action<Snapshot> onSnapshot)
    {
        _hardware = hardware;
        _state = state;
        _onSnapshot = onSnapshot;
    }

    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var token = _cts.Token;
        try
        {
            _hardware.Open(_state().Config);
        }
        catch (Exception ex)
        {
            Log.Error("Opening hardware failed", ex);
            return;
        }

        while (!token.IsCancellationRequested)
        {
            long start = Stopwatch.GetTimestamp();
            var (config, metrics) = _state();
            try
            {
                _onSnapshot(_hardware.Sample(config, metrics));
            }
            catch (Exception ex)
            {
                Log.Error("Sample failed", ex);
            }

            var interval = Math.Clamp(config.PollIntervalMs, 250, 10_000);
            var wait = interval - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, wait)), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { }
        _cts.Dispose();
    }
}
