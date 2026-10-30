using NAudio.Wave;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Reads from a <see cref="FloatRingBuffer"/> fed by another clock (capture device or another output)
/// while keeping latency bounded: it pre-buffers a small cushion and drops data when it drifts too far.
/// </summary>
public sealed class JitterBufferProvider : ISampleProvider
{
    private readonly FloatRingBuffer _ring;
    private readonly int _channels;
    private readonly int _targetSamples;
    private readonly int _maxSamples;
    private bool _priming = true;

    public JitterBufferProvider(FloatRingBuffer ring, int sampleRate, int channels, float targetMs = 25f, float maxMs = 90f)
    {
        _ring = ring;
        _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _targetSamples = AlignToFrame((int)(sampleRate * targetMs / 1000f) * channels);
        _maxSamples = AlignToFrame((int)(sampleRate * maxMs / 1000f) * channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Underruns { get; private set; }

    public int Read(float[] buffer, int offset, int count)
    {
        var dest = buffer.AsSpan(offset, count);
        var available = _ring.Count;

        if (_priming)
        {
            if (available < _targetSamples)
            {
                dest.Clear();
                return count;
            }
            _priming = false;
        }

        if (available > _maxSamples + count)
            _ring.Skip(AlignToFrame(available - _targetSamples - count));

        var read = _ring.Read(dest);
        if (read < count)
        {
            dest[read..].Clear();
            Underruns++;
            _priming = true;
        }
        return count;
    }

    private int AlignToFrame(int samples) => samples - samples % _channels;
}
