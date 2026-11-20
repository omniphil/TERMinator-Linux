using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TERMinator.Linux.Models;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    [ObservableProperty] private bool _playBellSound = true;
    [ObservableProperty] private int _bellSoundIndex = 28;
    [ObservableProperty] private string _bellDescription = string.Empty;
    [ObservableProperty] private int _bellVolume = 5;
    [ObservableProperty] private string _bellVolumeDisplay = "50%";

    [ObservableProperty] private string _defaultFont = "Codepage 437 English";
    [ObservableProperty] private int _defaultScreenMode;
    [ObservableProperty] private int _connectionTimeoutIndex = 1;   // 10 seconds
    [ObservableProperty] private bool _fontSmoothing;
    [ObservableProperty] private bool _rainwireGraphicalWindows = true;
    [ObservableProperty] private bool _boardNotifications;

    // Display
    [ObservableProperty] private bool _showBootSequence = true;
    [ObservableProperty] private bool _screensaverEnabled = true;
    [ObservableProperty] private bool _homeSideBySide;
    [ObservableProperty] private int _phosphorIndex;

    // Modem
    [ObservableProperty] private int _emulatedBaudIndex;
    [ObservableProperty] private bool _showModemLights = true;
    [ObservableProperty] private bool _keyClicksEnabled = false;
    [ObservableProperty] private int _keyClickVolume = 3;
    [ObservableProperty] private string _keyClickVolumeDisplay = "30%";

    [ObservableProperty] private string _downloadDirectory = string.Empty;
    [ObservableProperty] private bool _autoDetectZmodem = true;

    [ObservableProperty] private bool _soundtrackEnabled = true;
    [ObservableProperty] private int _soundtrackCacheLimitIndex;
    [ObservableProperty] private string _cacheSizeDisplay = "0 MB";

    // TRACE (TERMinator Render And Compute Engine)
    [ObservableProperty] private bool _traceEnabled = true;
    [ObservableProperty] private int _traceCacheLimitIndex;
    [ObservableProperty] private string _traceCacheDisplay = "0 KB";

    /// <summary>What the TRACE cache holds, one row per file, each with its own DELETE.</summary>
    public ObservableCollection<TraceCacheRow> TraceCacheItems { get; } = [];

    /// <summary>Whether the list of cached files is shown; folded away each time Settings opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TraceCacheToggleText))]
    private bool _traceCacheExpanded;

    /// <summary>The button that shows or hides the list: "SHOW (3)" / "HIDE".</summary>
    public string TraceCacheToggleText => TraceCacheExpanded ? "HIDE" : $"SHOW ({TraceCacheItems.Count})";

    [RelayCommand]
    private void ToggleTraceCache() => TraceCacheExpanded = !TraceCacheExpanded;

    /// <summary>The cached files running modules are using (TraceSessionManager.FilesInUse), which can't be deleted.</summary>
    public Func<IReadOnlyCollection<string>>? TraceFilesInUse { get; set; }

    [ObservableProperty] private bool _enableSessionLogging;
    [ObservableProperty] private string _logDirectory = string.Empty;

    public event Action? SaveRequested;
    public event Action? CancelRequested;
    public event Func<string, string, Task<string?>>? BrowseFolderRequested;

    /// <summary>The CRT button: the host opens the CRT / Scanline window (its settings live there, not here).</summary>
    public event Action? CrtSettingsRequested;

    /// <summary>The TEST button: the host plays the screensaver now (works even while it's switched off).</summary>
    public event Action? ScreensaverTestRequested;

    public string[] PhosphorModes { get; } = ["Color", "Green", "Amber", "White"];

    /// <summary>
    /// Raised as soon as a phosphor is picked, so the host can show it straight away. Nothing is stored until Save;
    /// Cancel reloads the stored settings, which moves the pick back and previews the stored mode again.
    /// </summary>
    public event Action<string>? PhosphorPreviewRequested;

    partial void OnPhosphorIndexChanged(int value) =>
        PhosphorPreviewRequested?.Invoke(PhosphorModes[Math.Clamp(value, 0, PhosphorModes.Length - 1)]);

    public string[] BaudRates { get; } = ModemSpeeds.Rates.Select(ModemSpeeds.Label).ToArray();

    partial void OnKeyClickVolumeChanged(int value)
    {
        KeyClickVolumeDisplay = $"{value * 10}%";
    }

    /// <summary>Re-reads the stored settings, e.g. after a menu or the command line changed one.</summary>
    public void Reload() => LoadFromSettings();

    public string[] BellSounds => BellService.BellNames;
    public string[] BellDescriptionsList => BellService.BellDescriptions;

    public string[] FontOptions { get; } =
    [
        "Codepage 437 English",
        "P0T NOoDLE",
        "mO'sOul",
        "MicroKnight Plus",
        "Topaz Plus",
        "MicroKnight",
        "Topaz"
    ];

    public string[] ConnectionTimeoutOptions { get; } = AppSettings.ConnectionTimeoutChoices.Select(n => $"{n} seconds").ToArray();

    public string[] ScreenModes { get; } =
    [
        "80x25 (Standard)",
        "80x30",
        "80x40",
        "80x50",
        "132x25",
        "132x30",
        "132x40",
        "132x50",
        "132x60",
        "C64 40x25 (PETSCII)",
        "C128 40x25 (PETSCII)",
        "C128 80x25 (PETSCII)"
    ];

    public string[] CacheLimits { get; } = ["100 MB", "250 MB", "500 MB"];
    private static readonly int[] CacheLimitValues = [100, 250, 500];

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromSettings();
    }

    partial void OnBellSoundIndexChanged(int value)
    {
        if (value >= 0 && value < BellService.BellDescriptions.Length)
            BellDescription = BellService.BellDescriptions[value];
    }

    partial void OnBellVolumeChanged(int value)
    {
        BellVolumeDisplay = $"{value * 10}%";
    }

    private void LoadFromSettings()
    {
        var s = _settingsService.Settings;
        PlayBellSound = s.PlayBellSound;
        BellSoundIndex = s.BellSoundIndex;
        OnBellSoundIndexChanged(BellSoundIndex);
        BellVolume = s.BellVolume;
        OnBellVolumeChanged(BellVolume);
        DefaultFont = s.DefaultFont;
        DefaultScreenMode = s.DefaultScreenMode;
        ConnectionTimeoutIndex = Array.IndexOf(AppSettings.ConnectionTimeoutChoices, AppSettings.ValidConnectionTimeout(s.ConnectionTimeoutSeconds));
        FontSmoothing = s.FontSmoothing;
        RainwireGraphicalWindows = s.RainwireGraphicalWindows;
        BoardNotifications = s.BoardNotifications;
        ShowBootSequence = s.ShowBootSequence;
        ScreensaverEnabled = s.ScreensaverEnabled;
        HomeSideBySide = s.HomeSideBySide;
        PhosphorIndex = Math.Max(0, Array.IndexOf(PhosphorModes, s.Phosphor));
        EmulatedBaudIndex = Math.Max(0, Array.IndexOf(ModemSpeeds.Rates, s.EmulatedBaud));
        ShowModemLights = s.ShowModemLights;
        KeyClicksEnabled = s.KeyClicksEnabled;
        KeyClickVolume = s.KeyClickVolume;
        OnKeyClickVolumeChanged(KeyClickVolume);
        DownloadDirectory = s.DownloadDirectory;
        AutoDetectZmodem = s.AutoDetectZmodem;
        SoundtrackEnabled = s.SoundtrackEnabled;
        SoundtrackCacheLimitIndex = Array.IndexOf(CacheLimitValues, s.SoundtrackCacheLimitMb);
        if (SoundtrackCacheLimitIndex < 0) SoundtrackCacheLimitIndex = 1; // Default to 250 MB
        UpdateCacheSizeDisplay();
        // TRACE
        TraceEnabled = s.TraceEnabled;
        TraceCacheLimitIndex = Array.IndexOf(CacheLimitValues, s.TraceCacheLimitMb);
        if (TraceCacheLimitIndex < 0) TraceCacheLimitIndex = 2; // default 500 MB
        TraceCacheExpanded = false;
        UpdateTraceCacheDisplay();
        EnableSessionLogging = s.EnableSessionLogging;
        LogDirectory = s.LogDirectory;
    }

    [RelayCommand]
    private void Save()
    {
        var s = _settingsService.Settings;
        s.PlayBellSound = PlayBellSound;
        s.BellSoundIndex = BellSoundIndex;
        s.BellVolume = BellVolume;
        s.DefaultFont = DefaultFont;
        s.DefaultScreenMode = DefaultScreenMode;
        s.ConnectionTimeoutSeconds = AppSettings.ConnectionTimeoutChoices[Math.Clamp(ConnectionTimeoutIndex, 0, AppSettings.ConnectionTimeoutChoices.Length - 1)];
        s.FontSmoothing = FontSmoothing;
        s.RainwireGraphicalWindows = RainwireGraphicalWindows;
        s.BoardNotifications = BoardNotifications;
        s.ShowBootSequence = ShowBootSequence;
        s.ScreensaverEnabled = ScreensaverEnabled;
        s.HomeSideBySide = HomeSideBySide;
        s.Phosphor = PhosphorModes[Math.Clamp(PhosphorIndex, 0, PhosphorModes.Length - 1)];
        s.EmulatedBaud = ModemSpeeds.Rates[Math.Clamp(EmulatedBaudIndex, 0, ModemSpeeds.Rates.Length - 1)];
        s.ShowModemLights = ShowModemLights;
        s.KeyClicksEnabled = KeyClicksEnabled;
        s.KeyClickVolume = KeyClickVolume;
        s.DownloadDirectory = DownloadDirectory;
        s.AutoDetectZmodem = AutoDetectZmodem;
        s.SoundtrackEnabled = SoundtrackEnabled;
        s.SoundtrackCacheLimitMb = SoundtrackCacheLimitIndex >= 0 && SoundtrackCacheLimitIndex < CacheLimitValues.Length
            ? CacheLimitValues[SoundtrackCacheLimitIndex]
            : 250;
        // TRACE
        s.TraceEnabled = TraceEnabled;
        s.TraceCacheLimitMb = TraceCacheLimitIndex >= 0 && TraceCacheLimitIndex < CacheLimitValues.Length
            ? CacheLimitValues[TraceCacheLimitIndex]
            : 500;
        s.EnableSessionLogging = EnableSessionLogging;
        s.LogDirectory = LogDirectory;
        _settingsService.Save();
        SaveRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        LoadFromSettings();
        CancelRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenCrtSettings() => CrtSettingsRequested?.Invoke();

    [RelayCommand]
    private void TestScreensaver() => ScreensaverTestRequested?.Invoke();

    [RelayCommand]
    private void TestKeyClick() => App.KeyClickService.PlayPreview(KeyClickVolume);

    [RelayCommand]
    private void TestBellSound()
    {
        float volume = Math.Clamp(BellVolume / 10f, 0f, 1f);
        App.BellService.PlayBell(BellSoundIndex, volume);
    }

    [RelayCommand]
    private async Task BrowseDownloadDir()
    {
        if (BrowseFolderRequested != null)
        {
            string initial = string.IsNullOrEmpty(DownloadDirectory)
                ? AppSettings.GetDefaultDownloadDir()
                : DownloadDirectory;
            var result = await BrowseFolderRequested.Invoke("Select Download Directory", initial);
            if (result != null)
                DownloadDirectory = result;
        }
    }

    [RelayCommand]
    private void ClearSoundtrackCache()
    {
        App.AudioService.ClearCache();
        UpdateCacheSizeDisplay();
    }

    public void RefreshCacheSize()
    {
        UpdateCacheSizeDisplay();
        UpdateTraceCacheDisplay();
    }

    [RelayCommand]
    private void ClearTraceCache()
    {
        TraceModules.ClearCache([.. TraceFilesInUse?.Invoke() ?? []]);   // what a running module uses stays
        UpdateTraceCacheDisplay();
    }

    private void DeleteTraceCacheEntry(string path)
    {
        TraceModules.DeleteCached(path, TraceFilesInUse?.Invoke() ?? []);
        UpdateTraceCacheDisplay();
    }

    private void UpdateTraceCacheDisplay()
    {
        long bytes = TraceModules.GetCacheSize();
        int count = TraceModules.GetCachedModuleCount();
        TraceCacheDisplay = $"{FormatCacheBytes(bytes)} ({count} module{(count == 1 ? "" : "s")})";

        var inUse = TraceFilesInUse?.Invoke() ?? [];
        TraceCacheItems.Clear();
        foreach (var entry in TraceModules.ListCache())
        {
            bool busy = TraceModules.IsInUse(entry.Path, inUse);
            string path = entry.Path;
            TraceCacheItems.Add(new TraceCacheRow(DescribeTraceCacheEntry(entry, busy),
                                                  new RelayCommand(() => DeleteTraceCacheEntry(path), () => !busy)));
        }
        OnPropertyChanged(nameof(TraceCacheToggleText));
    }

    private static string FormatCacheBytes(long bytes) =>
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:F0} KB" : $"{bytes / (1024.0 * 1024.0):F1} MB";

    /// <summary>
    /// "tyrian · game · 396 KB · Absinthe BBS · used today": the door's module id (Unknown when it was cached before
    /// TERMinator recorded that), game (the module) or data (an asset), size, the board (left out when not known), and
    /// when it was last used, or "in use" while a running module has it.
    /// </summary>
    private static string DescribeTraceCacheEntry(TraceModules.CacheEntry entry, bool inUse)
    {
        // A file the player imported: "Wing Commander · your file · 4.1 MB · 2 boards allowed · used today"
        if (entry.Imported)
            return string.Join(" · ", entry.Name ?? "Imported file", "your file", FormatCacheBytes(entry.Size),
                               entry.GrantCount == 1 ? "1 board allowed" : $"{entry.GrantCount} boards allowed",
                               inUse ? "in use" : FormatLastUsed(entry.LastUsedUtc));
        var parts = new List<string>
        {
            entry.ModuleId ?? "Unknown",
            entry.IsAsset ? "data" : "game",
            FormatCacheBytes(entry.Size),
        };
        if (entry.Bbs != null)
            parts.Add(entry.Bbs);
        parts.Add(inUse ? "in use" : FormatLastUsed(entry.LastUsedUtc));
        return string.Join(" · ", parts);
    }

    /// <summary>"used today", "used yesterday", "used 5 days ago" (under 30 days), else "used Sep 3, 2026".</summary>
    private static string FormatLastUsed(DateTime utc)
    {
        DateTime local = utc.ToLocalTime();
        int days = (DateTime.Today - local.Date).Days;
        return days <= 0 ? "used today"
            : days == 1 ? "used yesterday"
            : days < 30 ? $"used {days} days ago"
            : $"used {local.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";
    }

    private void UpdateCacheSizeDisplay()
    {
        long bytes = App.AudioService.GetCacheSize();
        CacheSizeDisplay = bytes < 1024 * 1024
            ? $"{bytes / 1024.0:F0} KB"
            : $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    [RelayCommand]
    private async Task BrowseLogDir()
    {
        if (BrowseFolderRequested != null)
        {
            string initial = string.IsNullOrEmpty(LogDirectory)
                ? Path.Combine(AppSettings.GetAppDataDir(), "logs")
                : LogDirectory;
            var result = await BrowseFolderRequested.Invoke("Select Log Directory", initial);
            if (result != null)
                LogDirectory = result;
        }
    }
}

/// <summary>One file in the TRACE cache, as Settings lists it; Delete is disabled while a running module uses it.</summary>
public sealed record TraceCacheRow(string Text, IRelayCommand DeleteCommand);
