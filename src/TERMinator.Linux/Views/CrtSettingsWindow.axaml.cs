using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TERMinator.Linux.Effects;
using TERMinator.Linux.Models;
using TERMinator.Linux.ViewModels;

namespace TERMinator.Linux.Views;

/// <summary>
/// Live editor for the CRT / scanline post-process. Every slider writes straight through to the
/// terminal behind the window, so the user can leave a BBS session on screen and dial the look
/// in against real ANSI art. Mirrors the Windows dialog of the same name.
/// </summary>
public partial class CrtSettingsWindow : Window
{
    private readonly CrtSettingsViewModel _viewModel;
    private readonly Action<CrtSettings> _apply;

    /// <summary>Parameterless ctor for the XAML previewer only.</summary>
    public CrtSettingsWindow() : this(new CrtSettings(), _ => { })
    {
    }

    public CrtSettingsWindow(CrtSettings current, Action<CrtSettings> apply)
    {
        InitializeComponent();

        _apply = apply;

        _viewModel = new CrtSettingsViewModel(current);
        _viewModel.Changed += OnSettingsChanged;
        DataContext = _viewModel;

        if (!CrtShader.IsSupported)
        {
            UnsupportedNote.IsVisible = true;
            EnableCheck.IsEnabled = false;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>The settings as last edited — the caller persists these.</summary>
    public CrtSettings Result => _viewModel.ToSettings();

    private void OnSettingsChanged(CrtSettings settings) => _apply(settings);

    /// <summary>
    /// Restores factory settings — every slider, the preset selection and the tube color all go
    /// back to defaults. The on/off switch is left alone, so Reset never turns the effect on
    /// behind the user's back.
    /// </summary>
    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        var factory = CrtSettings.Factory();
        factory.Enabled = _viewModel.Enabled;
        _viewModel.Load(factory);
    }

    private void DoneButton_Click(object? sender, RoutedEventArgs e) => Close();
}
