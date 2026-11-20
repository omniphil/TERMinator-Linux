using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Old-school cracktro for the About box (§11). Back to front: a parallax starfield with drifting planets and the
/// bouncing TERMinator logo; the splash art as a 3D card tumbling on all three axes while it zooms
/// in and out and now and then flies past the camera; a little Gradius-style shooter; then a rainbow sine-wave scroller
/// running right to left, a water reflection under it and colour-cycling edge lines.
///
/// Ported from the Windows client's CracktroScroller with its values intact. Two things are done differently:
///   • Windows draws the flat layers in two <c>OnRender</c> passes with a <c>Viewport3D</c> for the card between them.
///     Avalonia has no 3D, so the card is a manually projected quad — the four rotated corners are perspective
///     projected each frame and the art is drawn through the matching perspective <see cref="Matrix"/> (Avalonia's
///     Matrix carries the perspective terms, so Skia does the real thing rather than a fake-3D strip trick).
///   • WPF's per-pixel 3D lighting can brighten as well as darken; a DrawingContext can only multiply, so the lit
///     front face is drawn at full brightness (the emissive copy already kept it bright at every angle on Windows) and
///     only the back face is shaded down.
/// </summary>
public sealed class CracktroScroller : Control
{
    private const double ScrollSpeed = 140;     // px per second
    private const double ScrollerFontSize = 32;
    private const double WaveAmplitude = 20;
    private const double ScrollerCenterY = 128; // middle of the sine wave
    private const double WaterY = 178;          // where the reflection starts
    private const double LogoWidth = 290;
    private const double SplashWidth = 88;      // the splash art card (640x800 art) at zoom 1
    private const double SplashHeight = 110;
    private const double CameraFieldOfView = 45;
    private const double FlyByFirst = 9;        // seconds until the first fly-past
    private const double FlyByEvery = 17;       // then one every this many seconds
    private const double FlyByDuration = 3.2;   // flying at the camera
    private const double FlyByGone = 0.8;       // out of sight behind the camera
    private const double FlyByReturn = 2.6;     // coming back in from far away

    private static readonly FontFamily MonoFont = new("JetBrains Mono, DejaVu Sans Mono, monospace");
    private static readonly Typeface ScrollerTypeface = new(MonoFont, FontStyle.Normal, FontWeight.Bold);

    private readonly string _text;
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<char, (Geometry? Glyph, double Advance)> _glyphs = new();
    private readonly Dictionary<int, IBrush> _glyphFills = new();
    private readonly Dictionary<int, IPen> _glyphPens = new();
    private readonly Dictionary<int, IBrush> _rainbowBrushes = new();
    private double _textWidth;
    private double _glyphTop;
    private Bitmap? _logo;
    private bool _logoTried;
    private Bitmap? _splash;
    private bool _splashTried;
    private DispatcherTimer? _timer;

    private readonly (double X, double Y, int Layer)[] _stars;

