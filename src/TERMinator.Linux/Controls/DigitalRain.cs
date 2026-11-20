using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Falling columns of half-width katakana and digits, fading toward the top of each column. Shared by the Konami
/// easter egg and the screensaver. Call <see cref="Start"/> once the canvas has its size, <see cref="Stop"/>
/// when done. <see cref="RenderStill"/> draws one frame to a bitmap that tiles vertically, for the screensaver's
/// scrolling background.
/// </summary>
public sealed class DigitalRain : Canvas
{
    private const string Glyphs = "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789";
    private const double ColumnWidth = 16;
    private const double RainLineHeight = 17;
    private static readonly Color Green = Color.FromRgb(0x33, 0xFF, 0x33);

    // A monospace face with the half-width katakana as a fallback (no Consolas / MS Gothic on Linux)
    private static readonly FontFamily RainFont =
        new("JetBrains Mono, DejaVu Sans Mono, Noto Sans Mono CJK JP, Noto Sans CJK JP, monospace");

    private sealed class Column
    {
        public char[] Chars = [];
        public TextBlock Block = null!;
        public TranslateTransform Fall = null!;
        public double BeginSeconds;
        public double DurationSeconds;
    }

    private readonly List<Column> _columns = [];
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _fallTimer;
    private DispatcherTimer? _mutateTimer;
    private double _travelFrom, _travelTo;

    public DigitalRain()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public void Start()
    {
        if (_fallTimer != null)
            return;

        // Not laid out yet (the host started us before its first arrange): wait for a size
        if (Bounds.Width < 1 || Bounds.Height < 1)
        {
            void StartWhenSized(object? sender, SizeChangedEventArgs e)
            {
                if (Bounds.Width < 1 || Bounds.Height < 1) return;
                SizeChanged -= StartWhenSized;
                Start();
            }
            SizeChanged += StartWhenSized;
            return;
        }

        double width = Math.Max(1, Bounds.Width);
        double height = Math.Max(1, Bounds.Height);
        int rows = (int)(height / RainLineHeight) + 8;
        int count = (int)Math.Ceiling(width / ColumnWidth);
        _travelFrom = -rows * RainLineHeight;
        _travelTo = height;

        for (int i = 0; i < count; i++)
        {
            var chars = new char[rows];
            for (int r = 0; r < rows; r++)
                chars[r] = RandomGlyph();

            var transform = new TranslateTransform(0, _travelFrom);
            var column = new TextBlock
            {
                Text = string.Join("\n", chars),
                FontFamily = RainFont,
                FontSize = 14,
                LineHeight = RainLineHeight,
                Foreground = new SolidColorBrush(Green),
                // Fade the tail: transparent at the top of the column, bright at its leading edge
                OpacityMask = FadeMask(),
                RenderTransform = transform
            };
            SetLeft(column, i * ColumnWidth);
            SetTop(column, 0);
            Children.Add(column);

            _columns.Add(new Column
            {
                Chars = chars,
                Block = column,
                Fall = transform,
                // Each column falls from above the top to the bottom over 2.5-6.5 s, repeating, after a 0-1.5 s wait
                BeginSeconds = _rng.NextDouble() * 1.5,
                DurationSeconds = 2.5 + _rng.NextDouble() * 4
            });
        }

        _clock.Restart();
        _fallTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _fallTimer.Tick += (_, _) => FallTick();
        _fallTimer.Start();

        // Keep the glyphs churning: every 90 ms a quarter of the columns get one glyph changed
        _mutateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
        _mutateTimer.Tick += (_, _) =>
        {
            if (_columns.Count == 0)
                return;
            for (int n = 0; n < Math.Max(1, _columns.Count / 4); n++)
            {
                var column = _columns[_rng.Next(_columns.Count)];
                column.Chars[_rng.Next(column.Chars.Length)] = RandomGlyph();
                column.Block.Text = string.Join("\n", column.Chars);
            }
        };
        _mutateTimer.Start();
    }

    private void FallTick()
    {
        double t = _clock.Elapsed.TotalSeconds;
        double travel = _travelTo - _travelFrom;
        foreach (var column in _columns)
        {
            double y;
            if (t < column.BeginSeconds)
                y = _travelFrom;
            else
            {
                double cycle = (t - column.BeginSeconds) / column.DurationSeconds;
                y = _travelFrom + (cycle - Math.Floor(cycle)) * travel;
            }
            column.Fall.Y = y;
        }
    }

    public void Stop()
    {
        _fallTimer?.Stop();
        _fallTimer = null;
        _mutateTimer?.Stop();
        _mutateTimer = null;
        _clock.Stop();
    }

    private static LinearGradientBrush FadeMask() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Colors.Transparent, 0),
            new GradientStop(Colors.White, 1)
        }
    };

    /// <summary>
    /// One still frame of the rain as a bitmap, for a background that shouldn't cost anything per frame (the
    /// screensaver). Columns get random lengths and positions, each fading toward its top like the animated version,
    /// and every column is also drawn one height above and below so the bitmap tiles vertically without a seam.
    /// <paramref name="scaling"/> is the display's render scaling (device pixels per DIP).
    /// </summary>
    public static Bitmap RenderStill(double width, double height, double scaling)
    {
        var rng = new Random();
        var typeface = new Typeface(RainFont);
        var green = new SolidColorBrush(Green);
        if (scaling <= 0 || double.IsNaN(scaling)) scaling = 1.0;

        var bitmap = new RenderTargetBitmap(
            new PixelSize(Math.Max(1, (int)Math.Ceiling(width * scaling)), Math.Max(1, (int)Math.Ceiling(height * scaling))),
            new Vector(96 * scaling, 96 * scaling));

        using (var dc = bitmap.CreateDrawingContext())
        {
            int columns = (int)Math.Ceiling(width / ColumnWidth);
            int maxLength = (int)(height / RainLineHeight) + 4;
            for (int i = 0; i < columns; i++)
            {
                int length = rng.Next(6, Math.Max(7, maxLength));
                double top = rng.NextDouble() * (height + length * RainLineHeight * 0.5) - length * RainLineHeight;
                var chars = new char[length * 2 - 1];
                for (int n = 0; n < length; n++)
                {
                    chars[n * 2] = Glyphs[rng.Next(Glyphs.Length)];
                    if (n * 2 + 1 < chars.Length) chars[n * 2 + 1] = '\n';
                }
                var text = new FormattedText(new string(chars), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                             typeface, 14, green)
                {
                    LineHeight = RainLineHeight
                };
                double columnHeight = length * RainLineHeight;

                foreach (double shift in new[] { -height, 0, height })
                {
                    var origin = new Point(i * ColumnWidth, top + shift);
                    using (dc.PushOpacityMask(FadeMask(), new Rect(origin, new Size(ColumnWidth, columnHeight))))
                        dc.DrawText(text, origin);
                }
            }
        }
        return bitmap;
    }

    private char RandomGlyph() => Glyphs[_rng.Next(Glyphs.Length)];
}
