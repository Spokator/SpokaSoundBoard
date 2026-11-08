namespace SpokaSoundBoard.Core.Dsp;

/// <summary>
/// Stereo concert-hall reverb for the music bus: Freeverb topology (8 damped combs + 4 all-passes per side, the
/// right side detuned for a wide decorrelated tail) with a pre-delay and a high-passed input so the bass stays
/// tight. Separate from <see cref="ReverbEffect"/> (voice) on purpose. Allocation-free after construction.
/// </summary>
public sealed class StereoReverb
{
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllPassTunings = [556, 441, 341, 225];
    private const int StereoSpread = 23;
    private const float InputGain = 0.015f;
    private const float WetScale = 3f;

    private readonly Comb[] _combsL, _combsR;
    private readonly AllPass[] _allPassL, _allPassR;
    private readonly float[] _preDelay;
    private readonly Biquad _inputHighPass = new();
    private int _preIndex;

    public StereoReverb(int sampleRate, float preDelayMs = 22f)
    {
        var scale = sampleRate / 44100f;
        _combsL = CombTunings.Select(t => new Comb((int)(t * scale))).ToArray();
        _combsR = CombTunings.Select(t => new Comb((int)((t + StereoSpread) * scale))).ToArray();
        _allPassL = AllPassTunings.Select(t => new AllPass((int)(t * scale))).ToArray();
        _allPassR = AllPassTunings.Select(t => new AllPass((int)((t + StereoSpread) * scale))).ToArray();
        _preDelay = new float[Math.Max(1, (int)(sampleRate * preDelayMs / 1000f))];
        _inputHighPass.SetHighPass(sampleRate, 140f);
    }

    /// <summary>0..1, larger = longer tail (a hall is around 0.85).</summary>
    public float RoomSize { get; set; } = 0.86f;

    /// <summary>0..1, larger = darker tail.</summary>
    public float Damping { get; set; } = 0.35f;

    /// <summary>0 = mono tail, 1 = fully decorrelated.</summary>
    public float Width { get; set; } = 1f;

    public void Reset()
    {
        foreach (var c in _combsL) c.Reset();
        foreach (var c in _combsR) c.Reset();
        foreach (var a in _allPassL) a.Reset();
        foreach (var a in _allPassR) a.Reset();
        Array.Clear(_preDelay);
        _inputHighPass.Reset();
        _preIndex = 0;
    }

    /// <summary>Feeds one stereo frame and returns the wet (reverberated) signal only.</summary>
    public void Process(float left, float right, out float wetLeft, out float wetRight)
    {
        var feedback = 0.7f + Math.Clamp(RoomSize, 0f, 1f) * 0.28f;
        var damp = Math.Clamp(Damping, 0f, 1f) * 0.4f;

        var input = _inputHighPass.Process((left + right) * InputGain);
        var delayed = _preDelay[_preIndex];
        _preDelay[_preIndex] = input;
        if (++_preIndex >= _preDelay.Length) _preIndex = 0;

        float outL = 0, outR = 0;
        for (var i = 0; i < _combsL.Length; i++)
        {
            outL += _combsL[i].Process(delayed, feedback, damp);
            outR += _combsR[i].Process(delayed, feedback, damp);
        }
        for (var i = 0; i < _allPassL.Length; i++)
        {
            outL = _allPassL[i].Process(outL);
            outR = _allPassR[i].Process(outR);
        }

        var width = Math.Clamp(Width, 0f, 1f);
        var wet1 = (width * 0.5f + 0.5f) * WetScale;
        var wet2 = ((1f - width) * 0.5f) * WetScale;
        wetLeft = outL * wet1 + outR * wet2;
        wetRight = outR * wet1 + outL * wet2;
        if (!float.IsFinite(wetLeft) || !float.IsFinite(wetRight))
        {
            Reset();
            wetLeft = wetRight = 0f;
        }
    }

    private sealed class Comb(int size)
    {
        private readonly float[] _buffer = new float[Math.Max(1, size)];
        private int _index;
        private float _store;

        public void Reset()
        {
            Array.Clear(_buffer);
            _store = 0;
        }

        public float Process(float input, float feedback, float damp)
        {
            var output = _buffer[_index];
            _store = output * (1 - damp) + _store * damp;
            if (MathF.Abs(_store) < 1e-20f) _store = 0f; // flush denormals
            _buffer[_index] = input + _store * feedback;
            if (++_index >= _buffer.Length) _index = 0;
            return output;
        }
    }

    private sealed class AllPass(int size)
    {
        private readonly float[] _buffer = new float[Math.Max(1, size)];
        private int _index;

        public void Reset() => Array.Clear(_buffer);

        public float Process(float input)
        {
            var buffered = _buffer[_index];
            var output = -input + buffered;
            var next = input + buffered * 0.5f;
            _buffer[_index] = MathF.Abs(next) < 1e-20f ? 0f : next;
            if (++_index >= _buffer.Length) _index = 0;
            return output;
        }
    }
}
