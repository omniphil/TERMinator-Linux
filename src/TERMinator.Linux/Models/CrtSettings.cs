namespace TERMinator.Linux.Models;

/// <summary>
/// CRT / scanline post-process parameters. Persisted with the rest of the app
/// settings and pushed straight into the pixel shader, so edits take effect on
/// the live terminal without reconnecting.
/// </summary>
public class CrtSettings
{
    public bool Enabled { get; set; }

    /// <summary>Name of the last preset applied, for the options dialog.</summary>
    public string Preset { get; set; } = "Classic CRT";

    /// <summary>Depth of the raster lines, 0 (none) to 1 (black between lines).</summary>
    public double ScanlineIntensity { get; set; } = 0.35;

    /// <summary>Device pixels between raster lines. Kept in output pixels so the
    /// lines stay crisp at any window size.</summary>
    public double ScanlinePitch { get; set; } = 3;

    /// <summary>Barrel distortion of the tube face, 0 (flat) to 1.</summary>
    public double Curvature { get; set; }

    /// <summary>Aperture-grille strength, 0 to 1.</summary>
    public double MaskIntensity { get; set; }

    /// <summary>Device pixels per RGB triad of the aperture grille.</summary>
    public double MaskPitch { get; set; } = 3;

    /// <summary>Phosphor bloom around bright glyphs, 0 to 1.</summary>
    public double Glow { get; set; } = 0.25;

    /// <summary>Corner darkening, 0 (none) to 1.</summary>
    public double Vignette { get; set; } = 0.15;

    /// <summary>Output gain — scanlines and the grille cost light, so this
    /// buys it back.</summary>
    public double Brightness { get; set; } = 1.15;

    /// <summary>Horizontal red/blue separation, 0 to 1.</summary>
    public double Aberration { get; set; }

    /// <summary>Mains-hum brightness wobble, 0 to 1.</summary>
    public double Flicker { get; set; }

    /// <summary>Blend toward a single-phosphor tube, 0 (full color) to 1.</summary>
    public double TintAmount { get; set; }

    /// <summary>Pulls the picture back inside the curved face so a strong
    /// <see cref="Curvature"/> doesn't clip the outer rows and columns.</summary>
    public double Overscan { get; set; }

    /// <summary>Only pixels brighter than this bloom, so glow picks out bright
    /// glyphs instead of softening the whole screen.</summary>
    public double BloomThreshold { get; set; } = 0.35;

    /// <summary>Raster beam thickness. 1 is a soft wide sweep, 0.2 a tight
    /// bright filament with dark gaps between lines.</summary>
    public double BeamWidth { get; set; } = 1.0;

    /// <summary>Alternating-field dimming, 0 to 1.</summary>
    public double Interlace { get; set; }

    /// <summary>Rolling hum bar drifting up the screen, 0 to 1.</summary>
    public double VerticalHold { get; set; }

    /// <summary>Static / snow grain, 0 to 1.</summary>
    public double Noise { get; set; }

    /// <summary>Brightness of the refresh line sweeping down the screen, 0 to 1.</summary>
    public double ScanSweep { get; set; }

    /// <summary>Phosphor color used when <see cref="TintAmount"/> is above 0.</summary>
    public string TintColor { get; set; } = "#FFFFFF";

    public CrtSettings Clone() => (CrtSettings)MemberwiseClone();

    public void CopyFrom(CrtSettings other)
    {
        Enabled = other.Enabled;
        Preset = other.Preset;
        ScanlineIntensity = other.ScanlineIntensity;
        ScanlinePitch = other.ScanlinePitch;
        Curvature = other.Curvature;
        MaskIntensity = other.MaskIntensity;
        MaskPitch = other.MaskPitch;
        Glow = other.Glow;
        Vignette = other.Vignette;
        Brightness = other.Brightness;
        Aberration = other.Aberration;
        Flicker = other.Flicker;
        TintAmount = other.TintAmount;
        TintColor = other.TintColor;
        Overscan = other.Overscan;
        BloomThreshold = other.BloomThreshold;
        BeamWidth = other.BeamWidth;
        Interlace = other.Interlace;
        VerticalHold = other.VerticalHold;
        Noise = other.Noise;
        ScanSweep = other.ScanSweep;
    }

