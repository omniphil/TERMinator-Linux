using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Effects;

/// <summary>
/// The screen effect layer for the home screen and the command line (§7): a monochrome phosphor tube (Green / Amber /
/// White) and, on the console, the full CRT effect in the phosphor color.
///
/// Windows hangs a WPF <c>ShaderEffect</c> on the element itself (<c>CrtShaderEffect.CreateTintOnly</c> for the
/// phosphor, the full shader for the console). Avalonia has no element effect that can do either, so this is a
/// hit-test invisible sibling drawn ON TOP of what it tints: it snapshots the picture already drawn underneath and
/// paints it back through the effect. Everything else is the same — the same luminance × phosphor colour with
/// brightness × 1.2, and the same <see cref="CrtShader"/> the terminal uses.
///
/// Fail-soft, in three steps: shader (needs a GPU context) → luminance colour matrix (no shader needed) →
/// desaturate + multiply blend (no surface snapshot needed). The terminal is never tinted, so nothing here touches it.
/// </summary>
public sealed class ScreenEffectOverlay : Control
{
    private CrtSettings? _crt;
    private Color? _tint;
    private DispatcherTimer? _animTimer;
    private readonly Stopwatch _clock = new();

    public ScreenEffectOverlay()
    {
        IsHitTestVisible = false;
        AttachedToVisualTree += (_, _) => UpdateAnimation();
        DetachedFromVisualTree += (_, _) => StopAnimation();
    }

    /// <summary>
    /// Sets the effect: <paramref name="crt"/> non-null and enabled adds the full CRT post-process (the console only),
    /// <paramref name="tint"/> the phosphor colour. Both null / off removes the effect.
    /// </summary>
    public void Configure(CrtSettings? crt, Color? tint)
    {
        _crt = crt is { Enabled: true } ? crt : null;
        _tint = tint;
        UpdateAnimation();
        InvalidateVisual();
    }

    /// <summary>True when nothing at all is being drawn, so a host can skip its own per-frame invalidation.</summary>
    public bool IsIdle => _crt == null && _tint == null;

    // Never influence the window's size-to-content fit or the home layout measurement: the overlay just covers
    // whatever space its panel gives it.
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return default;
    }

    /// <summary>The CRT's animated features (flicker, interlace, roll, noise, sweep) only tick while this is up.</summary>
    private void UpdateAnimation()
    {
        bool animated = _crt != null && CrtParams.IsAnimated(_crt) && this.GetVisualRoot() != null;
        if (!animated)
        {
            StopAnimation();
            return;
        }
        if (_animTimer != null)
            return;
        _clock.Restart();
        _animTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _animTimer.Tick += (_, _) => InvalidateVisual();
        _animTimer.Start();
    }

    private void StopAnimation()
    {
        _animTimer?.Stop();
        _animTimer = null;
        _clock.Stop();
    }

    public override void Render(DrawingContext dc)
    {
        if (IsIdle || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var tint = _tint is { } color ? (color.R / 255f, color.G / 255f, color.B / 255f) : ((float, float, float)?)null;

        CrtParams? crt = null;
        if (_crt is { } settings && CrtShader.IsSupported)
        {
            try
            {
                double scaling = VisualRoot?.RenderScaling ?? 1.0;
                var p = CrtParams.From(settings, Bounds.Height, scaling, _clock.Elapsed.TotalSeconds);
                // The effect is drawn in real device pixels (see the draw operation), so the pitches are already in
                // device units there and the phosphor colour, if any, replaces the CRT window's own tint.
                crt = tint is { } t
                    ? p with { PixelScale = 1f, TintR = t.Item1, TintG = t.Item2, TintB = t.Item3, TintAmount = 1f }
                    : p with { PixelScale = 1f };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Screen effect parameters failed, tint only: {ex.Message}");
            }
        }

        dc.Custom(new BackdropEffectDrawOperation(rect, crt, tint));
    }
}

/// <summary>
/// Draws the picture already on the surface back through the CRT shader (or a luminance colour matrix), which is how
/// <see cref="ScreenEffectOverlay"/> tints what is underneath it without owning it.
/// </summary>
internal sealed class BackdropEffectDrawOperation : ICustomDrawOperation
{
    /// <summary>Phosphor brightness gain, matching CrtShaderEffect.CreateTintOnly on Windows.</summary>
    private const float TintBrightness = 1.2f;

    private readonly CrtParams? _crt;
    private readonly (float R, float G, float B)? _tint;

    public BackdropEffectDrawOperation(Rect bounds, CrtParams? crt, (float R, float G, float B)? tint)
    {
        Bounds = bounds;
        _crt = crt;
        _tint = tint;
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease == null)
            return;                                // not the Skia backend: leave the picture alone

