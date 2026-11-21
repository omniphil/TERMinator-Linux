using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Services;

/// <summary>
/// Handles audio playback: bell sounds, MP3/WAV/OGG via OpenAL Soft native bridge,
/// MOD/tracker via native libxmp.
/// Full OSC 800 Terminal Audio Protocol (TAP) support including remote downloading,
/// caching, pause, resume, fade, and cache management.
/// Supports 4 audio channels (0-3). Channel 0 supports all formats and feeds the visualizer.
/// Channels 1-3 support streaming formats only (MP3/WAV/OGG).
/// </summary>
public class AudioService : IDisposable
{
    private const string CacheDirName = "soundtracks";
    private const int MaxUrlLength = 2048;
    private const int MaxFilenameLength = 128;
    private const long MaxFileSize = 50L * 1024 * 1024; // 50MB per file
    private const int DefaultCacheLimitMb = 250;
    private const int DownloadTimeoutMs = 15000;
    private const float MasterGain = 0.85f;
    private const int ChannelCount = 4;
    private const int MaxConcurrentDownloads = 4;
    private const int PendingPlayWaitMs = 60000;  // a play waits this long for a file that is queued or downloading
    private const int UnknownPlayWaitMs = 10000;  // ...and this long for one no download is known for
    // A missing file whose download fails (stall, dropped connection, 5xx) is tried again after these delays
    private static readonly int[] RetryDelaysSec = [30, 60, 120];

    private static readonly HashSet<string> AllowedExtensions =
        [".mp3", ".wav", ".ogg", ".mod", ".s3m", ".xm", ".it", ".mp4", ".m4v", ".webm", ".jpg", ".jpeg", ".png", ".gif", ".webp"];
    private static readonly HashSet<string> TrackerExtensions =
        [".mod", ".s3m", ".xm", ".it"];
    private static readonly HashSet<string> AudioExtensions =
        [".mp3", ".wav", ".ogg"];
    private static readonly HashSet<string> VideoExtensions =
        [".mp4", ".m4v", ".webm"];
    private static readonly HashSet<string> ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp"];
    private static readonly Regex FilenameRegex = new(@"^[a-zA-Z0-9._\- ]+$");

    private readonly string _cacheDir;
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;
    private readonly AudioChannel[] _channels = new AudioChannel[ChannelCount];
    private bool _disposed;

    // Download queue (CacheFile / PumpDownloads), all under _dlLock
    private readonly object _dlLock = new();
    private readonly LinkedList<DownloadJob> _missingQueue = new();    // not cached yet: first
    private readonly LinkedList<DownloadJob> _freshnessQueue = new();  // cached: check for a newer copy
    private readonly HashSet<string> _queuedDownloads = new(StringComparer.OrdinalIgnoreCase); // queued, running or awaiting retry
    private readonly Dictionary<string, string> _urlByFilename = new(StringComparer.OrdinalIgnoreCase);
    private int _activeDownloads;
    private readonly Dictionary<string, DownloadJob> _retryWaiting = new(StringComparer.OrdinalIgnoreCase); // failed, retry timer running

    // Maps TAP display names to actual cached filenames
    private readonly Dictionary<string, string> _displayNameMap = new(StringComparer.OrdinalIgnoreCase);

    // MOD render loop
    private CancellationTokenSource? _modRenderCts;
    private Task? _modRenderTask;

    // Analyzer feed timer for streaming playback
    private System.Threading.Timer? _analyzerTimer;

    public AudioAnalyzer AudioAnalyzer { get; } = new();

    public float Volume
    {
        get => _channels[0].Volume;
        set => ApplyVolume(Math.Clamp(value, 0f, 1f), 0);
    }

    public bool IsPlaying => _channels[0].Playing;

    public AudioService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        float initialVolume = settingsService.Settings.ModPlayerVolume;

        _cacheDir = Path.Combine(AppSettings.GetAppDataDir(), CacheDirName);
        Directory.CreateDirectory(_cacheDir);

        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // DownloadAsync runs its own stall timeout

        for (int i = 0; i < ChannelCount; i++)
            _channels[i] = new AudioChannel { Volume = initialVolume };

