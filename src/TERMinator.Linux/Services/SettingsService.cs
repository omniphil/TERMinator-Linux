using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Services;

/// <summary>
/// Persists application settings to JSON.
///
/// Several TERMinator processes share settings.json (the main window, and one process per BBS window), so a save
/// never writes out this process's whole copy: it re-reads the file under <see cref="AppDataLock"/> and puts in only
/// the settings this process changed since it last loaded or saved (compared setting by setting, a nested group such
/// as Crt as one). A counter goes through <see cref="Update"/>, which reads the current value first, so two windows
/// adding a call each add up. <see cref="ReloadIfChanged"/> brings in what other processes saved.
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    /// <summary>The settings as this process last loaded or saved them: what a save compares against</summary>
    private JsonObject _baseline = new();
    private DateTime _stamp;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        _filePath = Path.Combine(AppSettings.GetAppDataDir(), "settings.json");
        Load();
    }

    public void Load()
    {
        using var _ = AppDataLock.Acquire();
        Settings = ReadFile() ?? NewInstallSettings();
        Normalize(Settings);
        _baseline = ToNode(Settings);
        _stamp = AppDataLock.Stamp(_filePath);
        // One-time move from the old 250 MB TRACE cache default to 500 MB (2026-10-12); a 100 MB pick is kept
        if (!Settings.TraceCache500Done)
        {
            if (Settings.TraceCacheLimitMb == 250)
                Settings.TraceCacheLimitMb = 500;
            Settings.TraceCache500Done = true;
            SaveLocked();
        }
    }

    public void Save()
    {
        using var _ = AppDataLock.Acquire();
        SaveLocked();
    }

    /// <summary>Changes settings from their current value on disk (e.g. a counter), and saves.</summary>
    public void Update(Action<AppSettings> change)
    {
        using var _ = AppDataLock.Acquire();
        RefreshLocked();
        change(Settings);
        SaveLocked();
    }

    /// <summary>Brings in settings another process saved since this one last loaded or saved. Settings changed here
    /// and not yet saved are kept. True when anything was read.</summary>
    public bool ReloadIfChanged()
    {
        if (AppDataLock.Stamp(_filePath) == _stamp)
            return false;
        using var _ = AppDataLock.Acquire();
        RefreshLocked();
        return true;
    }

    private void RefreshLocked()
    {
        var disk = ReadFileNode();
        if (disk == null)
            return;
        var current = ToNode(Settings);
        // Settings changed here and not yet saved stay as they are; everything else comes from the file
        foreach (var (key, value) in current)
            if (!JsonNode.DeepEquals(value, _baseline[key]))
                disk[key] = value?.DeepClone();
        var merged = disk.Deserialize<AppSettings>(JsonOptions);
        if (merged != null)
        {
            Normalize(merged);
            CopyInto(Settings, merged);
        }
        _baseline = ReadFileNode() ?? ToNode(Settings);
        _stamp = AppDataLock.Stamp(_filePath);
    }

    private void SaveLocked()
    {
        var current = ToNode(Settings);
        var disk = ReadFileNode() ?? new JsonObject();
        foreach (var (key, value) in current)
            if (!JsonNode.DeepEquals(value, _baseline[key]))
                disk[key] = value?.DeepClone();
        // A setting the file doesn't have yet (a new install, or a setting added since it was written)
        foreach (var (key, value) in current)
            if (!disk.ContainsKey(key))
                disk[key] = value?.DeepClone();
        AppDataLock.WriteAtomic(_filePath, disk.ToJsonString(JsonOptions));
        _baseline = (JsonObject)disk.DeepClone();
        _stamp = AppDataLock.Stamp(_filePath);
    }

    private AppSettings? ReadFile()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath), JsonOptions) ?? new AppSettings()
                : null;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private JsonObject? ReadFileNode()
    {
        try
        {
            return File.Exists(_filePath) ? JsonNode.Parse(File.ReadAllText(_filePath)) as JsonObject : null;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject ToNode(AppSettings settings) =>
        JsonSerializer.SerializeToNode(settings, JsonOptions) as JsonObject ?? new JsonObject();

    private static AppSettings NewInstallSettings() => new()
    {
        DownloadDirectory = AppSettings.GetDefaultDownloadDir(),
        // New installs start on the desktop side-by-side home screen. This is set here, not as the property default,
        // so an upgrade whose settings.json predates the field keeps the stacked layout it already had.
        HomeSideBySide = true
    };

    private static void Normalize(AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.DownloadDirectory))
            settings.DownloadDirectory = AppSettings.GetDefaultDownloadDir();

        // An explicit null in the file (or a hand-edited settings.json) would
        // otherwise NRE the first time the CRT dialog is opened.
        settings.Crt ??= new CrtSettings();
    }

    /// <summary>Copies every setting across, keeping the objects the app already holds (Settings itself, and a group
    /// like Crt, which an open dialog may be bound to) and filling them in place.</summary>
    private static void CopyInto(object target, object source)
    {
        foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0)
                continue;
            var value = p.GetValue(source);
            var existing = p.GetValue(target);
            bool group = p.PropertyType.IsClass && p.PropertyType != typeof(string) &&
                         !typeof(IEnumerable).IsAssignableFrom(p.PropertyType);
            if (group && existing != null && value != null)
                CopyInto(existing, value);
            else
                p.SetValue(target, value);
        }
    }
}
