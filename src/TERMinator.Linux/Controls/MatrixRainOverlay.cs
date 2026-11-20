using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Konami-code easter egg: full-window digital rain with a decrypting "GOD MODE ACTIVATED". It dismisses itself after a
/// few seconds, or on any key (forwarded by the host through <see cref="Dismiss"/>) or click, and raises
/// <see cref="Finished"/> once faded out so the host can remove it.
/// </summary>
public sealed class MatrixRainOverlay : Grid
{
    public event EventHandler? Finished;

    private static readonly Color Green = Color.FromRgb(0x33, 0xFF, 0x33);
    private static readonly FontFamily BannerFont = new("JetBrains Mono, DejaVu Sans Mono, monospace");

    private readonly DigitalRain _rain = new();
    private readonly TextBlock _banner;
    private DispatcherTimer? _autoCloseTimer;
    private FxTween? _fade;
    private bool _started, _closing;

    public MatrixRainOverlay()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x00, 0x00, 0x00));
        ClipToBounds = true;
        Children.Add(_rain);

        _banner = new TextBlock
        {
            FontFamily = BannerFont,
            FontSize = 42,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Green),
            Effect = new DropShadowEffect { Color = Green, OffsetX = 0, OffsetY = 0, BlurRadius = 24, Opacity = 1 }
        };
        Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x00, 0x00, 0x00)),
            BorderBrush = new SolidColorBrush(Green),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(36, 18, 36, 18),
            Child = _banner
        });

        Loaded += (_, _) => Start();
        PointerPressed += (_, e) =>
        {
            Dismiss();
            e.Handled = true;
        };
    }

    // Never influence the window's size-to-content fit: the overlay just fills whatever space the window has.
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return new Size(0, 0);
    }

    private void Start()
    {
        if (_started)
            return;
        _started = true;

        _rain.Start();
        DecryptText.Play(_banner, "GOD MODE ACTIVATED", TimeSpan.FromMilliseconds(1100));

        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6.5) };
        _autoCloseTimer.Tick += (_, _) => Dismiss();
        _autoCloseTimer.Start();
    }

    /// <summary>Fades the overlay out, then raises <see cref="Finished"/>.</summary>
    public void Dismiss()
    {
        if (_closing)
            return;
        _closing = true;
        _autoCloseTimer?.Stop();

        double from = Opacity;
        _fade = FxTween.Run(TimeSpan.FromMilliseconds(400), t => Opacity = from * (1 - t), completed: () =>
        {
            _rain.Stop();
            Finished?.Invoke(this, EventArgs.Empty);
        });
    }
}
