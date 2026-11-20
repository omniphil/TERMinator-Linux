namespace TERMinator.Linux.Services;

/// <summary>
/// Generates and plays historically-accurate bell sounds for terminal BEL character.
/// 30 unique waveforms matching classic computer hardware from 1975-1989.
/// Uses native OpenAL Soft audio bridge for playback (channel 4).
/// </summary>
public class BellService : IDisposable
{
    private const int SampleRate = 44100;

    public static readonly string[] BellNames =
    [
        "1975 \u2022 Altair 8800",
        "1977 \u2022 Apple II",
        "1977 \u2022 Commodore PET",
        "1977 \u2022 TRS-80",
        "1978 \u2022 DEC VT100",
        "1979 \u2022 Atari 800",
        "1980 \u2022 Apple III",
        "1980 \u2022 Commodore VIC-20",
        "1980 \u2022 Tandy Color Computer",
        "1981 \u2022 IBM PC",
        "1981 \u2022 BBC Micro",
        "1981 \u2022 Sinclair ZX81",
        "1981 \u2022 TI-99/4A",
        "1981 \u2022 Osborne 1",
        "1982 \u2022 Commodore 64",
        "1982 \u2022 ZX Spectrum",
        "1982 \u2022 Kaypro",
        "1982 \u2022 Colecovision",
        "1983 \u2022 NES / Famicom",
        "1983 \u2022 MSX",
        "1984 \u2022 Macintosh",
        "1984 \u2022 Amstrad CPC",
        "1984 \u2022 Tandy 1000",
        "1984 \u2022 IBM PCjr",
        "1985 \u2022 Amiga",
        "1987 \u2022 Acorn Archimedes",
        "1988 \u2022 NeXT Computer",
        "1989 \u2022 Game Boy",
        "1989 \u2022 Sun SPARCstation",
        "System Default"
    ];

    public static readonly string[] BellDescriptions =
    [
        "No Speaker (RF Interference) \u2022 Front panel music via CPU noise on AM radio",
        "1-bit Speaker Toggle \u2022 Two-tone sequence (880Hz \u2192 440Hz), high-low pattern",
        "MOS 6522 VIA + Piezo \u2022 Simple beeper, very short clicky tick with fast decay",
        "Cassette Port Audio \u2022 Buzzy square wave at 450Hz with frequency wobble",
        "DC305 Bell Generator \u2022 The canonical terminal bell, clean 750Hz tone",
        "POKEY C012294 Chip \u2022 Mixed frequencies with buzzy complex character",
        "MOS 6522 VIA + 6-bit DAC \u2022 Business computer, slightly different than Apple II",
        "MOS 6560/6561 VIC Chip \u2022 3 square wave voices, simpler predecessor to SID",
        "Motorola MC6847 + 6-bit DAC \u2022 Direct digital audio, unique character",
        "Intel 8253 PIT + PC Speaker \u2022 Square wave at 1000Hz, the harsh iconic beep",
        "Texas Instruments SN76489 \u2022 Descending frequency sweep (2000Hz \u2192 800Hz)",
        "Ferranti ULA + Piezo \u2022 Ultra-simple 1-bit toggle, more primitive than Spectrum",
        "TMS9919 PSG Chip \u2022 Distinctive warble/vibrato effect with two voices",
        "Piezo Speaker \u2022 First portable computer, high-pitched 2kHz tinny with ringing",
        "MOS 6581/8580 SID Chip \u2022 Pulse wave with modulating duty cycle, warm and rich",
        "Ferranti ULA Beeper (1-bit) \u2022 Rapid pitch bend, starts high, drops, stabilizes",
        "Piezo Beeper \u2022 Popular CP/M portable, clean business-like square wave",
        "Texas Instruments SN76489 \u2022 Game console style, descending arpeggio",
        "Ricoh 2A03 APU \u2022 Triangle wave bass + 25% duty pulse, warm NES sound",
        "General Instrument AY-3-8910 \u2022 Quick descending two-note, Japanese standard",
        "Apple Sound Chip (ASC) \u2022 Chorused 480Hz with additive synthesis",
        "General Instrument AY-3-8912 \u2022 Quick 3-note arpeggio, bright and punchy",
        "Texas Instruments SN76496 (3-voice) \u2022 C major chord, richer than PC speaker",
        "Texas Instruments SN76496 (3-voice) \u2022 Two voices in octave, PCjr home style",
        "MOS 8364 Paula (4-channel) \u2022 Warm 8-bit PCM with harmonics, stereo output",
        "VIDC1 (8-channel PCM) \u2022 Wavetable-style harmonic-rich bell timbre",
        "Motorola DSP56001 + DAC \u2022 FM synthesis, sophisticated curved envelope",
        "Sharp LR35902 APU (4-ch) \u2022 Pulse wave with duty sweep, 4-bit DAC",
        "AMD AM79C30A Codec \u2022 Clean Unix beep, pure 440Hz sine, professional",
        "Uses your system's default notification sound"
    ];

