using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Dsp;

/// <summary>
/// Complete mono voice chain: gain → high-pass → voice detection / gate → pitch → radio → robot
/// → echo → reverb → compressor → volume, plus radio squelch and roger-beep generation.
/// </summary>
public sealed class VoiceProcessor
{
    private const float VadHoldSeconds = 0.35f;
    private const float VadHysteresisDb = 5f;

    private readonly int _sampleRate;
    private readonly VoiceEffectSettings _settings;

    private readonly Biquad _rumbleFilter = new();
    private readonly Biquad[] _radioFilters = [new(), new(), new(), new()];
    private readonly Biquad _radioPresence = new();
    private readonly Biquad[] _noiseFilters = [new(), new()];
    private readonly PitchShifter _pitch;
    private readonly EchoEffect _echo;
    private readonly ReverbEffect _reverb;
    private readonly Compressor _compressor;
    private NoiseGenerator _noise = new(0xC0FFEE);

    private float _vadEnvelope;
    private readonly float _vadAttack, _vadRelease;
    private int _vadHoldRemaining;
    private bool _vadActive;

    private float _gateGain;
    private readonly float _gateAttack, _gateRelease;

    private bool _wasTransmitting;
    private float _transmitGain;
    private readonly float _transmitSmoothing;

    private int _squelchRemaining, _squelchLength;
    private int _beepRemaining, _beepLength;
    private float _beepPhase;

    private double _robotPhase;

    private float _lastLowCut = -1, _lastHighCut = -1;
    private float _lastPitch = float.NaN;

    public VoiceProcessor(int sampleRate, VoiceEffectSettings settings)
    {
        _sampleRate = sampleRate;
        _settings = settings;
        _pitch = new PitchShifter(sampleRate);
        _echo = new EchoEffect(sampleRate);
        _reverb = new ReverbEffect(sampleRate);
        _compressor = new Compressor(sampleRate);
        _rumbleFilter.SetHighPass(sampleRate, 80f);
        _vadAttack = DspUtil.TimeCoefficient(4f, sampleRate);
        _vadRelease = DspUtil.TimeCoefficient(120f, sampleRate);
        _gateAttack = DspUtil.TimeCoefficient(3f, sampleRate);
        _gateRelease = DspUtil.TimeCoefficient(120f, sampleRate);
        _transmitSmoothing = DspUtil.TimeCoefficient(8f, sampleRate);
    }

    /// <summary>Microphone level (dBFS, after input gain) for the meters.</summary>
    public float InputLevelDb { get; private set; } = -120f;

    /// <summary>True while the voice is detected above the threshold.</summary>
    public bool VoiceActive => _vadActive;

    /// <summary>True while the voice is actually sent (detected and, in radio mode, key held).</summary>
    public bool Transmitting { get; private set; }