    /// <summary>The look the Reset button restores.</summary>
    public const string DefaultPresetName = "Subtle Scanlines";

    /// <summary>
    /// Factory settings — the mildest preset, full color, white tube. Reset
    /// goes back to exactly this.
    /// </summary>
    public static CrtSettings Factory() => FromPreset(DefaultPresetName);

    /// <summary>
    /// Named starting points offered in the options dialog. Each returns a
    /// fresh instance so callers can edit it freely.
    /// </summary>
    public static readonly string[] PresetNames =
    {
        // Roughly ordered mildest-to-wildest: color tubes, then single
        // phosphor screens, then the beaten-up end of the range.
        "Subtle Scanlines",
        "Classic CRT",
        "Studio Monitor",
        "Composite TV",
        "Console TV",
        "Arcade Monitor",
        "Green Phosphor",
        "Amber Phosphor",
        "Paper White",
        "Gas Plasma",
        "Night Owl",
        "Worn Out Tube"
    };

    public static CrtSettings FromPreset(string name) => name switch
    {
        "Subtle Scanlines" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.20, ScanlinePitch = 3,
            Curvature = 0.0, MaskIntensity = 0.0, MaskPitch = 3,
            Glow = 0.10, Vignette = 0.0, Brightness = 1.08,
            Aberration = 0.0, Flicker = 0.0, TintAmount = 0.0,
            Overscan = 0.0, BloomThreshold = 0.45, BeamWidth = 1.0,
            Interlace = 0.0, VerticalHold = 0.0, Noise = 0.0, ScanSweep = 0.0
        },
        "Arcade Monitor" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.55, ScanlinePitch = 3,
            Curvature = 0.10, MaskIntensity = 0.55, MaskPitch = 3,
            Glow = 0.45, Vignette = 0.25, Brightness = 1.45,
            Aberration = 0.15, Flicker = 0.05, TintAmount = 0.0,
            Overscan = 0.0, BloomThreshold = 0.30, BeamWidth = 0.55,
            Interlace = 0.0, VerticalHold = 0.0, Noise = 0.0, ScanSweep = 0.0
        },
        // Flat, sharp, aperture-grille glass — the crisp end of the range.
        "Studio Monitor" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.30, ScanlinePitch = 3,
            Curvature = 0.02, MaskIntensity = 0.70, MaskPitch = 3,
            Glow = 0.20, Vignette = 0.10, Brightness = 1.50,
            Aberration = 0.03, Flicker = 0.0, TintAmount = 0.0,
            Overscan = 0.0, BloomThreshold = 0.45, BeamWidth = 0.60,
            Interlace = 0.20, VerticalHold = 0.0, Noise = 0.0, ScanSweep = 0.0
        },
        // Composite-fed home monitor: soft, warm, colors smearing sideways.
        "Composite TV" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.30, ScanlinePitch = 3,
            Curvature = 0.22, MaskIntensity = 0.10, MaskPitch = 3,
            Glow = 0.60, Vignette = 0.30, Brightness = 1.30,
            Aberration = 0.35, Flicker = 0.04, TintAmount = 0.0,
            Overscan = 0.0, BloomThreshold = 0.25, BeamWidth = 0.85,
            Interlace = 0.0, VerticalHold = 0.25, Noise = 0.06, ScanSweep = 0.0
        },
        // Big coarse consumer set — deep curve, chunky shadow mask.
        "Console TV" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.45, ScanlinePitch = 5,
            Curvature = 0.45, MaskIntensity = 0.40, MaskPitch = 6,
            Glow = 0.50, Vignette = 0.45, Brightness = 1.40,
            Aberration = 0.20, Flicker = 0.10, TintAmount = 0.0,
            Overscan = 0.0, BloomThreshold = 0.30, BeamWidth = 0.70,
            Interlace = 0.25, VerticalHold = 0.35, Noise = 0.10, ScanSweep = 0.0
        },
        "Green Phosphor" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.40, ScanlinePitch = 3,
            Curvature = 0.18, MaskIntensity = 0.0, MaskPitch = 3,
            Glow = 0.55, Vignette = 0.35, Brightness = 1.25,
            Aberration = 0.0, Flicker = 0.08, TintAmount = 1.0,
            TintColor = "#33FF66",
            Overscan = 0.0, BloomThreshold = 0.25, BeamWidth = 0.75,
            Interlace = 0.0, VerticalHold = 0.0, Noise = 0.0, ScanSweep = 0.0
        },
        "Amber Phosphor" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.40, ScanlinePitch = 3,
            Curvature = 0.18, MaskIntensity = 0.0, MaskPitch = 3,
            Glow = 0.55, Vignette = 0.35, Brightness = 1.25,
            Aberration = 0.0, Flicker = 0.08, TintAmount = 1.0,
            TintColor = "#FFB000",
            Overscan = 0.0, BloomThreshold = 0.25, BeamWidth = 0.75,
            Interlace = 0.0, VerticalHold = 0.0, Noise = 0.0, ScanSweep = 0.0
        },
        // Mono VGA paper-white — flat, clean, easy to read.
        "Paper White" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.25, ScanlinePitch = 3,
            Curvature = 0.05, MaskIntensity = 0.0, MaskPitch = 3,
            Glow = 0.30, Vignette = 0.10, Brightness = 1.15,
            Aberration = 0.0, Flicker = 0.0, TintAmount = 1.0,
            TintColor = "#FFE8C8",
            Overscan = 0.0, BloomThreshold = 0.45, BeamWidth = 0.90,
            Interlace = 0.0, VerticalHold = 0.0, Noise = 0.0, ScanSweep = 0.0
        },
        // Luggable orange gas-plasma panel: glowing, and no raster at all.
        "Gas Plasma" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.0, ScanlinePitch = 3,
            Curvature = 0.0, MaskIntensity = 0.0, MaskPitch = 3,
            Glow = 0.70, Vignette = 0.20, Brightness = 1.20,
            Aberration = 0.0, Flicker = 0.0, TintAmount = 1.0,
            TintColor = "#FFB000",
            Overscan = 0.0, BloomThreshold = 0.20, BeamWidth = 1.0,
            Interlace = 0.0, VerticalHold = 0.0, Noise = 0.0, ScanSweep = 0.0
        },
        // Dimmed and cooled down for reading in a dark room.
        "Night Owl" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.30, ScanlinePitch = 3,
            Curvature = 0.10, MaskIntensity = 0.0, MaskPitch = 3,
            Glow = 0.45, Vignette = 0.55, Brightness = 0.85,
            Aberration = 0.0, Flicker = 0.0, TintAmount = 0.35,
            TintColor = "#88BBFF",
            Overscan = 0.0, BloomThreshold = 0.35, BeamWidth = 0.90,
            Interlace = 0.0, VerticalHold = 0.0, Noise = 0.0, ScanSweep = 0.0
        },
        "Worn Out Tube" => new CrtSettings
        {
            Enabled = true, Preset = name,
            ScanlineIntensity = 0.50, ScanlinePitch = 4,
            Curvature = 0.35, MaskIntensity = 0.35, MaskPitch = 4,
            Glow = 0.65, Vignette = 0.55, Brightness = 1.35,
            Aberration = 0.40, Flicker = 0.30, TintAmount = 0.15,
            TintColor = "#FFE8C8",
            Overscan = 0.0, BloomThreshold = 0.20, BeamWidth = 0.60,
            Interlace = 0.30, VerticalHold = 0.55, Noise = 0.22, ScanSweep = 0.0
        },
        // "Classic CRT" and anything unrecognised
        _ => new CrtSettings
        {
            Enabled = true, Preset = "Classic CRT",
            ScanlineIntensity = 0.35, ScanlinePitch = 3,
            Curvature = 0.12, MaskIntensity = 0.25, MaskPitch = 3,
            Glow = 0.30, Vignette = 0.20, Brightness = 1.25,
            Aberration = 0.06, Flicker = 0.0, TintAmount = 0.0,
            Overscan = 0.0, BloomThreshold = 0.35, BeamWidth = 0.80,
            Interlace = 0.0, VerticalHold = 0.0, Noise = 0.0, ScanSweep = 0.0
        }
    };
}
