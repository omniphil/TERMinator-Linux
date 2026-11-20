using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace TERMinator.Linux.Controls;

/// <summary>
/// Screensaver prop: a (completely fake) GibsonCoin miner console that looks busy — a jittery hash rate, a share
/// progress bar, scrolling nonce / share log lines, GPU stats, a creeping balance and the occasional "BLOCK FOUND"
/// with a flashing border. The stats only change once a second, like a real miner's console.
///
/// Windows builds this out of a Border full of TextBlocks with animated text hinting; here it is drawn straight into
/// the screensaver's own <see cref="Avalonia.Media.DrawingContext"/> (one control drawing everything is what keeps the
/// moving picture smooth on Avalonia — see <see cref="ScreensaverOverlay"/>). Every value, colour and timing is the
/// Windows one. <see cref="Tick"/> advances it once per frame, <see cref="Draw"/> paints it.
/// </summary>
internal sealed class GibsonMinerPanel
{
    public const double BoxWidth = 310;

    private const int LogLines = 5;
    private const double LogLineHeight = 13;
    private const double BlockReward = 6.25;
    private const double TextUpdateInterval = 1.0; // seconds
    private const double Padding = 12;        // left / right
    private const double PaddingY = 10;       // top / bottom
    private const double BorderThickness = 1.5;
    private const double ContentWidth = BoxWidth - 2 * Padding - 2 * BorderThickness;

