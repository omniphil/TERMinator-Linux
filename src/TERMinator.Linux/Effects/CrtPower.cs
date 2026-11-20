using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TERMinator.Linux.Effects;

/// <summary>
/// Old-tube power effects on the window's content (§7).
///
/// Power on (~0.92 s): a small glowing dot appears in the middle, stretches into a bright horizontal line, and the line
/// opens up into the picture. Power off (~0.69 s): the picture collapses to that line, the line squeezes down into the
/// glowing dot, and the dot lingers and fades away.
///
/// The content is animated with a render transform only (layout never changes); the line and dot live in
/// <c>lineHost</c>, a hit-test invisible panel above everything. Values and timings come straight from the Windows
/// client's <c>Effects/CrtPower.cs</c>; WPF drives it with storyboards, so here one stopwatch-driven timer per effect
/// computes every channel from the elapsed time instead.
/// </summary>
internal static class CrtPower
{
    private const double LineScaleY = 0.004;
    private const double DotSize = 9;

    private static readonly TimeSpan DotDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SpreadDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(360);
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(170);
    private static readonly TimeSpan SqueezeDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan DotLingerDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan DotFadeDuration = TimeSpan.FromMilliseconds(260);

    /// <summary>How long <see cref="PowerOn"/> takes, e.g. to start something once the picture is fully open.</summary>
    public static TimeSpan PowerOnDuration => DotDuration + SpreadDuration + OpenDuration;

    private sealed record Parts(int Generation, ScaleTransform Scale, Rectangle Line, ScaleTransform LineScale,
                               Ellipse Dot, ScaleTransform DotScale);

    /// <summary>
    /// Bumped by every new effect, so one still running (a quit during power-on, say) stops touching the content
    /// instead of finishing on top of its replacement. WPF gets this for free: a new storyboard replaces the old one.
    /// </summary>
    private static int _generation;

    public static void PowerOn(Control content, Panel lineHost)
    {
        var parts = Prepare(content, lineHost, contentScaleX: 0, contentScaleY: LineScaleY, lineScaleX: 0);

        double spreadStart = DotDuration.TotalSeconds;                     // 0.30
        double openStart = spreadStart + SpreadDuration.TotalSeconds;      // 0.56
        double total = openStart + OpenDuration.TotalSeconds;              // 0.92

        Drive(parts, total, t =>
        {
            // A glowing dot pops up in the middle of the tube...
            double pop = 0.2 + 0.8 * EaseOut(Progress(t, 0, 0.12));
            parts.DotScale.ScaleX = pop;
            parts.DotScale.ScaleY = pop;
            parts.Dot.Opacity = Keys(t, (0, 1), (spreadStart, 1), (spreadStart + 0.14, 0));

            // ...stretches out into a line...
            double spread = EaseOut(Progress(t, spreadStart, SpreadDuration.TotalSeconds));
            parts.LineScale.ScaleX = spread;
            parts.Scale.ScaleX = spread;
            parts.Line.Opacity = Keys(t, (0, 0), (spreadStart, 0), (spreadStart + 0.08, 1), (openStart, 1), (total, 0));

            // ...then opens up into the picture as the line fades
            double open = EaseOut(Progress(t, openStart, OpenDuration.TotalSeconds));
            parts.Scale.ScaleY = LineScaleY + (1 - LineScaleY) * open;
        },
        () =>
        {
            Remove(lineHost, parts);
            content.RenderTransform = null;
        });
    }