        // Start analyzer polling timer (~23ms = ~43Hz)
        _analyzerTimer = new System.Threading.Timer(PollAnalyzerSnapshot, null, 100, 23);
    }

    public void PlayBell()
    {
        if (!_settingsService.Settings.PlayBellSound) return;
        var settings = _settingsService.Settings;
        float volume = Math.Clamp(settings.BellVolume / 10f, 0f, 1f);
        App.BellService.PlayBell(settings.BellSoundIndex, volume);
    }

    /// <summary>
    /// Handle an OSC 800 audio command from the BBS.
    /// Format: "command;param1;param2;..."
    /// Returns true if handled, false if the command should be routed elsewhere (e.g. video/image).
    /// </summary>
    public bool HandleAudioCommand(string command)
    {
        if (!_settingsService.Settings.SoundtrackEnabled) return true;

        var parts = command.Split(';');
        if (parts.Length == 0) return true;

        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "cache":
                    if (parts.Length >= 3)
                        CacheFile(parts[1], parts[2]);
                    break;
                case "play":
                    if (parts.Length >= 2)
                    {
                        bool loop = parts.Length >= 3 && parts[2] == "1";
                        int ch = 0;
                        if (parts.Length >= 4 && int.TryParse(parts[3], out int parsedCh))
                            ch = Math.Clamp(parsedCh, 0, ChannelCount - 1);
                        var playChan = _channels[ch];
                        playChan.Queue.Clear();
                        playChan.QueueIndex = 0;
                        playChan.QueueDelayCts?.Cancel();
                        playChan.QueueDelayCts = null;
                        Play(parts[1], loop, ch);
                    }
                    break;
                case "queue":
                    if (parts.Length >= 4)
                    {
                        int qCh = 0;
                        if (int.TryParse(parts[2], out int parsedQCh))
                            qCh = Math.Clamp(parsedQCh, 0, ChannelCount - 1);
                        int delay = 3;
                        if (int.TryParse(parts[3], out int parsedDelay))
                            delay = Math.Max(0, parsedDelay);
                        _channels[qCh].Queue.Add((parts[1], delay));
                        Debug.WriteLine($"AudioService: Queued '{parts[1]}' on ch{qCh} (delay={delay}s, queue={_channels[qCh].Queue.Count})");
                    }
                    break;
                case "stop":
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int stopCh))
                        StopChannel(Math.Clamp(stopCh, 0, ChannelCount - 1));
                    else
                        StopAll();
                    break;
                case "pause":
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int pauseCh))
                        Pause(Math.Clamp(pauseCh, 0, ChannelCount - 1));
                    else
                        for (int i = 0; i < ChannelCount; i++) Pause(i);
                    break;
                case "resume":
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int resumeCh))
                        Resume(Math.Clamp(resumeCh, 0, ChannelCount - 1));
                    else
                        for (int i = 0; i < ChannelCount; i++) Resume(i);
                    break;
                case "volume":
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int vol))
                    {
                        if (parts.Length >= 3 && int.TryParse(parts[2], out int volCh))
                            SetVolume(vol, Math.Clamp(volCh, 0, ChannelCount - 1));
                        else
                            for (int i = 0; i < ChannelCount; i++) SetVolume(vol, i);
                    }
                    break;
                case "fade":
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[1], out int targetVol) &&
                        long.TryParse(parts[2], out long durationMs))
                    {
                        if (parts.Length >= 4 && int.TryParse(parts[3], out int fadeCh))
                            Fade(targetVol, durationMs, Math.Clamp(fadeCh, 0, ChannelCount - 1));
                        else
                            for (int i = 0; i < ChannelCount; i++) Fade(targetVol, durationMs, i);
                    }
                    break;
                case "seek":
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int seekMs))
                    {
                        int seekCh = 0;
                        if (parts.Length >= 3 && int.TryParse(parts[2], out int parsedSeekCh))
                            seekCh = Math.Clamp(parsedSeekCh, 0, ChannelCount - 1);
                        Seek(seekMs, seekCh);
                    }
                    break;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioService: Error handling command '{command}': {ex.Message}");
        }
        return true;
    }

    /// <summary>
    /// Queue a file from a URL for the cache.
    /// The display name from TAP is used as a key; the actual filename from the URL is used for storage.
    /// Downloads go through a small queue (<see cref="MaxConcurrentDownloads"/> at a time): a door can send a hundred
    /// cache commands in one burst (Skyraiders sends ~127), and starting them all at once made most time out.
    /// </summary>
    private void CacheFile(string url, string displayName)
    {
        if (!IsValidUrl(url))
        {
            Debug.WriteLine($"AudioService: Invalid URL rejected: {url}");
            return;
        }

        // Extract the real filename from the URL (e.g. "AxelF.mod" from ".../AxelF.mod")
        string realFilename;
        try
        {
            realFilename = Path.GetFileName(new Uri(url).AbsolutePath);
        }
        catch
        {
            Debug.WriteLine($"AudioService: Could not extract filename from URL: {url}");
            return;
        }

        var safeFilename = SanitizeFilename(realFilename);
        if (safeFilename == null)
        {
            Debug.WriteLine($"AudioService: Invalid filename from URL rejected: {realFilename}");
            return;
        }

        // Map the display name to the real cached filename
        if (!string.IsNullOrEmpty(displayName))
            _displayNameMap[displayName] = safeFilename;

        var targetFile = Path.Combine(_cacheDir, safeFilename);
        bool cached = File.Exists(targetFile);
        lock (_dlLock)
        {
            _urlByFilename[safeFilename] = url; // lets Play retry a download that failed
            if (!_queuedDownloads.Add(safeFilename))
                return; // already queued or downloading
            var job = new DownloadJob(url, safeFilename, targetFile, freshnessOnly: cached);
            // Missing files go before freshness checks of files we already have
            (cached ? _freshnessQueue : _missingQueue).AddLast(job);
        }
        PumpDownloads();
    }

    /// <summary>Starts queued downloads while there are free slots; missing files first.</summary>
    private void PumpDownloads()
    {
        lock (_dlLock)
        {
            while (!_disposed && _activeDownloads < MaxConcurrentDownloads)
            {
                var queue = _missingQueue.Count > 0 ? _missingQueue : _freshnessQueue;
                if (queue.Count == 0) break;
                var job = queue.First!.Value;
                queue.RemoveFirst();
                _activeDownloads++;
                _ = Task.Run(() => RunDownload(job));
            }
        }
    }

    private async Task RunDownload(DownloadJob job)
    {
        bool retryable = false;
        try
        {
            retryable = await DownloadAsync(job) == DownloadResult.RetryableFailure;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"AudioService: Download stalled (no data for {DownloadTimeoutMs / 1000}s): {job.SafeFilename}");
            retryable = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioService: Download error for {job.Url}: {ex.Message}");
            retryable = true;
        }
        finally
        {
            // Only files we don't have are retried; a failed freshness check just leaves the cached copy in place
            bool retry = retryable && !job.FreshnessOnly && job.Attempt < RetryDelaysSec.Length && !_disposed;
            lock (_dlLock)
            {
                _activeDownloads--;
                if (retry)
                    _retryWaiting[job.SafeFilename] = job; // stays in _queuedDownloads: still pending
                else
                    _queuedDownloads.Remove(job.SafeFilename);
            }
            if (retry)
                _ = RetryLater(job);
            PumpDownloads();
        }
    }

    /// <summary>Puts a failed download back in the queue after its back-off delay, unless a play already did.</summary>
    private async Task RetryLater(DownloadJob job)
    {
        int delaySec = RetryDelaysSec[job.Attempt];
        job.Attempt++;
        int attempt = job.Attempt; // a later failure schedules its own timer; this one must not act for it
        Debug.WriteLine($"AudioService: Will retry {job.SafeFilename} in {delaySec}s (retry {job.Attempt} of {RetryDelaysSec.Length})");
        await Task.Delay(TimeSpan.FromSeconds(delaySec)).ConfigureAwait(false);
        lock (_dlLock)
        {
            if (_disposed || job.Attempt != attempt || !_retryWaiting.Remove(job.SafeFilename))
                return; // a play pulled it forward already (and it may have failed again since)
            _missingQueue.AddLast(job);
        }
        PumpDownloads();
    }

    /// <summary>True while a file is waiting in the download queue or downloading.</summary>
    private bool IsDownloadPending(string safeFilename)
    {
        lock (_dlLock) return _queuedDownloads.Contains(safeFilename);
    }

    /// <summary>
    /// A play wants a file that isn't cached: move it to the front of the download queue, or queue it again if an
    /// earlier download of it failed. Returns true if it is now queued or downloading.
    /// </summary>
    private bool RequestDownloadNow(string safeFilename)
    {
        lock (_dlLock)
        {
            for (var node = _missingQueue.First; node != null; node = node.Next)
            {
                if (!string.Equals(node.Value.SafeFilename, safeFilename, StringComparison.OrdinalIgnoreCase)) continue;
                _missingQueue.Remove(node);
                _missingQueue.AddFirst(node);
                Debug.WriteLine($"AudioService: Moved {safeFilename} to the front of the download queue");
                return true;
            }
            if (_retryWaiting.Remove(safeFilename, out var waiting))
            {
                _missingQueue.AddFirst(waiting); // a play wants it now: skip the rest of its back-off
                Debug.WriteLine($"AudioService: Retrying {safeFilename} now (a play wants it)");
            }
            else if (_queuedDownloads.Contains(safeFilename))
                return true; // downloading right now
            else
            {
                if (!_urlByFilename.TryGetValue(safeFilename, out var url))
                    return false; // never cached by the board
                _queuedDownloads.Add(safeFilename);
                _missingQueue.AddFirst(new DownloadJob(url, safeFilename, Path.Combine(_cacheDir, safeFilename), freshnessOnly: false));
                Debug.WriteLine($"AudioService: Re-queued failed download at the front: {safeFilename}");
            }
        }
        PumpDownloads();
        return true;
    }

    /// <summary>
    /// Downloads one file to the cache (or, for a file already cached, fetches it only if the server has a newer
    /// copy, via If-Modified-Since). The timeout is a stall timeout: it restarts with every block received, so a big
    /// file on a slow line still finishes.
    /// </summary>
    private async Task<DownloadResult> DownloadAsync(DownloadJob job)
    {
        using var stall = new CancellationTokenSource(DownloadTimeoutMs);
        using var request = new HttpRequestMessage(HttpMethod.Get, job.Url);
        if (job.FreshnessOnly)
            request.Headers.IfModifiedSince = new DateTimeOffset(File.GetLastWriteTimeUtc(job.TargetFile));

        Debug.WriteLine($"AudioService: {(job.FreshnessOnly ? "Checking for newer" : "Downloading")}: {job.Url}");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);

        if (job.FreshnessOnly && response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            Debug.WriteLine($"AudioService: Cache up-to-date: {job.SafeFilename}");
            return DownloadResult.Done;
        }
        if (!response.IsSuccessStatusCode)
        {
            Debug.WriteLine($"AudioService: Download failed: HTTP {(int)response.StatusCode} {response.StatusCode} for {job.Url}");
            // Server trouble or throttling may pass; a 404 and the like won't
            int code = (int)response.StatusCode;
            return code >= 500 || code == 408 || code == 429 ? DownloadResult.RetryableFailure : DownloadResult.PermanentFailure;
        }
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaxFileSize)
        {
            Debug.WriteLine($"AudioService: File too large: {contentLength} bytes (max: {MaxFileSize})");
            return DownloadResult.PermanentFailure;
        }

        var tempFile = Path.Combine(_cacheDir, job.SafeFilename + ".tmp");
        long totalBytes = 0;
        bool complete = false;
        try
        {
            await using (var stream = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[65536];
                while (true)
                {
                    stall.CancelAfter(DownloadTimeoutMs);
                    int bytesRead = await stream.ReadAsync(buffer, stall.Token);
                    if (bytesRead == 0) break;
                    totalBytes += bytesRead;
                    if (totalBytes > MaxFileSize)
                    {
                        Debug.WriteLine($"AudioService: Download exceeded size limit, aborting: {job.SafeFilename}");
                        return DownloadResult.PermanentFailure;
                    }
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), stall.Token);
                }
            }

            File.Move(tempFile, job.TargetFile, overwrite: true);
            complete = true;
            // Stamp the file with the server's Last-Modified: the next visit's If-Modified-Since sends it back, and
            // nginx (if_modified_since exact, its default) answers 304 only to that exact date. With the download
            // time instead, every cached file was downloaded again on every visit.
            if (response.Content.Headers.LastModified is { } serverModified)
            {
                try { File.SetLastWriteTimeUtc(job.TargetFile, serverModified.UtcDateTime); }
                catch { /* the file still works, it's just re-checked in full next time */ }
            }
            Debug.WriteLine($"AudioService: {(job.FreshnessOnly ? "Updated" : "Cached")}: {job.SafeFilename} ({totalBytes} bytes)");
        }
        finally
        {
            if (!complete)
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }
        if (!job.FreshnessOnly)
            PruneCache();
        return DownloadResult.Done;
    }

    private enum DownloadResult { Done, RetryableFailure, PermanentFailure }

    private sealed class DownloadJob(string url, string safeFilename, string targetFile, bool freshnessOnly)
    {
        public string Url { get; } = url;
        public string SafeFilename { get; } = safeFilename;
        public string TargetFile { get; } = targetFile;
        public bool FreshnessOnly { get; } = freshnessOnly; // already cached: only replace it if the server's is newer
        public int Attempt;                                  // retries used so far (see RetryDelaysSec)
    }

    /// <summary>
    /// Play a cached audio file by filename on the specified channel.
    /// If the file isn't cached yet (still queued or downloading), it waits for it.
    /// </summary>
    private void Play(string filename, bool loop, int channel = 0)
    {
        // Any wait for an earlier play on this channel is now stale
        int generation = ++_channels[channel].PlayGeneration;

        // First try the display name map (TAP sends display names like "Axel F")
        if (_displayNameMap.TryGetValue(filename, out var mapped))
        {
            Debug.WriteLine($"AudioService: Resolved display name '{filename}' -> '{mapped}'");
            filename = mapped;
        }

        var safeFilename = SanitizeFilename(filename);
        if (safeFilename == null)
        {
            // Last resort -- search cache for a matching file
            safeFilename = FindCachedFile(filename);
            if (safeFilename == null)
            {
                Debug.WriteLine($"AudioService: No cached file found for: {filename}");
                return;
            }
        }

        var file = Path.Combine(_cacheDir, safeFilename);

        // Not cached yet: pull it to the front of the download queue and wait for it
        if (!File.Exists(file))
        {
            bool pending = RequestDownloadNow(safeFilename);
            int waitMs = pending ? PendingPlayWaitMs : UnknownPlayWaitMs;
            Debug.WriteLine($"AudioService: File not cached yet, waiting up to {waitMs / 1000}s: {safeFilename}" +
                              (pending ? "" : " (no download known for it)"));
            _ = WaitAndPlay(file, safeFilename, loop, channel, generation, waitMs);
            return;
        }

        PlayInternal(file, safeFilename, loop, channel);
    }

    private async Task WaitAndPlay(string file, string safeFilename, bool loop, int channel, int generation, int waitMs)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < waitMs)
        {
            await Task.Delay(250);
            if (_disposed) return;
            if (_channels[channel].PlayGeneration != generation)
            {
                Debug.WriteLine($"AudioService: Dropped wait for {safeFilename}: ch{channel} got a newer play/stop");
                return;
            }
            if (File.Exists(file))
            {
                Debug.WriteLine($"AudioService: File ready after {Environment.TickCount64 - started}ms: {safeFilename}");
                if (_channels[channel].PlayGeneration == generation)
                    PlayInternal(file, safeFilename, loop, channel);
                return;
            }
            // Its download failed while we waited: there is nothing left to wait for
            if (!IsDownloadPending(safeFilename) && waitMs == PendingPlayWaitMs)
            {
                Debug.WriteLine($"AudioService: Gave up on {safeFilename}: its download failed");
                return;
            }
        }
        Debug.WriteLine($"AudioService: Timed out waiting for file: {safeFilename}");
    }

    private string? FindCachedFile(string baseName)
    {
        if (string.IsNullOrEmpty(baseName) || baseName.Length > MaxFilenameLength) return null;
        foreach (var ext in AllowedExtensions)
        {
            var candidate = baseName + ext;
            if (SanitizeFilename(candidate) != null && File.Exists(Path.Combine(_cacheDir, candidate)))
                return candidate;
        }
        return null;
    }

    private void PlayInternal(string file, string safeFilename, bool loop, int channel)
    {
        var ch = _channels[channel];
        StopChannelInternal(channel);

        var ext = Path.GetExtension(safeFilename).ToLowerInvariant();

        // Skip video/image files — visual media handled by TerminalView
        if (VideoExtensions.Contains(ext) || ImageExtensions.Contains(ext))
            return;

        // Tracker formats only on channel 0
        if (TrackerExtensions.Contains(ext))
        {
            if (channel != 0)
            {
                Debug.WriteLine($"AudioService: Tracker formats only supported on channel 0, ignoring on channel {channel}");
                return;
            }
            PlayMod(file, loop);
        }
        else if (AudioExtensions.Contains(ext))
        {
            PlayStreaming(file, loop, channel);
        }

        ch.CurrentFilename = safeFilename;
        ch.Playing = true;
        ch.IsPaused = false;
    }

    /// <summary>
    /// Play a file directly by path (for local file playback, always channel 0).
    /// </summary>
    public void PlayFile(string filePath)
    {
        StopChannelInternal(0);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (TrackerExtensions.Contains(ext))
            PlayMod(filePath, false);
        else if (AudioExtensions.Contains(ext))
            PlayStreaming(filePath, false, 0);

        _channels[0].CurrentFilename = Path.GetFileName(filePath);
        _channels[0].Playing = true;
        _channels[0].IsPaused = false;
    }

    private void PlayMod(string filePath, bool loop)
    {
        // One native libxmp player: a BBS tune takes it over from the About box. The pending count keeps
        // PlayAboutMusic from loading in the gap before this tune's render loop is running.
        Interlocked.Increment(ref _modLoadsPending);
        StopAboutMusic();
        var ch = _channels[0];
        Debug.WriteLine($"AudioService: Playing MOD on ch0: {filePath} (volume={ch.Volume})");
        if (NativeBridge.syncterm_mod_load(filePath) != 1)
        {
            Debug.WriteLine("AudioService: syncterm_mod_load failed");
            Interlocked.Decrement(ref _modLoadsPending);
            return;
        }

        // Use only the libxmp software volume multiplier for MOD playback.
        // Do not set device-level volume — audio_bridge_queue_pcm applies per-source
        // gain from ch->volume, so keep native ch->volume at 1.0 to avoid double-application.
        NativeBridge.audio_bridge_set_volume(0, 1.0f);
        NativeBridge.syncterm_mod_set_volume(ToAmplitude(ch.Volume));

        // Start MOD render loop in a background task
        _modRenderCts = new CancellationTokenSource();
        var ct = _modRenderCts.Token;

        _modRenderTask = Task.Run(() =>
        {
            var pcmBuffer = new short[4096 * 2]; // stereo
            int pending = 0; // frames rendered but not yet queued
            while (!ct.IsCancellationRequested)
            {
                if (pending == 0)
                {
                    int framesRead = NativeBridge.syncterm_mod_get_pcm(pcmBuffer, 4096);
                    if (framesRead <= 0)
                    {
                        if (loop)
                        {
                            NativeBridge.syncterm_mod_restart();
                            continue;
                        }
                        // Module ended: let what's already queued play out before advancing
                        while (!ct.IsCancellationRequested && NativeBridge.audio_bridge_is_playing(0) != 0)
                            Thread.Sleep(20);
                        if (ct.IsCancellationRequested) return;
                        ch.Playing = false;
                        ch.CurrentFilename = null;
                        AudioAnalyzer.Reset();
                        PlayNextFromQueue(0);
                        return;
                    }

                    // Feed audio analyzer for visualizer
                    AudioAnalyzer.FeedPcm(pcmBuffer, framesRead);
                    pending = framesRead;
                }

                // Queue PCM onto OpenAL gapless. (audio_bridge_play_pcm restarts the source on every call, which
                // cut each ~93 ms chunk off after a few ms and played tracker tunes chopped and fast.)
                if (NativeBridge.audio_bridge_queue_pcm(0, pcmBuffer, pending, 44100, 2) == 1)
                    pending = 0;
                else
                    Thread.Sleep(20); // queue full (about 370 ms ahead): let some of it play
            }
        }, ct);
        Interlocked.Decrement(ref _modLoadsPending);
    }

    private void PlayStreaming(string filePath, bool loop, int channel)
    {
        var ch = _channels[channel];
        Debug.WriteLine($"AudioService: Playing streaming on ch{channel}: {filePath} (volume={ch.Volume}, loop={loop})");

        // Sync per-stream software volume to native channel before starting decode thread.
        // The decode thread reads ch->volume for per-source gain — no device-level volume is set.
        NativeBridge.audio_bridge_set_volume(channel, ToAmplitude(ch.Volume));
        NativeBridge.audio_bridge_play_file(channel, filePath, loop ? 1 : 0);

        // Monitor for playback completion to trigger queue advance
        ch.MonitorCts?.Cancel();
        ch.MonitorCts = new CancellationTokenSource();
        var ct = ch.MonitorCts.Token;
        _ = Task.Run(async () =>
        {
            // Give time for playback to start
            await Task.Delay(500, ct);
            while (!ct.IsCancellationRequested)
            {
                if (NativeBridge.audio_bridge_is_playing(channel) == 0)
                {
                    ch.Playing = false;
                    ch.CurrentFilename = null;
                    PlayNextFromQueue(channel);
                    return;
                }
                await Task.Delay(250, ct);
            }
        }, ct);
    }

    public void StopAll()
    {
        for (int i = 0; i < ChannelCount; i++)
        {
            _channels[i].FadeCts?.Cancel();
            _channels[i].FadeCts = null;
            _channels[i].Queue.Clear();
            _channels[i].QueueIndex = 0;
            _channels[i].QueueDelayCts?.Cancel();
            _channels[i].QueueDelayCts = null;
            _channels[i].PlayGeneration++;
            StopChannelInternal(i);
        }
        AudioAnalyzer.Reset();
    }

    public void StopChannel(int channel)
    {
        _channels[channel].FadeCts?.Cancel();
        _channels[channel].FadeCts = null;
        _channels[channel].Queue.Clear();
        _channels[channel].QueueIndex = 0;
        _channels[channel].QueueDelayCts?.Cancel();
        _channels[channel].QueueDelayCts = null;
        _channels[channel].PlayGeneration++;
        StopChannelInternal(channel);
    }

    private void StopChannelInternal(int channel)
    {
        var ch = _channels[channel];

        // Cancel playback monitor
        ch.MonitorCts?.Cancel();
        ch.MonitorCts = null;

        if (channel == 0)
        {
            // Stop MOD render loop before the source, so it can't queue another chunk and restart it
            _modRenderCts?.Cancel();
            try { _modRenderTask?.Wait(1000); } catch { /* ignore */ }
            _modRenderCts = null;
            _modRenderTask = null;
        }

        // Stop native audio
        NativeBridge.audio_bridge_stop(channel);

        if (channel == 0)
        {
            NativeBridge.syncterm_mod_stop();
            AudioAnalyzer.Reset();
        }

        ch.Playing = false;
        ch.IsPaused = false;
        ch.CurrentFilename = null;
    }

    private void PlayNextFromQueue(int channel)
    {
        var ch = _channels[channel];
        if (ch.Queue.Count == 0) return;

        var (filename, delaySec) = ch.Queue[ch.QueueIndex];
        ch.QueueIndex = (ch.QueueIndex + 1) % ch.Queue.Count;

        Debug.WriteLine($"AudioService: Auto-advance ch{channel} -> '{filename}' in {delaySec}s");

        ch.QueueDelayCts?.Cancel();
        ch.QueueDelayCts = new CancellationTokenSource();
        var token = ch.QueueDelayCts.Token;

        Task.Run(async () =>
        {
            try
            {
                if (delaySec > 0)
                    await Task.Delay(delaySec * 1000, token);

                if (!token.IsCancellationRequested)
                    Play(filename, false, channel);
            }
            catch (OperationCanceledException) { /* queue was cleared */ }
        }, token);
    }

    private void Pause(int channel)
    {
        var ch = _channels[channel];
        if (!ch.Playing || ch.IsPaused) return;
        NativeBridge.audio_bridge_pause(channel);
        ch.IsPaused = true;
    }

    private void Resume(int channel)
    {
        var ch = _channels[channel];
        if (!ch.IsPaused) return;
        NativeBridge.audio_bridge_resume(channel);
        ch.IsPaused = false;
    }

    private void Seek(int positionMs, int channel)
    {
        var pos = Math.Max(0, positionMs);
        try
        {
            if (channel == 0 && NativeBridge.syncterm_mod_is_playing() != 0)
            {
                NativeBridge.syncterm_mod_seek(pos);
                // Drop the pre-seek audio still queued on the source (a stop resets the native paused flag, so
                // not while paused)
                if (!_channels[0].IsPaused)
                    NativeBridge.audio_bridge_stop(0);
                Debug.WriteLine($"AudioService: ch0 MOD seek to {pos}ms");
            }
            else
            {
                NativeBridge.audio_bridge_seek(channel, pos);
                Debug.WriteLine($"AudioService: ch{channel} seek to {pos}ms");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioService: ch{channel} seek error: {ex.Message}");
        }
    }

    private void SetVolume(int volumePercent, int channel)
    {
        ApplyVolume(Math.Clamp(volumePercent, 0, 100) / 100f, channel);
    }

    private void ApplyVolume(float vol, int channel)
    {
        var ch = _channels[channel];
        ch.Volume = Math.Clamp(vol, 0f, 1f);
        float amplitude = ToAmplitude(ch.Volume);
        try
        {
            // Only use per-stream software volume multipliers, never device-level volume,
            // so channels don't bleed into each other. (A module playing for the About box isn't ch0's; ch0 may be
            // streaming under it.)
            if (channel == 0 && NativeBridge.syncterm_mod_is_playing() != 0 && !AboutMusicPlaying)
            {
                // MOD playback: use libxmp software volume only (PCM amplitude scaling).
                // Do NOT set audio_bridge volume — audio_bridge_queue_pcm would double-apply.
                NativeBridge.syncterm_mod_set_volume(amplitude);
            }
            else
            {
                // Streaming playback: use per-source OpenAL gain only.
                NativeBridge.audio_bridge_set_volume(channel, amplitude);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioService: ch{channel} volume error: {ex.Message}");
        }
    }

    private static float ToAmplitude(float linear)
    {
        if (linear <= 0f) return 0f;
        return Math.Min(linear * MasterGain, 1f);
    }

    private void Fade(int targetVolPercent, long durationMs, int channel)
    {
        if (durationMs <= 0)
        {
            SetVolume(targetVolPercent, channel);
            return;
        }

        var ch = _channels[channel];
        ch.FadeCts?.Cancel();
        ch.FadeCts = new CancellationTokenSource();
        var ct = ch.FadeCts.Token;

        _ = Task.Run(async () =>
        {
            float targetVol = Math.Clamp(targetVolPercent, 0, 100) / 100f;
            float startVol = ch.Volume;
            int steps = (int)Math.Clamp(durationMs / 50, 1, 200);
            long stepDelay = durationMs / steps;
            float volStep = (targetVol - startVol) / steps;

            for (int i = 1; i <= steps; i++)
            {
                if (ct.IsCancellationRequested) return;
                ApplyVolume(startVol + volStep * i, channel);
                await Task.Delay((int)stepDelay, ct);
            }
            ApplyVolume(targetVol, channel);

            if (targetVol <= 0f)
                StopChannel(channel);
        }, ct);
    }

    // ================================================================
    // Analyzer snapshot polling (for streaming file playback)
    // ================================================================

    private void PollAnalyzerSnapshot(object? state)
    {
        if (_disposed) return;

        // Only poll for non-MOD streaming on channel 0
        var ch = _channels[0];
        if (!ch.Playing || ch.IsPaused) return;

        // Skip if MOD is playing (MOD feeds analyzer directly) -- unless it's the About music, which never does
        if (NativeBridge.syncterm_mod_is_playing() != 0 && !AboutMusicPlaying) return;

        try
        {
            var buf = new short[1024 * 2]; // stereo
            int frames = NativeBridge.audio_bridge_get_pcm_snapshot(0, buf, 1024);
            if (frames > 0)
                AudioAnalyzer.FeedPcm(buf, frames);
        }
        catch { /* ignore snapshot errors */ }
    }

    // ================================================================
    // About box music
    // ================================================================

    private const string AboutMusicResource = "avares://TERMinator/Assets/Music/ELYSIUM.MOD";
    private const float AboutMusicGain = 0.4f; // 60% quieter than a BBS tune at the same setting
    private const int AboutMusicChannel = 5;   // audio_bridge's own source, apart from TAP channels 0-3 and the bell (4)
    private const int AboutMusicChunkFrames = 4096;

    // Everything that touches the native libxmp player or channel 5 for the About music happens under this lock
    private readonly object _aboutLock = new();
    private object? _aboutPlayback;  // this playback's token while the About music holds the player; null otherwise
    private int _modLoadsPending;    // BBS tracker tunes between the start of PlayMod and their render loop running

    private bool AboutMusicPlaying => Volatile.Read(ref _aboutPlayback) != null;

    /// <summary>
    /// Play the About box's module (Elysium by Jester of Sanity) once through, no looping, not fed to the visualizer.
    /// Skipped while a BBS tracker tune is playing on ch0 -- libxmp is a single native player, so loading this would
    /// cut the BBS off.
    /// </summary>
    public void PlayAboutMusic()
    {
        StopAboutMusic();

        lock (_aboutLock)
        {
            if (Volatile.Read(ref _modLoadsPending) != 0 || _modRenderTask is { IsCompleted: false }) return;

            try
            {
                // libxmp loads from a path, so the embedded module is written out once next to the settings
                string path = Path.Combine(AppSettings.GetAppDataDir(), "about_music.mod");
                using var module = new MemoryStream();
                using (var res = AssetLoader.Open(new Uri(AboutMusicResource)))
                    res.CopyTo(module);
                if (!File.Exists(path) || new FileInfo(path).Length != module.Length)
                    File.WriteAllBytes(path, module.ToArray());

                if (NativeBridge.syncterm_mod_load(path) != 1)
                {
                    Debug.WriteLine("AudioService: About music load failed");
                    return;
                }

                // Same volume path as a BBS tune (PlayMod: libxmp's volume only), then AboutMusicGain as the
                // channel's OpenAL gain, which scales the samples the same way
                NativeBridge.syncterm_mod_set_volume(ToAmplitude(_settingsService.Settings.ModPlayerVolume));
                NativeBridge.audio_bridge_set_volume(AboutMusicChannel, AboutMusicGain);

                var token = new object();
                _aboutPlayback = token;
                _ = Task.Run(() => AboutMusicLoop(token));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AudioService: About music error: {ex.Message}");
                StopAboutMusic();
            }
        }
    }

    /// <summary>Stop the About music at once (the box closed, or a BBS tune needs the player).</summary>
    public void StopAboutMusic()
    {
        lock (_aboutLock)
        {
            // Always silence channel 5 (the tail of a song that ended on its own may still be draining), but free the
            // native player only while it's still the About music's -- never under a BBS tune
            try { NativeBridge.audio_bridge_stop(AboutMusicChannel); } catch { /* ignore */ }
            ReleaseAboutPlayer(_aboutPlayback);
        }
    }

    /// <summary>Free the native player if <paramref name="token"/> is still the About playback holding it.</summary>
    private void ReleaseAboutPlayer(object? token)
    {
        lock (_aboutLock)
        {
            if (token == null || _aboutPlayback != token) return;
            _aboutPlayback = null;
            try { NativeBridge.syncterm_mod_stop(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Render loop for the About music: renders the module and queues it gapless onto channel 5, waiting while the
    /// source's queue is full. Each step checks, under the lock, that the playback is still current, so once
    /// StopAboutMusic returns this never touches the native player again.
    /// </summary>
    private void AboutMusicLoop(object token)
    {
        var pcm = new short[AboutMusicChunkFrames * 2]; // stereo
        int pending = 0;                                 // frames rendered but not yet queued
        try
        {
            while (true)
            {
                lock (_aboutLock)
                {
                    if (_aboutPlayback != token) return;

                    if (pending == 0)
                    {
                        pending = NativeBridge.syncterm_mod_get_pcm(pcm, AboutMusicChunkFrames);
                        if (pending <= 0)
                        {
                            // Song over (libxmp's play-once mode): free the player; what's queued plays out
                            ReleaseAboutPlayer(token);
                            return;
                        }
                    }

                    if (NativeBridge.audio_bridge_queue_pcm(AboutMusicChannel, pcm, pending, 44100, 2) == 1)
                        pending = 0;
                }

                // Queue full (about 370 ms of audio ahead): let some of it play
                if (pending != 0)
                    Thread.Sleep(20);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioService: About music render error: {ex.Message}");
            ReleaseAboutPlayer(token);
        }
    }

    // ================================================================
    // SyncTERM audio APC
    // ================================================================

    // SyncTERM:A audio (doors like SyncDoom) is decoded, scheduled and mixed natively (native/apc/apc_audio.c,
    // apc_mixer.c); this only carries that mix onto OpenAL. It never touches the TAP+ channels.
    private const int SyncTermAudioChannel = 6;        // audio_bridge's own source: TAP 0-3, bell 4, About music 5
    private const int SyncTermAudioChunkFrames = 1024; // ~23 ms; audio_bridge queues up to four ahead (~93 ms)
    private Task? _syncTermAudioTask;

    /// <summary>Set by App once <c>audio_bridge_init</c> succeeds. Without OpenAL nothing would pull the door mix.</summary>
    public static bool AudioBridgeReady { get; set; }

    // TRACE module sound (Services/TraceAudioStream.cs) rides this same mix, as Windows adds it to its effects output
    // at the soundtrack volume: each running module's queue is summed into every chunk pulled below.
    private static readonly List<TraceAudioStream> _traceStreams = new();

    /// <summary>Starts mixing a TRACE module's sound into the door mix (called on its first sound).</summary>
    public static void AddTraceAudio(TraceAudioStream stream)
    {
        lock (_traceStreams)
        {
            if (!_traceStreams.Contains(stream))
                _traceStreams.Add(stream);
        }
    }

    /// <summary>Stops mixing a TRACE module's sound (the module ended).</summary>
    public static void RemoveTraceAudio(TraceAudioStream stream)
    {
        lock (_traceStreams)
            _traceStreams.Remove(stream);
    }

    /// <summary>
    /// Call before each connect: applies the soundtrack setting natively (while off, doors are told there is
    /// no audio) and starts the loop carrying the native mix to OpenAL the first time it's on. Windows'
    /// <c>PrepareSyncTermAudio</c>, which adds a permanent input to its music output instead.
    /// </summary>
    public void PrepareSyncTermAudio()
    {
        // No OpenAL = nothing pulls the mix, so doors must hear there is no audio (their A;Wait would sit out its cap)
        bool enabled = _settingsService.Settings.SoundtrackEnabled && AudioBridgeReady;
        try
        {
            NativeBridge.syncterm_set_apc_audio_enabled(enabled ? 1 : 0);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return;
        }
        // Started whenever OpenAL is up, not only with the soundtrack on: TRACE module sound rides this loop, and
        // Windows' effects output drains it (at volume 0) whatever the setting, so a module's audio queue never
        // stays full just because the soundtrack was off at connect time. The loop mutes itself while it's off.
        if (AudioBridgeReady && _syncTermAudioTask == null)
            _syncTermAudioTask = Task.Factory.StartNew(SyncTermAudioLoop, TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// Windows' <c>SyncTermAudioSource</c> on OpenAL: pulls the native mix (S16 stereo 44.1 kHz) and queues it gapless
    /// onto channel 6 at the soundtrack volume, for the life of the app. Pulled even when silent or muted, so native
    /// channels and TRACE queues drain and <c>A;Update</c> / <c>A;Wait</c> still resolve. But nothing is handed to
    /// OpenAL until there is something to hear: silent or muted chunks are paced by the clock instead, so the audio
    /// device (opened lazily by the bridge) stays closed until a door or module actually makes a sound, and the source
    /// goes idle again after a second of silence. While OpenAL is fed, its full queue paces the pulls.
    /// </summary>
    private void SyncTermAudioLoop()
    {
        const int SilentChunksBeforeIdle = 44100 / SyncTermAudioChunkFrames; // ~1 s of silence, then stop feeding
        var pcm = new short[SyncTermAudioChunkFrames * 2];
        bool pending = false;       // a pulled chunk not yet queued
        bool feeding = false;       // chunks are going to OpenAL (its queue paces the loop)
        bool noDevice = false;      // the bridge couldn't open the device: everything is clock-paced
        int silentChunks = 0;
        float appliedGain = -1f;
        var clock = Stopwatch.StartNew();
        long pacedFrames = 0;       // frames pulled since the clock was last reset (clock-paced mode)
        while (!_disposed)
        {
            try
            {
                var settings = _settingsService.Settings;
                float gain = settings.SoundtrackEnabled ? ToAmplitude(settings.ModPlayerVolume) : 0f;
                if (gain != appliedGain)
                {
                    NativeBridge.audio_bridge_set_volume(SyncTermAudioChannel, gain);
                    appliedGain = gain;
                }

                if (!pending)
                {
                    NativeBridge.syncterm_apc_audio_pull(pcm, SyncTermAudioChunkFrames);
                    lock (_traceStreams)
                    {
                        foreach (var trace in _traceStreams)
                            trace.MixInto(pcm, pcm.Length);
                    }
                    pending = true;

                    bool audible = gain > 0f && HasSignal(pcm);
                    silentChunks = audible ? 0 : silentChunks + 1;
                    if (!feeding && audible)
                        feeding = true;
                    else if (feeding && silentChunks > SilentChunksBeforeIdle)
                        feeding = false;   // what's queued plays out; the source goes idle
                    if (!feeding || noDevice)
                    {
                        // Nothing to hear: drop the chunk and pace the pulls in real time
                        pending = false;
                        if (pacedFrames == 0) clock.Restart();
                        pacedFrames += SyncTermAudioChunkFrames;
                        long wait = pacedFrames * 1000 / 44100 - clock.ElapsedMilliseconds;
                        if (wait > 0) Thread.Sleep((int)wait);
                        continue;
                    }
                    pacedFrames = 0;
                }

                if (NativeBridge.audio_bridge_queue_pcm(SyncTermAudioChannel, pcm, SyncTermAudioChunkFrames, 44100, 2) == 1)
                    pending = false;
                else if (NativeBridge.audio_bridge_is_playing(SyncTermAudioChannel) == 0)
                {
                    noDevice = true;  // refused but not full: no device. Drop it; from now on drain by the clock
                    pending = false;
                }
                else
                    Thread.Sleep(5); // queue full: let some of it play
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AudioService: SyncTERM audio loop error: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    private static bool HasSignal(short[] pcm)
    {
        foreach (short v in pcm)
            if (v != 0) return true;
        return false;
    }

    // ================================================================
    // Cache management
    // ================================================================

    private void PruneCache()
    {
        int limitMb = _settingsService.Settings.SoundtrackCacheLimitMb;
        if (limitMb <= 0) limitMb = DefaultCacheLimitMb;
        long limitBytes = (long)limitMb * 1024 * 1024;

        var files = new DirectoryInfo(_cacheDir)
            .GetFiles()
            .Where(f => !f.Name.EndsWith(".tmp"))
            .ToArray();

        long totalSize = files.Sum(f => f.Length);
        if (totalSize <= limitBytes) return;

        var playingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ChannelCount; i++)
        {
            if (_channels[i].CurrentFilename != null)
                playingFiles.Add(_channels[i].CurrentFilename!);
        }

        // Oldest download first. Creation time, not LastWriteTime: a download's write time is stamped with the server's
        // Last-Modified (for If-Modified-Since), which can be years old and would get a new download pruned first.
        var sorted = files.OrderBy(f => f.CreationTimeUtc).ToArray();
        foreach (var file in sorted)
        {
            if (totalSize <= limitBytes) break;
            if (playingFiles.Contains(file.Name)) continue;
            try
            {
                long size = file.Length;
                file.Delete();
                totalSize -= size;
                Debug.WriteLine($"AudioService: Pruned: {file.Name} ({size} bytes)");
            }
            catch { /* ignore delete errors */ }
        }
    }

    public long GetCacheSize()
    {
        if (!Directory.Exists(_cacheDir)) return 0;
        return new DirectoryInfo(_cacheDir)
            .GetFiles()
            .Where(f => !f.Name.EndsWith(".tmp"))
            .Sum(f => f.Length);
    }

    public void ClearCache()
    {
        StopAll();
        if (!Directory.Exists(_cacheDir)) return;
        foreach (var file in Directory.GetFiles(_cacheDir))
        {
            try { File.Delete(file); } catch { /* ignore */ }
        }
        Debug.WriteLine("AudioService: Cache cleared");
    }

    // ================================================================
    // Validation helpers
    // ================================================================

    private static string? SanitizeFilename(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        if (filename.Length > MaxFilenameLength) return null;
        if (!FilenameRegex.IsMatch(filename)) return null;
        if (filename.Contains("..")) return null;
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) return null;
        return filename;
    }

    private static bool IsValidUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrlLength) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https";
    }

    public string? ResolveFile(string displayName)
    {
        string? filename = null;

        if (_displayNameMap.TryGetValue(displayName, out var mapped))
            filename = mapped;

        filename ??= SanitizeFilename(displayName);
        filename ??= FindCachedFile(displayName);

        if (filename == null) return null;

        var path = Path.Combine(_cacheDir, filename);
        return File.Exists(path) ? path : null;
    }

    // ================================================================
    // Disposal
    // ================================================================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAboutMusic();
        _syncTermAudioTask?.Wait(500); // it checks _disposed on every pass

        _analyzerTimer?.Dispose();
        _analyzerTimer = null;

        for (int i = 0; i < ChannelCount; i++)
        {
            _channels[i].FadeCts?.Cancel();
            _channels[i].FadeCts?.Dispose();
            _channels[i].MonitorCts?.Cancel();
            _channels[i].MonitorCts?.Dispose();
            StopChannelInternal(i);
        }
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    // ================================================================
    // Helper classes
    // ================================================================

    private class AudioChannel
    {
        public CancellationTokenSource? FadeCts;
        public CancellationTokenSource? MonitorCts;
        public float Volume = 1.0f;
        public bool Playing;
        public bool IsPaused;
        public string? CurrentFilename;
        public int PlayGeneration;      // bumped by every play/stop, so a play still waiting on a download knows it is stale

        // Queue for auto-advance playback
        public List<(string Filename, int DelaySec)> Queue = new();
        public int QueueIndex;
        public CancellationTokenSource? QueueDelayCts;
    }
}
