namespace SpokaSoundBoard.Core.Dsp;

/// <summary>Mono Freeverb-style reverb (8 damped combs + 4 all-passes).</summary>
public sealed class ReverbEffect
{
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllPassTunings = [556, 441, 341, 225];

    private readonly Comb[] _combs;
    private readonly AllPass[] _allPasses;

    public ReverbEffect(int sampleRate)
    {
        var scale = sampleRate / 44100f;
        _combs = CombTunings.Select(t => new Comb((int)(t * scale))).ToArray();
        _allPasses = AllPassTunings.Select(t => new AllPass((int)(t * scale))).ToArray();
    }

    /// <summary>0..1, larger = longer tail.</summary>
    public float RoomSize { get; set; } = 0.6f;

    public float Damping { get; set; } = 0.4f;

    public float Mix { get; set; } = 0.25f;

    public void Reset()
    {
        foreach (var c in _combs) c.Reset();
        foreach (var a in _allPasses) a.Reset();
    }

    public float Process(float input)
    {
        var feedback = 0.7f + Math.Clamp(RoomSize, 0f, 1f) * 0.28f;
        var damp = Math.Clamp(Damping, 0f, 1f) * 0.4f;
        var x = input * 0.015f;

        var wet = 0f;
        foreach (var comb in _combs) wet += comb.Process(x, feedback, damp);
        foreach (var ap in _allPasses) wet = ap.Process(wet);

        return input * (1f - Mix * 0.5f) + wet * Mix * 3f;
    }

    private sealed class Comb(int size)
    {
        private readonly float[] _buffer = new float[Math.Max(1, size)];
        private int _index;
        private float _store;

        public void Reset() { Array.Clear(_buffer); _store = 0; }

        public float Process(float input, float feedback, float damp)
        {
            var output = _buffer[_index];
            _store = output * (1 - damp) + _store * damp;
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
            _buffer[_index] = input + buffered * 0.5f;
            if (++_index >= _buffer.Length) _index = 0;
            return output;
        }
    }
}
