using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpokaSoundBoard.Core.Audio;

namespace SpokaSoundBoard.Core.Speech;

/// <summary>
/// Turns raw model output (mono, model sample rate) into what the mixer plays: <see cref="MixerCore.SampleRate"/>
/// stereo float, peak-normalized, with short fades so phrases never click. Runs off the audio thread.
/// </summary>
public static class SpeechAudio
{
    /// <summary>Peak level of a rendered phrase (-1 dBFS): as loud as the Windows voices, never clipping.</summary>
    public const float TargetPeak = 0.89f;

    /// <summary>Quiet output is raised at most this much (a near-silent result stays near-silent).</summary>
    public const float MaxGain = 4f;

    /// <summary>Fade-in / fade-out length, in milliseconds.</summary>
    public const double FadeMilliseconds = 6;

    /// <summary>Full pipeline: resample, peak-normalize, fade, duplicate to stereo (interleaved).</summary>
    public static float[] ToMixerFormat(ReadOnlySpan<float> mono, int sourceRate, int targetRate = MixerCore.SampleRate)
    {
        var resampled = Resample(mono, sourceRate, targetRate);
        Normalize(resampled);
        Fade(resampled, (int)(targetRate * FadeMilliseconds / 1000));
        return ToStereo(resampled);
    }

    /// <summary>Mono resampling with NAudio's WDL resampler (the one the sound player uses for files).</summary>
    public static float[] Resample(ReadOnlySpan<float> mono, int sourceRate, int targetRate)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate));
        if (mono.IsEmpty) return [];
        if (sourceRate == targetRate) return mono.ToArray();

        var source = new ArraySampleProvider(mono.ToArray(), sourceRate);
        var resampler = new WdlResamplingSampleProvider(source, targetRate);
        var expected = (int)Math.Ceiling(mono.Length * (double)targetRate / sourceRate);
        var output = new float[expected + 1024];
        var total = 0;
        while (true)
        {
            if (total == output.Length) Array.Resize(ref output, output.Length * 2);
            var n = resampler.Read(output, total, output.Length - total);
            if (n <= 0) break;
            total += n;
        }
        // The resampler keeps a few samples of latency: never return more than the exact length.
        Array.Resize(ref output, Math.Min(total, expected));
        return output;
    }

    /// <summary>Scales <paramref name="samples"/> so the peak reaches <see cref="TargetPeak"/> (gain capped at <see cref="MaxGain"/>).</summary>
    public static void Normalize(Span<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            if (!float.IsFinite(s)) continue;
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        if (peak <= 1e-6f) return;
        var gain = Math.Min(TargetPeak / peak, MaxGain);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = float.IsFinite(samples[i]) ? samples[i] * gain : 0f;
    }

    /// <summary>Linear fade-in and fade-out over <paramref name="frames"/> samples (clamped to half the length).</summary>
    public static void Fade(Span<float> samples, int frames)
    {
        frames = Math.Min(frames, samples.Length / 2);
        for (var i = 0; i < frames; i++)
        {
            var g = (float)i / frames;
            samples[i] *= g;
            samples[samples.Length - 1 - i] *= g;
        }
    }

    /// <summary>Mono to interleaved stereo.</summary>
    public static float[] ToStereo(ReadOnlySpan<float> mono)
    {
        var stereo = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            stereo[i * 2] = mono[i];
            stereo[i * 2 + 1] = mono[i];
        }
        return stereo;
    }

    /// <summary>Writes interleaved float samples as an IEEE float WAV file.</summary>
    public static void WriteWav(string path, float[] interleaved, int sampleRate, int channels)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
        writer.WriteSamples(interleaved, 0, interleaved.Length);
    }

    private sealed class ArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, samples.Length - _position);
            if (n <= 0) return 0;
            Array.Copy(samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
