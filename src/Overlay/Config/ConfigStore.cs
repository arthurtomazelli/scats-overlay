using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Config;

/// <summary>Loads config.json (creating it with defaults if missing) and reloads it when it changes on disk.</summary>
public sealed class ConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;

    public ConfigStore(string path)
    {
        FilePath = path;
        Current = LoadOrCreate();

        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        // Editors fire several events per save; settle before reading.
        FileSystemEventHandler kick = (_, _) => _debounce.Change(250, Timeout.Infinite);
        _watcher.Changed += kick;
        _watcher.Created += kick;
        _watcher.Renamed += (_, _) => _debounce.Change(250, Timeout.Infinite);
    }

    public string FilePath { get; }

    /// <summary>Replaced (never mutated) on reload, so reference equality means "unchanged".</summary>
    public WidgetConfig Current { get; private set; }

    /// <summary>Raised on a thread-pool thread after a successful reload.</summary>
    public event Action? Changed;

    public void StartWatching() => _watcher.EnableRaisingEvents = true;

    public void Reload()
    {
        if (TryLoad(out var config))
        {
            Current = config;
            Log.Info("Config reloaded");
            Changed?.Invoke();
        }
    }

    /// <summary>Writes <paramref name="config"/> to disk; the watcher then applies it like a hand edit.</summary>
    public void Save(WidgetConfig config)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Json)); }
        catch (Exception ex) { Log.Error("Could not save config", ex); }
    }

    /// <summary>A deep copy of <see cref="Current"/> that is safe to edit.</summary>
    public WidgetConfig CloneCurrent() =>
        JsonSerializer.Deserialize<WidgetConfig>(JsonSerializer.Serialize(Current, Json), Json) ?? new WidgetConfig();

    private WidgetConfig LoadOrCreate()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = new WidgetConfig();
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, Json)); }
            catch (Exception ex) { Log.Error("Could not write default config", ex); }
            return defaults;
        }
        return TryLoad(out var config) ? config : new WidgetConfig();
    }

    private bool TryLoad(out WidgetConfig config)
    {
        config = null!;
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            config = JsonSerializer.Deserialize<WidgetConfig>(stream, Json) ?? new WidgetConfig();
            config.Metrics = Normalize(config.Metrics);
            config.GameMode.Metrics = Normalize(config.GameMode.Metrics);
            return true;
        }
        catch (Exception ex)
        {
            // Half-written file or a typo: keep the previous config.
            Log.Error("Config could not be read; keeping previous", ex);
            return false;
        }
    }

    private static List<string> Normalize(List<string>? ids) =>
        (ids ?? []).Select(m => m.Trim().ToLowerInvariant()).Where(m => m.Length > 0).Distinct().ToList();

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