    public CracktroScroller(string text)
    {
        _text = text;
        Height = 220;
        ClipToBounds = true;

        var rng = new Random(1989);
        _stars = new (double, double, int)[140];
        for (int i = 0; i < _stars.Length; i++)
            _stars[i] = (rng.NextDouble() * 2000, rng.NextDouble() * 220, rng.Next(3));


        AttachedToVisualTree += (_, _) =>
        {
            _clock.Start();
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) =>
            {
                UpdateShooter(_clock.Elapsed.TotalSeconds);
                InvalidateVisual();
            };
            _timer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _timer?.Stop();
            _timer = null;
            _clock.Stop();
        };
    }

    public override void Render(DrawingContext dc)
    {
        double width = Bounds.Width, height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;
        double t = _clock.Elapsed.TotalSeconds;

        dc.FillRectangle(Brushes.Black, new Rect(0, 0, width, height));

        // Back: stars, planets, logo
        DrawStars(dc, width, t);
        DrawPlanets(dc, width, t);
        DrawLogo(dc, width, t);

        // The tumbling card sits above all of that and below the scroller, water and rainbow lines
        DrawSplashCard(dc, width, height, t);

        // Front: the shooter, the sine scroller, the water reflection, the colour-cycling lines
        DrawShooter(dc, t);

        EnsureGlyphs();
        double scroll = t * ScrollSpeed % (_textWidth + width);
        double startX = width - scroll;
        DrawScroller(dc, width, t, startX, reflection: false);

        using (dc.PushClip(new Rect(0, WaterY, width, height - WaterY)))
        {
            dc.FillRectangle(WaterBrush, new Rect(0, WaterY, width, height - WaterY));
            using (dc.PushTransform(new Matrix(1, 0, 0, -1, 0, 2 * WaterY)))
            using (dc.PushOpacity(0.32))
            {
                DrawShooter(dc, t);    // the ship, shots and enemies mirror in the water once they get low enough
                DrawScroller(dc, width, t, startX, reflection: true);
            }
        }

        DrawRainbowLine(dc, width, 0, 2, t);
        DrawRainbowLine(dc, width, WaterY - 1, 1, t + 0.5);
        DrawRainbowLine(dc, width, height - 2, 2, t + 1);
    }

    // ================================================================ starfield, planets, logo

    private void DrawStars(DrawingContext dc, double width, double t)
    {
        foreach (var (x, y, layer) in _stars)
        {
            double speed = 25 + layer * 55;
            double sx = width - (x + t * speed) % (width + 4);
            byte shade = (byte)(90 + layer * 80);
            double size = layer == 2 ? 2 : 1;
            dc.FillRectangle(StarBrush(shade), new Rect(sx, y, size, size));
        }
    }

    private static readonly Dictionary<byte, IBrush> StarBrushes = new();

    private static IBrush StarBrush(byte shade)
    {
        if (StarBrushes.TryGetValue(shade, out var brush))
            return brush;
        brush = new SolidColorBrush(Color.FromRgb(shade, shade, shade));
        StarBrushes[shade] = brush;
        return brush;
    }

    /// <summary>A background planet. Start staggers where it is in its crossing; Speed (px/s) gives the parallax.</summary>
    private sealed record Planet(double Start, double Y, double Radius, double Speed, Color Base, Color[] Bands, Color Glow,
                                 bool Ring, (double X, double Y, double R)[] Craters);

    // Slowest (farthest) first, so nearer planets pass in front
    private static readonly Planet[] Planets =
    [
        new(0.15, 150, 12, 4, Color.FromRgb(0x5A, 0x9B, 0xFF), [], Color.FromRgb(0x88, 0xCC, 0xFF), false, []),   // ice world
        new(0.75, 58, 7, 6, Color.FromRgb(0xB0, 0x7C, 0xE0), [], Color.FromRgb(0xB0, 0x7C, 0xE0), false,
            [(-2, -1, 1.5), (2.5, 2, 1.2)]),                                                                      // little moon
        new(0.55, 118, 30, 9, Color.FromRgb(0xD9, 0x9A, 0x5B),
            [Color.FromRgb(0xB8, 0x73, 0x33), Color.FromRgb(0xEA, 0xC0, 0x8A), Color.FromRgb(0x9C, 0x5A, 0x2E), Color.FromRgb(0xE0, 0xA9, 0x6D)],
            Color.FromRgb(0xFF, 0xCC, 0x88), true, []),                                                           // ringed gas giant
        new(0.02, 46, 18, 13, Color.FromRgb(0xC8, 0x50, 0x3C), [], Color.FromRgb(0xFF, 0x77, 0x55), false,
            [(-6, -4, 3), (5, 3, 2.5), (-2, 8, 2), (8, -6, 1.6)]),                                                // red rock
        new(0.9, 172, 15, 17, Color.FromRgb(0x2F, 0xB8, 0xA0), [], Color.FromRgb(0x66, 0xFF, 0xDD), false, [])     // teal world
    ];

    private static readonly IBrush PlanetShade = CreatePlanetShade();
    private static readonly IBrush CraterBrush = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
    private static readonly IPen RingOuterPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xE0, 0xCC, 0xA0)), 2.5);
    private static readonly IPen RingInnerPen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xA8, 0x88, 0x60)), 1.2);
    private static readonly Dictionary<Color, IBrush> PlanetBrushes = new();
    private static readonly Dictionary<Color, IBrush> PlanetGlows = new();

    private static IBrush Flat(Color color)
    {
        if (PlanetBrushes.TryGetValue(color, out var brush))
            return brush;
        brush = new SolidColorBrush(color);
        PlanetBrushes[color] = brush;
        return brush;
    }

    /// <summary>Lit from the upper left: a soft highlight fading to a dark night side at the rim.</summary>
    private static IBrush CreatePlanetShade() => new RadialGradientBrush
    {
        GradientOrigin = new RelativePoint(0.32, 0.3, RelativeUnit.Relative),
        Center = new RelativePoint(0.4, 0.38, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.35),
            new GradientStop(Color.FromArgb(0xC8, 0x00, 0x00, 0x00), 0.95),
            new GradientStop(Color.FromArgb(0xE6, 0x00, 0x00, 0x00), 1)
        }
    };

    private static IBrush PlanetGlow(Color glow)
    {
        if (PlanetGlows.TryGetValue(glow, out var brush))
            return brush;
        brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x55, glow.R, glow.G, glow.B), 0.78),
                new GradientStop(Color.FromArgb(0x00, glow.R, glow.G, glow.B), 1)
            }
        };
        PlanetGlows[glow] = brush;
        return brush;
    }

    private static void DrawPlanets(DrawingContext dc, double width, double t)
    {
        using (dc.PushOpacity(0.8))   // background: never louder than the foreground
        {
            foreach (var planet in Planets)
            {
                double r = planet.Radius;
                double cycle = width + 2 * r + 160;
                double cx = width + r + 80 - (t * planet.Speed + planet.Start * cycle) % cycle;
                if (cx < -r * 2 - 10 || cx > width + r * 2 + 10)
                    continue;
                var center = new Point(cx, planet.Y);

                dc.DrawEllipse(PlanetGlow(planet.Glow), null, center, r * 1.3, r * 1.3);

                if (planet.Ring)
                    DrawRingHalf(dc, center, r, back: true);

                using (dc.PushGeometryClip(new EllipseGeometry(new Rect(cx - r, planet.Y - r, 2 * r, 2 * r))))
                {
                    dc.FillRectangle(Flat(planet.Base), new Rect(cx - r, planet.Y - r, 2 * r, 2 * r));
                    if (planet.Bands.Length > 0)
                    {
                        const int stripes = 9;
                        double stripeHeight = 2 * r / stripes;
                        for (int i = 0; i < stripes; i += 2)
                        {
                            var band = planet.Bands[i / 2 % planet.Bands.Length];
                            double wobble = Math.Sin(i * 1.7) * stripeHeight * 0.3;
                            dc.FillRectangle(Flat(band),
                                new Rect(cx - r, planet.Y - r + i * stripeHeight + wobble, 2 * r, stripeHeight * 0.9));
                        }
                    }
                    foreach (var (x, y, craterRadius) in planet.Craters)
                        dc.DrawEllipse(CraterBrush, null, new Point(cx + x, planet.Y + y), craterRadius, craterRadius);
                    dc.DrawEllipse(PlanetShade, null, center, r, r);
                }

                if (planet.Ring)
                    DrawRingHalf(dc, center, r, back: false);
            }
        }
    }

    /// <summary>One half of a tilted ring: the far half goes behind the planet, the near half in front.</summary>
    private static void DrawRingHalf(DrawingContext dc, Point center, double r, bool back)
    {
        var rotate = Matrix.CreateTranslation(-center.X, -center.Y)
                     * Matrix.CreateRotation(Matrix.ToRadians(-18))
                     * Matrix.CreateTranslation(center.X, center.Y);
        using (dc.PushTransform(rotate))
        using (dc.PushClip(back
                   ? new Rect(center.X - 3 * r, center.Y - 3 * r, 6 * r, 3 * r)
                   : new Rect(center.X - 3 * r, center.Y, 6 * r, 3 * r)))
        {
            dc.DrawEllipse(null, RingOuterPen, center, r * 1.9, r * 0.45);
            dc.DrawEllipse(null, RingInnerPen, center, r * 1.55, r * 0.33);
        }
    }

    private void DrawLogo(DrawingContext dc, double width, double t)
    {
        if (!_logoTried)
        {
            _logoTried = true;
            _logo = LoadLumaKeyedLogo();
        }
        if (_logo == null)
            return;

        double logoHeight = LogoWidth * _logo.PixelSize.Height / _logo.PixelSize.Width;
        double x = (width - LogoWidth) / 2 + Math.Sin(t * 0.9) * (width - LogoWidth) * 0.35;
        double y = 4 + (1 - Math.Abs(Math.Sin(t * 2.4))) * 16; // bounces like a ball off the top of the scroller
        dc.DrawImage(_logo, new Rect(x, y, LogoWidth, logoHeight));
    }

    // ================================================================ 3D splash card

    /// <summary>
    /// The splash art as a flat card tumbling on all three axes while it zooms in and out, kept on screen and above the
    /// water, with a fly-past at the camera every so often. World units are screen pixels at depth 0.
    /// </summary>
    private void DrawSplashCard(DrawingContext dc, double width, double height, double t)
    {
        if (!_splashTried)
        {
            _splashTried = true;
            _splash = LoadAsset("avares://TERMinator/Assets/splash_screen.png");
        }
        if (_splash == null)
            return;

        double cameraDistance = width / 2 / Math.Tan(CameraFieldOfView / 2 * Math.PI / 180);

        // Zoom: mostly far away, then a swoop in close
        double swoop = 0.5 + 0.5 * Math.Sin(t * 0.55);
        double scale = 0.45 + 0.8 * swoop * swoop;
        double depth = cameraDistance * (1 - 1 / scale);

        // Keep the whole card (up to its half-diagonal when rolled) inside the screen and above the waterline
        double reach = Math.Sqrt(SplashWidth * SplashWidth + SplashHeight * SplashHeight) / 2 * scale;
        double screenX = width / 2 + Math.Sin(t * 0.63) * Math.Max(0, width / 2 - reach);
        double middleY = WaterY / 2;
        double screenY = middleY + Math.Sin(t * 1.21) * Math.Max(0, middleY - reach);

        double offsetX = (screenX - width / 2) / scale;
        double offsetY = (height / 2 - screenY) / scale;

        // Every so often it breaks off and flies straight at us, past the camera and out of sight, then shoots back in
        // from far away and picks up its usual drift
        if (t >= FlyByFirst)
        {
            double local = (t - FlyByFirst) % FlyByEvery;
            if (local < FlyByDuration)
            {
                double p = local / FlyByDuration;
                double ease = p * p; // gently accelerates toward the camera
                depth += (cameraDistance * 1.2 - depth) * ease;
                offsetX *= 1 - Math.Min(1, p * 1.6);
                offsetY *= 1 - Math.Min(1, p * 1.6);
            }
            else if (local < FlyByDuration + FlyByGone)
            {
                return;              // behind the camera: not drawn
            }
            else if (local < FlyByDuration + FlyByGone + FlyByReturn)
            {
                double p = (local - FlyByDuration - FlyByGone) / FlyByReturn;
                double ease = 1 - Math.Pow(1 - p, 3); // decelerates into place
                double farDepth = -cameraDistance * 14;
                depth = farDepth + (depth - farDepth) * ease;
                offsetX *= ease;
                offsetY *= ease;
            }
        }

        double ax = Matrix.ToRadians(t * 53 % 360);
        double ay = Matrix.ToRadians(t * 97 % 360);
        double az = Matrix.ToRadians(t * 31 % 360);

        // The card's four corners in world space, rotated X then Y then Z and moved into place. Texture coordinates:
        // (0,0) is the art's top left, which is the corner at +y (world y is up).
        var corners = new (double X, double Y)[]
        {
            (-SplashWidth / 2,  SplashHeight / 2),   // art top left
            ( SplashWidth / 2,  SplashHeight / 2),   // art top right
            ( SplashWidth / 2, -SplashHeight / 2),   // art bottom right
            (-SplashWidth / 2, -SplashHeight / 2)    // art bottom left
        };

        var screen = new Point[4];
        for (int i = 0; i < 4; i++)
        {
            var (x, y, z) = Rotate(corners[i].X, corners[i].Y, 0, ax, ay, az);
            x += offsetX;
            y += offsetY;
            z += depth;
            double toCamera = cameraDistance - z;
            if (toCamera < 1)
                return;                              // at or behind the camera: nothing sensible to draw
            double f = cameraDistance / toCamera;
            screen[i] = new Point(width / 2 + x * f, height / 2 - y * f);
        }

        // Which face is toward the viewer? The card's normal is +z before it tumbles.
        var (nx, ny, nz) = Rotate(0, 0, 1, ax, ay, az);
        bool front = nx * -offsetX + ny * -offsetY + nz * (cameraDistance - depth) > 0;

        if (UnitSquareTo(screen, _splash.PixelSize.Width, _splash.PixelSize.Height) is not { } project)
            return;

        using (dc.PushTransform(project))
        using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            var art = new Rect(0, 0, _splash.PixelSize.Width, _splash.PixelSize.Height);
            dc.DrawImage(_splash, art);
            if (!front)
            {
                // Back face: the art tinted #B0B0B0 with a 35% self-lit copy over it, i.e. a little darker whichever
                // way it is facing. A multiply is all a DrawingContext can do, so it is drawn as a black veil.
                double lit = Math.Clamp(0.69 * (0.5647 + 0.627 * Math.Max(0, -nz)) + 0.35, 0, 1);
                using (dc.PushOpacity(1 - lit))
                    dc.FillRectangle(Brushes.Black, art);
            }
        }
    }

    /// <summary>Rotates a point around X, then Y, then Z (the order the Windows card's transform group uses).</summary>
    private static (double X, double Y, double Z) Rotate(double x, double y, double z, double ax, double ay, double az)
    {
        double cx = Math.Cos(ax), sx = Math.Sin(ax);
        (y, z) = (y * cx - z * sx, y * sx + z * cx);
        double cy = Math.Cos(ay), sy = Math.Sin(ay);
        (x, z) = (x * cy + z * sy, -x * sy + z * cy);
        double cz = Math.Cos(az), sz = Math.Sin(az);
        (x, y) = (x * cz - y * sz, x * sz + y * cz);
        return (x, y, z);
    }

    /// <summary>
    /// The perspective transform that maps the art's pixel rectangle onto the projected quad
    /// (top left, top right, bottom right, bottom left). Null when the quad is degenerate.
    /// </summary>
    private static Matrix? UnitSquareTo(Point[] quad, double imageWidth, double imageHeight)
    {
        // Unit square (0,0) (1,0) (1,1) (0,1) -> quad, the classic square-to-quad homography
        double x0 = quad[0].X, y0 = quad[0].Y;
        double x1 = quad[1].X, y1 = quad[1].Y;
        double x2 = quad[2].X, y2 = quad[2].Y;
        double x3 = quad[3].X, y3 = quad[3].Y;

        double dx1 = x1 - x2, dy1 = y1 - y2;
        double dx2 = x3 - x2, dy2 = y3 - y2;
        double sx = x0 - x1 + x2 - x3;
        double sy = y0 - y1 + y2 - y3;

        double a, b, c, d, e, f, g, h;
        double det = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(sx) < 1e-9 && Math.Abs(sy) < 1e-9)
        {
            // Affine: the quad is a parallelogram
            g = h = 0;
            a = x1 - x0; b = x2 - x1; c = x0;
            d = y1 - y0; e = y2 - y1; f = y0;
        }
        else
        {
            if (Math.Abs(det) < 1e-9)
                return null;
            g = (sx * dy2 - dx2 * sy) / det;
            h = (dx1 * sy - sx * dy1) / det;
            a = x1 - x0 + g * x1;
            b = x3 - x0 + h * x3;
            c = x0;
            d = y1 - y0 + g * y1;
            e = y3 - y0 + h * y3;
            f = y0;
        }

        if (imageWidth <= 0 || imageHeight <= 0)
            return null;

        // Fold "image pixels -> unit square" into the same matrix, so one transform draws the art directly.
        // Avalonia's Matrix is row-vector: [x y 1] * M, with M13 / M23 / M33 carrying the perspective.
        return new Matrix(a / imageWidth, d / imageWidth, g / imageWidth,
                          b / imageHeight, e / imageHeight, h / imageHeight,
                          c, f, 1);
    }

    // ================================================================ shooter

    // A little Gradius-style ship on the left: it bobs around, steers toward the next enemy or power-up and keeps
    // firing; waves of spinning fans snake in from the right, some shoot pellets back, and hits burst into sparks and a
    // shock ring.

    private sealed class Shot { public double X, Y, Vy; }
    private sealed class Enemy { public double X, Y, BaseY; public bool Fired; }
    private sealed class Pellet { public double X, Y, Vx, Vy; }
    private sealed class Spark { public double X, Y, Vx, Vy, Age, Life; public Color Color; }
    private sealed class ShockRing { public double X, Y, Age; public Color Color = Color.FromRgb(0xFF, 0xDD, 0x88); }
    private sealed class PowerUp { public double X, Y, BaseY; }
    private sealed class Popup { public double X, Y, Age; public string Text = ""; }

    private const int MaxFireLevel = 4;              // single, double, triple, quad
    private const double MaxLevelHoldSeconds = 8;    // then it powers back down so the cycle repeats
    private const double PowerUpSpeed = 80;
    private const double DiagonalShotVy = 110;
    private static readonly string[] FireLevelNames = ["", "SINGLE", "DOUBLE", "TRIPLE", "QUAD"];
    private static readonly IBrush CapsuleBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x22));
    private static readonly IPen CapsulePen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0x66)), 1);
    private static readonly IBrush CapsuleShineBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));

    private const double ShipTop = 26;
    private const double ShipBottom = 166;
    private const double ShotSpeed = 460;
    private const double EnemySpeed = 105;
    private const double PelletSpeed = 120;

    private static readonly Geometry ShipHull = Polygon((15, 0), (4, -3), (-9, -4), (-13, -2), (-13, 2), (-9, 4), (4, 3));
    private static readonly Geometry ShipWings = Polygons(
        [(2, -3), (-8, -10), (-11, -10), (-6, -3)],
        [(2, 3), (-8, 10), (-11, 10), (-6, 3)]);
    private static readonly Geometry FanGeometry = Polygons(Enumerable.Range(0, 4).Select(i =>
    {
        double a = i * Math.PI / 2;
        return new[] { (0.0, 0.0), (Math.Cos(a) * 9, Math.Sin(a) * 9), (Math.Cos(a + 0.7) * 6, Math.Sin(a + 0.7) * 6) };
    }).ToArray());

    private static readonly IBrush HullBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0));
    private static readonly IPen HullPen = new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x7A)), 0.8);
    private static readonly IBrush WingBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xE6, 0xFF));
    private static readonly IPen WingPen = new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x6A, 0x80)), 0.8);
    private static readonly IBrush CockpitBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x2B, 0xD6));
    private static readonly IBrush FlameOuterBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x00));
    private static readonly IBrush FlameInnerBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xAA));
    private static readonly IBrush ShotGlowBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0x55));
    private static readonly IBrush ShotCoreBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xCC));
    private static readonly IBrush FanBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00));
    private static readonly IPen FanPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x33)), 1);
    private static readonly IBrush PelletBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0xFF));
    private static readonly Color[] SparkColors =
        [Color.FromRgb(0xFF, 0xFF, 0xAA), Color.FromRgb(0xFF, 0xAA, 0x00), Color.FromRgb(0xFF, 0x55, 0x55)];
    private static readonly IBrush WaterBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0xD0, 0x02, 0x0A, 0x30), 0),
            new GradientStop(Color.FromArgb(0xF0, 0x00, 0x00, 0x08), 1)
        }
    };

    private readonly List<Shot> _shots = [];
    private readonly List<Enemy> _enemies = [];
    private readonly List<Pellet> _pellets = [];
    private readonly List<Spark> _sparks = [];
    private readonly List<ShockRing> _rings = [];
    private readonly List<PowerUp> _powerUps = [];
    private readonly List<Popup> _popups = [];
    private int _fireLevel = 1;
    private double _maxLevelSince;
    private double _nextPowerUpTime = 2.0;
    private readonly Random _rng = new();
    private double _shipX = 42, _shipY = 95;
    private double _lastShooterTime;
    private double _nextShotTime;
    private double _nextWaveTime = 0.8;
    private int _waveSpawnsLeft;
    private double _nextSpawnTime;
    private double _waveBaseY;

    private void UpdateShooter(double t)
    {
        double width = Bounds.Width;
        if (width <= 0)
            return;
        double dt = Math.Clamp(t - _lastShooterTime, 0, 0.05);
        _lastShooterTime = t;

        // Waves of five fans, snaking in along the same curve
        if (t >= _nextWaveTime)
        {
            _waveSpawnsLeft = 5;
            _nextSpawnTime = t;
            _waveBaseY = 45 + _rng.NextDouble() * 105;
            _nextWaveTime = t + 2.6 + _rng.NextDouble() * 1.4;
        }
        if (_waveSpawnsLeft > 0 && t >= _nextSpawnTime)
        {
            _enemies.Add(new Enemy { X = width + 12, Y = _waveBaseY, BaseY = _waveBaseY });
            _waveSpawnsLeft--;
            _nextSpawnTime = t + 0.2;
        }

        foreach (var enemy in _enemies)
        {
            enemy.X -= EnemySpeed * dt;
            enemy.Y = enemy.BaseY + Math.Sin(enemy.X * 0.035) * 24;
            if (!enemy.Fired && enemy.X < width * 0.8 && enemy.X > _shipX + 80 && _rng.NextDouble() < 0.012)
            {
                enemy.Fired = true;
                double dx = _shipX - enemy.X, dy = _shipY - enemy.Y;
                double length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                _pellets.Add(new Pellet { X = enemy.X, Y = enemy.Y, Vx = dx / length * PelletSpeed, Vy = dy / length * PelletSpeed });
            }
        }
        _enemies.RemoveAll(enemy => enemy.X < -20);

        // Power-up capsules drift in every couple of seconds, bobbing a little
        if (t >= _nextPowerUpTime)
        {
            double y = 40 + _rng.NextDouble() * 120;
            _powerUps.Add(new PowerUp { X = width + 10, Y = y, BaseY = y });
            _nextPowerUpTime = t + 1.8 + _rng.NextDouble() * 0.8;
        }
        foreach (var powerUp in _powerUps)
        {
            powerUp.X -= PowerUpSpeed * dt;
            powerUp.Y = powerUp.BaseY + Math.Sin(t * 3 + powerUp.BaseY) * 6;
        }

        // The ship drifts side to side and steers for the next capsule once it's close, otherwise the next enemy in
        // line (or cruises when the sky is clear)
        _shipX = 42 + Math.Sin(t * 0.7) * 16;
        var nextPowerUp = _powerUps.Where(powerUp => powerUp.X > _shipX).MinBy(powerUp => powerUp.X);
        var nextEnemy = _enemies.Where(enemy => enemy.X > _shipX + 30).MinBy(enemy => enemy.X);
        double? targetY = nextPowerUp != null && nextPowerUp.X < width * 0.7 ? nextPowerUp.Y : nextEnemy?.Y ?? nextPowerUp?.Y;
        double desiredY = Math.Clamp(targetY ?? 95 + Math.Sin(t * 0.9) * 45, ShipTop, ShipBottom);
        _shipY += Math.Clamp(desiredY - _shipY, -95 * dt, 95 * dt);

        foreach (var powerUp in _powerUps)
        {
            if (Math.Abs(powerUp.X - _shipX) >= 14 || Math.Abs(powerUp.Y - _shipY) >= 12)
                continue;
            powerUp.X = -100;
            string label = "MAX";
            if (_fireLevel < MaxFireLevel)
            {
                _fireLevel++;
                label = FireLevelNames[_fireLevel];
                if (_fireLevel == MaxFireLevel)
                    _maxLevelSince = t;
            }
            _rings.Add(new ShockRing { X = _shipX, Y = _shipY, Color = Color.FromRgb(0x55, 0xFF, 0xFF) });
            _popups.Add(new Popup { X = _shipX, Y = _shipY - 14, Text = label });
        }
        _powerUps.RemoveAll(powerUp => powerUp.X < -15);

        if (_fireLevel == MaxFireLevel && t - _maxLevelSince > MaxLevelHoldSeconds)
        {
            _fireLevel = 1;
            _popups.Add(new Popup { X = _shipX, Y = _shipY - 14, Text = "POWER DOWN" });
        }

        if (t >= _nextShotTime)
        {
            double noseX = _shipX + 15;
            switch (_fireLevel)
            {
                case 1:
                    Fire(noseX, _shipY, 0);
                    break;
                case 2:
                    Fire(noseX, _shipY - 4, 0);
                    Fire(noseX, _shipY + 4, 0);
                    break;
                case 3:
                    Fire(noseX, _shipY, 0);
                    Fire(noseX, _shipY, -DiagonalShotVy);
                    Fire(noseX, _shipY, DiagonalShotVy);
                    break;
                default:
                    Fire(noseX, _shipY - 4, 0);
                    Fire(noseX, _shipY + 4, 0);
                    Fire(noseX, _shipY, -DiagonalShotVy);
                    Fire(noseX, _shipY, DiagonalShotVy);
                    break;
            }
            _nextShotTime = t + 0.2;
        }
        foreach (var shot in _shots)
        {
            shot.X += ShotSpeed * dt;
            shot.Y += shot.Vy * dt;
        }

        foreach (var popup in _popups)
        {
            popup.Age += dt;
            popup.Y -= 25 * dt;
        }
        _popups.RemoveAll(popup => popup.Age >= 0.9);

        foreach (var pellet in _pellets)
        {
            pellet.X += pellet.Vx * dt;
            pellet.Y += pellet.Vy * dt;
            if (Math.Abs(pellet.X - _shipX) < 9 && Math.Abs(pellet.Y - _shipY) < 6)
            {
                Burst(pellet.X, pellet.Y, 4, ring: false); // soaks it up on the shields
                pellet.X = -100;
            }
        }
        _pellets.RemoveAll(pellet => pellet.X < -10 || pellet.X > width + 10 || pellet.Y < -10 || pellet.Y > 230);

        // Hits
        foreach (var shot in _shots)
        {
            var hit = _enemies.FirstOrDefault(enemy => Math.Abs(enemy.X - shot.X) < 10 && Math.Abs(enemy.Y - shot.Y) < 9);
            if (hit == null)
                continue;
            Burst(hit.X, hit.Y, 14, ring: true);
            _enemies.Remove(hit);
            shot.X = width + 100;
        }
        _shots.RemoveAll(shot => shot.X > width + 10 || shot.Y < -10 || shot.Y > 230);

        foreach (var spark in _sparks)
        {
            spark.Age += dt;
            spark.X += spark.Vx * dt;
            spark.Y += spark.Vy * dt;
            spark.Vx *= 0.97;
            spark.Vy *= 0.97;
        }
        _sparks.RemoveAll(spark => spark.Age >= spark.Life);
        foreach (var ring in _rings)
            ring.Age += dt;
        _rings.RemoveAll(ring => ring.Age >= 0.35);
    }

    private void Fire(double x, double y, double vy) => _shots.Add(new Shot { X = x, Y = y, Vy = vy });

    private void Burst(double x, double y, int count, bool ring)
    {
        for (int i = 0; i < count; i++)
        {
            double angle = _rng.NextDouble() * Math.PI * 2;
            double speed = 40 + _rng.NextDouble() * 120;
            _sparks.Add(new Spark
            {
                X = x, Y = y,
                Vx = Math.Cos(angle) * speed, Vy = Math.Sin(angle) * speed,
                Life = 0.3 + _rng.NextDouble() * 0.3,
                Color = SparkColors[_rng.Next(SparkColors.Length)]
            });
        }
        if (ring)
            _rings.Add(new ShockRing { X = x, Y = y });
    }

    private void DrawShooter(DrawingContext dc, double t)
    {
        foreach (var pellet in _pellets)
        {
            dc.DrawEllipse(PelletBrush, null, new Point(pellet.X, pellet.Y), 3, 3);
            dc.DrawEllipse(Brushes.White, null, new Point(pellet.X, pellet.Y), 1.2, 1.2);
        }

        foreach (var powerUp in _powerUps)
        {
            double pulse = 0.5 + 0.5 * Math.Sin(t * 10);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(0x40 + 0x50 * pulse), 0x55, 0xFF, 0xFF)), null,
                           new Point(powerUp.X, powerUp.Y), 11, 11);
            dc.DrawRectangle(CapsuleBrush, CapsulePen, new Rect(powerUp.X - 7, powerUp.Y - 5, 14, 10), 5, 5);
            dc.FillRectangle(CapsuleShineBrush, new Rect(powerUp.X - 4, powerUp.Y - 3, 5, 1.5));
        }

        foreach (var shot in _shots)
        {
            bool angled = shot.Vy != 0;
            DrawingContext.PushedState rotation = default;
            if (angled)
            {
                double angle = Math.Atan2(shot.Vy, ShotSpeed);
                rotation = dc.PushTransform(Matrix.CreateTranslation(-shot.X, -shot.Y)
                                           * Matrix.CreateRotation(angle)
                                           * Matrix.CreateTranslation(shot.X, shot.Y));
            }
            dc.FillRectangle(ShotGlowBrush, new Rect(shot.X - 3, shot.Y - 2.5, 18, 5));
            dc.FillRectangle(ShotCoreBrush, new Rect(shot.X, shot.Y - 1, 12, 2));
            if (angled)
                rotation.Dispose();
        }

        foreach (var enemy in _enemies)
        {
            using (dc.PushTransform(Matrix.CreateRotation(Matrix.ToRadians(t * 480 % 360))
                                    * Matrix.CreateTranslation(enemy.X, enemy.Y)))
            {
                dc.DrawGeometry(FanBrush, FanPen, FanGeometry);
                dc.DrawEllipse(Brushes.White, null, new Point(0, 0), 2, 2);
            }
        }

        // The ship, with a flickering engine flame
        using (dc.PushTransform(Matrix.CreateTranslation(_shipX, _shipY)))
        {
            double flame = 5 + _rng.NextDouble() * 6;
            dc.DrawGeometry(FlameOuterBrush, null, Polygon((-13, -2.5), (-13 - flame, 0), (-13, 2.5)));
            dc.DrawGeometry(FlameInnerBrush, null, Polygon((-13, -1.2), (-13 - flame * 0.55, 0), (-13, 1.2)));
            dc.DrawGeometry(WingBrush, WingPen, ShipWings);
            dc.DrawGeometry(HullBrush, HullPen, ShipHull);
            dc.DrawEllipse(CockpitBrush, null, new Point(3, -1), 3, 1.6);
        }

        foreach (var spark in _sparks)
        {
            byte alpha = (byte)(255 * (1 - spark.Age / spark.Life));
            dc.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, spark.Color.R, spark.Color.G, spark.Color.B)),
                             new Rect(spark.X - 1, spark.Y - 1, 2, 2));
        }
        foreach (var ring in _rings)
        {
            byte alpha = (byte)(255 * (1 - ring.Age / 0.35));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, ring.Color.R, ring.Color.G, ring.Color.B)), 1.5);
            double radius = 4 + ring.Age * 70;
            dc.DrawEllipse(null, pen, new Point(ring.X, ring.Y), radius, radius);
        }

        foreach (var popup in _popups)
        {
            byte alpha = (byte)(255 * Math.Min(1, 2.5 * (1 - popup.Age / 0.9)));
            var text = new FormattedText(popup.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                         ScrollerTypeface, 11,
                                         new SolidColorBrush(Color.FromArgb(alpha, 0x55, 0xFF, 0xFF)));
            dc.DrawText(text, new Point(popup.X - text.Width / 2, popup.Y - text.Height / 2));
        }
    }

    private static Geometry Polygon(params (double X, double Y)[] points) => Polygons(points);

    private static Geometry Polygons(params (double X, double Y)[][] figures)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var points in figures)
            {
                context.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: true);
                for (int i = 1; i < points.Length; i++)
                    context.LineTo(new Point(points[i].X, points[i].Y));
                context.EndFigure(isClosed: true);
            }
        }
        return geometry;
    }

    // ================================================================ sine scroller, water, rainbow lines

    private void DrawScroller(DrawingContext dc, double width, double t, double startX, bool reflection)
    {
        double x = startX;
        int index = 0;
        foreach (char c in _text)
        {
            var (glyph, advance) = Glyph(c);
            if (x > width)
                break;
            if (x + advance >= 0 && glyph != null && !char.IsWhiteSpace(c))
            {
                double wave = Math.Sin(x * 0.018 + t * 4.2) * WaveAmplitude;
                double y = ScrollerCenterY - _glyphTop + wave;
                double ripple = reflection ? Math.Sin(t * 6 + index * 0.9) * 2.5 : 0;
                int hue = (int)(((x * 0.6 + t * 220) % 360 + 360) % 360);

                if (!reflection)
                {
                    using (dc.PushTransform(Matrix.CreateTranslation(x + 3, y + 3)))
                        dc.DrawGeometry(Brushes.Black, null, glyph);
                }
                using (dc.PushTransform(Matrix.CreateTranslation(x + ripple, y)))
                    dc.DrawGeometry(GlyphFill(hue), GlyphPen(hue), glyph);
            }
            x += advance;
            index++;
        }
    }

    /// <summary>White at the top fading into the rainbow colour, the hue running across the text and cycling in time.</summary>
    private IBrush GlyphFill(int hue)
    {
        if (_glyphFills.TryGetValue(hue, out var brush))
            return brush;
        brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0.05),
                new GradientStop(Hsv(hue, 0.85, 1), 0.55)
            }
        };
        _glyphFills[hue] = brush;
        return brush;
    }

    private IPen GlyphPen(int hue)
    {
        if (_glyphPens.TryGetValue(hue, out var pen))
            return pen;
        pen = new Pen(new SolidColorBrush(Darken(Hsv(hue, 0.85, 1))), 1);
        _glyphPens[hue] = pen;
        return pen;
    }

    private void DrawRainbowLine(DrawingContext dc, double width, double y, double thickness, double t)
    {
        int phase = (int)((t * 180 % 360 + 360) % 360);
        if (!_rainbowBrushes.TryGetValue(phase, out var brush))
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative)
            };
            for (int i = 0; i <= 6; i++)
                gradient.GradientStops.Add(new GradientStop(Hsv((i * 60 + phase) % 360, 1, 1), i / 6.0));
            brush = gradient;
            _rainbowBrushes[phase] = brush;
        }
        dc.FillRectangle(brush, new Rect(0, y, width, thickness));
    }

    private void EnsureGlyphs()
    {
        if (_textWidth > 0)
            return;
        _textWidth = _text.Sum(c => Glyph(c).Advance);
        var m = Glyph('M').Glyph;
        _glyphTop = m == null ? ScrollerFontSize / 2 : m.Bounds.Height / 2 + m.Bounds.Top;
    }

    private (Geometry? Glyph, double Advance) Glyph(char c)
    {
        if (_glyphs.TryGetValue(c, out var cached))
            return cached;

        var formatted = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                          ScrollerTypeface, ScrollerFontSize, Brushes.White);
        var entry = (formatted.BuildGeometry(new Point(0, 0)), formatted.WidthIncludingTrailingWhitespace);
        _glyphs[c] = entry;
        return entry;
    }

    // ================================================================ helpers

    private static Bitmap? LoadAsset(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cracktro asset {uri} could not be loaded: {ex.Message}");
            return null;
        }
    }

    /// <summary>The header logo with its black background keyed out (alpha = brightness), so stars show around it.</summary>
    private static Bitmap? LoadLumaKeyedLogo()
    {
        try
        {
            using var source = LoadAsset("avares://TERMinator/Assets/header_logo.png");
            if (source == null)
                return null;

            var size = source.PixelSize;
            var keyed = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var buffer = keyed.Lock())
            {
                source.CopyPixels(buffer, AlphaFormat.Unpremul);
                int bytes = buffer.RowBytes * size.Height;
                var pixels = new byte[bytes];
                Marshal.Copy(buffer.Address, pixels, 0, bytes);
                for (int i = 0; i + 3 < bytes; i += 4)
                {
                    byte max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                    if (max == 0)
                    {
                        pixels[i + 3] = 0;
                        continue;
                    }
                    for (int channel = 0; channel < 3; channel++)
                        pixels[i + channel] = (byte)(pixels[i + channel] * 255 / max);
                    pixels[i + 3] = max;
                }
                Marshal.Copy(pixels, 0, buffer.Address, bytes);
            }
            return keyed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cracktro logo could not be keyed: {ex.Message}");
            return null;
        }
    }

    private static Color Darken(Color c) => Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));

    private static Color Hsv(double hue, double saturation, double value)
    {
        double c = value * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = value - c;
        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
