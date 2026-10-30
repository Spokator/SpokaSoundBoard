using NAudio.Wave;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Reads from a <see cref="FloatRingBuffer"/> fed by another clock (capture device or another output).
/// Two devices never run at exactly the same rate: a 0.1 % difference empties a 30 ms cushion in about
/// 30 seconds, which caused short periodic drop-outs. This provider therefore resamples very slightly
/// (at most ±0.5 %, inaudible) to keep its fill level around the target, and only falls back to
/// re-priming / skipping on real hiccups.
/// </summary>
public sealed class JitterBufferProvider : ISampleProvider
{
    private const double MaxCorrection = 0.005;
    private const double Gain = 0.04;
    private const double LevelSmoothing = 0.03;

    private readonly FloatRingBuffer _ring;
    private readonly int _channels;
    private readonly int _targetFrames;
    private readonly int _maxFrames;
    private bool _priming = true;

    // Input frames waiting to be interpolated; frame 0 is the last frame of the previous block.
    private float[] _input = new float[4096];
    private int _inputFrames;
    private double _position;
    private double _averageLevel = -1;

    public JitterBufferProvider(FloatRingBuffer ring, int sampleRate, int channels, float targetMs = 25f, float maxMs = 90f)
    {
        _ring = ring;
        _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _targetFrames = Math.Max(1, (int)(sampleRate * targetMs / 1000f));
        _maxFrames = Math.Max(_targetFrames * 2, (int)(sampleRate * maxMs / 1000f));
    }

    public WaveFormat WaveFormat { get; }

    public int Underruns { get; private set; }
    public int Overruns { get; private set; }

    /// <summary>Current playback-rate correction (1 = none), exposed for diagnostics and tests.</summary>
    public double Ratio { get; private set; } = 1.0;

    public int Read(float[] buffer, int offset, int count)
    {
        var dest = buffer.AsSpan(offset, count);
        var outFrames = count / _channels;
        var available = _ring.Count / _channels;

        if (_priming)
        {
            if (available < _targetFrames)
            {
                dest.Clear();
                return count;
            }
            _priming = false;
            _averageLevel = available;
            _inputFrames = 0;
            _position = 0;
        }

        // Real hiccup (e.g. the other device stalled then delivered a burst): drop the excess at once.
        if (available > _maxFrames + outFrames)
        {
            var skip = available - _targetFrames - outFrames;
            _ring.Skip(skip * _channels);
            available -= skip;
            _averageLevel = available;
            Overruns++;
        }

        // Proportional control of the playback rate from the smoothed fill level.
        _averageLevel = _averageLevel < 0 ? available : _averageLevel + (available - _averageLevel) * LevelSmoothing;
        var error = (_averageLevel - _targetFrames) / _targetFrames;
        Ratio = 1.0 + Math.Clamp(error * Gain, -MaxCorrection, MaxCorrection);

        // Frames needed so that every output frame has two neighbours to interpolate between.
        var needed = (int)Math.Floor(_position + outFrames * Ratio) + 2;
        EnsureInputCapacity(needed);
        if (_inputFrames < needed)
        {
            var want = needed - _inputFrames;
            var read = _ring.Read(_input.AsSpan(_inputFrames * _channels, want * _channels)) / _channels;
            _inputFrames += read;
            if (_inputFrames < needed)
            {
                // True underrun: output what we have, then wait for the cushion to refill.
                dest.Clear();
                Underruns++;
                _priming = true;
                _inputFrames = 0;
                _position = 0;
                return count;
            }
        }

        for (var f = 0; f < outFrames; f++)
        {
            var index = (int)_position;
            var t = (float)(_position - index);
            var a = index * _channels;
            var b = a + _channels;
            for (var c = 0; c < _channels; c++)
            {
                var s0 = _input[a + c];
                dest[f * _channels + c] = s0 + (_input[b + c] - s0) * t;
            }
            _position += Ratio;
        }

        // Keep the last consumed frame as the left neighbour of the next block.
        var consumed = (int)_position;
        var keep = _inputFrames - consumed;
        if (keep > 0)
            _input.AsSpan(consumed * _channels, keep * _channels).CopyTo(_input);
        _inputFrames = Math.Max(0, keep);
        _position -= consumed;

        dest[(outFrames * _channels)..].Clear();
        return count;
    }

    private void EnsureInputCapacity(int frames)
    {
        if (_input.Length >= frames * _channels) return;
        var bigger = new float[frames * _channels * 2];
        _input.AsSpan(0, _inputFrames * _channels).CopyTo(bigger);
        _input = bigger;
    }
}
