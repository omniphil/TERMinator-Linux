using System.IO;
using System.Text;
using Avalonia.Platform;

namespace TERMinator.Linux.Services;

/// <summary>
/// IBM Model M key clicks while typing into a board or the command line. The clips are real buckling-spring
/// recordings cut into single presses: "Typing - Model M 1986.ogg" by Raymangold22 (CC0) and "Modelm.ogg" by
/// Cpuwhiz13 (public domain), both from Wikimedia Commons.
///
/// Played through the native OpenAL bridge: each clip is uploaded once into its own buffer, and every keypress plays
/// one on a small pool of sources, so fast typing overlaps the clicks. (The bridge's play_pcm restarts its channel on
/// every call, which would cut each click off.)
/// </summary>
public sealed class KeyClickService
{
    private const int KeyClipCount = 8;
    private const int SpaceClipCount = 2;
    private const int FirstSpaceClipId = KeyClipCount; // ids 0-7: keys, 8-9: spacebar

    private readonly SettingsService _settings;
    private bool _loaded;
    private int _keysLoaded;
    private int _spacesLoaded;
    private int _lastKey = -1;

    public KeyClickService(SettingsService settings) => _settings = settings;

    /// <summary>Uploads the clips ahead of the first keystroke. Call once the audio bridge is up.</summary>
    public void WarmUp() => EnsureLoaded();

    /// <summary>One keystroke, if key clicks are on. <paramref name="space"/> uses the deeper spacebar sound.</summary>
    public void Play(bool space = false)
    {
        var settings = _settings.Settings;
        if (settings.KeyClicksEnabled)
            PlayAt(settings.KeyClickVolume, space);
    }

    /// <summary>The Preferences test button: plays at the given volume even while key clicks are off.</summary>
    public void PlayPreview(int volume) => PlayAt(volume, space: false);

    private void PlayAt(int volume, bool space)
    {
        if (volume <= 0 || !EnsureLoaded())
            return;

        int clipId;
        if (space && _spacesLoaded > 0)
        {
            clipId = FirstSpaceClipId + Random.Shared.Next(_spacesLoaded);
        }
        else
        {
            // Never the same clip twice running, so a held-down rhythm doesn't sound like a loop
            int index = Random.Shared.Next(_keysLoaded);
            if (index == _lastKey && _keysLoaded > 1)
                index = (index + 1) % _keysLoaded;
            _lastKey = index;
            clipId = index;
        }

        float gain = Math.Clamp(volume / 10f, 0f, 1f) * (0.85f + 0.15f * (float)Random.Shared.NextDouble());
        try
        {
            NativeBridge.audio_bridge_play_clip(clipId, gain);
        }
        catch
        {
            // No audio bridge: no clicks
        }
    }

    private bool EnsureLoaded()
    {
        if (_loaded)
            return _keysLoaded > 0;
        _loaded = true;

        // Ids stay contiguous: a clip that fails to load doesn't leave a hole the random pick could land in
        for (int i = 1; i <= KeyClipCount; i++)
        {
            if (TryLoad($"modelm_key{i}", _keysLoaded))
                _keysLoaded++;
        }
        for (int i = 1; i <= SpaceClipCount; i++)
        {
            if (TryLoad($"modelm_space{i}", FirstSpaceClipId + _spacesLoaded))
                _spacesLoaded++;
        }
        return _keysLoaded > 0;
    }

    /// <summary>Reads an embedded 16-bit PCM WAV and uploads it to the bridge as clip <paramref name="clipId"/>.</summary>
    private static bool TryLoad(string name, int clipId)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://TERMinator/Assets/Sounds/{name}.wav"));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var wav = memory.ToArray();
            if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
                return false;

            int channels = 0, rate = 0, bits = 0;
            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                int body = pos + 8;
                if (size < 0 || body + size > wav.Length)
                    size = wav.Length - body;

                if (id == "fmt " && size >= 16)
                {
                    channels = BitConverter.ToInt16(wav, body + 2);
                    rate = BitConverter.ToInt32(wav, body + 4);
                    bits = BitConverter.ToInt16(wav, body + 14);
                }
                else if (id == "data")
                {
                    if (bits != 16 || channels is < 1 or > 2 || rate <= 0)
                        return false;
                    var samples = new short[size / 2];
                    Buffer.BlockCopy(wav, body, samples, 0, samples.Length * 2);
                    int frames = samples.Length / channels;
                    return frames > 0 && NativeBridge.audio_bridge_load_clip(clipId, samples, frames, rate, channels) != 0;
                }
                pos = body + size + (size & 1);
            }
        }
        catch
        {
            // A missing or unreadable clip just isn't used
        }
        return false;
    }
}
