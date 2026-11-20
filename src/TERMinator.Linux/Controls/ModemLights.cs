using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TERMinator.Linux.Controls;

/// <summary>
/// The front panel of an external modem, in the US Robotics Sportster order: HS AA CD OH RD SD TR MR, a red LED over
/// each label. TerminalView drives it from the connection: RD and SD flicker on real received and sent bytes, CD and OH
/// light while connected, HS while the line runs at 9600 bps or faster. Same look as the Windows client.
/// </summary>
public sealed class ModemLights : StackPanel
{
    public static readonly string[] Labels = ["HS", "AA", "CD", "OH", "RD", "SD", "TR", "MR"];

    private static readonly IBrush LitBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x2F));
    private static readonly IBrush DarkBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x3A, 0x12, 0x12));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x77, 0x77, 0x99));
    private static readonly FontFamily LabelFont = new("JetBrains Mono, DejaVu Sans Mono, monospace");

    private readonly Ellipse[] _leds = new Ellipse[Labels.Length];
    private readonly DropShadowEffect[] _glows = new DropShadowEffect[Labels.Length];
    private readonly bool[] _lit = new bool[Labels.Length];

    public ModemLights()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(this, "HS high speed · AA auto answer · CD carrier detect · OH off hook · " +
                             "RD receive data · SD send data · TR terminal ready · MR modem ready");

        for (int i = 0; i < Labels.Length; i++)
        {
            var led = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = DarkBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1)
            };
            var label = new TextBlock
            {
                Text = Labels[i],
                FontFamily = LabelFont,
                FontSize = 9,
                Foreground = LabelBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            cell.Children.Add(led);
            cell.Children.Add(label);
            Children.Add(cell);
            _leds[i] = led;
            _glows[i] = new DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0x30, 0x20),
                BlurRadius = 8,
                OffsetX = 0,
                OffsetY = 0,
                Opacity = 0.9
            };
        }
    }

    public void Set(bool hs, bool aa, bool cd, bool oh, bool rd, bool sd, bool tr, bool mr)
    {
        Light(0, hs);
        Light(1, aa);
        Light(2, cd);
        Light(3, oh);
        Light(4, rd);
        Light(5, sd);
        Light(6, tr);
        Light(7, mr);
    }

    public void AllOff() => Set(false, false, false, false, false, false, false, false);

    private void Light(int index, bool on)
    {
        if (_lit[index] == on)
            return;
        _lit[index] = on;
        _leds[index].Fill = on ? LitBrush : DarkBrush;
        _leds[index].Effect = on ? _glows[index] : null;
    }
}