        using var api = lease.Lease();
        var canvas = api.SkCanvas;
        var local = SKRect.Create((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Width, (float)Bounds.Height);

        var surface = api.SkSurface;
        if (surface == null)
        {
            BlendTint(canvas, local);              // no surface to read back: approximate with blend modes
            return;
        }

        // The region to re-process, in the surface's own device pixels. Deliberately the WHOLE overlay every time,
        // only clamped to the surface: the shader's geometry (scanline count, curvature, vignette) is relative to the
        // region it is given, so processing just the dirty rectangle would draw bands at a different pitch there. The
        // canvas clip still limits what actually lands on screen, so a partial repaint costs only the snapshot.
        var region = SKRectI.Round(canvas.TotalMatrix.MapRect(local));
        if (region.Left < 0) region.Left = 0;
        if (region.Top < 0) region.Top = 0;
        if (region.Width <= 0 || region.Height <= 0 || !region.IntersectsWith(canvas.DeviceClipBounds))
            return;

        SKImage? backdrop = null;
        try
        {
            backdrop = surface.Snapshot(region);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Screen effect snapshot failed: {ex.Message}");
        }
        if (backdrop == null)
        {
            BlendTint(canvas, local);
            return;
        }

        try
        {
            canvas.Save();
            // Device pixels from here on: the snapshot is in them, and the CRT's pitches are specified in them.
            canvas.ResetMatrix();
            canvas.Translate(region.Left, region.Top);
            // Skia clamps a snapshot request to the surface, so the image's own size is the truth here
            var target = SKRect.Create(0, 0, backdrop.Width, backdrop.Height);

            // ⚠️ NEVER run an SkSL runtime effect without a GPU context — SkiaSharp 2.88 on Linux aborts the whole
            // process on Skia's CPU backend (see CrtDrawOperation for the full story).
            if (_crt is { } p && api.GrContext != null && CrtShader.Effect is { } effect)
            {
                using var input = SKShader.CreateImage(backdrop, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                var uniforms = CrtDrawOperation.BuildUniforms(effect, p, backdrop.Width, backdrop.Height);
                var children = new SKRuntimeEffectChildren(effect) { ["Input"] = input };
                using var shader = effect.ToShader(true, uniforms, children);
                if (shader != null)
                {
                    using var paint = new SKPaint { Shader = shader, IsAntialias = false, BlendMode = SKBlendMode.Src };
                    canvas.DrawRect(target, paint);
                    return;
                }
            }

            // No shader (or CRT mode off): the phosphor on its own, as a luminance × colour matrix. Same maths as the
            // shader's tint stage, and it needs no GPU context.
            if (_tint is { } tint)
            {
                using var paint = new SKPaint
                {
                    IsAntialias = false,
                    BlendMode = SKBlendMode.Src,
                    ColorFilter = SKColorFilter.CreateColorMatrix(LuminanceMatrix(tint))
                };
                canvas.DrawImage(backdrop, target, paint);
            }
        }
        finally
        {
            canvas.Restore();
            backdrop.Dispose();
        }
    }

    /// <summary>Luminance (0.299 / 0.587 / 0.114) times the phosphor colour, brightness × 1.2.</summary>
    private static float[] LuminanceMatrix((float R, float G, float B) tint)
    {
        float r = tint.R * TintBrightness, g = tint.G * TintBrightness, b = tint.B * TintBrightness;
        return
        [
            0.299f * r, 0.587f * r, 0.114f * r, 0, 0,
            0.299f * g, 0.587f * g, 0.114f * g, 0, 0,
            0.299f * b, 0.587f * b, 0.114f * b, 0, 0,
            0,          0,          0,          1, 0
        ];
    }

    /// <summary>
    /// Last resort where the surface can't be read back: desaturate what is underneath with a Saturation blend, then
    /// multiply the phosphor colour over it. Skia's HSL luminance (0.3 / 0.59 / 0.11) is near enough to the shader's,
    /// and the × 1.2 gain is folded into the colour instead of applied after the multiply.
    /// </summary>
    private void BlendTint(SKCanvas canvas, SKRect rect)
    {
        if (_tint is not { } tint)
            return;
        using var desaturate = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.Saturation };
        canvas.DrawRect(rect, desaturate);
        using var multiply = new SKPaint
        {
            Color = new SKColor(Scale(tint.R), Scale(tint.G), Scale(tint.B)),
            BlendMode = SKBlendMode.Modulate
        };
        canvas.DrawRect(rect, multiply);
    }

    private static byte Scale(float channel) => (byte)Math.Clamp(channel * TintBrightness * 255f, 0, 255);
}
