using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Path = Avalonia.Controls.Shapes.Path;

namespace TERMinator.Linux.Controls;

/// <summary>
/// HUD "target lock" laid over a Quick Connect snapshot. When the pointer enters the tile, corner brackets snap in
/// around the image and the connection target decrypts across a caption strip. It is hit-test invisible, so clicks
/// still reach the tile button underneath.
/// </summary>
public sealed class TargetLockOverlay : Grid
{
    public static readonly StyledProperty<string> TargetProperty =
        AvaloniaProperty.Register<TargetLockOverlay, string>(nameof(Target), string.Empty);

    /// <summary>Caption text, e.g. "TELNET // absinthebbs.net:1940". Nothing is shown while it's empty.</summary>
    public string Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    private static readonly Color HudGreen = Color.FromRgb(0x33, 0xFF, 0x33);
    private static readonly FontFamily HudFont = new("JetBrains Mono, DejaVu Sans Mono, monospace");

    private readonly ScaleTransform _snap = new(1, 1);
    private readonly TextBlock _caption;
    private Button? _host;
    private FxTween? _fade;
    private FxTween? _snapTween;

    public TargetLockOverlay()
    {
        IsHitTestVisible = false;
        Opacity = 0;

        _caption = new TextBlock
        {
            FontFamily = HudFont,
            FontSize = 10,
            Foreground = new SolidColorBrush(HudGreen),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Children.Add(new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(3, 0, 3, 3),
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)),
            Child = _caption
        });

        // Brackets go on top of the caption strip
        var brackets = new Grid
        {
            RenderTransform = _snap,
            RenderTransformOrigin = RelativePoint.Center,
            Effect = new DropShadowEffect { Color = HudGreen, OffsetX = 0, OffsetY = 0, BlurRadius = 6, Opacity = 0.9 }
        };
        brackets.Children.Add(Corner("M0,14 L0,0 L14,0", HorizontalAlignment.Left, VerticalAlignment.Top));
        brackets.Children.Add(Corner("M0,0 L14,0 L14,14", HorizontalAlignment.Right, VerticalAlignment.Top));
        brackets.Children.Add(Corner("M0,0 L0,14 L14,14", HorizontalAlignment.Left, VerticalAlignment.Bottom));
        brackets.Children.Add(Corner("M14,0 L14,14 L0,14", HorizontalAlignment.Right, VerticalAlignment.Bottom));
        Children.Add(brackets);

        AttachedToVisualTree += (_, _) => HookHost();
    }

    private static Path Corner(string data, HorizontalAlignment horizontal, VerticalAlignment vertical) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = new SolidColorBrush(HudGreen),
        StrokeThickness = 2,
        Width = 14,
        Height = 14,
        Stretch = Stretch.None,
        HorizontalAlignment = horizontal,
        VerticalAlignment = vertical,
        Margin = new Thickness(3)
    };

    /// <summary>Finds the enclosing tile button once; the home view is reused, so attachment fires repeatedly.</summary>
    private void HookHost()
    {
        if (_host != null)
            return;

        _host = this.FindAncestorOfType<Button>();
        if (_host == null)
            return;

        _host.PointerEntered += (_, _) => Lock();
        _host.PointerExited += (_, _) => Release();
    }

    private void Lock()
    {
        if (string.IsNullOrEmpty(Target))
            return;

        FadeTo(1, TimeSpan.FromMilliseconds(110));

        // Brackets snap in from 1.22x to 1x, easing out
        _snapTween?.Cancel();
        _snapTween = FxTween.Run(TimeSpan.FromMilliseconds(170), t =>
        {
            double scale = 1.22 + (1 - 1.22) * t;
            _snap.ScaleX = scale;
            _snap.ScaleY = scale;
        }, ease: FxTween.EaseOutCubic);

        DecryptText.Play(_caption, Target, TimeSpan.FromMilliseconds(420));
    }

    private void Release() => FadeTo(0, TimeSpan.FromMilliseconds(140));

    private void FadeTo(double target, TimeSpan duration)
    {
        _fade?.Cancel();
        double from = Opacity;
        _fade = FxTween.Run(duration, t => Opacity = from + (target - from) * t);
    }
}
