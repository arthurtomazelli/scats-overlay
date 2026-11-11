using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Overlay.Config;
using Overlay.Sensors;

namespace Overlay;

/// <summary>
/// Command line:
///   --no-elevate   don't ask for admin (no CPU temps/clocks/fans, handy while tweaking the look)
///   --dump         write every sensor to sensors.txt and exit
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var dir = AppContext.BaseDirectory;
        Log.Init(Path.Combine(dir, "overlay.log"));
        var config = new ConfigStore(Path.Combine(dir, "config.json"));

        bool elevated = IsElevated();
        if (config.Current.RequireAdmin && !config.Current.CatOnly && !elevated && !args.Contains("--no-elevate") && TryRelaunchElevated(args))
            return 0;

        if (args.Contains("--dump"))
        {
            using var hw = new HardwareMonitorService();
            hw.Open(config.Current);
            File.WriteAllText(Path.Combine(dir, "sensors.txt"), hw.Dump());
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\Overlay.HardwareWidget", out bool first);
        if (!first)
        {
            Log.Info("Already running");
            return 0;
        }

        Log.Info($"Starting (elevated: {elevated})");
        if (config.Current.SoftwareRendering) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var host = new WidgetHost(config);
        app.Startup += (_, _) => host.Start();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
        return app.Run();
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args.Select(a => $"\"{a}\"")),
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return true;
        }
        catch (Win32Exception ex)
        {
            // 1223 = user said no at the UAC prompt. Carry on without admin.
            Log.Info($"Not elevated ({ex.NativeErrorCode}); CPU temperature and fans will be missing");
            return false;
        }
    }
}
