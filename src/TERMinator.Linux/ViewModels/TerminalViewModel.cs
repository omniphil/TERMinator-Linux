using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TERMinator.Linux.Models;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.ViewModels;

public partial class TerminalViewModel : ObservableObject
{
    private readonly ConnectionManager _connectionManager;
    private readonly SessionLogger _sessionLogger;
    private readonly AudioService _audioService;
    private readonly SettingsService _settingsService;
    private readonly CredentialService _credentialService;

    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _windowTitle = "TERMinator - BBS Terminal";
    [ObservableProperty] private bool _isLogging;

    private SavedConnection? _currentConnection;

    public ConnectionManager ConnectionManager => _connectionManager;
    public SavedConnection? CurrentConnection => _currentConnection;

    public event Action? DisconnectedEvent;
    private int _redrawQueued; // 1 while a redraw is queued on the dispatcher
    public event Action? ScreenUpdatedEvent;
    public event Action<string>? TapPlusCommandReceived;
    public event Action<byte[]>? FontDataLoaded;
    public event Func<Task<string?>>? PasteRequested;

    public TerminalViewModel(
        ConnectionManager connectionManager,
        SessionLogger sessionLogger,
        AudioService audioService,
        SettingsService settingsService,
        CredentialService credentialService)
    {
        _connectionManager = connectionManager;
        _sessionLogger = sessionLogger;
        _audioService = audioService;
        _settingsService = settingsService;
        _credentialService = credentialService;

        _connectionManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionManager.StatusText))
                StatusText = _connectionManager.StatusText;
            if (e.PropertyName == nameof(ConnectionManager.State))
                IsConnected = _connectionManager.State == ConnectionState.Connected;
        };

        _connectionManager.Disconnected += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = false;
                StatusText = "Disconnected";
                StopLogging();
                DisconnectedEvent?.Invoke();
            });
        };

        _connectionManager.ScreenUpdated += () =>
        {
            // Coalesced: at most one redraw is queued, and the poll loop never waits on it (a door pacing frames on
            // our replies would otherwise run at the UI's redraw speed). Input priority lets keystrokes through
            // between frames. Windows 2026-09-15.
            if (Interlocked.Exchange(ref _redrawQueued, 1) != 0)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                // Cleared first, so a change landing during this redraw queues the next one
                Interlocked.Exchange(ref _redrawQueued, 0);
                _connectionManager.FetchScreen();

                if (_sessionLogger.IsEnabled)
                    _sessionLogger.Flush();
                ScreenUpdatedEvent?.Invoke();
            }, DispatcherPriority.Input);
        };

        _connectionManager.BellReceived += () =>
        {
            // Silent while the Boss Hide screen is up
            if (_settingsService.Settings.PlayBellSound && !App.BossKeyActive)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _audioService.PlayBell();
                });
            }
        };

        _connectionManager.AudioCommandReceived += cmd =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _audioService.HandleAudioCommand(cmd);
                TapPlusCommandReceived?.Invoke(cmd);
            });
        };
    }

    public bool ConnectTo(SavedConnection connection, string? password = null)
    {
        if (!_connectionManager.IsInitialized)
            return false;

        if (password == null && connection.SaveCredentials)
            password = _credentialService.GetPassword(connection.Id);

        // Modem speed emulation (0 = full speed), set before connecting so the first byte is already metered
        try { NativeBridge.syncterm_set_emulated_bps(_settingsService.Settings.EmulatedBaud); }
        catch (DllNotFoundException) { return false; }

        var downloadDir = _settingsService.Settings.DownloadDirectory;
        if (!string.IsNullOrEmpty(downloadDir))
        {
            try { NativeBridge.syncterm_set_download_dir(downloadDir); }
            catch (DllNotFoundException) { return false; }
        }

        bool result = _connectionManager.Connect(connection, password);
        if (result)
        {
            _currentConnection = connection;
            connection.LastConnected = DateTime.Now;

            if (_settingsService.Settings.EnableSessionLogging)
                StartLogging(connection);

            var fontData = _connectionManager.CachedFontBitmap;
            if (fontData != null)
                FontDataLoaded?.Invoke(fontData);
        }
        return result;
    }

    public string? ResolveMediaFile(string displayName) => _audioService.ResolveFile(displayName);
    public double MediaVolume => _audioService.Volume;
    public AudioAnalyzer AudioAnalyzer => _audioService.AudioAnalyzer;
    public void StopAllAudio() => _audioService.StopAll();
    public bool IsTapPlusEnabled => _settingsService.Settings.SoundtrackEnabled;

    [RelayCommand]
    private void Disconnect()
    {
        StopLogging();
        _audioService.StopAll();
        _connectionManager.Disconnect();
        DisconnectedEvent?.Invoke();
    }

    [RelayCommand]
    private void CopySelection() { }

    [RelayCommand]
    private async Task Paste()
    {
        if (!IsConnected) return;
        string? text = null;
        if (PasteRequested != null)
        {
            try { text = await PasteRequested.Invoke(); } catch { }
        }
        if (!string.IsNullOrEmpty(text))
            _connectionManager.SendString(text);
    }

    [RelayCommand]
    private void ToggleLogging()
    {
        if (_sessionLogger.IsEnabled)
        {
            StopLogging();
        }
        else if (IsConnected)
        {
            var logDir = _settingsService.Settings.LogDirectory;
            if (string.IsNullOrEmpty(logDir))
                logDir = Path.Combine(AppSettings.GetAppDataDir(), "logs");
            var fileName = $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            var filePath = Path.Combine(logDir, fileName);
            _sessionLogger.Start(filePath);
            IsLogging = true;
        }
    }

    private void StartLogging(SavedConnection connection)
    {
        var logDir = _settingsService.Settings.LogDirectory;
        if (string.IsNullOrEmpty(logDir))
            logDir = Path.Combine(AppSettings.GetAppDataDir(), "logs");
        var safeName = string.Join("_", connection.Name.Split(Path.GetInvalidFileNameChars()));
        var fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        var filePath = Path.Combine(logDir, fileName);
        _sessionLogger.Start(filePath);
        IsLogging = true;
    }

    private void StopLogging()
    {
        if (_sessionLogger.IsEnabled)
        {
            _sessionLogger.Stop();
            IsLogging = false;
        }
    }
}
