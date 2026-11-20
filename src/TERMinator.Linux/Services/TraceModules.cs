using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using TERMinator.Linux.Models;

namespace TERMinator.Linux.Services;

/// <summary>
/// Where TRACE (TERMinator Render And Compute Engine) finds what it runs. Door modules are WebAssembly that the door
/// sends and TRACE caches by SHA-256, run by the sandboxed host (gamesandbox); nothing door-specific ships with
/// TERMinator. See BBSGames/Doom/ENGINE.md. Linux port of the Windows client's Services/TraceModules.cs (the master).
/// </summary>
public static class TraceModules
{
    /// <summary>
    /// One module: its id and version (what doors ask for), executable and frame buffer capacity. Exclusive modules
    /// (games) fill the window and take the keyboard; the others draw over terminal cells while the door keeps the
    /// keyboard. PrepareArgs runs just before launch and returns the module's command line. CachePath is the cached
    /// .wasm it runs from, so Settings won't delete it while it runs.
    /// </summary>
    public sealed record Module(string Id, int Version, string ExePath, int CapacityWidth, int CapacityHeight,
                                bool Exclusive = false, Func<IReadOnlyList<string>>? PrepareArgs = null,
                                string? CachePath = null);

    /// <summary>Where modules keep their settings and saves: ~/.config/TERMinator/trace/&lt;id&gt; (the app-data dir).</summary>
    public static string DataDir(string id) => Path.Combine(AppSettings.GetAppDataDir(), "trace", id);

    /// <summary>The largest WebAssembly module a door may send.</summary>
    public const int MaxModuleBytes = 16 * 1024 * 1024;

    /// <summary>The largest asset (a WAD, a tileset, a music pack) a door may send.</summary>
    public const int MaxAssetBytes = 64 * 1024 * 1024;

    /// <summary>
    /// The largest file a player may import (their own copy of a game or ROM: it never crosses the link). Modules
    /// read assets with 32-bit offsets, so this stays well under 2 GB.
    /// </summary>
    public const long MaxImportBytes = 1L << 30;

    /// <summary>
    /// The sandboxed WebAssembly host (with libwasmtime.so beside it), or null when it isn't built/installed. Shipped
    /// in the app's trace/ folder; TERMINATOR_TRACE_HOST overrides it for testing a freshly built host.
    /// </summary>
    public static string? HostPath => FirstExisting(
        Environment.GetEnvironmentVariable("TERMINATOR_TRACE_HOST") ?? "",
        Path.Combine(AppContext.BaseDirectory, "trace", "gamesandbox"));

    /// <summary>Where door modules are cached.</summary>
    public static string CacheDir => DataDir("cache");

    /// <summary>The cached copy of a door module with this SHA-256 (lower-case hex).</summary>
    public static string CachedModulePath(string sha256) => Path.Combine(CacheDir, sha256 + ".wasm");

    /// <summary>The cached copy of a door asset with this SHA-256; modules read these by hash, never by name.</summary>
    public static string CachedAssetPath(string sha256) => Path.Combine(CacheDir, sha256 + ".bin");

    /// <summary>
    /// A module's own settings blob (trace_store_*). The id comes from the door, so it's cut down to plain
    /// characters first: it must name one file inside the store folder and nothing else.
    /// </summary>
    public static string StorePath(string id)
    {
        var safe = new string(id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(32).ToArray());
        if (safe.Length == 0)
            safe = "module";
        return Path.Combine(DataDir("store"), safe + ".bin");
    }

    /// <summary>Everything the cache holds: door modules and the assets they were sent with.</summary>
    private static FileInfo[] CachedFiles() => Directory.Exists(CacheDir)
        ? [.. new DirectoryInfo(CacheDir).GetFiles("*.wasm"), .. new DirectoryInfo(CacheDir).GetFiles("*.bin")]
        : [];

    /// <summary>Total size of the cache, in bytes.</summary>
    public static long GetCacheSize() => CachedFiles().Sum(f => f.Length);

