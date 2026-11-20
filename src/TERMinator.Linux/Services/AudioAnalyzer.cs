using System;

namespace TERMinator.Linux.Services;

/// <summary>
/// Real-time audio analyzer for visualizer overlay.
/// Uses Goertzel algorithm to compute energy at 16 logarithmic frequency bands (60Hz-16kHz).
/// Thread-safe: audio thread writes via volatile reference swap, UI thread reads snapshots.
/// </summary>
public class AudioAnalyzer
{
    private const int NumBands = 16;
    private const int ScopeSamples = 128;
    private const float SampleRate = 44100f;
    private const int BlockSize = 1024;
    private const float Attack = 0.6f;
    private const float Decay = 0.25f;

    private readonly float[] _bandFreqs = new float[NumBands];
    private readonly float[] _goertzelCoeff = new float[NumBands];
    private readonly float[] _bandGain = new float[NumBands];
    private readonly float[] _monoBuffer = new float[BlockSize];
    private readonly float[] _smoothedBands = new float[NumBands];

    private volatile float[] _currentBands = new float[NumBands];
    private volatile float[] _currentScope = new float[ScopeSamples];

    public float[] CurrentBands => _currentBands;
    public float[] CurrentScope => _currentScope;

    public AudioAnalyzer()
    {
        double minFreq = 60.0;
        double maxFreq = 16000.0;
        double logMin = Math.Log(minFreq);
        double logMax = Math.Log(maxFreq);

        for (int b = 0; b < NumBands; b++)
        {
            double freqLow = Math.Exp(logMin + (logMax - logMin) * b / NumBands);
            double freqHigh = Math.Exp(logMin + (logMax - logMin) * (b + 1) / NumBands);
            _bandFreqs[b] = (float)Math.Sqrt(freqLow * freqHigh);

            double k = _bandFreqs[b] * BlockSize / SampleRate;
            _goertzelCoeff[b] = (float)(2.0 * Math.Cos(2.0 * Math.PI * k / BlockSize));

            _bandGain[b] = 6f * (1f + b * 0.5f);
        }
    }

    /// <summary>
    /// Feed stereo 16-bit PCM data from the audio playback thread.
    /// Buffer is interleaved stereo (L, R, L, R, ...).
    /// </summary>
    public void FeedPcm(short[] buffer, int frames)
    {
        int count = Math.Min(frames, BlockSize);

        // Mix stereo to mono, normalize to [-1, 1]
        for (int i = 0; i < count; i++)
        {
            float left = buffer[i * 2];
            float right = buffer[i * 2 + 1];
            _monoBuffer[i] = (left + right) * 0.5f / 32768f;
        }
        for (int i = count; i < BlockSize; i++)
            _monoBuffer[i] = 0f;

        // Capture scope waveform
        var scopeBuf = new float[ScopeSamples];
        int scopeCount = Math.Min(count, ScopeSamples);
        Array.Copy(_monoBuffer, scopeBuf, scopeCount);
        _currentScope = scopeBuf;

        // Compute energy per band using Goertzel algorithm
        var newBands = new float[NumBands];
        for (int b = 0; b < NumBands; b++)
        {
            float coeff = _goertzelCoeff[b];
            float s1 = 0f, s2 = 0f;

            for (int i = 0; i < count; i++)
            {
                float s0 = _monoBuffer[i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            float magSq = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            float mag = MathF.Sqrt(MathF.Max(magSq, 0f)) / count;
            float scaled = MathF.Sqrt(MathF.Min(mag * _bandGain[b], 1f));

            _smoothedBands[b] = scaled > _smoothedBands[b]
                ? _smoothedBands[b] + (scaled - _smoothedBands[b]) * Attack
                : _smoothedBands[b] + (scaled - _smoothedBands[b]) * Decay;

            newBands[b] = _smoothedBands[b];
        }
        _currentBands = newBands;
    }

    /// <summary>
    /// Reset all bands and scope to zero (e.g. when playback stops).
    /// </summary>
    public void Reset()
    {
        Array.Clear(_smoothedBands);
        _currentBands = new float[NumBands];
        _currentScope = new float[ScopeSamples];
    }
}
