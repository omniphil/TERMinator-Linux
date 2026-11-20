using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using TERMinator.Linux.Effects;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Controls;

/// <summary>
/// A snapshot thumbnail that can be drawn through the terminal's CRT shader.
///
/// Draws <see cref="Source"/> stretched to fill its bounds (nearest-neighbour, the way the terminal
/// displays its raw 16:10 pixel grid at 4:3) and, when CRT mode is on, pushes it through the same
/// <see cref="CrtDrawOperation"/> the terminal uses so the Quick Connect tiles look the way the board
/// will once connected. Fail-soft like the terminal: no shader, no GPU or a broken copy just draws the
/// plain picture.
///
/// Scanline / grille pitch is in real device pixels of the thumbnail's ON-SCREEN size. The tiles sit
/// inside the home screen's scaling Viewbox, so the host measures that scale and hands it over as
/// <see cref="LayoutScale"/>; it is folded into the shader's pixel scale together with the display's
/// render scaling.
/// </summary>
public class CrtImage : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<CrtImage, Bitmap?>(nameof(Source));

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Live CRT settings, or null / disabled for the plain picture.</summary>
    public CrtSettings? Crt { get; private set; }

    /// <summary>Extra scale between this control's layout units and the screen (the Viewbox factor). 1 = none.</summary>
    public double LayoutScale { get; set; } = 1.0;

    /// <summary>Seconds for the shader's animated features; the host advances it while the home screen is visible.</summary>
    public double Time { get; set; }

    private SKImage? _skImage;
    // Old copies are parked here instead of disposed on the spot: a draw operation queued for the
    // render thread may still be reading the previous one. Two swaps later it is safely gone.
    private readonly Queue<SKImage> _retired = new();

    static CrtImage()
    {
        AffectsRender<CrtImage>(SourceProperty);
        SourceProperty.Changed.AddClassHandler<CrtImage>((img, _) => img.RebuildSkImage());
    }

    /// <summary>Apply (or with null / disabled, remove) the CRT effect and repaint.</summary>
    public void ApplyCrt(CrtSettings? settings)
    {
        Crt = settings;
        InvalidateVisual();
    }

    private void RebuildSkImage()
    {
        if (_skImage != null)
        {
            _retired.Enqueue(_skImage);
            _skImage = null;
        }
        while (_retired.Count > 2)
            _retired.Dequeue().Dispose();

        _skImage = TerminalCanvas.ToSKImage(Source);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var src = Source;
        return src != null ? new Size(src.PixelSize.Width, src.PixelSize.Height) : default;
    }

    public override void Render(DrawingContext dc)
    {
        var src = Source;
        if (src == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var dest = new Rect(0, 0, Bounds.Width, Bounds.Height);

        if (Crt is { Enabled: true } && CrtShader.IsSupported && _skImage != null)
        {
            try
            {
                double scaling = (VisualRoot?.RenderScaling ?? 1.0) * Math.Max(0.01, LayoutScale);
                var p = CrtParams.From(Crt, Bounds.Height, scaling, Time);
                dc.Custom(new CrtDrawOperation(dest, _skImage, p, ownsContent: false));
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CRT thumbnail failed, drawing plain: {ex.Message}");
            }
        }

        using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            dc.DrawImage(src, new Rect(0, 0, src.PixelSize.Width, src.PixelSize.Height), dest);
        }
    }
}
