using System.IO;
using System.Text.Json;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Services;

/// <summary>One board's line in the call log.</summary>
public sealed class BoardCallStats
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public DateTime FirstCall { get; set; }
    public DateTime LastCall { get; set; }
    public int Calls { get; set; }
    public double SecondsOnline { get; set; }
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
}

/// <summary>
/// The call log: calls, time online and data per board, keyed by the connection's Id. Kept in its own file
/// (callstats.json in the app data folder) rather than on SavedConnection, because the phonebook is reloaded from
/// disk, replaced wholesale by edits and imports, and exported without extra fields — any of which would wipe or lose
/// stats. A call starts when the connection succeeds and ends when the session disconnects.
///
/// Each BBS window is a process of its own, all sharing the file, so a call's start and end are applied to the log as
/// it is on disk at that moment (under <see cref="AppDataLock"/>), and reading the log picks up what other windows
/// recorded.
/// </summary>
public sealed class CallStatsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private Dictionary<string, BoardCallStats> _stats = [];

    /// <summary>The call in progress, if any: which board, and when it connected.</summary>
    private string? _activeId;
    private DateTime _activeStartUtc;
    private DateTime _stamp;

    public CallStatsService()
    {
        _path = Path.Combine(AppSettings.GetAppDataDir(), "callstats.json");
        Load();
    }

    public BoardCallStats? Get(string id)
    {
        ReloadIfChanged();
        return _stats.TryGetValue(id, out var stats) ? stats : null;
    }

    public IReadOnlyList<BoardCallStats> All
    {
        get
        {
            ReloadIfChanged();
            return _stats.Values.ToList();
        }
    }

    private void ReloadIfChanged()
    {
        if (AppDataLock.Stamp(_path) == _stamp)
            return;
        using var _ = AppDataLock.Acquire();
        Load();
    }

    /// <summary>A call to <paramref name="connection"/> just connected.</summary>
    public void BeginCall(SavedConnection connection)
    {
        // A call that was never closed (shouldn't happen) keeps its time but no byte counts
        EndCall(0, 0);

        using var _ = AppDataLock.Acquire();
        Load();
        var now = DateTime.Now;
        if (!_stats.TryGetValue(connection.Id, out var stats))
        {
            stats = new BoardCallStats { FirstCall = now };
            _stats[connection.Id] = stats;
        }
        stats.Name = connection.Name;
        stats.Address = $"{connection.Host}:{connection.Port}";
        stats.LastCall = now;
        stats.Calls++;

        _activeId = connection.Id;
        _activeStartUtc = DateTime.UtcNow;
        Save();
    }

    /// <summary>The call in progress ended; adds its time online and data. Does nothing if no call is open.</summary>
    public void EndCall(long bytesSent, long bytesReceived)
    {
        if (_activeId == null)
            return;

        using var _ = AppDataLock.Acquire();
        Load();
        if (_stats.TryGetValue(_activeId, out var stats))
        {
            stats.SecondsOnline += Math.Max(0, (DateTime.UtcNow - _activeStartUtc).TotalSeconds);
            stats.BytesSent += Math.Max(0, bytesSent);
            stats.BytesReceived += Math.Max(0, bytesReceived);
        }
        _activeId = null;
        Save();
    }

    /// <summary>One line for the phonebook: "12 calls · 3h 12m online · first … · last … · ↓ 1.2 MB ↑ 45.0 KB".</summary>
    public string Describe(string id)
    {
        var stats = Get(id);
        if (stats == null || stats.Calls == 0)
            return "never called";
        return $"{stats.Calls} call{(stats.Calls == 1 ? "" : "s")} · {FormatDuration(stats.SecondsOnline)} online · " +
               $"first {stats.FirstCall:yyyy-MM-dd} · last {stats.LastCall:yyyy-MM-dd HH:mm} · " +
               $"↓ {FormatBytes(stats.BytesReceived)}  ↑ {FormatBytes(stats.BytesSent)}";
    }

    public static string FormatDuration(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}h {time.Minutes:00}m";
        if (time.TotalMinutes >= 1)
            return $"{time.Minutes}m {time.Seconds:00}s";
        return $"{time.Seconds}s";
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024L => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    /// <summary>Reads the file. Callers other than the constructor hold <see cref="AppDataLock"/>.</summary>
    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _stats = JsonSerializer.Deserialize<Dictionary<string, BoardCallStats>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch
        {
            _stats = [];
        }
        _stamp = AppDataLock.Stamp(_path);
    }

    private void Save()
    {
        try
        {
            AppDataLock.WriteAtomic(_path, JsonSerializer.Serialize(_stats, JsonOptions));
            _stamp = AppDataLock.Stamp(_path);
        }
        catch
        {
            // The log is a nicety; never let a locked or full disk interrupt a call
        }
    }
}
