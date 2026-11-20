using System.Diagnostics;
using Avalonia.Threading;

namespace TERMinator.Linux.Controls;

/// <summary>
/// A tiny time-driven tween for the flair overlays (fades, snaps): calls <c>apply(progress)</c> at render priority
/// until the duration is up, then <c>completed</c>. Driven by a stopwatch, not by counting ticks, so it keeps time
/// even when the dispatcher is busy. <see cref="Cancel"/> stops it where it is.
/// </summary>
internal sealed class FxTween
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();

    private FxTween(TimeSpan duration, Action<double> apply, Action? completed, Func<double, double>? ease)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            double t = duration <= TimeSpan.Zero ? 1 : Math.Min(1.0, _clock.Elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            apply(ease != null ? ease(t) : t);
            if (t >= 1.0)
            {
                _timer.Stop();
                completed?.Invoke();
            }
        };
    }

    /// <summary>Starts a tween from 0 to 1 over <paramref name="duration"/>; the first frame is applied at once.</summary>
    public static FxTween Run(TimeSpan duration, Action<double> apply, Action? completed = null, Func<double, double>? ease = null)
    {
        var tween = new FxTween(duration, apply, completed, ease);
        apply(0);
        tween._clock.Start();
        tween._timer.Start();
        return tween;
    }

    public void Cancel() => _timer.Stop();

    /// <summary>Cubic ease-out (fast start, gentle landing).</summary>
    public static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
}
