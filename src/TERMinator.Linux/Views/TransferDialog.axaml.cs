using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TERMinator.Linux.Services;
using TERMinator.Linux.ViewModels;

namespace TERMinator.Linux.Views;

public partial class TransferDialog : Window
{
    private readonly TransferViewModel _viewModel;

    public TransferDialog() : this(null) { }

    public TransferDialog(ConnectionManager? connectionManager)
    {
        InitializeComponent();
        _viewModel = new TransferViewModel(connectionManager);
        DataContext = _viewModel;

        // Keep the newest status line in view.
        //
        // The scroll must NOT happen inside the CollectionChanged notification:
        // it forces a layout pass while the batch of adds is still being
        // applied. So coalesce -- one scroll queued per burst, run at
        // Background priority once layout has settled.
        _viewModel.LogLines.CollectionChanged += (_, _) =>
        {
            if (_scrollQueued)
                return;
            _scrollQueued = true;

            Dispatcher.UIThread.Post(() =>
            {
                _scrollQueued = false;
                LogScroll.ScrollToEnd();
            }, DispatcherPriority.Background);
        };

        _viewModel.TransferCompleted += OnTransferCompleted;
        _viewModel.TransferFailed += OnTransferFailed;
        _viewModel.AutoCloseRequested += OnAutoCloseRequested;

        CloseButton.Click += CloseButton_Click;

        // Any deliberate click or keypress inside the dialog stops the
        // post-transfer countdown. Tunnel-routed (the WPF Preview* events) so a
        // button or the log that handles the event itself still counts.
        AddHandler(PointerPressedEvent, (_, _) => _viewModel.CancelAutoClose(),
                   RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => _viewModel.CancelAutoClose(),
                   RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Set while a scroll-to-bottom is already queued, so a burst of
    /// log lines produces one scroll rather than one per line.</summary>
    private bool _scrollQueued;

    /// <summary>The transfer ended: true when it completed, false when it failed, was cancelled or the dialog closed
    /// first. Raised once (Rainwire file drops wait on it rather than on the auto-close).</summary>
    public event Action<bool>? Finished;
    private bool _finished;

    private void RaiseFinished(bool ok)
    {
        if (_finished)
            return;
        _finished = true;
        Finished?.Invoke(ok);
    }

    private void OnAutoCloseRequested()
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _viewModel.Dispose();
            Close(true);
        });
    }

    public void StartReceive()
    {
        _viewModel.StartReceive();
    }

    public void StartSend(string filePath)
    {
        _viewModel.StartSend(filePath);
    }

    public void StartSend(string[] filePaths)
    {
        _viewModel.StartSend(filePaths);
    }

    public void StartXmodemReceive(string? savePath, int mode)
    {
        _viewModel.StartXmodemReceive(savePath, mode);
    }

    public void StartXmodemSend(string filePath, int mode)
    {
        _viewModel.StartXmodemSend(filePath, mode);
    }

    private void OnTransferCompleted()
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            RaiseFinished(true);
            CancelButton.IsVisible = false;
            CloseButton.IsVisible = true;
        });
    }

    private void OnTransferFailed(string error)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            RaiseFinished(false);
            CancelButton.IsVisible = false;
            CloseButton.IsVisible = true;
        });
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.CancelAutoClose();
        Close(true);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_viewModel.IsTransferring)
        {
            e.Cancel = true;

            // Show a confirmation dialog
            var msgBox = new Window
            {
                Title = "Transfer In Progress",
                Width = 360,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var panel = new StackPanel { Margin = new Avalonia.Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = "A transfer is in progress. Cancel it?",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 0, 0, 16)
            });

            var buttonPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
            };

            bool confirmed = false;
            var yesBtn = new Button { Content = "Yes", Width = 80, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
            var noBtn = new Button { Content = "No", Width = 80 };
            yesBtn.Click += (_, _) => { confirmed = true; msgBox.Close(); };
            noBtn.Click += (_, _) => { msgBox.Close(); };
            buttonPanel.Children.Add(yesBtn);
            buttonPanel.Children.Add(noBtn);
            panel.Children.Add(buttonPanel);
            msgBox.Content = panel;

            await msgBox.ShowDialog(this);

            if (confirmed)
            {
                _viewModel.CancelTransferCommand.Execute(null);
                _viewModel.Dispose();
                Close(false);
            }

            return;
        }

        _viewModel.Dispose();
        base.OnClosing(e);
        RaiseFinished(false);
    }
}
