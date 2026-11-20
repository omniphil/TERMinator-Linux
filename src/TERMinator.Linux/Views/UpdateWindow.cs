using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.Views;

/// <summary>
/// Help → Check for Updates: checking, then up to date / an error / the new version's notes with
/// Download &amp; Install, then the download progress. Running as an AppImage, the download replaces it
/// and <see cref="_relaunching"/> closes TERMinator while the new one starts. A development build
/// (no $APPIMAGE) only offers the website. Same as the Windows client's Views/UpdateWindow.cs.
/// </summary>
public sealed class UpdateWindow : Window
{
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#FFFFAA00"));
    private static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#FF888899"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FFFF3333"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#FF33FF33"));
    private static readonly IBrush NoteGreen = new SolidColorBrush(Color.FromRgb(0x55, 0xDD, 0x55));

    private readonly StackPanel _panel = new() { Margin = new Thickness(36, 30, 36, 26), Width = 560 };
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string?> _hangUpWarning;
    private readonly Action _relaunching;
    private readonly Action<string> _openUrl;

    /// <param name="hangUpWarning">What installing would interrupt (calls, BBS windows), or null.</param>
    /// <param name="relaunching">Closes TERMinator; called once the new AppImage is starting.</param>
    /// <param name="openUrl">Opens a page in the browser.</param>
    public UpdateWindow(Func<string?> hangUpWarning, Action relaunching, Action<string> openUrl)
    {
        _hangUpWarning = hangUpWarning;
        _relaunching = relaunching;
        _openUrl = openUrl;
        Title = "Check for Updates";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.Parse("#FF0D0D1A"));
        Foreground = new SolidColorBrush(Color.Parse("#FFCCCCCC"));
        FontSize = 16;   // the notes; the heading, lines and buttons set their own
        Content = _panel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        Closed += (_, _) => _cts.Cancel();
        Opened += async (_, _) => await CheckAsync();
    }

    private void Show(string heading, string? text, IBrush? headingBrush = null)
    {
        _panel.Children.Clear();
        _panel.Children.Add(new TextBlock
        {
            Text = heading,
            Foreground = headingBrush ?? Amber,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        });
        if (text != null)
            _panel.Children.Add(Line(text, Secondary));
    }

    private static TextBlock Line(string text, IBrush brush) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = 16,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(0, 8, 0, 0)
    };

    private void AddButtons(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 0)
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].FontSize = 15;   // the button style's own size is small next to this box's text
            buttons[i].Padding = new Thickness(24, 10, 24, 10);
            if (i < buttons.Length - 1)
                buttons[i].Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(buttons[i]);
        }
        _panel.Children.Add(row);
    }

    private Button CloseButton(string label = "CLOSE")
    {
        var b = new Button { Content = label, IsCancel = true, IsDefault = true };
        b.Click += (_, _) => Close();
        return b;
    }

    private async Task CheckAsync()
    {
        Show("Checking for updates...", $"You have version {UpdateService.CurrentVersion.ToString(3)}.");
        UpdateInfo info;
        try
        {
            info = await UpdateService.CheckAsync(_cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            Show("Couldn't check for updates", Reason(ex), Red);
            AddButtons(CloseButton());
            return;
        }

        if (info.Version <= UpdateService.CurrentVersion)
        {
            Show("You're up to date", $"Version {UpdateService.CurrentVersion.ToString(3)} is the latest.", Green);
            AddButtons(CloseButton("OK"));
            return;
        }

        Show($"Version {info.Version.ToString(3)} is available",
            $"You have {UpdateService.CurrentVersion.ToString(3)}. Download size {info.Size / 1048576.0:0.0} MB.");
        if (info.Notes.Count > 0)
        {
            var notes = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
            notes.Inlines!.Add(new Run("─── What's New ───\n") { Foreground = Amber, FontWeight = FontWeight.Bold });
            foreach (var line in info.Notes)
            {
                notes.Inlines.Add(new Run("• ") { Foreground = Amber });
                notes.Inlines.Add(new Run(line + "\n") { Foreground = NoteGreen });
            }
            _panel.Children.Add(new ScrollViewer
            {
                Content = notes,
                MaxHeight = 300,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            });
        }

        // A development build (dotnet TERMinator.dll) has no AppImage to replace: send them to the website
        if (UpdateService.AppImagePath == null)
        {
            var site = new Button { Content = "OPEN WEBSITE", Classes = { "amber" }, IsDefault = true };
            site.Click += (_, _) => { _openUrl(UpdateService.WebsiteUrl); Close(); };
            var close = new Button { Content = "LATER", IsCancel = true };
            close.Click += (_, _) => Close();
            AddButtons(site, close);
            return;
        }

        string path = UpdateService.DownloadPath(info, out bool replaces);
        if (replaces && _hangUpWarning() is { } warning)
            _panel.Children.Add(Line(warning, Red));

        var install = new Button
            { Content = replaces ? "DOWNLOAD & INSTALL" : "DOWNLOAD", Classes = { "amber" }, IsDefault = true };
        install.Click += async (_, _) => await DownloadAndInstallAsync(info, path, replaces);
        var later = new Button { Content = "LATER", IsCancel = true };
        later.Click += (_, _) => Close();
        AddButtons(install, later);
    }

    private async Task DownloadAndInstallAsync(UpdateInfo info, string path, bool replaces)
    {
        Show($"Downloading {info.Version.ToString(3)}...", null);
        var bar = new ProgressBar { Height = 22, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0) };
        var pct = Line("", Secondary);
        pct.Margin = new Thickness(0, 6, 0, 0);
        _panel.Children.Add(bar);
        _panel.Children.Add(pct);
        var cancel = new Button { Content = "CANCEL", IsCancel = true };
        cancel.Click += (_, _) => Close();
        AddButtons(cancel);

        try
        {
            await UpdateService.DownloadAsync(info, path, new Progress<double>(p =>
            {
                bar.Value = p;
                pct.Text = $"{p * 100:0}%  ({p * info.Size / 1048576.0:0.0} of {info.Size / 1048576.0:0.0} MB)";
            }), _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            Show("The download failed", Reason(ex), Red);
            AddButtons(CloseButton());
            return;
        }

        // The AppImage's folder can't be written: leave the new one in ~/Downloads for them to use
        if (!replaces)
        {
            Show($"Version {info.Version.ToString(3)} downloaded",
                $"TERMinator can't replace itself in its folder, so the new version is here:\n{path}\n\nClose TERMinator and run that file instead.",
                Green);
            AddButtons(CloseButton("OK"));
            return;
        }

        Show("Installing...", "TERMinator closes and starts again with the new version.");
        if (UpdateService.ReplaceAndRelaunch(path) is { } error)
        {
            try { File.Delete(path); } catch { /* nothing to clean up */ }
            Show("The update couldn't be installed", error, Red);
            AddButtons(CloseButton());
            return;
        }
        Close();
        _relaunching();
    }

    private static string Reason(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
            "The TERMinator website has no update information yet.",
        System.Net.Http.HttpRequestException => "Couldn't reach the TERMinator website. Check your internet connection.",
        OperationCanceledException or TimeoutException => "The TERMinator website took too long to answer.",
        _ => ex.Message
    };
}
