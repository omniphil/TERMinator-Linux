using System.Linq;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.ViewModels;

/// <summary>
/// Backing model for the CRT options dialog. Every change raises
/// <see cref="Changed"/> so the host can push it straight at the live
/// terminal — there is no OK button to wait for.
/// </summary>
public partial class CrtSettingsViewModel : ObservableObject
{
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _preset = "Classic CRT";
    [ObservableProperty] private double _scanlineIntensity;
    [ObservableProperty] private double _scanlinePitch;
    [ObservableProperty] private double _curvature;
    [ObservableProperty] private double _maskIntensity;
    [ObservableProperty] private double _maskPitch;
    [ObservableProperty] private double _glow;
    [ObservableProperty] private double _vignette;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _aberration;
    [ObservableProperty] private double _flicker;
    [ObservableProperty] private double _tintAmount;
    [ObservableProperty] private string _tintColor = "#FFFFFF";
    [ObservableProperty] private double _overscan;
    [ObservableProperty] private double _bloomThreshold;
    [ObservableProperty] private double _beamWidth;
    [ObservableProperty] private double _interlace;
    [ObservableProperty] private double _verticalHold;
    [ObservableProperty] private double _noise;
    [ObservableProperty] private double _scanSweep;

    /// <summary>Preset list plus the "Custom" slot the sliders fall into.</summary>
    public string[] PresetOptions { get; } =
        CrtSettings.PresetNames.Append("Custom").ToArray();

    public PhosphorOption[] TintOptions { get; } =
    {
        new("White",  "#FFFFFF"),
        new("Green",  "#33FF66"),
        new("Amber",  "#FFB000"),
        new("Blue",   "#88BBFF"),
        new("Paper",  "#FFE8C8")
    };

    public record PhosphorOption(string Name, string Hex);

    /// <summary>
    /// The tube-colour combo's selection as an object.
    ///
    /// ⚠️ Avalonia has no SelectedValuePath, which is what the Windows dialog binds the same
    /// combo through. Mapping here keeps <see cref="TintColor"/> the single source of truth and
    /// keeps the two dialogs behaviourally identical.
    /// </summary>
    public PhosphorOption? TintOption
    {
        get => TintOptions.FirstOrDefault(
                   o => string.Equals(o.Hex, TintColor, StringComparison.OrdinalIgnoreCase))
               ?? TintOptions[0];
        set
        {
            if (value != null && !string.Equals(value.Hex, TintColor, StringComparison.OrdinalIgnoreCase))
                TintColor = value.Hex;
        }
    }

    /// <summary>True while a preset is being loaded, so the slider writes it
    /// causes don't immediately flip the selection back to "Custom".</summary>
    private bool _loading;

    public event Action<CrtSettings>? Changed;

    public CrtSettingsViewModel(CrtSettings initial)
    {
        Load(initial);
    }

    public void Load(CrtSettings s)
    {
        _loading = true;
        try
        {
            Enabled = s.Enabled;
            ScanlineIntensity = s.ScanlineIntensity;
            ScanlinePitch = s.ScanlinePitch;
            Curvature = s.Curvature;
            MaskIntensity = s.MaskIntensity;
            MaskPitch = s.MaskPitch;
            Glow = s.Glow;
            Vignette = s.Vignette;
            Brightness = s.Brightness;
            Aberration = s.Aberration;
            Flicker = s.Flicker;
            TintAmount = s.TintAmount;
            TintColor = s.TintColor;
            Overscan = s.Overscan;
            BloomThreshold = s.BloomThreshold;
            BeamWidth = s.BeamWidth;
            Interlace = s.Interlace;
            VerticalHold = s.VerticalHold;
            Noise = s.Noise;
            ScanSweep = s.ScanSweep;
            Preset = s.Preset;
        }
        finally
        {
            _loading = false;
        }
        Changed?.Invoke(ToSettings());
    }

    public CrtSettings ToSettings() => new()
    {
        Enabled = Enabled,
        Preset = Preset,
        ScanlineIntensity = ScanlineIntensity,
        ScanlinePitch = ScanlinePitch,
        Curvature = Curvature,
        MaskIntensity = MaskIntensity,
        MaskPitch = MaskPitch,
        Glow = Glow,
        Vignette = Vignette,
        Brightness = Brightness,
        Aberration = Aberration,
        Flicker = Flicker,
        TintAmount = TintAmount,
        TintColor = TintColor,
        Overscan = Overscan,
        BloomThreshold = BloomThreshold,
        BeamWidth = BeamWidth,
        Interlace = Interlace,
        VerticalHold = VerticalHold,
        Noise = Noise,
        ScanSweep = ScanSweep
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // A preset can move TintColor without the combo knowing; nudge it either way.
        if (e.PropertyName == nameof(TintColor))
            OnPropertyChanged(nameof(TintOption));

        if (_loading) return;

        if (e.PropertyName == nameof(Preset))
        {
            // Selecting a real preset reloads every slider; "Custom" is just a
            // label for whatever the sliders currently say.
            if (Preset != "Custom")
            {
                var preset = CrtSettings.FromPreset(Preset);
                preset.Enabled = Enabled;
                Load(preset);
                return;
            }
        }
        else if (e.PropertyName != nameof(Enabled))
        {
            // A slider moved — this is no longer any named preset.
            if (Preset != "Custom")
            {
                _loading = true;
                Preset = "Custom";
                _loading = false;
            }
        }

        Changed?.Invoke(ToSettings());
    }
}
