using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TERMinator.Linux.Services;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Transparent overlay that renders a real-time audio visualizer on top of the terminal.
/// Supports "bars" (16-band EQ), "scope" (multi-trace oscilloscope),
/// "fire" (demoscene fire), and "starfield" modes.
/// ~30fps animation loop via DispatcherTimer (only when active).
/// </summary>
public class VisualizerOverlay : Control
{
    private const int NumBands = 16;
    private const int FireW = 64;
    private const int FireH = 32;
    private const float FireCooling = 1.0f;
    private const int NumStars = 80;
    private const float StarBaseSpeed = 0.015f;
    private const float StarAudioSpeed = 0.8f;

    private AudioAnalyzer? _analyzer;
    private DispatcherTimer? _timer;
    private bool _active;
    private string _style = "bars";

    // Position in screen pixels (set by caller)
    private double _visLeft, _visTop, _visWidth, _visHeight;

    // Pre-computed brushes for performance
    private static readonly IBrush BgBrush;
    private static readonly IBrush[] BarBrushes;
    private static readonly IPen ScopeBassPen;
    private static readonly IPen ScopeLowMidPen;
    private static readonly IPen ScopeHighMidPen;
    private static readonly IPen ScopeTreblePen;

    private readonly Color[] _firePalette = new Color[256];
    private readonly IBrush[] _fireBrushes = new IBrush[256];
    private float[] _fireHeat = new float[FireW * FireH];

    // Scope phase
    private readonly float[] _scopePhase = new float[4];
    private static readonly float[][] ScopeCycles =
    [
        [1.0f, 1.5f, 2.0f, 2.7f],
        [3.0f, 4.2f, 5.5f, 6.8f],
        [8.0f, 10.0f, 12.5f, 15.0f],
        [17.0f, 21.0f, 26.0f, 32.0f]
    ];

    // Starfield
    private class Star
    {
        public float X, Y, Z, Speed;
        public Color BaseColor;
        public int Band;
    }
    private Star[] _stars;
    private static readonly Random Rng = new();