    private bool _disposed;

    /// <summary>
    /// Pre-warm the OpenAL bell channel so the first real bell has no source-allocation latency.
    /// Plays a very short silent buffer on channel 4 to force OpenAL to allocate its source.
    /// </summary>
    public void WarmUp()
    {
        try
        {
            // 5ms of silence at 44.1kHz mono = 220 zero samples. Enough to instantiate
            // the channel's OpenAL source without producing audible output.
            var silence = new short[220];
            NativeBridge.audio_bridge_play_pcm(4, silence, silence.Length, SampleRate, 1);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BellService.WarmUp error: {ex.Message}");
        }
    }

    public void PlayBell(int index, float volume)
    {
        try
        {
            if (index == 29)
            {
                // System default: simple 440Hz sine tone
                var defaultSamples = GenerateSun();
                if (defaultSamples != null)
                {
                    if (volume < 1.0f)
                        for (int i = 0; i < defaultSamples.Length; i++)
                            defaultSamples[i] = (short)(defaultSamples[i] * volume);
                    NativeBridge.audio_bridge_play_pcm(4, defaultSamples, defaultSamples.Length, SampleRate, 1);
                }
                return;
            }

            var samples = GenerateBell(index);
            if (samples == null || samples.Length == 0)
            {
                // Fallback to a simple 440Hz sine tone
                samples = GenerateSun();
                if (samples == null) return;
            }

            if (volume < 1.0f)
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (short)(samples[i] * volume);
            }

            NativeBridge.audio_bridge_play_pcm(4, samples, samples.Length, SampleRate, 1);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BellService.PlayBell error: {ex.Message}");
        }
    }

    private static short[]? GenerateBell(int index)
    {
        return index switch
        {
            0 => GenerateAltair(),
            1 => GenerateAppleII(),
            2 => GeneratePET(),
            3 => GenerateTRS80(),
            4 => GenerateVT100(),
            5 => GenerateAtari(),
            6 => GenerateAppleIII(),
            7 => GenerateVIC20(),
            8 => GenerateCoCo(),
            9 => GenerateSquareWave(1000, 150),
            10 => GenerateBBCMicro(),
            11 => GenerateZX81(),
            12 => GenerateTI99(),
            13 => GenerateOsborne(),
            14 => GenerateC64(),
            15 => GenerateZXSpectrum(),
            16 => GenerateKaypro(),
            17 => GenerateColeco(),
            18 => GenerateNES(),
            19 => GenerateMSX(),
            20 => GenerateMacClassic(),
            21 => GenerateAmstradCPC(),
            22 => GenerateTandy1000(),
            23 => GeneratePCjr(),
            24 => GenerateAmiga(),
            25 => GenerateArchimedes(),
            26 => GenerateNeXT(),
            27 => GenerateGameBoy(),
            28 => GenerateSun(),
            _ => null
        };
    }

    private static short[] GenerateAltair()
    {
        int durationMs = 200;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        double phase = 0.0;
        int fadeLength = numSamples / 10;

        for (int i = 0; i < numSamples; i++)
        {
            double freq1 = 800.0 + (i % 100) * 5;
            double freq2 = 1200.0 + (i % 73) * 7;
            double freq3 = 400.0 + (i % 150) * 3;

            phase += 2.0 * Math.PI / SampleRate;
            double amplitude = Math.Sin(phase * freq1) * 0.4 +
                               Math.Sin(phase * freq2) * 0.3 +
                               Math.Sin(phase * freq3) * 0.3;

            if (i % 44 < 22) amplitude *= 0.8;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GenerateAppleII()
    {
        int durationMs = 100;
        int frequency = 1000;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int period = SampleRate / frequency;
        int fadeLength = numSamples / 10;

        for (int i = 0; i < numSamples; i++)
        {
            double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GeneratePET()
    {
        int durationMs = 50;
        int frequency = 1000;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int period = SampleRate / frequency;

        for (int i = 0; i < numSamples; i++)
        {
            double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;
            double progress = (double)i / numSamples;
            amplitude *= 1.0 - progress;
            samples[i] = (short)(amplitude * short.MaxValue * 0.6);
        }
        return samples;
    }

    private static short[] GenerateTRS80()
    {
        int durationMs = 120;
        double baseFreq = 450.0;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 10;

        for (int i = 0; i < numSamples; i++)
        {
            double wobble = 1.0 + 0.02 * Math.Sin(2.0 * Math.PI * i * 30.0 / SampleRate);
            double frequency = baseFreq * wobble;
            int period = Math.Max((int)(SampleRate / frequency), 1);

            double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.55);
        }
        return samples;
    }

    private static short[] GenerateVT100()
    {
        int durationMs = 100;
        int frequency = 800;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int attackLength = numSamples / 20;
        int decayStart = numSamples / 3;

        for (int i = 0; i < numSamples; i++)
        {
            double angle = 2.0 * Math.PI * i * frequency / SampleRate;
            double amplitude = Math.Sin(angle);

            if (i < attackLength)
                amplitude *= (double)i / attackLength;
            else if (i > decayStart)
            {
                double decayProgress = (double)(i - decayStart) / (numSamples - decayStart);
                amplitude *= 1.0 - decayProgress * decayProgress;
            }

            samples[i] = (short)(amplitude * short.MaxValue * 0.75);
        }
        return samples;
    }

    private static short[] GenerateAtari()
    {
        int durationMs = 120;
        int frequency = 960;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int period = SampleRate / frequency;
        int fadeLength = Math.Min(numSamples / 10, 300);

        for (int i = 0; i < numSamples; i++)
        {
            double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GenerateAppleIII()
    {
        int durationMs = 140;
        double frequency = 770.0;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 12;

        for (int i = 0; i < numSamples; i++)
        {
            double angle = 2.0 * Math.PI * i * frequency / SampleRate;
            double amplitude = Math.Sin(angle) * 0.8 + Math.Sin(angle * 3) * 0.2;
            amplitude = (int)(amplitude * 32) / 32.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.6);
        }
        return samples;
    }

    private static short[] GenerateVIC20()
    {
        int durationMs = 150;
        int freq1 = 523, freq2 = 659;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 8;
        int period1 = SampleRate / freq1;
        int period2 = SampleRate / freq2;

        for (int i = 0; i < numSamples; i++)
        {
            double wave1 = (i % period1) < (period1 / 2) ? 1.0 : -1.0;
            double wave2 = (i % period2) < (period2 / 2) ? 1.0 : -1.0;
            double amplitude = wave1 * 0.6 + wave2 * 0.4;
            amplitude = (int)(amplitude * 8) / 8.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.55);
        }
        return samples;
    }

    private static short[] GenerateCoCo()
    {
        int durationMs = 130;
        double frequency = 660.0;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 10;

        for (int i = 0; i < numSamples; i++)
        {
            double angle = 2.0 * Math.PI * i * frequency / SampleRate;
            double amplitude = Math.Sin(angle) * 0.7 + Math.Sin(angle * 2) * 0.2 + Math.Sin(angle * 3) * 0.1;
            amplitude = (int)(amplitude * 32) / 32.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.6);
        }
        return samples;
    }

    private static short[] GenerateSquareWave(int frequency, int durationMs)
    {
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int period = SampleRate / frequency;
        int fadeLength = Math.Min(numSamples / 10, 500);

        for (int i = 0; i < numSamples; i++)
        {
            double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.3);
        }
        return samples;
    }

    private static short[] GenerateBBCMicro()
    {
        int durationMs = 200;
        double startFreq = 2000.0, endFreq = 800.0;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 10;
        double phase = 0.0;

        for (int i = 0; i < numSamples; i++)
        {
            double progress = (double)i / numSamples;
            double frequency = startFreq + (endFreq - startFreq) * progress;
            phase += 2.0 * Math.PI * frequency / SampleRate;
            double amplitude = Math.Sin(phase);

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.6);
        }
        return samples;
    }

    private static short[] GenerateZX81()
    {
        int durationMs = 60;
        int frequency = 900;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int period = SampleRate / frequency;

        for (int i = 0; i < numSamples; i++)
        {
            double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;
            double progress = (double)i / numSamples;
            amplitude *= 1.0 - progress * 0.5;
            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GenerateTI99()
    {
        int durationMs = 200;
        double baseFreq = 660.0;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 10;

        for (int i = 0; i < numSamples; i++)
        {
            double vibratoRate = 25.0;
            double vibratoDepth = 0.03;
            double vibrato = 1.0 + vibratoDepth * Math.Sin(2.0 * Math.PI * i * vibratoRate / SampleRate);
            double frequency = baseFreq * vibrato;
            int period = Math.Max((int)(SampleRate / frequency), 1);

            double freq2 = baseFreq * 1.5 * vibrato;
            int period2 = Math.Max((int)(SampleRate / freq2), 1);

            double wave1 = (i % period) < (period / 2) ? 1.0 : -1.0;
            double wave2 = (i % period2) < (period2 / 2) ? 1.0 : -1.0;
            double amplitude = wave1 * 0.6 + wave2 * 0.4;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GenerateOsborne()
    {
        int durationMs = 80;
        int frequency = 2000;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int period = SampleRate / frequency;

        for (int i = 0; i < numSamples; i++)
        {
            double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;
            double ring = Math.Sin(2.0 * Math.PI * i * 4000.0 / SampleRate) * 0.15;
            amplitude = amplitude * 0.85 + ring;

            double progress = (double)i / numSamples;
            amplitude *= 1.0 - progress * progress;

            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GenerateC64()
    {
        int durationMs = 200;
        int frequency = 750;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int decayStart = numSamples / 20;

        for (int i = 0; i < numSamples; i++)
        {
            double phase = ((double)i * frequency / SampleRate) % 1.0;
            double amplitude = phase < 0.5 ? 4 * phase - 1 : 3 - 4 * phase;

            if (i < decayStart)
                amplitude *= (double)i / decayStart;
            else
            {
                double decayProgress = (double)(i - decayStart) / (numSamples - decayStart);
                amplitude *= (1.0 - decayProgress) * (1.0 - decayProgress);
            }

            samples[i] = (short)(amplitude * short.MaxValue * 0.7);
        }
        return samples;
    }

    private static short[] GenerateZXSpectrum()
    {
        int durationMs = 150;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 12;
        double phase = 0.0;

        for (int i = 0; i < numSamples; i++)
        {
            double progress = (double)i / numSamples;
            double frequency;
            if (progress < 0.1)
                frequency = 2400.0 - (progress / 0.1) * 1400.0;
            else if (progress < 0.3)
                frequency = 1000.0 - ((progress - 0.1) / 0.2) * 200.0;
            else
                frequency = 800.0;

            phase += 2.0 * Math.PI * frequency / SampleRate;
            double amplitude = Math.Sin(phase) > 0 ? 1.0 : -1.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GenerateKaypro()
    {
        int durationMs = 100;
        int frequency = 800;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int period = SampleRate / frequency;
        int fadeLength = numSamples / 15;

        for (int i = 0; i < numSamples; i++)
        {
            double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GenerateColeco()
    {
        int durationMs = 120;
        int[] freqs = [1047, 880, 698];
        int numSamples = SampleRate * durationMs / 1000;
        int samplesPerNote = numSamples / freqs.Length;
        var samples = new short[numSamples];

        for (int noteIdx = 0; noteIdx < freqs.Length; noteIdx++)
        {
            int freq = freqs[noteIdx];
            int period = SampleRate / freq;
            int noteStart = noteIdx * samplesPerNote;

            for (int i = 0; i < samplesPerNote; i++)
            {
                double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;
                double progress = (double)i / samplesPerNote;

                if (progress < 0.1)
                    amplitude *= progress / 0.1;
                else if (progress > 0.7)
                    amplitude *= (1.0 - progress) / 0.3;

                samples[noteStart + i] = (short)(amplitude * short.MaxValue * 0.5);
            }
        }
        return samples;
    }

    private static short[] GenerateNES()
    {
        int durationMs = 150;
        double pulseFreq = 440.0, triFreq = 220.0;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 8;

        for (int i = 0; i < numSamples; i++)
        {
            double triPhase = (i * triFreq / SampleRate) % 1.0;
            double triValue = triPhase < 0.5 ? 4 * triPhase - 1 : 3 - 4 * triPhase;
            triValue = (int)(triValue * 7.5) / 7.5;

            double pulsePhase = (i * pulseFreq / SampleRate) % 1.0;
            double pulse = pulsePhase < 0.25 ? 1.0 : -1.0;

            double amplitude = triValue * 0.6 + pulse * 0.4;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.55);
        }
        return samples;
    }

    private static short[] GenerateMSX()
    {
        int note1Ms = 60, note2Ms = 80;
        int freq1 = 1047, freq2 = 784;
        int note1Samples = SampleRate * note1Ms / 1000;
        int totalSamples = note1Samples + SampleRate * note2Ms / 1000;
        var samples = new short[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            int freq = i < note1Samples ? freq1 : freq2;
            int noteStart = i < note1Samples ? 0 : note1Samples;
            int noteLen = i < note1Samples ? note1Samples : totalSamples - note1Samples;
            int notePos = i - noteStart;
            int period = SampleRate / freq;

            double amplitude = (notePos % period) < (period / 2) ? 1.0 : -1.0;
            double noteProgress = (double)notePos / noteLen;

            if (noteProgress < 0.05)
                amplitude *= noteProgress / 0.05;
            else if (noteProgress > 0.7)
                amplitude *= (1.0 - noteProgress) / 0.3;

            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GenerateMacClassic()
    {
        int durationMs = 150;
        int frequency = 600;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int period = SampleRate / frequency;
        int fadeLength = numSamples / 15;

        for (int i = 0; i < numSamples; i++)
        {
            double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength * 2)
                amplitude *= (double)(numSamples - i) / (fadeLength * 2);

            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GenerateAmstradCPC()
    {
        int noteMs = 50;
        int[] notes = [880, 1109, 1319];
        int samplesPerNote = SampleRate * noteMs / 1000;
        int totalSamples = samplesPerNote * notes.Length;
        var samples = new short[totalSamples];

        for (int noteIdx = 0; noteIdx < notes.Length; noteIdx++)
        {
            int freq = notes[noteIdx];
            int period = SampleRate / freq;
            int noteStart = noteIdx * samplesPerNote;

            for (int i = 0; i < samplesPerNote; i++)
            {
                double amplitude = (i % period) < (period / 2) ? 1.0 : -1.0;
                double noteProgress = (double)i / samplesPerNote;

                if (noteProgress < 0.1)
                    amplitude *= noteProgress / 0.1;
                else if (noteProgress > 0.8)
                    amplitude *= (1.0 - noteProgress) / 0.2;

                samples[noteStart + i] = (short)(amplitude * short.MaxValue * 0.5);
            }
        }
        return samples;
    }

    private static short[] GenerateTandy1000()
    {
        int durationMs = 140;
        int freq1 = 523, freq2 = 659, freq3 = 784;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 8;
        int period1 = SampleRate / freq1;
        int period2 = SampleRate / freq2;
        int period3 = SampleRate / freq3;

        for (int i = 0; i < numSamples; i++)
        {
            double wave1 = (i % period1) < (period1 / 2) ? 1.0 : -1.0;
            double wave2 = (i % period2) < (period2 / 2) ? 1.0 : -1.0;
            double wave3 = (i % period3) < (period3 / 2) ? 1.0 : -1.0;
            double amplitude = (wave1 + wave2 * 0.8 + wave3 * 0.6) / 2.4;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength * 2)
                amplitude *= (double)(numSamples - i) / (fadeLength * 2);

            samples[i] = (short)(amplitude * short.MaxValue * 0.45);
        }
        return samples;
    }

    private static short[] GeneratePCjr()
    {
        int durationMs = 150;
        int freq1 = 440, freq2 = 880;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 10;
        int period1 = SampleRate / freq1;
        int period2 = SampleRate / freq2;

        for (int i = 0; i < numSamples; i++)
        {
            double wave1 = (i % period1) < (period1 / 2) ? 1.0 : -1.0;
            double wave2 = (i % period2) < (period2 / 2) ? 1.0 : -1.0;
            double amplitude = wave1 * 0.7 + wave2 * 0.3;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.5);
        }
        return samples;
    }

    private static short[] GenerateAmiga()
    {
        int durationMs = 100;
        int frequency = 880;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 10;

        for (int i = 0; i < numSamples; i++)
        {
            double angle = 2.0 * Math.PI * i * frequency / SampleRate;
            double amplitude = Math.Sin(angle);
            amplitude = (int)(amplitude * 127) / 127.0;

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.7);
        }
        return samples;
    }

    private static short[] GenerateArchimedes()
    {
        int durationMs = 180;
        double baseFreq = 523.0;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int attackEnd = numSamples / 10;
        int sustainEnd = numSamples * 2 / 3;

        for (int i = 0; i < numSamples; i++)
        {
            double phase = ((double)i * baseFreq / SampleRate) % 1.0;
            double amplitude = Math.Sin(2.0 * Math.PI * phase) * 0.5 +
                               Math.Sin(4.0 * Math.PI * phase) * 0.25 +
                               Math.Sin(6.0 * Math.PI * phase) * 0.15 +
                               Math.Sin(8.0 * Math.PI * phase) * 0.1;

            double pitchEnv = 1.0 + 0.02 * Math.Exp(-(double)i * 10.0 / numSamples);
            double phase2 = ((double)i * baseFreq * pitchEnv / SampleRate) % 1.0;
            amplitude = amplitude * 0.7 + Math.Sin(2.0 * Math.PI * phase2) * 0.3;

            if (i < attackEnd)
                amplitude *= (double)i / attackEnd;
            else if (i > sustainEnd)
            {
                double release = (double)(i - sustainEnd) / (numSamples - sustainEnd);
                amplitude *= 1.0 - release * release;
            }

            amplitude = (int)(amplitude * 127) / 127.0;
            samples[i] = (short)(amplitude * short.MaxValue * 0.6);
        }
        return samples;
    }

    private static short[] GenerateNeXT()
    {
        int durationMs = 250;
        double carrierFreq = 587.0;
        double modulatorFreq = carrierFreq * 2;
        double modulationIndex = 0.3;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int attackLength = numSamples / 20;
        int sustainEnd = numSamples / 2;

        for (int i = 0; i < numSamples; i++)
        {
            double modulator = Math.Sin(2.0 * Math.PI * i * modulatorFreq / SampleRate);
            double carrier = Math.Sin(2.0 * Math.PI * i * carrierFreq / SampleRate + modulationIndex * modulator);
            double sub = Math.Sin(2.0 * Math.PI * i * (carrierFreq / 2) / SampleRate);
            double amplitude = carrier * 0.8 + sub * 0.2;

            if (i < attackLength)
            {
                double attackCurve = (double)i / attackLength;
                amplitude *= attackCurve * attackCurve;
            }
            else if (i > sustainEnd)
            {
                double releaseProgress = (double)(i - sustainEnd) / (numSamples - sustainEnd);
                amplitude *= (1.0 - releaseProgress) * (1.0 - releaseProgress);
            }

            samples[i] = (short)(amplitude * short.MaxValue * 0.6);
        }
        return samples;
    }

    private static short[] GenerateGameBoy()
    {
        int durationMs = 120;
        double frequency = 880.0;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        double period = SampleRate / frequency;

        for (int i = 0; i < numSamples; i++)
        {
            double progress = (double)i / numSamples;
            double dutyCycle = 0.125 + progress * 0.375;
            double posInPeriod = (i % (int)period) / period;
            double amplitude = posInPeriod < dutyCycle ? 1.0 : -1.0;

            amplitude *= Math.Max(0.0, 1.0 - progress * 1.5);
            amplitude = (int)(amplitude * 7.5) / 7.5;

            samples[i] = (short)(amplitude * short.MaxValue * 0.6);
        }
        return samples;
    }

    private static short[] GenerateSun()
    {
        int durationMs = 100;
        int frequency = 440;
        int numSamples = SampleRate * durationMs / 1000;
        var samples = new short[numSamples];
        int fadeLength = numSamples / 15;

        for (int i = 0; i < numSamples; i++)
        {
            double angle = 2.0 * Math.PI * i * frequency / SampleRate;
            double amplitude = Math.Sin(angle);

            if (i < fadeLength)
                amplitude *= (double)i / fadeLength;
            else if (i > numSamples - fadeLength)
                amplitude *= (double)(numSamples - i) / fadeLength;

            samples[i] = (short)(amplitude * short.MaxValue * 0.7);
        }
        return samples;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
