using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace TERMinator.Linux.Views;

/// <summary>
/// The in-app manual. Content mirrors the Windows and macOS help section for section, with the
/// shortcuts and menu paths of THIS client — copying another platform's key names across would
/// document an app the user is not running.
/// </summary>
public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
