namespace TERMinator.Linux.Services;

/// <summary>
/// Sound from a TRACE module (engine contract: TE_OUT_AUDIO records, S16 stereo at TE_AUDIO_RATE). The module
/// writes frames as it makes them and this carries them into TERMinator's own output, so the player's volume control
/// covers module sound like everything else. A short queue holds them in between; when it runs dry the mix gets
/// silence rather than a glitch, and TERMinator keeps telling the module how much room is left (TE_IN_AUDIO_ROOM) so
/// it never runs ahead.
/// Linux port of the Windows client's Services/TraceAudioStream.cs: instead of an NAudio sample provider it is summed
/// into the SyncTERM door mix (AudioService.SyncTermAudioLoop, OpenAL channel 6), which already plays at the
/// soundtrack volume, so the gain here is only on (1) or off (0).
/// </summary>
public sealed class TraceAudioStream
{
    /// <summary>Half a second of stereo frames: enough to ride out a stall, short enough to stay responsive.</summary>
    public const int CapacityFrames = 22050;

    private readonly Func<float> _gain;
    private readonly short[] _ring = new short[CapacityFrames * 2];
    private readonly object _lock = new();
    private int _read, _count;   // samples (not frames)

    public TraceAudioStream(Func<float> gain) => _gain = gain;

    /// <summary>Frames the queue can still take.</summary>
    public int RoomFrames
    {
        get { lock (_lock) return (_ring.Length - _count) / 2; }
    }

    /// <summary>Queues frames from a module. Returns the frames taken (the rest is dropped: the module was too fast).</summary>
    public int Write(ReadOnlySpan<short> samples)
    {
        lock (_lock)
        {
            int take = Math.Min(samples.Length, _ring.Length - _count);
            int write = (_read + _count) % _ring.Length;
            for (int i = 0; i < take; i++)
            {
                _ring[write] = samples[i];
                write = write + 1 == _ring.Length ? 0 : write + 1;
            }
            _count += take;
            return take / 2;
        }
    }

    /// <summary>Throws away what's queued (the module stopped, or its sound is no longer wanted).</summary>
    public void Clear()
    {
        lock (_lock)
            (_read, _count) = (0, 0);
    }

    /// <summary>
    /// Takes up to <paramref name="count"/> samples off the queue and adds them into <paramref name="mix"/> (S16
    /// interleaved stereo), clipping. An underrun just adds nothing: the door mix around it keeps flowing.
    /// </summary>
    public void MixInto(short[] mix, int count)
    {
        float gain = _gain();
        lock (_lock)
        {
            int take = Math.Min(count, _count);
            for (int i = 0; i < take; i++)
            {
                if (gain > 0f)
                {
                    int sum = mix[i] + (int)(_ring[_read] * gain);
                    mix[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
                }
                _read = _read + 1 == _ring.Length ? 0 : _read + 1;
            }
            _count -= take;
        }
    }
}
