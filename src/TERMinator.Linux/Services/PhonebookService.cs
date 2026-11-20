using System.IO;
using System.Text.Json;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Services;

/// <summary>
/// Manages saved BBS connections in a JSON phonebook file.
///
/// Several TERMinator processes share phonebook.json (the main window, and one process per BBS window), so a save
/// doesn't write out this process's list as it stands: it re-reads the file under <see cref="AppDataLock"/> and applies
/// only what changed here since the last load or save -- entries edited, added or removed, and the order if it was
/// changed here. A BBS window's own saves (its last call, a snapshot) therefore can't undo an edit made in the main
/// window meanwhile. The last-connected time always keeps the later of the two.
/// </summary>
public class PhonebookService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Maps (Host, Port) to snapshot index for the 10 default BBSes
    private static readonly Dictionary<(string Host, int Port), int> DefaultSnapshotMap = new()
    {
        { ("absinthebbs.net", 1940), 0 },
        { ("telnet.deadmodemsociety.com", 1337), 1 },
        { ("20forbeers.com", 1337), 2 },
        { ("bbs.bottomlessabyss.net", 2023), 3 },
        { ("sbbs.dmine.net", 24), 4 },
        { ("bbs.erb.pw", 23), 5 },
        { ("dura-bbs.net", 6359), 6 },
        { ("wizardsrainbow.com", 23), 7 },
        { ("xibalba.l33t.codes", 44510), 8 },
        { ("d1st.org", 23), 9 },
    };

    private readonly string _filePath;
    private List<SavedConnection> _connections = [];

    /// <summary>Each entry as this process last loaded or saved it, and their order: what a save compares against</summary>
    private Dictionary<string, string> _baseline = [];
    private List<string> _baselineOrder = [];
    private DateTime _stamp;

    public IReadOnlyList<SavedConnection> Connections => _connections;

    public PhonebookService()
    {
        _filePath = Path.Combine(AppSettings.GetAppDataDir(), "phonebook.json");
        Load();
        MigrateDefaultSnapshots();

        if (_connections.Count == 0)
            SeedDefaults();
    }

    public void Load()
    {
        using var _ = AppDataLock.Acquire();
        _connections = ReadFile();
        SetBaseline();
    }

    /// <summary>Re-reads the phonebook if another process saved it since this one last loaded or saved. True when it
    /// was re-read (the caller refreshes what it shows).</summary>
    public bool ReloadIfChanged()
    {
        if (AppDataLock.Stamp(_filePath) == _stamp)
            return false;
        Load();
        return true;
    }

    /// <summary>Changes one entry and saves just that, whatever else has changed in the file meanwhile. Does nothing
    /// for a connection that isn't in the phonebook (a quick connect typed in the command line).</summary>
    public void UpdateEntry(string id, Action<SavedConnection> change)
    {
        var entry = GetById(id);
        if (entry == null)
            return;
        change(entry);
        Save();
    }

    private List<SavedConnection> ReadFile()
    {
        if (!File.Exists(_filePath))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<SavedConnection>>(File.ReadAllText(_filePath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SetBaseline()
    {
        _baseline = _connections.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => JsonSerializer.Serialize(g.First(), JsonOptions));
        _baselineOrder = _connections.Select(c => c.Id).ToList();
        _stamp = AppDataLock.Stamp(_filePath);
    }

    private void SeedDefaults()
    {
        var defaults = new (string Name, string Host, int Port, int ScreenMode, string? Font)[]
        {
            ("aBSiNTHE BBS",          "absinthebbs.net",              1940, NativeBridge.SCREEN_MODE_80X50, "Topaz Plus (Amiga)"),
            ("Dead Modem Society",     "telnet.deadmodemsociety.com",  1337, NativeBridge.SCREEN_MODE_80X25, null),
            ("20 For Beers",           "20forbeers.com",               1337, NativeBridge.SCREEN_MODE_80X25, null),
            ("The Bottomless Abyss",   "bbs.bottomlessabyss.net",      2023, NativeBridge.SCREEN_MODE_80X25, null),
            ("Diamond Mine Online",    "sbbs.dmine.net",                 24, NativeBridge.SCREEN_MODE_80X25, null),
            ("The Quantum Wormhole",   "bbs.erb.pw",                     23, NativeBridge.SCREEN_MODE_80X25, null),
            ("Dura-Europos",           "dura-bbs.net",                 6359, NativeBridge.SCREEN_MODE_80X25, null),
            ("Wizard's Rainbow",       "wizardsrainbow.com",             23, NativeBridge.SCREEN_MODE_80X25, null),
            ("Xibalba",                "xibalba.l33t.codes",          44510, NativeBridge.SCREEN_MODE_80X25, null),
            ("Distortion",             "d1st.org",                       23, NativeBridge.SCREEN_MODE_80X25, null),
        };

        for (int i = 0; i < defaults.Length; i++)
        {
            var d = defaults[i];
            _connections.Add(new SavedConnection
            {
                Name = d.Name,
                Host = d.Host,
                Port = d.Port,
                ScreenMode = d.ScreenMode,
                FontName = d.Font,
                SortOrder = i,
                SnapshotPath = $"avares://TERMinator/Assets/snapshots/snapshot_{i}.png",
            });
        }

        Save();
    }

    private void MigrateDefaultSnapshots()
    {
        bool changed = false;
        foreach (var conn in _connections)
        {
            if (conn.SnapshotPath != null)
                continue;

            if (DefaultSnapshotMap.TryGetValue((conn.Host, conn.Port), out int index))
            {
                conn.SnapshotPath = $"avares://TERMinator/Assets/snapshots/snapshot_{index}.png";
                changed = true;
            }
        }

        if (changed)
            Save();
    }

    public void Save()
    {
        using var _ = AppDataLock.Acquire();
        var disk = ReadFile();
        var byId = disk.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var mine = _connections.Select(c => c.Id).ToHashSet();

        // Removed here
        disk.RemoveAll(c => _baseline.ContainsKey(c.Id) && !mine.Contains(c.Id));

        // Edited or added here. The last call is the later of the two, since a BBS window records its own.
        foreach (var c in _connections)
        {
            bool changed = !_baseline.TryGetValue(c.Id, out var was) || was != JsonSerializer.Serialize(c, JsonOptions);
            if (byId.TryGetValue(c.Id, out var onDisk))
            {
                if (onDisk.LastConnected > c.LastConnected)
                    c.LastConnected = onDisk.LastConnected;
                if (changed)
                    disk[disk.IndexOf(onDisk)] = c;
                else if (onDisk.LastConnected == c.LastConnected)
                    disk[disk.IndexOf(onDisk)] = c;     // same content: keep this process's object in the list
            }
            else if (changed)
            {
                disk.Add(c);
            }
        }

        // The order, if it was changed here: this process's order, with anything added elsewhere after it
        if (!_connections.Select(c => c.Id).SequenceEqual(_baselineOrder))
        {
            var rank = _connections.Select((c, i) => (c.Id, i)).ToDictionary(t => t.Id, t => t.i);
            disk = disk.OrderBy(c => rank.TryGetValue(c.Id, out int r) ? r : int.MaxValue).ToList();
        }

        AppDataLock.WriteAtomic(_filePath, JsonSerializer.Serialize(disk, JsonOptions));
        _connections = disk;
        SetBaseline();
    }

    public void Add(SavedConnection connection)
    {
        connection.SortOrder = _connections.Count;
        _connections.Add(connection);
        Save();
    }

    public void Update(SavedConnection connection)
    {
        var index = _connections.FindIndex(c => c.Id == connection.Id);
        if (index >= 0)
        {
            _connections[index] = connection;
            Save();
        }
    }

    public void Remove(string id)
    {
        _connections.RemoveAll(c => c.Id == id);
        Save();
    }

    public void MoveTo(string id, int newIndex)
    {
        var index = _connections.FindIndex(c => c.Id == id);
        if (index < 0 || index == newIndex) return;
        newIndex = Math.Clamp(newIndex, 0, _connections.Count - 1);
        var item = _connections[index];
        _connections.RemoveAt(index);
        _connections.Insert(newIndex, item);
        Save();
    }

    public void MoveUp(string id)
    {
        var index = _connections.FindIndex(c => c.Id == id);
        if (index > 0)
        {
            (_connections[index], _connections[index - 1]) =
                (_connections[index - 1], _connections[index]);
            Save();
        }
    }

    private List<string>? _preSortOrder;

    public bool IsSorted => _preSortOrder != null;

    public void SortAlphabetically()
    {
        _preSortOrder = _connections.Select(c => c.Id).ToList();
        _connections.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void RestoreCustomOrder()
    {
        if (_preSortOrder == null) return;
        var lookup = _connections.ToDictionary(c => c.Id);
        var restored = new List<SavedConnection>();
        foreach (var id in _preSortOrder)
        {
            if (lookup.TryGetValue(id, out var conn))
            {
                restored.Add(conn);
                lookup.Remove(id);
            }
        }
        // Append any new entries added after sort
        restored.AddRange(lookup.Values);
        _connections = restored;
        _preSortOrder = null;
        Save();
    }

    public void ClearSortState()
    {
        _preSortOrder = null;
    }

    public void MoveDown(string id)
    {
        var index = _connections.FindIndex(c => c.Id == id);
        if (index >= 0 && index < _connections.Count - 1)
        {
            (_connections[index], _connections[index + 1]) =
                (_connections[index + 1], _connections[index]);
            Save();
        }
    }

    public SavedConnection? GetById(string id) =>
        _connections.FirstOrDefault(c => c.Id == id);
}
