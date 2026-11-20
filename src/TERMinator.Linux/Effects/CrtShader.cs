using SkiaSharp;

namespace TERMinator.Linux.Effects;

/// <summary>
/// The CRT / scanline post-process, as an SKSL runtime effect.
///
/// This is a translation of the Windows client's ps_3_0 shader
/// (TERMinator-Windows/src/TERMinator.Windows/Shaders/CrtEffect.fx). Windows drives that
/// through a WPF <c>ShaderEffect</c>; Avalonia has no equivalent, so the same maths runs here
/// as a Skia runtime shader inside a custom draw operation. Keep the two in step — the knobs,
/// their ranges and the presets in <see cref="Models.CrtSettings"/> are shared, and a change to
/// the look belongs in BOTH files or the clients drift apart.
///
/// ⚠️ Two things differ from HLSL and will silently produce a wrong picture if forgotten:
///   • SKSL runs in LOCAL PIXEL coordinates, not 0..1 texture space. Everything below converts
///     with OutputSize, and the input child shader is sampled in pixels via Input.eval().
///   • SKSL has no tex2D/lerp/frac/saturate/fmod. They map to eval/mix/fract/clamp/mod.
/// </summary>
internal static class CrtShader
{
    /// <summary>
    /// SKSL source. The uniform names match the CrtSettings property names so
    /// <see cref="CrtDrawOperation"/> can set them without a lookup table.
    /// </summary>
    private const string Sksl = @"
uniform shader Input;

uniform float2 OutputSize;        // render rect size in the shader's own (DIP) space
uniform float  PixelScale;        // DIPs -> device pixels, for the pitches that must be crisp
uniform float  Curvature;         // 0 = flat, 1 = strongly bowed tube
uniform float  ScanlineIntensity; // 0..1 depth of the raster lines
uniform float  ScanlineCount;     // raster lines down the screen
uniform float  MaskIntensity;     // 0..1 aperture-grille strength
uniform float  MaskPitch;         // output pixels per RGB triad
uniform float  Glow;              // phosphor bloom amount
uniform float  Vignette;          // corner darkening (exponent)
uniform float  Brightness;        // output gain
uniform float  Aberration;        // R/B horizontal separation
uniform float  Flicker;           // mains-hum brightness wobble
uniform float  Time;              // seconds, animated by the host
uniform float4 Tint;              // monochrome phosphor color
uniform float  TintAmount;        // 0 = full color, 1 = monochrome
uniform float  Overscan;          // pulls the picture back inside the curve
uniform float  BloomThreshold;    // only pixels above this bloom
uniform float  BeamWidth;         // raster beam thickness, 0..1
uniform float  Interlace;         // alternating-field dimming
uniform float  VerticalHold;      // rolling hum bar
uniform float  Noise;             // static / snow
uniform float  ScanSweep;         // bright refresh line sweeping down

const float PI = 3.14159265;

// Sample the source in UV space. The child shader is in pixels, so scale back up.
//
// ⚠️ sample(Input, coord), NOT Input.eval(coord). SkiaSharp 2.88 bundles a Skia whose SKSL
// still models a child shader as a 'fragmentProcessor' with no .eval member, and the newer
// spelling fails to COMPILE at runtime with: type 'fragmentProcessor' does not have a field
// named 'eval'. Nothing catches that at build time — the shader source is a string.
half4 tex(float2 uv) { return sample(Input, uv * OutputSize); }

// Barrel-distort the sample position so the picture bows out like a tube face,
// then divide back down so the source edges land on the screen edges instead of
// being pushed off it. Without the overscan term a strong curve simply clips
// the outer rows and columns.
float2 CurveUV(float2 uv, float amount, float overscan) {
    uv = uv * 2.0 - 1.0;
    float2 off = abs(uv.yx) * float2(0.1666667, 0.25);
    uv += uv * off * off * amount;
    uv /= (1.0 + overscan);
    return uv * 0.5 + 0.5;
}

// x modulo y. ⚠️ This SKSL has no mod() either — fract() is the portable spelling.
float Mod(float x, float y) { return x - y * floor(x / y); }

// Cheap value noise -- enough for grain, and it costs one sin.
float Hash(float2 p) {
    return fract(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

half4 main(float2 fragCoord) {
    float2 uv0 = fragCoord / OutputSize;

    // Rolling hum bar: a soft band drifting up the screen that both darkens
    // and slightly pulls the picture, the way a tube with the vertical hold
    // just off does.
    float barPhase = fract(uv0.y - Time * 0.12);
    float bar = pow(clamp(sin(barPhase * PI), 0.0, 1.0), 8.0);

    float2 uvIn = uv0;
    uvIn.y += VerticalHold * 0.004 * bar;

    float2 uv = CurveUV(uvIn, Curvature * 2.0, Overscan);

    // Anything the curvature pushed off the tube face contributes nothing.
    // ⚠️ Written as a comparison rather than step(): this SKSL has no step().
    float inside = (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0) ? 1.0 : 0.0;

    float2 cuv = clamp(uv, 0.0, 1.0);
    half4 center = tex(cuv);
    float alpha = float(center.a);

    // Texel size in UV terms -- HLSL got this as a uniform; here it is just 1/OutputSize.
    float2 texel = 1.0 / OutputSize;

    // Chromatic aberration: pull red and blue apart horizontally.
    float ab = Aberration * texel.x * 4.0;
    float3 col;
    col.r = float(tex(float2(clamp(cuv.x + ab, 0.0, 1.0), cuv.y)).r);
    col.g = float(center.g);
    col.b = float(tex(float2(clamp(cuv.x - ab, 0.0, 1.0), cuv.y)).b);

    // Phosphor glow: cheap 8-tap ring blur, thresholded so only genuinely
    // bright pixels bloom instead of the whole picture going soft.
    float2 o = texel * 1.5;
    float3 g  = float3(tex(cuv + float2( o.x, 0.0)).rgb);
    g += float3(tex(cuv + float2(-o.x, 0.0)).rgb);
    g += float3(tex(cuv + float2(0.0,  o.y)).rgb);
    g += float3(tex(cuv + float2(0.0, -o.y)).rgb);
    g += float3(tex(cuv + float2( o.x,  o.y)).rgb);
    g += float3(tex(cuv + float2(-o.x,  o.y)).rgb);
    g += float3(tex(cuv + float2( o.x, -o.y)).rgb);
    g += float3(tex(cuv + float2(-o.x, -o.y)).rgb);
    g *= 0.125;
    g = max(g - BloomThreshold, 0.0) / max(1.0 - BloomThreshold, 0.001);
    col += g * Glow;

    // Monochrome phosphor (green/amber/white tubes).
    float lum = dot(col, float3(0.299, 0.587, 0.114));
    col = mix(col, lum * Tint.rgb, TintAmount);

    // Raster lines. The beam is a gaussian across the line rather than a plain
    // cosine, so BeamWidth can go from a soft wide sweep to a tight bright
    // filament with dark gaps either side.
    float linePos = fract(uv.y * ScanlineCount) - 0.5;
    float sigma = max(BeamWidth * BeamWidth * 0.25, 0.0001);
    float beam = exp(-(linePos * linePos) / sigma);
    col *= mix(1.0, beam, ScanlineIntensity);

    // Interlace: dim alternate raster lines, swapping fields every frame.
    float field = Mod(floor(Time * 60.0), 2.0);
    float odd = Mod(floor(uv.y * ScanlineCount), 2.0);
    col *= 1.0 - Interlace * 0.5 * abs(odd - field);

    // Aperture grille, measured in real output pixels so it stays put when
    // the window is resized.
    // ⚠️ MaskPitch is specified in DEVICE pixels so the grille stays crisp on a HiDPI
    // screen. The shader works in DIPs (Skia evaluates a shader in local space, and the
    // rect is drawn in DIPs), so the width has to be converted before dividing.
    float m = fract(uv0.x * OutputSize.x * PixelScale / max(MaskPitch, 1.0)) * 3.0;
    float3 mask;
    mask.r = clamp(1.0 - abs(m - 0.5), 0.0, 1.0);
    mask.g = clamp(1.0 - abs(m - 1.5), 0.0, 1.0);
    mask.b = clamp(1.0 - abs(m - 2.5), 0.0, 1.0);
    col *= mix(float3(1.0), clamp(mask * 1.5 + 0.25, 0.0, 1.0), MaskIntensity);

    // Vignette.
    float2 v = uv * (1.0 - uv);
    col *= pow(clamp(v.x * v.y * 16.0, 0.0, 1.0), Vignette);

    // Rolling bar darkening.
    col *= 1.0 - VerticalHold * 0.30 * bar;

    // Mains-hum flicker.
    col *= 1.0 + Flicker * 0.08 * sin(Time * 47.0);

    col *= Brightness;

    // Rolling refresh line: a bright band sweeping down the tube, the way an
    // out-of-sync CRT shows its own scan. Mostly proportional to what is
    // already lit, plus a small additive floor so the sweep stays visible
    // crossing black areas.
    float sweepY = fract(Time * 0.30);
    float sd = abs(uv.y - sweepY);
    sd = min(sd, 1.0 - sd);                       // wrap instead of popping
    float sweep = exp(-(sd * sd) / 0.0004);
    col += (col * 0.8 + 0.05) * sweep * ScanSweep;

    // Static / snow, added last so the grain sits on top of the picture.
    float grain = Hash(uv0 * OutputSize * PixelScale + Time * 60.0) - 0.5;
    col += grain * Noise * 0.35;

    col *= inside;
    alpha *= inside;

    // Skia works in premultiplied alpha -- never let a channel exceed alpha.
    col = min(clamp(col, 0.0, 1.0), float3(alpha));
    return half4(half3(col), half(alpha));
}
";

    private static SKRuntimeEffect? _effect;
    private static bool _tried;
    private static string? _error;

    /// <summary>
    /// The compiled effect, or null if this machine's Skia rejected the source.
    /// Compiled once and cached; a failure is remembered so we don't retry every frame.
    /// </summary>
    internal static SKRuntimeEffect? Effect
    {
        get
        {
            if (_tried) return _effect;
            _tried = true;
            try
            {
                _effect = SKRuntimeEffect.Create(Sksl, out _error);
                if (_effect == null)
                    Console.Error.WriteLine($"[CRT] shader failed to compile: {_error}");
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                Console.Error.WriteLine($"[CRT] shader unavailable: {ex.Message}");
            }
            return _effect;
        }
    }

    /// <summary>
    /// False when the shader could not be compiled — the caller then draws the terminal
    /// unfiltered rather than showing a blank surface, matching what the Windows client does
    /// on a GPU without Shader Model 3.0.
    /// </summary>
    internal static bool IsSupported => Effect != null;

    /// <summary>Compiler diagnostics from the failed build, for the options window.</summary>
    internal static string? Error => _error;
}