    static VisualizerOverlay()
    {
        BgBrush = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));

        ScopeBassPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 100, 30)), 3);
        ScopeLowMidPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 220, 50)), 2.5);
        ScopeHighMidPen = new Pen(new SolidColorBrush(Color.FromRgb(50, 220, 50)), 2.5);
        ScopeTreblePen = new Pen(new SolidColorBrush(Color.FromRgb(50, 200, 255)), 2);

        // Pre-compute bar segment brushes (green -> yellow -> red)
        BarBrushes = new IBrush[13];
        for (int s = 0; s <= 12; s++)
        {
            float ratio = (float)s / 12;
            byte r, g;
            if (ratio < 0.5f)
            {
                float t = ratio * 2f;
                r = (byte)(255 * t);
                g = (byte)(200 + 55 * (1f - t));
            }
            else
            {
                float t = (ratio - 0.5f) * 2f;
                r = 255;
                g = (byte)(255 * (1f - t));
            }
            BarBrushes[s] = new SolidColorBrush(Color.FromRgb(r, g, 0));
        }
    }

    public VisualizerOverlay()
    {
        IsHitTestVisible = false;

        // Initialize fire palette and brushes
        for (int i = 0; i < 256; i++)
        {
            byte r, g, b;
            if (i < 64) { r = (byte)(i * 4); g = 0; b = 0; }
            else if (i < 128) { r = 255; g = (byte)((i - 64) * 3); b = 0; }
            else if (i < 192) { r = 255; g = (byte)(192 + (i - 128)); b = 0; }
            else { r = 255; g = 255; b = (byte)Math.Min((i - 192) * 4, 255); }
            _firePalette[i] = Color.FromRgb(r, g, b);
            _fireBrushes[i] = new SolidColorBrush(_firePalette[i]);
        }

        _stars = new Star[NumStars];
        for (int i = 0; i < NumStars; i++)
            _stars[i] = CreateStar(scattered: true);
    }

    public void SetAnalyzer(AudioAnalyzer? analyzer) => _analyzer = analyzer;

    public void Show(double left, double top, double width, double height, string style = "bars")
    {
        string newStyle = style.ToLowerInvariant();
        if (newStyle != _style)
            ResetEffectState();

        _visLeft = left;
        _visTop = top;
        _visWidth = width;
        _visHeight = height;
        _style = newStyle;
        _active = true;
        IsVisible = true;
        StartAnimation();
    }

    public void Hide()
    {
        _active = false;
        IsVisible = false;
        _timer?.Stop();
        ResetEffectState();
    }

    public bool IsActive => _active;

    private void StartAnimation()
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => { if (_active) InvalidateVisual(); };
        _timer.Start();
    }

    private void ResetEffectState()
    {
        _fireHeat = new float[FireW * FireH];
        for (int i = 0; i < NumStars; i++)
            _stars[i] = CreateStar(scattered: true);
        Array.Clear(_scopePhase);
    }

    public override void Render(DrawingContext dc)
    {
        if (!_active || _visWidth <= 0 || _visHeight <= 0) return;

        dc.DrawRectangle(BgBrush, null, new Rect(_visLeft, _visTop, _visWidth, _visHeight));

        switch (_style)
        {
            case "scope": DrawScope(dc); break;
            case "fire": DrawFire(dc); break;
            case "starfield": DrawStarfield(dc); break;
            default: DrawBars(dc); break;
        }
    }

    // ================================================================
    // Bars mode -- 16-band chunky EQ
    // ================================================================

    private void DrawBars(DrawingContext dc)
    {
        var bands = _analyzer?.CurrentBands;
        if (bands == null || bands.Length < NumBands) return;

        int maxSegs = 12;
        double blockH = Math.Max(_visHeight / (maxSegs + 2), 2);
        double gap = Math.Max(_visWidth / 48.0, 2);
        double barWidth = Math.Max((_visWidth - gap * (NumBands + 1)) / NumBands, gap);
        double totalBarsWidth = barWidth * NumBands + gap * (NumBands - 1);
        double startX = _visLeft + (_visWidth - totalBarsWidth) / 2;
        double pad = blockH;
        double maxBarHeight = _visHeight - pad * 2;
        double bottomY = _visTop + _visHeight - pad;

        for (int i = 0; i < NumBands; i++)
        {
            float level = Math.Clamp(bands[i], 0f, 1f);
            double barHeight = level * maxBarHeight;
            if (barHeight <= 0) continue;

            double barLeft = startX + i * (barWidth + gap);
            int numSegs = Math.Max((int)(barHeight / blockH), 1);
            double segGap = Math.Max(blockH * 0.15, 1);

            for (int s = 0; s < numSegs; s++)
            {
                double segBottom = bottomY - s * blockH;
                double segTop = segBottom - blockH + segGap;
                int brushIdx = Math.Clamp(s, 0, 12);

                dc.DrawRectangle(BarBrushes[brushIdx], null,
                    new Rect(barLeft, segTop, barWidth, segBottom - segTop));
            }
        }
    }

    // ================================================================
    // Scope mode -- multi-trace oscilloscope
    // ================================================================

    private void DrawScope(DrawingContext dc)
    {
        var bands = _analyzer?.CurrentBands;
        if (bands == null || bands.Length < NumBands) return;

        double amplitude = _visHeight / 2.0 * 0.9;
        float[] offsets = [-0.15f, -0.05f, 0.05f, 0.15f];
        IPen[] pens = [ScopeBassPen, ScopeLowMidPen, ScopeHighMidPen, ScopeTreblePen];
        float[] phaseSpeeds = [0.08f, 0.12f, 0.18f, 0.25f];

        int numPoints = 128;
        double step = _visWidth / Math.Max(numPoints - 1, 1);
        float twoPi = 2f * MathF.PI;

        for (int t = 0; t < 4; t++)
        {
            int bandStart = t * 4;
            float b0 = bands[bandStart], b1 = bands[bandStart + 1];
            float b2 = bands[bandStart + 2], b3 = bands[bandStart + 3];
            float groupEnergy = (b0 + b1 + b2 + b3) / 4f;

            _scopePhase[t] += phaseSpeeds[t] + groupEnergy * 0.4f;
            float phase = _scopePhase[t];
            float[] cycles = ScopeCycles[t];
            double traceMidY = _visTop + _visHeight / 2.0 + offsets[t] * _visHeight;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                float frac0 = 0f;
                float synth0 = b0 * MathF.Sin(frac0 * cycles[0] * twoPi + phase) +
                               b1 * MathF.Sin(frac0 * cycles[1] * twoPi + phase * 1.3f) +
                               b2 * MathF.Sin(frac0 * cycles[2] * twoPi + phase * 0.7f) +
                               b3 * MathF.Sin(frac0 * cycles[3] * twoPi + phase * 1.6f);
                ctx.BeginFigure(new Point(_visLeft, traceMidY - synth0 * amplitude), false);

                for (int i = 1; i < numPoints; i++)
                {
                    float frac = (float)i / numPoints;
                    double x = _visLeft + i * step;
                    float synth = b0 * MathF.Sin(frac * cycles[0] * twoPi + phase) +
                                  b1 * MathF.Sin(frac * cycles[1] * twoPi + phase * 1.3f) +
                                  b2 * MathF.Sin(frac * cycles[2] * twoPi + phase * 0.7f) +
                                  b3 * MathF.Sin(frac * cycles[3] * twoPi + phase * 1.6f);
                    ctx.LineTo(new Point(x, traceMidY - synth * amplitude));
                }
            }
            dc.DrawGeometry(null, pens[t], geometry);
        }
    }

    // ================================================================
    // Fire mode -- demoscene fire driven by audio
    // ================================================================

    private void DrawFire(DrawingContext dc)
    {
        var bands = _analyzer?.CurrentBands;

        // Seed bottom three rows with high energy for taller, more visible yellow flames
        for (int row = FireH - 3; row < FireH; row++)
        {
            float energyScale = row == FireH - 1 ? 320f : row == FireH - 2 ? 260f : 200f;
            float jitterRange = row == FireH - 1 ? 40f : 30f;
            for (int x = 0; x < FireW; x++)
            {
                int bandIdx = Math.Clamp(x * NumBands / FireW, 0, NumBands - 1);
                float energy = bands != null && bandIdx < bands.Length ? bands[bandIdx] : 0f;
                float jitter = (float)(Rng.NextDouble() * jitterRange * 2 - jitterRange);
                _fireHeat[row * FireW + x] = Math.Clamp(energy * energyScale + jitter, 0f, 255f);
            }
        }

        // Propagate fire upward
        for (int y = 0; y < FireH - 2; y++)
        {
            for (int x = 0; x < FireW; x++)
            {
                int xl = Math.Max(x - 1, 0);
                int xr = Math.Min(x + 1, FireW - 1);
                float below = _fireHeat[(y + 2) * FireW + x];
                float belowL = _fireHeat[(y + 2) * FireW + xl];
                float belowR = _fireHeat[(y + 2) * FireW + xr];
                float mid = _fireHeat[(y + 1) * FireW + x];
                _fireHeat[y * FireW + x] = Math.Max((below + belowL + belowR + mid) / 4f - FireCooling, 0f);
            }
        }

        // Render heat buffer
        double cellW = _visWidth / FireW;
        double cellH = _visHeight / FireH;

        for (int y = 0; y < FireH; y++)
        {
            for (int x = 0; x < FireW; x++)
            {
                int heat = Math.Clamp((int)_fireHeat[y * FireW + x], 0, 255);
                if (heat <= 2) continue;
                double px = _visLeft + x * cellW;
                double py = _visTop + y * cellH;
                dc.DrawRectangle(_fireBrushes[heat], null,
                    new Rect(px, py, cellW + 0.5, cellH + 0.5));
            }
        }
    }

    // ================================================================
    // Starfield mode -- audio-reactive stars from center
    // ================================================================

    private void DrawStarfield(DrawingContext dc)
    {
        var bands = _analyzer?.CurrentBands;
        float[] bandEnergies = new float[3];
        if (bands != null && bands.Length >= 16)
        {
            bandEnergies[0] = (bands[0] + bands[1] + bands[2] + bands[3]) / 4f;
            float s = 0;
            for (int i = 4; i <= 11; i++) s += bands[i];
            bandEnergies[1] = s / 8f;
            bandEnergies[2] = (bands[12] + bands[13] + bands[14] + bands[15]) / 4f;
        }

        double centerX = _visLeft + _visWidth / 2;
        double centerY = _visTop + _visHeight / 2;
        double halfW = _visWidth / 2;
        double halfH = _visHeight / 2;

        foreach (var star in _stars)
        {
            float energy = bandEnergies[star.Band];
            float speedMult = StarBaseSpeed + energy * StarAudioSpeed;

            float dist = MathF.Max(MathF.Sqrt(star.X * star.X + star.Y * star.Y), 0.001f);
            float dirX = star.X / dist;
            float dirY = star.Y / dist;
            float push = speedMult * star.Speed * 0.01f;

            star.X += dirX * push + star.X * speedMult * star.Speed;
            star.Y += dirY * push + star.Y * speedMult * star.Speed;
            star.Z = MathF.Min(star.Z + speedMult * star.Speed * 0.5f, 1f);

            double screenX = centerX + star.X * halfW;
            double screenY = centerY + star.Y * halfH;

            // Respawn if outside area
            if (screenX < _visLeft - 5 || screenX > _visLeft + _visWidth + 5 ||
                screenY < _visTop - 5 || screenY > _visTop + _visHeight + 5)
            {
                RespawnStar(star);
            }

            // Small dots with shooting star trails
            float size = (0.8f + star.Z * 1.2f) * (0.5f + energy * 1.5f);
            float brightness = (0.35f + energy * 0.65f) * (0.5f + star.Z * 0.5f);
            var color = Color.FromRgb(
                (byte)Math.Clamp((int)(star.BaseColor.R * brightness), 0, 255),
                (byte)Math.Clamp((int)(star.BaseColor.G * brightness), 0, 255),
                (byte)Math.Clamp((int)(star.BaseColor.B * brightness), 0, 255));

            // Shooting star trail -- line from tail (towards center) to head
            double sdirX = screenX - centerX;
            double sdirY = screenY - centerY;
            double sdist = Math.Sqrt(sdirX * sdirX + sdirY * sdirY);
            if (sdist > 1)
            {
                double ndirX = sdirX / sdist;
                double ndirY = sdirY / sdist;
                double trailLen = (4 + star.Z * 16) * (0.5 + energy * 4);
                double tailX = screenX - ndirX * trailLen;
                double tailY = screenY - ndirY * trailLen;

                // Trail fades from full color at head to dim at tail
                var tailColor = Color.FromArgb(60, color.R, color.G, color.B);
                var trailBrush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(tailX, tailY, RelativeUnit.Absolute),
                    EndPoint = new RelativePoint(screenX, screenY, RelativeUnit.Absolute),
                    GradientStops =
                    {
                        new GradientStop(tailColor, 0),
                        new GradientStop(color, 1)
                    }
                };
                double penWidth = Math.Max(size * 0.5, 1.5);
                var trailPen = new Pen(trailBrush, penWidth);
                dc.DrawLine(trailPen, new Point(tailX, tailY), new Point(screenX, screenY));
            }

            // Star head -- bright dot
            var brush = new SolidColorBrush(color);
            dc.DrawRectangle(brush, null, new Rect(
                screenX - size / 2, screenY - size / 2, size, size));
        }
    }

    private Star CreateStar(bool scattered = false)
    {
        float angle = (float)(Rng.NextDouble() * 2 * Math.PI);
        float dist = scattered ? 0.05f + (float)Rng.NextDouble() * 0.75f : 0.02f;
        int band = Rng.Next(3);
        var color = band switch
        {
            0 => Color.FromRgb(255, (byte)(80 + Rng.Next(80)), (byte)(20 + Rng.Next(40))),
            1 => Color.FromRgb((byte)(50 + Rng.Next(50)), (byte)(200 + Rng.Next(55)), (byte)(50 + Rng.Next(50))),
            _ => Color.FromRgb((byte)(50 + Rng.Next(50)), (byte)(180 + Rng.Next(75)), 255)
        };
        return new Star
        {
            X = MathF.Cos(angle) * dist,
            Y = MathF.Sin(angle) * dist,
            Z = scattered ? (float)Rng.NextDouble() : 0f,
            Speed = 0.3f + (float)Rng.NextDouble() * 0.7f,
            BaseColor = color,
            Band = band
        };
    }

    private void RespawnStar(Star star)
    {
        float angle = (float)(Rng.NextDouble() * 2 * Math.PI);
        star.X = MathF.Cos(angle) * 0.02f;
        star.Y = MathF.Sin(angle) * 0.02f;
        star.Z = 0f;
        star.Speed = 0.3f + (float)Rng.NextDouble() * 0.7f;
        star.Band = Rng.Next(3);
        star.BaseColor = star.Band switch
        {
            0 => Color.FromRgb(255, (byte)(80 + Rng.Next(80)), (byte)(20 + Rng.Next(40))),
            1 => Color.FromRgb((byte)(50 + Rng.Next(50)), (byte)(200 + Rng.Next(55)), (byte)(50 + Rng.Next(50))),
            _ => Color.FromRgb((byte)(50 + Rng.Next(50)), (byte)(180 + Rng.Next(75)), 255)
        };
    }
}
