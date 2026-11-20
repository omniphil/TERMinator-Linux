using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TERMinator.Linux.Controls;

/// <summary>
/// "Decrypt" text effect: the characters churn through random glyphs and lock in left to right.
///
/// Set <c>controls:DecryptText.Text</c> instead of <c>TextBlock.Text</c> (bindings welcome). The effect plays whenever
/// the value changes and each time the TextBlock is loaded, so it replays every time the home screen comes back.
/// With <c>controls:DecryptText.ReplayOnHover</c> it also replays when the pointer enters the nearest enclosing button.
/// <see cref="Play"/> runs it on any TextBlock directly (the target-lock caption, the Konami banner).
/// </summary>
public static class DecryptText
{
    private const string Glyphs = "!<>-_\\/[]{}=+*^?#%&$@01";
    private static readonly Random Rng = new();

    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(DecryptText));

    public static string? GetText(TextBlock tb) => tb.GetValue(TextProperty);
    public static void SetText(TextBlock tb, string? value) => tb.SetValue(TextProperty, value);

    public static readonly AttachedProperty<bool> ReplayOnHoverProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("ReplayOnHover", typeof(DecryptText));

    public static bool GetReplayOnHover(TextBlock tb) => tb.GetValue(ReplayOnHoverProperty);
    public static void SetReplayOnHover(TextBlock tb, bool value) => tb.SetValue(ReplayOnHoverProperty, value);

    // Per-TextBlock state: the running animation, and the one-time Loaded / hover hooks
    private sealed class State
    {
        public DispatcherTimer? Timer;
        public bool Hooked;
        public Button? HoverHost;
    }

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, State?>("State", typeof(DecryptText));

    static DecryptText()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>(OnTextChanged);
    }

    private static State StateOf(TextBlock tb)
    {
        var state = tb.GetValue(StateProperty);
        if (state == null)
        {
            state = new State();
            tb.SetValue(StateProperty, state);
        }
        return state;
    }

    private static void OnTextChanged(TextBlock tb, AvaloniaPropertyChangedEventArgs e)
    {
        var state = StateOf(tb);
        if (!state.Hooked)
        {
            state.Hooked = true;
            tb.Loaded += OnLoaded;
            tb.Unloaded += (_, _) => Stop(tb, snapToFinal: true);
        }

        var text = e.NewValue as string ?? string.Empty;
        if (tb.IsLoaded)
            Play(tb, text);
        else
            tb.Text = text;
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var tb = (TextBlock)sender!;
        Play(tb, GetText(tb) ?? string.Empty);

        // Hook the hover replay once, even though the view (and this Loaded) comes and goes
        var state = StateOf(tb);
        if (GetReplayOnHover(tb) && state.HoverHost == null && tb.FindAncestorOfType<Button>() is { } host)
        {
            state.HoverHost = host;
            host.PointerEntered += (_, _) => Play(tb, GetText(tb) ?? string.Empty);
        }
    }

    /// <summary>Plays the effect on <paramref name="tb"/>, ending on <paramref name="finalText"/>.</summary>
    public static void Play(TextBlock tb, string finalText, TimeSpan? duration = null)
    {
        Stop(tb, snapToFinal: false);
        if (finalText.Length == 0)
        {
            tb.Text = finalText;
            return;
        }

        var total = duration ?? TimeSpan.FromMilliseconds(Math.Clamp(220 + finalText.Length * 14, 250, 700));
        var started = DateTime.UtcNow;
        var buffer = new char[finalText.Length];

        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            double progress = (DateTime.UtcNow - started).TotalMilliseconds / total.TotalMilliseconds;
            if (progress >= 1)
            {
                Stop(tb, snapToFinal: false);
                tb.Text = finalText;
                return;
            }

            int locked = (int)(progress * finalText.Length);
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = i < locked || char.IsWhiteSpace(finalText[i])
                    ? finalText[i]
                    : Glyphs[Rng.Next(Glyphs.Length)];
            tb.Text = new string(buffer);
        };
        StateOf(tb).Timer = timer;
        timer.Start();
    }

    private static void Stop(TextBlock tb, bool snapToFinal)
    {
        var state = StateOf(tb);
        if (state.Timer != null)
        {
            state.Timer.Stop();
            state.Timer = null;
        }
        if (snapToFinal && GetText(tb) is { } final)
            tb.Text = final;
    }
}