    private static readonly FontFamily MonoFont = new("JetBrains Mono, DejaVu Sans Mono, monospace");
    private static readonly Color Gold = Color.FromRgb(0xFF, 0xD7, 0x00);
    private static readonly IBrush GoldBrush = new SolidColorBrush(Gold);
    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xDD, 0x55));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x33, 0xFF, 0x33));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x8C));
    private static readonly IBrush AmberBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x00));
    private static readonly IBrush CoinLetterBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x48, 0x00));
    private static readonly IBrush BoxBrush = new SolidColorBrush(Color.FromArgb(0xEE, 0x06, 0x06, 0x10));
    private static readonly IBrush BarBackBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x20));
    private static readonly IPen BarBorderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A)), 1);
    private static readonly IPen CoinPen = new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0x62, 0x00)), 1.5);
    private static readonly IPen GoldPen = new Pen(GoldBrush, BorderThickness);
    private static readonly IPen WhitePen = new Pen(Brushes.White, BorderThickness);

    private static readonly IBrush CoinBrush = new RadialGradientBrush
    {
        GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xFF, 0xF3, 0xA0), 0),
            new GradientStop(Color.FromRgb(0xC8, 0x92, 0x00), 1)
        }
    };

    private static readonly IBrush BarFillBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0x55, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromRgb(0x22, 0x88, 0xFF), 1)
        }
    };

    /// <summary>One line of console text, its FormattedText rebuilt only when the text actually changes.</summary>
    private sealed class Line(double size, IBrush brush, bool bold = false)
    {
        private string _text = string.Empty;
        private FormattedText? _formatted;

        public IBrush Brush { get; } = brush;

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value;
                _formatted = null;
            }
        }

        public FormattedText Formatted => _formatted ??= new FormattedText(
            _text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(MonoFont, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal),
            size, Brush);
    }

    private readonly Line _title = new(14, GoldBrush, bold: true);
    private readonly Line _rig = new(10, DimBrush);
    private readonly Line _hashrate = new(13, GreenBrush, bold: true);
    private readonly Line _hardware = new(10, AmberBrush);
    private readonly Line _balance = new(13, GoldBrush, bold: true);
    private readonly Line _coinLetter = new(14, CoinLetterBrush, bold: true);
    private readonly List<Line> _log = [];
    private readonly Random _rng = new();

    private double _nextTextUpdate;
    private double _progress;
    private double _balanceValue;
    private double _nextBlockTime;
    private double _flashUntil;
    private long _blockHeight;
    private int _accepted;
    private int _submitted;
    private bool _borderWhite;
    private double _height;

    public GibsonMinerPanel()
    {
        _balanceValue = 12.48213377 + _rng.NextDouble() * 3;
        _blockHeight = 482113 + _rng.Next(5000);
        _nextBlockTime = 6 + _rng.NextDouble() * 6;

        _title.Text = "GIBSONCOIN MINER";
        _rig.Text = "rig GIBSON-01  pool stratum+tcp://gibson.pool:1337";
        _coinLetter.Text = "G";
        // Placeholders so the box measures its real height before the first Tick fills these in
        _hashrate.Text = "HASH  1,337.00 GH/s";
        _hardware.Text = "GPU0 84°C  FAN 100%  PWR 410W";
        _balance.Text = FormattableString.Invariant($"BALANCE  {_balanceValue:F8} GBC");
        AddLog("connecting to pool... authorized", SuccessBrush);
    }

    /// <summary>The box's size, so the screensaver can keep it inside its bounds.</summary>
    public Size Size
    {
        get
        {
            if (_height <= 0)
            {
                // Header (24 coin + 6) · rig · 6 · hash · 4 · bar 8 · 6 · log · 6 · hardware · 4 · balance
                _height = 2 * PaddingY + 2 * BorderThickness
                          + 30 + _rig.Formatted.Height + 6 + _hashrate.Formatted.Height + 4 + 8 + 6
                          + LogLines * LogLineHeight + 6 + _hardware.Formatted.Height + 4 + _balance.Formatted.Height;
            }
            return new Size(BoxWidth, _height);
        }
    }

    /// <summary>Advances the fake mining. <paramref name="t"/> is seconds since the screensaver started.</summary>
    public void Tick(double t, double dt)
    {
        // Share progress fills up; each full bar submits a share (the odd one gets rejected)
        _progress += dt * (0.35 + _rng.NextDouble() * 0.3);
        if (_progress >= 1)
        {
            _progress = 0;
            _submitted++;
            bool accepted = _rng.NextDouble() > 0.08;
            if (accepted)
                _accepted++;
            AddLog(accepted ? $"✔ share accepted ({_accepted}/{_submitted})" : $"✘ share rejected ({_accepted}/{_submitted})",
                   accepted ? SuccessBrush : ErrorBrush);
        }

        _balanceValue += dt * 0.00042;

        // Now and then: a block!
        if (t >= _nextBlockTime)
        {
            _blockHeight++;
            _balanceValue += BlockReward;
            AddLog(FormattableString.Invariant($"*** BLOCK #{_blockHeight:N0} FOUND! +{BlockReward} GBC ***"), GoldBrush);
            _flashUntil = t + 0.7;
            _nextBlockTime = t + 12 + _rng.NextDouble() * 8;
        }
        _borderWhite = t < _flashUntil && (int)(t * 12) % 2 == 0;

        // The stats refresh once a second
        if (t < _nextTextUpdate)
            return;
        _nextTextUpdate = t + TextUpdateInterval;

        double rate = 1337 + Math.Sin(t * 0.7) * 120 + _rng.NextDouble() * 40;
        _hashrate.Text = FormattableString.Invariant($"HASH  {rate:N2} GH/s");
        _hardware.Text = $"GPU0 {84 + _rng.Next(5)}°C  FAN 100%  PWR {410 + _rng.Next(25)}W";
        _balance.Text = FormattableString.Invariant($"BALANCE  {_balanceValue:F8} GBC");
        if (_rng.NextDouble() < 0.8)
            AddLog($"nonce 0x{_rng.Next():X8}  hash 0000{RandomHex(12)}", DimBrush);
    }

    /// <summary>Paints the box with its top-left corner at <paramref name="x"/>, <paramref name="y"/>.</summary>
    public void Draw(DrawingContext dc, double x, double y)
    {
        var size = Size;
        double half = BorderThickness / 2;
        dc.DrawRectangle(BoxBrush, _borderWhite ? WhitePen : GoldPen,
                         new Rect(x + half, y + half, size.Width - BorderThickness, size.Height - BorderThickness), 6, 6);

        double cx = x + Padding + BorderThickness;
        double cy = y + PaddingY + BorderThickness;

        // Header: coin and title
        double coinCx = cx + 12, coinCy = cy + 12;
        dc.DrawEllipse(CoinBrush, CoinPen, new Rect(cx, cy, 24, 24));
        var letter = _coinLetter.Formatted;
        dc.DrawText(letter, new Point(coinCx - letter.Width / 2, coinCy - letter.Height / 2));

        var title = _title.Formatted;
        dc.DrawText(title, new Point(cx + 32, coinCy - title.Height / 2));

        double lineY = cy + 30;
        dc.DrawText(_rig.Formatted, new Point(cx, lineY));
        lineY += _rig.Formatted.Height + 6;
        dc.DrawText(_hashrate.Formatted, new Point(cx, lineY));
        lineY += _hashrate.Formatted.Height + 4;

        // Share progress bar
        dc.DrawRectangle(BarBackBrush, BarBorderPen, new Rect(cx + 0.5, lineY + 0.5, ContentWidth - 1, 7));
        double fill = Math.Max(0, (ContentWidth - 2) * _progress);
        if (fill > 0)
            dc.FillRectangle(BarFillBrush, new Rect(cx + 1, lineY + 1, fill, 6));
        lineY += 8 + 6;

        // Log: the last five lines, clipped to their band
        using (dc.PushClip(new Rect(cx, lineY, ContentWidth, LogLines * LogLineHeight)))
        {
            double logY = lineY;
            foreach (var line in _log)
            {
                dc.DrawText(line.Formatted, new Point(cx, logY));
                logY += LogLineHeight;
            }
        }
        lineY += LogLines * LogLineHeight + 6;

        dc.DrawText(_hardware.Formatted, new Point(cx, lineY));
        lineY += _hardware.Formatted.Height + 4;
        dc.DrawText(_balance.Formatted, new Point(cx, lineY));
    }

    private void AddLog(string text, IBrush brush)
    {
        var line = new Line(10, brush) { Text = text };
        line.Formatted.MaxTextWidth = ContentWidth;
        line.Formatted.Trimming = TextTrimming.CharacterEllipsis;
        line.Formatted.MaxLineCount = 1;
        _log.Add(line);
        while (_log.Count > LogLines)
            _log.RemoveAt(0);
    }

    private string RandomHex(int length)
    {
        const string hex = "0123456789abcdef";
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = hex[_rng.Next(hex.Length)];
        return new string(chars);
    }
}