    /// <summary>How many modules and assets are cached.</summary>
    public static int GetCachedModuleCount() => CachedFiles().Length;

    /// <summary>Whether path is one of paths (the files a running module is using).</summary>
    public static bool IsInUse(string path, IEnumerable<string?> paths) =>
        paths.Any(k => k != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(k), StringComparison.Ordinal));

    /// <summary>
    /// Deletes everything cached except the files in keep (whatever a running module is using: it may still read an
    /// asset), with the sidecars, and any sidecar left without its file.
    /// </summary>
    public static void ClearCache(params string?[] keep)
    {
        foreach (var file in CachedFiles().Select(f => f.FullName))
        {
            if (IsInUse(file, keep))
                continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            DeleteMeta(file);
        }
        if (!Directory.Exists(CacheDir))
            return;
        foreach (var meta in new DirectoryInfo(CacheDir).GetFiles("*.json"))
        {
            if (!File.Exists(meta.FullName[..^".json".Length]))
            {
                try { meta.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Removes the least recently used files until the cache fits limitMb, never the ones in keep: what was just
    /// stored, and whatever a running module is using. "Used" is the file's last write time, touched on every use.
    /// </summary>
    public static void EnforceCacheLimit(int limitMb, params string?[] keep)
    {
        if (!Directory.Exists(CacheDir) || limitMb <= 0)
            return;
        long limit = (long)limitMb * 1024 * 1024;
        // Files the player imported don't count and are never trimmed: they'd have to pick them again, and a door
        // can't send them back (that's the point). They go only when the player deletes them in Settings.
        var files = CachedFiles().Where(f => !IsImported(f.FullName)).OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total <= limit)
                break;
            if (IsInUse(f.FullName, keep))
                continue;
            try
            {
                long len = f.Length;
                f.Delete();
                total -= len;
                DeleteMeta(f.FullName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---- What each cached file is ----------------------------------------------------------------------------
    // The cache is by hash only, so beside each file sits a small sidecar, <file>.json (e.g. <sha256>.wasm.json):
    //   {"module":"doom","kind":"module"|"asset"|"imported","size":<bytes>,"bbs":"<board>",
    //    "firstCached":"2026-09-18T12:34:56Z","lastUsed":"2026-09-18T12:34:56Z"}
    // An imported file (the player's own copy, picked when a door asked for it) also has "name" (what the door
    // called it) and "grants": the boards the player allowed to use it (host:port), each asked once.
    // Written when a door's Open/Asset finds the file cached, and when an upload is stored. Files cached before
    // sidecars existed have none and list as "Unknown", with their size and file date.

    private sealed class CacheMeta
    {
        [JsonPropertyName("module")] public string? Module { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("bbs")] public string? Bbs { get; set; }
        [JsonPropertyName("firstCached")] public string? FirstCached { get; set; }
        [JsonPropertyName("lastUsed")] public string? LastUsed { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("grants")] public List<string>? Grants { get; set; }
    }

    /// <summary>
    /// One cached file for Settings: its door module id (null = Unknown, cached before sidecars), module or asset,
    /// size, the board it came from (null = not known) and when it was last used.
    /// </summary>
    public sealed record CacheEntry(string Path, bool IsAsset, long Size, string? ModuleId, string? Bbs,
                                    DateTime LastUsedUtc, bool Imported = false, string? Name = null,
                                    int GrantCount = 0);

    /// <summary>The sidecar describing a cached file.</summary>
    private static string MetaPath(string cachedPath) => cachedPath + ".json";

    private static string FormatTime(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTime? ParseTime(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture,
                          DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    private static CacheMeta? ReadMeta(string cachedPath)
    {
        try
        {
            string meta = MetaPath(cachedPath);
            return File.Exists(meta) ? JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(meta)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;   // unreadable: the file lists as Unknown until its next use rewrites it
        }
    }

    private static void DeleteMeta(string cachedPath)
    {
        try { File.Delete(MetaPath(cachedPath)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A door's module id (or the asset it sent for it) was just cached or found in the cache: record what it is and
    /// that it was used now. The board is kept from the first time it's known; the module id is the latest door's.
    /// </summary>
    public static void RecordUse(string cachedPath, string moduleId, bool isAsset, string? bbs)
    {
        try
        {
            var file = new FileInfo(cachedPath);
            if (!file.Exists)
                return;
            var meta = ReadMeta(cachedPath) ?? new CacheMeta();
            string now = FormatTime(DateTime.UtcNow);
            meta.Module = moduleId.Length > 64 ? moduleId[..64] : moduleId;
            if (meta.Kind != "imported")   // an imported file stays the player's, whoever uses it
                meta.Kind = isAsset ? "asset" : "module";
            meta.Size = file.Length;
            if (string.IsNullOrWhiteSpace(meta.Bbs) && !string.IsNullOrWhiteSpace(bbs))
                meta.Bbs = bbs.Trim();
            meta.FirstCached ??= now;
            meta.LastUsed = now;
            File.WriteAllText(MetaPath(cachedPath), JsonSerializer.Serialize(meta));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>What the cache holds, by module id (Unknown last), the module before its assets, then biggest first.</summary>
    public static IReadOnlyList<CacheEntry> ListCache() => CachedFiles()
        .Select(f =>
        {
            var meta = ReadMeta(f.FullName);
            string? id = string.IsNullOrWhiteSpace(meta?.Module) ? null : meta.Module;
            string? bbs = string.IsNullOrWhiteSpace(meta?.Bbs) ? null : meta.Bbs;
            return new CacheEntry(f.FullName, f.Extension.Equals(".bin", StringComparison.OrdinalIgnoreCase), f.Length,
                                  id, bbs, ParseTime(meta?.LastUsed) ?? f.LastWriteTimeUtc,
                                  meta?.Kind == "imported", meta?.Name, meta?.Grants?.Count ?? 0);
        })
        .OrderBy(e => e.ModuleId == null)
        .ThenBy(e => e.ModuleId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(e => e.IsAsset)
        .ThenByDescending(e => e.Size)
        .ToList();

    /// <summary>
    /// Deletes one cached file and its sidecar. Refused (false) when a running module is using it (inUse), or when
    /// path isn't a file in the cache.
    /// </summary>
    public static bool DeleteCached(string path, IEnumerable<string?> inUse)
    {
        var file = new FileInfo(path);
        if (!CachedFiles().Any(f => string.Equals(f.FullName, file.FullName, StringComparison.Ordinal))
            || IsInUse(file.FullName, inUse))
            return false;
        try
        {
            file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        DeleteMeta(file.FullName);
        return true;
    }

    // ---- Files the player imported (their own copy of a game or ROM) ----------------------------------------------

    /// <summary>Whether a cached file is one the player imported.</summary>
    public static bool IsImported(string cachedPath) => ReadMeta(cachedPath)?.Kind == "imported";

    /// <summary>Whether the player allowed this board (host:port) to use an imported file.</summary>
    public static bool IsGranted(string cachedPath, string bbsKey) =>
        ReadMeta(cachedPath)?.Grants?.Contains(bbsKey, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>What the door called an imported file when it was picked.</summary>
    public static string? ImportedName(string cachedPath) => ReadMeta(cachedPath)?.Name;

    /// <summary>
    /// Records a file the player just imported (name = what the door called it), or a board they just allowed to
    /// use one already imported.
    /// </summary>
    public static void RecordImport(string cachedPath, string name, string bbsKey, string? bbsName)
    {
        try
        {
            var file = new FileInfo(cachedPath);
            if (!file.Exists)
                return;
            var meta = ReadMeta(cachedPath) ?? new CacheMeta();
            string now = FormatTime(DateTime.UtcNow);
            meta.Kind = "imported";
            meta.Size = file.Length;
            meta.Name ??= name;
            meta.Grants ??= [];
            if (!meta.Grants.Contains(bbsKey, StringComparer.OrdinalIgnoreCase))
                meta.Grants.Add(bbsKey);
            if (string.IsNullOrWhiteSpace(meta.Bbs) && !string.IsNullOrWhiteSpace(bbsName))
                meta.Bbs = bbsName.Trim();
            meta.FirstCached ??= now;
            meta.LastUsed = now;
            File.WriteAllText(MetaPath(cachedPath), JsonSerializer.Serialize(meta));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Copies the player's file into the cache while working out its SHA-256, in one pass (a big file is read once).
    /// Returns the hash, and whether it was kept: only when it's one of expected (what the door asked for), stored
    /// as &lt;sha256&gt;.bin like any asset so a module reads it by hash. Null hash when the file couldn't be read or
    /// is bigger than MaxImportBytes.
    /// </summary>
    public static async Task<(string? Hash, bool Kept)> ImportFileAsync(string sourcePath, IReadOnlyCollection<string> expected)
    {
        string temp = Path.Combine(CacheDir, $"import-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(CacheDir);
            string hash;
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            {
                if (source.Length > MaxImportBytes)
                    return (null, false);
                await using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read));
                }
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            if (!expected.Contains(hash, StringComparer.OrdinalIgnoreCase))
                return (hash, false);
            File.Move(temp, CachedAssetPath(hash), overwrite: true);
            return (hash, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, false);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A door-supplied WebAssembly module, run by the sandboxed host from the cache.</summary>
    public static Module? WasmModule(string id, string sha256, bool exclusive)
    {
        string? host = HostPath;
        string path = CachedModulePath(sha256);
        if (host == null || !File.Exists(path))
            return null;
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);  // most recently used, for the cache limit
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        // Renders at the on-screen size, so allow up to a 4K-class window. The host is given the two places a module
        // may touch: the asset cache (read-only, by hash) and its own settings blob.
        string store = StorePath(id);
        try { Directory.CreateDirectory(Path.GetDirectoryName(store)!); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return new Module(id, 2, host, 3840, 2400, exclusive,
                          () => ["-module", path, "-assets", CacheDir, "-store", store], path);
    }

    /// <summary>
    /// What this TERMinator can run, for the reply to a door's TRACE;Query. A door reads the flags it cares about and
    /// picks a module to match: wasm (it can run a module a door sends), audio, assets (big files by hash), send
    /// (the module can answer the door), store (settings that survive), tick (steady updates for a game), gz (it takes
    /// gzip uploads: Put ... enc=gz, TraceGzip), paste (Ctrl+V and the door's Paste give a module the clipboard),
    /// url (the door's OpenUrl opens a web link the player clicked in a module), keep (1.1.6: a hidden module keeps running
    /// for the board, TraceSessionManager.Hide).
    /// Empty when TRACE is switched off in Preferences, and then the Query goes unanswered, as on a terminal
    /// without TRACE.
    /// </summary>
    public static string Capabilities(bool enabled) =>
        !enabled ? "" : HostPath == null ? "wasm=0"
            : "wasm=1;audio=1;assets=1;send=1;store=1;tick=1;text=1;mouse=1;pad=1;import=1;bin=1;gz=1;paste=1;url=1;keep=1" + (GpuInstalled ? ";gpu=1;gpu2=1" : "");

    /// <summary>
    /// Whether modules can use the graphics card: the sandbox host draws with WebGPU (libwgpu_native.so, beside it).
    /// Without the library the host still runs and a module falls back to the CPU, so doors check for gpu=1.
    /// gpu2=1: the host has trace_gpu.h version 2 (mipmaps, indirect draws, several targets, pass timings, a present
    /// that doesn't wait), which a module importing those calls needs; it comes with this TERMinator's own host.
    /// </summary>
    public static bool GpuInstalled =>
        HostPath != null && File.Exists(Path.Combine(Path.GetDirectoryName(HostPath)!, "libwgpu_native.so"));

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(p => p.Length > 0 && File.Exists(p));
}
