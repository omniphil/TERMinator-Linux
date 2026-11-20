using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TERMinator.Linux.Models;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private const string NoSnapshot = "avares://TERMinator/Assets/snapshots/no_snapshot.png";

    private readonly PhonebookService _phonebook;
    private readonly SettingsService _settings;

    [ObservableProperty] private SavedConnection? _quickConnect1;
    [ObservableProperty] private SavedConnection? _quickConnect2;
    // Slots 3-4 are only shown by the side-by-side home layout (View menu)
    [ObservableProperty] private SavedConnection? _quickConnect3;
    [ObservableProperty] private SavedConnection? _quickConnect4;

    public event Action<SavedConnection>? ConnectRequested;
    public event Action? PhonebookRequested;
    public event Action? SettingsRequested;
    public event Action? CrtRequested;
    public event Action? ToggleLayoutRequested;
    public event Action? CommandLineRequested;
    public event Action<int>? PickQuickConnectRequested;

    public bool HasQuickConnect1 => QuickConnect1 != null;
    public bool HasQuickConnect2 => QuickConnect2 != null;
    public bool HasQuickConnect3 => QuickConnect3 != null;
    public bool HasQuickConnect4 => QuickConnect4 != null;
    public bool HasNoQuickConnect1 => QuickConnect1 == null;
    public bool HasNoQuickConnect2 => QuickConnect2 == null;
    public bool HasNoQuickConnect3 => QuickConnect3 == null;
    public bool HasNoQuickConnect4 => QuickConnect4 == null;
    public string QuickConnect1Label => QuickConnect1?.Name ?? "(empty)";
    public string QuickConnect2Label => QuickConnect2?.Name ?? "(empty)";
    public string QuickConnect3Label => QuickConnect3?.Name ?? "(empty)";
    public string QuickConnect4Label => QuickConnect4?.Name ?? "(empty)";
    public string QuickConnect1Image => QuickConnect1?.SnapshotImagePath ?? NoSnapshot;
    public string QuickConnect2Image => QuickConnect2?.SnapshotImagePath ?? NoSnapshot;
    public string QuickConnect3Image => QuickConnect3?.SnapshotImagePath ?? NoSnapshot;
    public string QuickConnect4Image => QuickConnect4?.SnapshotImagePath ?? NoSnapshot;
    public string QuickConnect1Target => TargetLabel(QuickConnect1);
    public string QuickConnect2Target => TargetLabel(QuickConnect2);
    public string QuickConnect3Target => TargetLabel(QuickConnect3);
    public string QuickConnect4Target => TargetLabel(QuickConnect4);

    /// <summary>
    /// Caption for the hover target lock on a Quick Connect tile, e.g. "TELNET // absinthebbs.net:1940".
    /// Controls.TargetLockOverlay decrypts it across the bottom of the snapshot while the pointer is on the tile.
    /// </summary>
    private static string TargetLabel(SavedConnection? connection)
    {
        if (connection == null)
            return string.Empty;

        string protocol = connection.Protocol switch
        {
            ConnectionProtocol.SecureWebSocket => "WSS",
            ConnectionProtocol.WebSocket => "WS",
            _ => connection.Protocol.ToString().ToUpperInvariant()
        };
        return $"{protocol} // {connection.Host}:{connection.Port}";
    }

    public HomeViewModel(PhonebookService phonebook, SettingsService settings)
    {
        _phonebook = phonebook;
        _settings = settings;
        LoadQuickConnects();
    }

    partial void OnQuickConnect1Changed(SavedConnection? value)
    {
        OnPropertyChanged(nameof(HasQuickConnect1));
        OnPropertyChanged(nameof(HasNoQuickConnect1));
        OnPropertyChanged(nameof(QuickConnect1Label));
        OnPropertyChanged(nameof(QuickConnect1Image));
        OnPropertyChanged(nameof(QuickConnect1Target));
    }

    partial void OnQuickConnect2Changed(SavedConnection? value)
    {
        OnPropertyChanged(nameof(HasQuickConnect2));
        OnPropertyChanged(nameof(HasNoQuickConnect2));
        OnPropertyChanged(nameof(QuickConnect2Label));
        OnPropertyChanged(nameof(QuickConnect2Image));
        OnPropertyChanged(nameof(QuickConnect2Target));
    }

    partial void OnQuickConnect3Changed(SavedConnection? value)
    {
        OnPropertyChanged(nameof(HasQuickConnect3));
        OnPropertyChanged(nameof(HasNoQuickConnect3));
        OnPropertyChanged(nameof(QuickConnect3Label));
        OnPropertyChanged(nameof(QuickConnect3Image));
        OnPropertyChanged(nameof(QuickConnect3Target));
    }

    partial void OnQuickConnect4Changed(SavedConnection? value)
    {
        OnPropertyChanged(nameof(HasQuickConnect4));
        OnPropertyChanged(nameof(HasNoQuickConnect4));
        OnPropertyChanged(nameof(QuickConnect4Label));
        OnPropertyChanged(nameof(QuickConnect4Image));
        OnPropertyChanged(nameof(QuickConnect4Target));
    }

    [RelayCommand]
    private void PickQuickConnect1() => PickQuickConnectRequested?.Invoke(1);

    [RelayCommand]
    private void PickQuickConnect2() => PickQuickConnectRequested?.Invoke(2);

    [RelayCommand]
    private void PickQuickConnect3() => PickQuickConnectRequested?.Invoke(3);

    [RelayCommand]
    private void PickQuickConnect4() => PickQuickConnectRequested?.Invoke(4);

    [RelayCommand]
    private void ConnectQuick1()
    {
        if (QuickConnect1 != null)
            ConnectRequested?.Invoke(QuickConnect1);
    }

    [RelayCommand]
    private void ConnectQuick2()
    {
        if (QuickConnect2 != null)
            ConnectRequested?.Invoke(QuickConnect2);
    }

    [RelayCommand]
    private void ConnectQuick3()
    {
        if (QuickConnect3 != null)
            ConnectRequested?.Invoke(QuickConnect3);
    }

    [RelayCommand]
    private void ConnectQuick4()
    {
        if (QuickConnect4 != null)
            ConnectRequested?.Invoke(QuickConnect4);
    }

    [RelayCommand]
    private void OpenPhonebook() => PhonebookRequested?.Invoke();

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void OpenCrt() => CrtRequested?.Invoke();

    [RelayCommand]
    private void ToggleLayout() => ToggleLayoutRequested?.Invoke();

    [RelayCommand]
    private void OpenCommandLine() => CommandLineRequested?.Invoke();

    public void SetQuickConnect(int slot, SavedConnection conn) => AssignSlot(slot, conn);

    public void ClearQuickConnect(int slot) => AssignSlot(slot, null);

    private void AssignSlot(int slot, SavedConnection? conn)
    {
        switch (slot)
        {
            case 1:
                QuickConnect1 = conn;
                _settings.Settings.QuickConnect1Id = conn?.Id;
                break;
            case 2:
                QuickConnect2 = conn;
                _settings.Settings.QuickConnect2Id = conn?.Id;
                break;
            case 3:
                QuickConnect3 = conn;
                _settings.Settings.QuickConnect3Id = conn?.Id;
                break;
            case 4:
                QuickConnect4 = conn;
                _settings.Settings.QuickConnect4Id = conn?.Id;
                break;
            default:
                return;
        }
        _settings.Save();
    }

    public void LoadQuickConnects()
    {
        var id1 = _settings.Settings.QuickConnect1Id;
        var id2 = _settings.Settings.QuickConnect2Id;
        var id3 = _settings.Settings.QuickConnect3Id;
        var id4 = _settings.Settings.QuickConnect4Id;

        QuickConnect1 = id1 != null ? _phonebook.GetById(id1) : null;
        QuickConnect2 = id2 != null ? _phonebook.GetById(id2) : null;
        QuickConnect3 = id3 != null ? _phonebook.GetById(id3) : null;
        QuickConnect4 = id4 != null ? _phonebook.GetById(id4) : null;
    }
}
