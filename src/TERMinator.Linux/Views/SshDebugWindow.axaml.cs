using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TERMinator.Linux.Views;

public partial class SshDebugWindow : Window
{
    public SshDebugWindow(string logText)
    {
        InitializeComponent();
        LogTextBox.Text = logText;

        // Match Windows: open scrolled to the end so the newest log lines are visible.
        // CaretIndex needs the visual tree, so defer until Opened.
        Opened += (_, _) =>
        {
            LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
        };

        CopyButton.Click += CopyButton_Click;
        CloseButton.Click += CloseButton_Click;
    }

    private async void CopyButton_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null && LogTextBox.Text != null)
            await clipboard.SetTextAsync(LogTextBox.Text);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