    /// <param name="input">Mono microphone samples.</param>
    /// <param name="output">Processed mono voice.</param>
    /// <param name="micEnabled">When false the voice is muted (no ducking, no squelch).</param>
    /// <param name="pushToTalk">Radio mode enabled.</param>
    /// <param name="pushToTalkHeld">Radio key currently held.</param>
    public void Process(ReadOnlySpan<float> input, Span<float> output, bool micEnabled, bool pushToTalk, bool pushToTalkHeld)
    {
        var s = _settings;
        UpdateParameters(s);

        var inputGain = DspUtil.DbToLinear(s.InputGainDb);
        var volume = DspUtil.DbToLinear(s.VoiceVolumeDb);
        var openThreshold = DspUtil.DbToLinear(s.VoiceThresholdDb);
        var closeThreshold = DspUtil.DbToLinear(s.VoiceThresholdDb - VadHysteresisDb);
        var holdSamples = (int)(VadHoldSeconds * _sampleRate);

        var fx = s.EffectsEnabled;
        var radio = fx && s.RadioEnabled;
        var robot = fx && s.RobotEnabled;
        var echo = fx && s.EchoEnabled;
        var reverb = fx && s.ReverbEnabled;
        var compress = fx && s.CompressorEnabled;
        var pitch = fx && MathF.Abs(s.PitchSemitones) > 0.05f;
        var squelch = radio && s.SquelchEnabled;
        var drive = 1f + s.RadioDrive * 14f;
        var driveNorm = 1f / MathF.Tanh(drive);
        var noiseLevel = s.RadioNoise * 0.08f;
        var robotIncrement = 2 * Math.PI * s.RobotFrequency / _sampleRate;

        var peakIn = 0f;
        for (var i = 0; i < input.Length; i++)
        {
            var x = _rumbleFilter.Process(input[i] * inputGain);
            var ax = MathF.Abs(x);
            if (ax > peakIn) peakIn = ax;

            // Voice activity detection with hysteresis and hold.
            var c = ax > _vadEnvelope ? _vadAttack : _vadRelease;
            _vadEnvelope = c * _vadEnvelope + (1 - c) * ax;
            if (_vadEnvelope >= openThreshold)
            {
                _vadActive = true;
                _vadHoldRemaining = holdSamples;
            }
            else if (_vadEnvelope < closeThreshold && _vadActive && --_vadHoldRemaining <= 0)
            {
                _vadActive = false;
            }

            // A transmission is a "radio keying": key held in PTT mode, voice detected otherwise.
            var transmitting = micEnabled && (pushToTalk ? pushToTalkHeld : _vadActive || !s.GateEnabled && !squelch);
            if (transmitting != _wasTransmitting)
            {
                if (squelch) StartSquelch(transmitting ? 0.06f : 0.16f);
                if (!transmitting && radio && s.RogerBeepEnabled) StartBeep();
                _wasTransmitting = transmitting;
            }

            // Gate: in PTT mode the key opens the gate; otherwise the optional noise gate does.
            var gateTarget = micEnabled
                ? (pushToTalk ? (pushToTalkHeld ? 1f : 0f) : (!s.GateEnabled || _vadActive ? 1f : 0f))
                : 0f;
            var gc = gateTarget > _gateGain ? _gateAttack : _gateRelease;
            _gateGain = gc * _gateGain + (1 - gc) * gateTarget;
            x *= _gateGain;

            if (pitch) x = _pitch.Process(x);

            if (radio)
            {
                foreach (var f in _radioFilters) x = f.Process(x);
                x = _radioPresence.Process(x);
                x = MathF.Tanh(x * drive) * driveNorm * 0.6f;
            }

            if (robot)
            {
                var carrier = (float)Math.Sin(_robotPhase);
                _robotPhase += robotIncrement;
                if (_robotPhase > 2 * Math.PI) _robotPhase -= 2 * Math.PI;
                x = x * (1 - s.RobotMix) + x * carrier * s.RobotMix * 1.6f;
            }

            if (echo) x = _echo.Process(x);
            if (reverb) x = _reverb.Process(x);
            if (compress) x = _compressor.Process(x);

            // Radio background hiss, only while the "radio" is keyed.
            var t = transmitting ? 1f : 0f;
            _transmitGain = _transmitSmoothing * _transmitGain + (1 - _transmitSmoothing) * t;
            if (radio && noiseLevel > 0)
                x += FilteredNoise() * noiseLevel * _transmitGain;

            if (_squelchRemaining > 0)
            {
                var progress = 1f - _squelchRemaining / (float)_squelchLength;
                var env = MathF.Sin(MathF.PI * progress);
                x += FilteredNoise() * 0.35f * env;
                _squelchRemaining--;
            }

            if (_beepRemaining > 0)
            {
                var progress = 1f - _beepRemaining / (float)_beepLength;
                var env = MathF.Min(1f, MathF.Min(progress * 40f, (1 - progress) * 40f));
                // Two-tone "roger beep".
                var freq = progress < 0.5f ? 1450f : 1050f;
                _beepPhase += 2 * MathF.PI * freq / _sampleRate;
                if (_beepPhase > 2 * MathF.PI) _beepPhase -= 2 * MathF.PI;
                x += MathF.Sin(_beepPhase) * 0.18f * env;
                _beepRemaining--;
            }

            output[i] = x * volume;
        }

        Transmitting = _wasTransmitting && micEnabled;
        InputLevelDb = DspUtil.LinearToDb(peakIn);
    }

    private float FilteredNoise()
    {
        var n = _noise.Next();
        foreach (var f in _noiseFilters) n = f.Process(n);
        return n;
    }

    private void StartSquelch(float seconds)
    {
        _squelchLength = _squelchRemaining = (int)(seconds * _sampleRate);
    }

    private void StartBeep()
    {
        _beepLength = _beepRemaining = (int)(0.17f * _sampleRate);
        _beepPhase = 0;
    }

    private void UpdateParameters(VoiceEffectSettings s)
    {
        var low = Math.Clamp(s.RadioLowCutHz, 50f, 2000f);
        var high = Math.Clamp(s.RadioHighCutHz, low + 200f, 12000f);
        if (low != _lastLowCut || high != _lastHighCut)
        {
            _lastLowCut = low;
            _lastHighCut = high;
            _radioFilters[0].SetHighPass(_sampleRate, low, 0.75f);
            _radioFilters[1].SetHighPass(_sampleRate, low, 0.75f);
            _radioFilters[2].SetLowPass(_sampleRate, high, 0.75f);
            _radioFilters[3].SetLowPass(_sampleRate, high, 0.75f);
            var center = MathF.Sqrt(low * high);
            _radioPresence.SetPeaking(_sampleRate, center, 1.2f, 5f);
            _noiseFilters[0].SetHighPass(_sampleRate, low);
            _noiseFilters[1].SetLowPass(_sampleRate, high);
        }

        if (s.PitchSemitones != _lastPitch)
        {
            _lastPitch = s.PitchSemitones;
            _pitch.SetSemitones(s.PitchSemitones);
        }

        _echo.DelayMs = s.EchoDelayMs;
        _echo.Feedback = s.EchoFeedback;
        _echo.Mix = s.EchoMix;
        _reverb.RoomSize = s.ReverbSize;
        _reverb.Mix = s.ReverbMix;
        _compressor.SetAmount(s.CompressorAmount);
    }
}
