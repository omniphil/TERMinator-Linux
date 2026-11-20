using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TERMinator.Linux.Models;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.ViewModels;

public partial class PhonebookViewModel : ObservableObject
{
    private readonly PhonebookService _phonebook;
    private readonly CredentialService _credentials;
    private readonly SettingsService _settings;

    public ObservableCollection<SavedConnection> Connections { get; } = [];

    [ObservableProperty] private SavedConnection? _selectedConnection;
    [ObservableProperty] private int _viewMode;

    /// <summary>The selected board's line from the call log, shown under the list.</summary>
    [ObservableProperty] private string _selectedStatsText = string.Empty;

    partial void OnSelectedConnectionChanged(SavedConnection? value) =>
        SelectedStatsText = value == null ? string.Empty : $"{value.Name}: {App.CallStatsService.Describe(value.Id)}";

    public event Action<SavedConnection>? ConnectRequested;
    public event Action<SavedConnection>? EditRequested;
    public event Action? BackRequested;
    public event Func<string, Task<bool>>? ConfirmDeleteRequested;

    public PhonebookViewModel(PhonebookService phonebook, CredentialService credentials, SettingsService settings)
    {
        _phonebook = phonebook;
        _credentials = credentials;
        _settings = settings;
        _viewMode = settings.Settings.PhonebookViewMode;
        Refresh();
    }

    public string ViewModeLabel => ViewMode switch
    {
        0 => "List",
        1 => "Medium",
        2 => "Large",
        3 => "Small",
        _ => "Medium"
    };

    partial void OnViewModeChanged(int value)
    {
        OnPropertyChanged(nameof(ViewModeLabel));
    }

    [RelayCommand]
    private void CycleView()
    {
        ViewMode = ViewMode switch
        {
            1 => 2,
            2 => 0,
            0 => 3,
            3 => 1,
            _ => 1
        };
        _settings.Settings.PhonebookViewMode = ViewMode;
        _settings.Save();
    }

    public void Refresh()
    {
        // Load() makes new objects, so the list's selection would be lost (it
        // happened every time the window came back from a BBS window, which
        // saves the last call into the phonebook): pick the same board again.
        var keepId = SelectedConnection?.Id;
        _phonebook.Load();
        Connections.Clear();
        foreach (var conn in _phonebook.Connections)
            Connections.Add(conn);
        if (keepId != null)
            SelectedConnection = Connections.FirstOrDefault(c => c.Id == keepId);
    }

    [RelayCommand]
    private void AddConnection()
    {
        var conn = new SavedConnection
        {
            Name = "New Connection",
            Host = "localhost",
            Port = 23,
            Protocol = ConnectionProtocol.Telnet
        };
        _phonebook.Add(conn);
        Refresh();
        SelectedConnection = Connections.FirstOrDefault(c => c.Id == conn.Id);
    }

    [RelayCommand]
    private void EditConnection()
    {
        if (SelectedConnection == null) return;
        EditRequested?.Invoke(SelectedConnection);
    }

    public void SaveEditedConnection(SavedConnection connection)
    {
        _phonebook.Update(connection);
        Refresh();
        SelectedConnection = Connections.FirstOrDefault(c => c.Id == connection.Id);
    }

    [RelayCommand]
    private async Task DeleteConnection()
    {
        if (SelectedConnection == null) return;

        bool confirmed = true;
        if (ConfirmDeleteRequested != null)
            confirmed = await ConfirmDeleteRequested.Invoke(SelectedConnection.Name);

        if (!confirmed) return;

        var id = SelectedConnection.Id;
        _credentials.DeletePassword(id);
        _phonebook.Remove(id);
        Refresh();
    }

    public void MoveConnectionTo(string id, int newIndex)
    {
        _phonebook.ClearSortState();
        IsSorted = false;
        _phonebook.MoveTo(id, newIndex);
        Refresh();
        SelectedConnection = null;
    }

    [ObservableProperty] private bool _isSorted;

    public string SortButtonLabel => IsSorted ? "UNSORT" : "SORT";

    partial void OnIsSortedChanged(bool value)
    {
        OnPropertyChanged(nameof(SortButtonLabel));
    }

    [RelayCommand]
    private void ToggleSort()
    {
        if (_phonebook.IsSorted)
        {
            _phonebook.RestoreCustomOrder();
            IsSorted = false;
        }
        else
        {
            _phonebook.SortAlphabetically();
            IsSorted = true;
        }
        Refresh();
    }

    [RelayCommand]
    private void ConnectToSelected()
    {
        if (SelectedConnection == null) return;
        ConnectRequested?.Invoke(SelectedConnection);
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedConnection == null) return;
        var id = SelectedConnection.Id;
        _phonebook.MoveUp(id);
        Refresh();
        SelectedConnection = Connections.FirstOrDefault(c => c.Id == id);
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedConnection == null) return;
        var id = SelectedConnection.Id;
        _phonebook.MoveDown(id);
        Refresh();
        SelectedConnection = Connections.FirstOrDefault(c => c.Id == id);
    }

    [RelayCommand]
    private void GoBack() => BackRequested?.Invoke();
}
