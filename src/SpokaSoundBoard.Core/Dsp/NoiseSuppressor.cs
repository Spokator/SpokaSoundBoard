using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpokaSoundBoard.Core.Dsp;

/// <summary>
/// Removes keyboard clicks, fans, hum and room noise from the microphone with RNNoise (Xiph, BSD-3-Clause), a small
/// neural network made for voice. It works on 10 ms frames of 480 samples at 48 kHz (the mixer rate). While it is on,
/// the voice comes out <see cref="LatencySamples"/> later (30 ms: one frame of buffering here, two inside RNNoise's
/// overlapping windows); off, the samples pass through untouched. The original voice used for a partial
/// <c>amount</c> is delayed by the same amount, so blending never sounds metallic.
/// </summary>
/// <remarks>
/// Audio thread rules: no allocation (the native call pins the preallocated arrays), no blocking, never throws: if the
/// native library fails, the voice passes through unchanged. When <see cref="IsAvailable"/> is false (DLL missing),
/// the processor does nothing.
/// </remarks>
public sealed class NoiseSuppressor : IDisposable
{
    public const int FrameSize = 480;

    /// <summary>RNNoise returns frame N's denoised audio while given frame N + 2 (its analysis windows overlap).</summary>
    private const int InternalDelayFrames = 2;

    /// <summary>Delay of the output while the processor runs: 1 440 samples, 30 ms at 48 kHz.</summary>
    public const int LatencySamples = (1 + InternalDelayFrames) * FrameSize;

    // RNNoise works on 16-bit sample values stored as floats.
    private const float Scale = 32768f;

    private readonly StateHandle? _state;
    private readonly float[] _in = new float[FrameSize];
    private readonly float[] _out = new float[FrameSize];
    private readonly float[] _dryIn = new float[FrameSize];
    private readonly float[] _denoised = new float[FrameSize];
    private readonly float[] _dryOut = new float[FrameSize];
    // Original frames waiting for their denoised version (RNNoise's own delay).
    private readonly float[][] _dryHistory = [new float[FrameSize], new float[FrameSize]];
    private readonly float _rampStep;
    private int _fill;
    private float _blend;
    private bool _running;
    private bool _failed;

    public NoiseSuppressor(int sampleRate = 48000)
    {
        _rampStep = 1f / (sampleRate * 0.02f); // 20 ms fades when switching on, off or changing the amount
        if (sampleRate != 48000) return; // RNNoise is made for 48 kHz only
        try
        {
            var handle = Native.rnnoise_create(IntPtr.Zero);
            if (!handle.IsInvalid) _state = handle;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _state = null;
        }
    }

    /// <summary>False when rnnoise.dll could not be loaded: the option is then hidden.</summary>
    public bool IsAvailable => _state != null && !_failed;

    /// <summary>Probability that the last frame contained speech (0..1), as estimated by RNNoise.</summary>
    public float VoiceProbability { get; private set; }

    /// <summary>True while the processor is in the signal path (adds one frame of delay).</summary>
    public bool IsRunning => _running;

    /// <summary>Denoises mono samples in place. Audio thread.</summary>
    /// <param name="amount">0..1: share of the denoised voice in the result.</param>
    public void Process(Span<float> samples, bool enabled, float amount)
    {
        if (_state == null || _failed) return;
        var target = enabled ? (float.IsFinite(amount) ? Math.Clamp(amount, 0f, 1f) : 1f) : 0f;
        if (!_running)
        {
            if (target <= 0f) return; // bypass: no processing, no delay
            _running = true;
            _blend = 0f;
            _fill = 0;
            Array.Clear(_denoised);
            Array.Clear(_dryOut);
            foreach (var h in _dryHistory) Array.Clear(h);
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            _dryIn[_fill] = x;
            _in[_fill] = float.IsFinite(x) ? Math.Clamp(x, -1f, 1f) * Scale : 0f;

            if (_blend != target)
                _blend = _blend < target ? MathF.Min(target, _blend + _rampStep) : MathF.Max(target, _blend - _rampStep);
            samples[i] = _dryOut[_fill] + (_denoised[_fill] - _dryOut[_fill]) * _blend;

            if (++_fill == FrameSize)
            {
                _fill = 0;
                try
                {
                    VoiceProbability = Native.rnnoise_process_frame(_state, _out, _in);
                }
                catch
                {
                    // The native code failed: let the voice through untouched from now on.
                    _failed = true;
                    _running = false;
                    return;
                }
                // The denoised frame matches the original from two frames ago: play them side by side.
                var oldest = _dryHistory[0];
                _dryHistory[0] = _dryHistory[1];
                _dryHistory[1] = oldest;
                for (var k = 0; k < FrameSize; k++)
                {
                    var y = _out[k] / Scale;
                    _denoised[k] = float.IsFinite(y) ? y : 0f;
                    _dryOut[k] = oldest[k];
                    oldest[k] = _dryIn[k];
                }
            }
        }

        // Fully faded out: leave the signal path (and its delay) until switched on again.
        if (target <= 0f && _blend <= 0f && _fill == 0) _running = false;
    }

    public void Dispose() => _state?.Dispose();

    private sealed class StateHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle()
        {
            Native.rnnoise_destroy(handle);
            return true;
        }
    }

    private static class Native
    {
        private const string Library = "rnnoise";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern StateHandle rnnoise_create(IntPtr model);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rnnoise_destroy(IntPtr state);

        /// <summary>Blittable arrays are pinned for the call, not copied: no allocation.</summary>
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern float rnnoise_process_frame(StateHandle state, [Out] float[] output, [In] float[] input);
    }
}
