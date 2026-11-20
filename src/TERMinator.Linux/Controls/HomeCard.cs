using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Home screen section card: a rounded lifted surface (#1A1A2E, radius 10, padding 20) with three layers —
/// an empty shadow layer behind it carrying the soft violet glow, the surface itself with a TRANSPARENT
/// 1.5 px border that only reserves the outline's width, and a hit-test-invisible outline layer on top in
/// the card's own colour (<see cref="OutlineColor"/>). While the pointer is anywhere over the card the
/// outline thickens 1.5 → 3 px growing inward (nothing moves) and a glow in the same colour fades in on
/// both sides of the line (blur 14, opacity 0 → 0.9 in 0.15 s, back to 0 in 0.2 s). Static otherwise.
///
/// The template lives in RetroTheme.axaml (Selector "controls|HomeCard"); this class only turns the one
/// colour into the brush and the two BoxShadows the template binds to. The shadow is a separate layer so
/// the animated splash inside the card never renders through a blur.
/// </summary>
public class HomeCard : ContentControl
{
    public static readonly StyledProperty<Color> OutlineColorProperty =
        AvaloniaProperty.Register<HomeCard, Color>(nameof(OutlineColor), Color.Parse("#FF7B5CFF"));

    public static readonly DirectProperty<HomeCard, IBrush> OutlineBrushProperty =
        AvaloniaProperty.RegisterDirect<HomeCard, IBrush>(nameof(OutlineBrush), o => o.OutlineBrush);

    public static readonly DirectProperty<HomeCard, BoxShadows> IdleGlowProperty =
        AvaloniaProperty.RegisterDirect<HomeCard, BoxShadows>(nameof(IdleGlow), o => o.IdleGlow);

    public static readonly DirectProperty<HomeCard, BoxShadows> HoverGlowProperty =
        AvaloniaProperty.RegisterDirect<HomeCard, BoxShadows>(nameof(HoverGlow), o => o.HoverGlow);

    /// <summary>The card's own colour: splash magenta #FF2BD6, Quick Connect cyan #22E6FF, shortcuts violet #7B5CFF, facts amber #FFAA00.</summary>
    public Color OutlineColor
    {
        get => GetValue(OutlineColorProperty);
        set => SetValue(OutlineColorProperty, value);
    }

    private IBrush _outlineBrush = Brushes.Transparent;
    public IBrush OutlineBrush
    {
        get => _outlineBrush;
        private set => SetAndRaise(OutlineBrushProperty, ref _outlineBrush, value);
    }

    private BoxShadows _idleGlow;
    /// <summary>The hover glow at opacity 0 — same shape as <see cref="HoverGlow"/> so the transition interpolates colour only.</summary>
    public BoxShadows IdleGlow
    {
        get => _idleGlow;
        private set => SetAndRaise(IdleGlowProperty, ref _idleGlow, value);
    }

    private BoxShadows _hoverGlow;
    /// <summary>Blur 14 at opacity 0.9 in the outline colour, outside and inside the line.</summary>
    public BoxShadows HoverGlow
    {
        get => _hoverGlow;
        private set => SetAndRaise(HoverGlowProperty, ref _hoverGlow, value);
    }

    static HomeCard()
    {
        OutlineColorProperty.Changed.AddClassHandler<HomeCard>((card, _) => card.UpdateFromColor());
    }

    public HomeCard()
    {
        UpdateFromColor();
    }

    private void UpdateFromColor()
    {
        var c = OutlineColor;
        OutlineBrush = new SolidColorBrush(c);
        IdleGlow = Glow(Color.FromArgb(0, c.R, c.G, c.B));
        HoverGlow = Glow(Color.FromArgb(0xE6, c.R, c.G, c.B)); // 0.9 opacity
    }

    private static BoxShadows Glow(Color color)
    {
        var outer = new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 14, Spread = 0, Color = color, IsInset = false };
        var inner = new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 14, Spread = 0, Color = color, IsInset = true };
        return new BoxShadows(outer, new[] { inner });
    }
}