    /// <summary>
    /// Completes when the dot has faded. The content is left collapsed so nothing flashes before the caller swaps the
    /// view; follow up with <see cref="PowerOn"/> (or close the window).
    /// </summary>
    public static Task PowerOffAsync(Control content, Panel lineHost)
    {
        var done = new TaskCompletionSource();
        var parts = Prepare(content, lineHost, contentScaleX: 1, contentScaleY: 1, lineScaleX: 1);

        double collapse = CollapseDuration.TotalSeconds;                   // 0.17
        double squeezeEnd = collapse + SqueezeDuration.TotalSeconds;       // 0.31
        double fadeStart = squeezeEnd + DotLingerDuration.TotalSeconds;    // 0.43
        double total = fadeStart + DotFadeDuration.TotalSeconds;           // 0.69

        Drive(parts, total, t =>
        {
            // The picture collapses to a line as the line lights up...
            double collapseP = EaseIn(Progress(t, 0, collapse));
            parts.Scale.ScaleY = 1 + (LineScaleY - 1) * collapseP;
            parts.Line.Opacity = Keys(t, (0, 0), (0.08, 0), (0.20, 1), (squeezeEnd - 0.03, 1), (squeezeEnd, 0));

            // ...the line squeezes down into a glowing dot...
            double squeeze = 1 - EaseIn(Progress(t, collapse, SqueezeDuration.TotalSeconds));
            parts.Scale.ScaleX = squeeze;
            parts.LineScale.ScaleX = squeeze;

            // ...which lingers for a moment, then shrinks and fades away
            parts.Dot.Opacity = Keys(t, (0, 0), (squeezeEnd - 0.06, 0), (squeezeEnd, 1), (fadeStart, 1), (total, 0));
            double shrink = 1 - 0.7 * Progress(t, fadeStart, DotFadeDuration.TotalSeconds);
            parts.DotScale.ScaleX = shrink;
            parts.DotScale.ScaleY = shrink;
        },
        () =>
        {
            Remove(lineHost, parts);
            done.TrySetResult();
        });

        return done.Task;
    }

    private static Parts Prepare(Control content, Panel lineHost, double contentScaleX, double contentScaleY,
                                 double lineScaleX)
    {
        // An effect already running (e.g. quitting during power-on) is simply replaced
        foreach (var stale in lineHost.Children.OfType<Shape>().ToList())
            lineHost.Children.Remove(stale);

        int generation = ++_generation;
        var scale = new ScaleTransform(contentScaleX, contentScaleY);
        content.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        content.RenderTransform = scale;

        var glowColor = Color.FromRgb(0xE8, 0xFF, 0xFF);

        var lineScale = new ScaleTransform(lineScaleX, 1);
        var line = new Rectangle
        {
            Height = 3,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(glowColor),
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = lineScale,
            Effect = new DropShadowEffect { Color = Colors.White, OffsetX = 0, OffsetY = 0, BlurRadius = 18, Opacity = 1 }
        };

        var dotScale = new ScaleTransform(1, 1);
        var dot = new Ellipse
        {
            Width = DotSize,
            Height = DotSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0xFF, 0xFF)),
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = dotScale,
            // A wide soft halo, so it reads as a glowing point of light rather than a speck
            Effect = new DropShadowEffect { Color = glowColor, OffsetX = 0, OffsetY = 0, BlurRadius = 28, Opacity = 1 }
        };

        lineHost.Children.Add(line);
        lineHost.Children.Add(dot);
        return new Parts(generation, scale, line, lineScale, dot, dotScale);
    }

    private static void Remove(Panel lineHost, Parts parts)
    {
        lineHost.Children.Remove(parts.Line);
        lineHost.Children.Remove(parts.Dot);
    }

    /// <summary>Runs <paramref name="frame"/> with the elapsed seconds until <paramref name="seconds"/> is up.</summary>
    private static void Drive(Parts parts, double seconds, Action<double> frame, Action completed)
    {
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (parts.Generation != _generation)
            {
                timer.Stop();              // superseded: the newer effect owns the content now
                clock.Stop();
                return;
            }
            double t = clock.Elapsed.TotalSeconds;
            if (t >= seconds)
            {
                timer.Stop();
                clock.Stop();
                frame(seconds);
                completed();
                return;
            }
            frame(t);
        };
        frame(0);
        timer.Start();
    }

    /// <summary>0 before <paramref name="start"/>, 1 after it plus <paramref name="duration"/>.</summary>
    private static double Progress(double t, double start, double duration) =>
        duration <= 0 ? (t >= start ? 1 : 0) : Math.Clamp((t - start) / duration, 0, 1);

    /// <summary>Linear interpolation through (time, value) keyframes, the stand-in for WPF's key-frame animation.</summary>
    private static double Keys(double t, params (double Time, double Value)[] frames)
    {
        if (t <= frames[0].Time) return frames[0].Value;
        for (int i = 1; i < frames.Length; i++)
        {
            if (t > frames[i].Time) continue;
            double span = frames[i].Time - frames[i - 1].Time;
            double p = span <= 0 ? 1 : (t - frames[i - 1].Time) / span;
            return frames[i - 1].Value + (frames[i].Value - frames[i - 1].Value) * p;
        }
        return frames[^1].Value;
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    private static double EaseIn(double t) => t * t * t;
}
