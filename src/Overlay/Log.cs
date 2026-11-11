using System.IO;

namespace Overlay;

/// <summary>Tiny append-only log next to the exe; the widget has no other way to report problems.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string path)
    {
        _path = path;
        try
        {
            // Keep it from growing forever across restarts.
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
        }
        catch { }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} [{Environment.ProcessId}] {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}
