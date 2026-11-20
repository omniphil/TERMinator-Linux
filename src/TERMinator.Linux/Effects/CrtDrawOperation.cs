using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Effects;

/// <summary>
/// The uniform values for one frame of the CRT shader. A plain snapshot so the draw operation
/// never touches live control state on the render thread.
/// </summary>
internal readonly struct CrtParams
{
    public float Curvature { get; init; }
    public float ScanlineIntensity { get; init; }
    public float ScanlineCount { get; init; }
    public float MaskIntensity { get; init; }
    public float MaskPitch { get; init; }
    public float Glow { get; init; }
    public float Vignette { get; init; }
    public float Brightness { get; init; }
    public float Aberration { get; init; }
    public float Flicker { get; init; }
    public float Time { get; init; }
    public float TintR { get; init; }
    public float TintG { get; init; }
    public float TintB { get; init; }
    public float TintAmount { get; init; }
    public float Overscan { get; init; }
    public float BloomThreshold { get; init; }
    public float BeamWidth { get; init; }
    public float Interlace { get; init; }
    public float VerticalHold { get; init; }
    public float Noise { get; init; }
    public float ScanSweep { get; init; }
    /// <summary>DIPs to device pixels, so the raster and grille pitches stay crisp on HiDPI.</summary>
    public float PixelScale { get; init; }

    /// <summary>
    /// Build the per-frame uniforms from the persisted settings.
    /// <paramref name="outputHeight"/> is the render rect height in DIPs and
    /// <paramref name="scaling"/> the display's render scaling. Scanline and grille pitch are
    /// specified in DEVICE pixels so the raster stays crisp however the window is sized and
    /// whatever the monitor DPI is, mirroring UpdateCrtGeometry() in the Windows client.
    /// </summary>
    public static CrtParams From(CrtSettings s, double outputHeight, double scaling, double seconds)
    {
        double deviceHeight = Math.Max(1.0, outputHeight * scaling);
        var (r, g, b) = ParseTint(s.TintColor);
        return new CrtParams
        {
            Curvature = (float)s.Curvature,
            ScanlineIntensity = (float)s.ScanlineIntensity,
            // The shader's beam repeats ScanlineCount times over the height, and one
            // repeat is a line plus its gap.
            ScanlineCount = (float)(deviceHeight / Math.Max(1.0, s.ScanlinePitch)),
            MaskIntensity = (float)s.MaskIntensity,
            MaskPitch = (float)Math.Max(1.0, s.MaskPitch),
            Glow = (float)s.Glow,
            Vignette = (float)s.Vignette,
            Brightness = (float)s.Brightness,
            Aberration = (float)s.Aberration,
            Flicker = (float)s.Flicker,
            Time = (float)seconds,
            TintR = r, TintG = g, TintB = b,
            TintAmount = (float)s.TintAmount,
            Overscan = (float)s.Overscan,
            BloomThreshold = (float)s.BloomThreshold,
            BeamWidth = (float)Math.Max(0.05, s.BeamWidth),
            Interlace = (float)s.Interlace,
            VerticalHold = (float)s.VerticalHold,
            Noise = (float)s.Noise,
            ScanSweep = (float)s.ScanSweep,
            PixelScale = (float)Math.Max(0.1, scaling)
        };
    }

    /// <summary>True when any effect reads the Time uniform, so the host knows to animate.</summary>
    public static bool IsAnimated(CrtSettings s) =>
        s.Flicker > 0 || s.Interlace > 0 || s.VerticalHold > 0 || s.Noise > 0 || s.ScanSweep > 0;

    private static (float r, float g, float b) ParseTint(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return (1f, 1f, 1f);
        var t = hex.TrimStart('#');
        if (t.Length == 6
            && int.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out int v))
        {
            return (((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
        }
        return (1f, 1f, 1f);
    }
}

/// <summary>
/// Draws the composed terminal picture through the CRT shader.
///
/// Avalonia has no shader-effect equivalent to the WPF <c>ShaderEffect</c> the Windows client
/// hangs on its terminal canvas, so the post-process happens here instead: the caller hands over
/// the whole picture as one SKImage and this stretches it into the render rect through
/// <see cref="CrtShader"/>.
///
/// ⚠️ The image arrives at the terminal's NATIVE pixel size (640x400 and so on), not at output
/// size, because composing at native size is cheap and it is what the canvas already has. SKSL
/// evaluates a child shader in the coordinates it is given, so the image shader carries a local
/// matrix scaling native pixels up to the render rect — get that wrong and the picture samples
/// from a corner of itself.
/// </summary>
internal sealed class CrtDrawOperation : ICustomDrawOperation
{
    private readonly SKImage _content;
    private readonly CrtParams _p;
    private readonly bool _ownsContent;

    public CrtDrawOperation(Rect bounds, SKImage content, CrtParams p, bool ownsContent = true)
    {
        Bounds = bounds;
        _content = content;
        _p = p;
        _ownsContent = ownsContent;
    }

    public Rect Bounds { get; }

    /// <summary>The canvas handles its own input; the effect is purely visual.</summary>
    public bool HitTest(Point p) => false;

    /// <summary>Every frame is a new picture, so nothing is ever reusable.</summary>
    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
        if (_ownsContent)
            _content.Dispose();
    }

    public void Render(ImmediateDrawingContext context)
    {
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease == null)
            return;                        // not the Skia backend — caller drew a fallback

        using var api = lease.Lease();
        var canvas = api.SkCanvas;

        // ⚠️ NO GPU CONTEXT MEANS NO SHADER — and this check is what stops the app dying.
        //
        // SkiaSharp 2.88's Linux build ABORTS THE PROCESS (SIGILL, an SkASSERT in release) the
        // moment an SkSL runtime effect is drawn on the CPU rasterizer. Not an exception, not a
        // blank frame: the whole client goes. It is not catchable, so it has to be prevented.
        // Verified 2026-09-12 with a standalone probe: a runtime effect with no child and no
        // sampling still aborts on a raster surface, while a plain SKShader draws fine.
        //
        // The effect COMPILES fine on that path, so CrtShader.IsSupported cannot be the guard.
        // GrContext is null exactly when Avalonia has fallen back to software rendering, which
        // is the case that kills it — no VM, no remote X session without GLX, no GPU.
        if (api.GrContext == null)
        {
            ReportOnce("CRT: software rendering in use, effect disabled "
                     + "(an SkSL runtime effect cannot run on Skia's CPU backend)");
            DrawPlain(canvas);
            return;
        }

        var effect = CrtShader.Effect;
        if (effect == null)
        {
            // Shader unavailable: show the terminal unfiltered rather than a blank surface.
            ReportOnce("CRT: shader unavailable, effect disabled" +
                       (CrtShader.Error is { Length: > 0 } e ? " - " + e : ""));
            DrawPlain(canvas);
            return;
        }

        float outW = (float)Bounds.Width;
        float outH = (float)Bounds.Height;
        if (outW <= 0 || outH <= 0) return;

        // Native image pixels -> render rect pixels. Bounds is already in the local space the
        // canvas is drawing in, so the shader's coordinates and this matrix agree.
        var localMatrix = SKMatrix.CreateScale(
            outW / Math.Max(1, _content.Width),
            outH / Math.Max(1, _content.Height));

        using var input = SKShader.CreateImage(
            _content, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, localMatrix);

        var uniforms = BuildUniforms(effect, _p, outW, outH);

        var children = new SKRuntimeEffectChildren(effect) { ["Input"] = input };

        // isOpaque:false — the composed picture can carry transparency where a TAP+ background
        // underlay shows through, and the shader multiplies colour by alpha at the end.
        using var shader = effect.ToShader(false, uniforms, children);
        if (shader == null)
        {
            DrawPlain(canvas);
            return;
        }

        ReportOnce("CRT: active");

        using var paint = new SKPaint { Shader = shader, IsAntialias = false };
        canvas.Save();
        canvas.Translate((float)Bounds.X, (float)Bounds.Y);
        canvas.DrawRect(SKRect.Create(0, 0, outW, outH), paint);
        canvas.Restore();
    }

    /// <summary>
    /// Every uniform for one frame. Shared with <see cref="BackdropEffectDrawOperation"/>, which runs the same shader
    /// over a snapshot of the picture underneath it instead of over a composed terminal image.
    /// </summary>
    internal static SKRuntimeEffectUniforms BuildUniforms(SKRuntimeEffect effect, CrtParams p, float outW, float outH) =>
        new(effect)
        {
            ["OutputSize"] = new[] { outW, outH },
            ["Curvature"] = p.Curvature,
            ["ScanlineIntensity"] = p.ScanlineIntensity,
            ["ScanlineCount"] = p.ScanlineCount,
            ["MaskIntensity"] = p.MaskIntensity,
            ["MaskPitch"] = p.MaskPitch,
            ["Glow"] = p.Glow,
            ["Vignette"] = p.Vignette,
            ["Brightness"] = p.Brightness,
            ["Aberration"] = p.Aberration,
            ["Flicker"] = p.Flicker,
            ["Time"] = p.Time,
            ["Tint"] = new[] { p.TintR, p.TintG, p.TintB, 1f },
            ["TintAmount"] = p.TintAmount,
            ["Overscan"] = p.Overscan,
            ["BloomThreshold"] = p.BloomThreshold,
            ["BeamWidth"] = p.BeamWidth,
            ["Interlace"] = p.Interlace,
            ["VerticalHold"] = p.VerticalHold,
            ["Noise"] = p.Noise,
            ["ScanSweep"] = p.ScanSweep,
            ["PixelScale"] = p.PixelScale
        };

    private static string? _reported;

    /// <summary>
    /// Say once, on stderr, whether the effect is running and why not if it isn't. The user
    /// turned the CRT on and would otherwise have no way to tell a silent GPU fallback from a
    /// preset that simply looks subtle. Repeats are suppressed because this is on the render
    /// path — but a CHANGE of state is worth saying, so the last message is what gets compared.
    /// </summary>
    private static void ReportOnce(string message)
    {
        if (_reported == message) return;
        _reported = message;
        Console.Error.WriteLine(message);
    }

    /// <summary>Unfiltered fallback so a shader problem never blanks the terminal.</summary>
    private void DrawPlain(SKCanvas canvas)
    {
        using var paint = new SKPaint { IsAntialias = false };
        canvas.DrawImage(_content,
            SKRect.Create((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Width, (float)Bounds.Height),
            paint);
    }
}
