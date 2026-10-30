using NAudio.Wave;
using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>Real-time level information for the UI (written by the audio thread).</summary>
public sealed class EngineMeters
{
    public volatile float MicLevelDb = -120f;
    public volatile float VoicePeak;
    public volatile float MediaPeak;
    public volatile float OutputPeak;
    public volatile float MonitorPeak;
    public volatile float DuckGain = 1f;
    public volatile bool VoiceActive;
    public volatile bool Transmitting;
}

/// <summary>
/// The mixing graph. Every call to <see cref="Render"/> produces the virtual-microphone bus and the
/// headphone (monitor) bus for the same block of time.
/// </summary>
public sealed class MixerCore
{
    public const int SampleRate = 48000;

    private readonly MixSettings _mix;
    private readonly VoiceProcessor _voice;
    private readonly Ducker _ducker = new(SampleRate);
    private readonly Limiter _mainLimiter = new(SampleRate);
    private readonly Limiter _monitorLimiter = new(SampleRate);
    private ISampleProvider? _micSource;
    private float[] _mic = new float[2048];
    private float[] _voiceOut = new float[2048];
    private float[] _music = new float[4096];
    private float[] _pads = new float[4096];

    public MixerCore(VoiceEffectSettings voice, MixSettings mix, MusicPlayer music, SoundPadPlayer pads)
    {
        _mix = mix;
        Music = music;
        Pads = pads;
        _voice = new VoiceProcessor(SampleRate, voice);
    }

    public MusicPlayer Music { get; }
    public SoundPadPlayer Pads { get; }
    public EngineMeters Meters { get; } = new();

    /// <summary>Radio-mode key state, set by the hotkey service.</summary>
    public volatile bool PushToTalkHeld;

    public void SetMicSource(ISampleProvider? source) => Volatile.Write(ref _micSource, source);

    /// <param name="main">Stereo interleaved output for the virtual microphone.</param>
    /// <param name="monitor">Stereo interleaved output for the headphones.</param>
    public void Render(Span<float> main, Span<float> monitor, int frames)
    {
        EnsureCapacity(frames);
        var mix = _mix;

        var micSpan = _mic.AsSpan(0, frames);
        var mic = Volatile.Read(ref _micSource);
        if (mic != null)
        {
            var read = 0;
            while (read < frames)
            {
                var n = mic.Read(_mic, read, frames - read);
                if (n <= 0) break;
                read += n;
            }
            micSpan[read..].Clear();
        }
        else
        {
            micSpan.Clear();
        }

        var voiceSpan = _voiceOut.AsSpan(0, frames);
        _voice.Process(micSpan, voiceSpan, mix.MicEnabled, mix.PushToTalkEnabled, PushToTalkHeld);

        Music.Read(_music, 0, frames * 2);
        Pads.Read(_pads, 0, frames * 2);

        var duckActive = mix.DuckingEnabled && _voice.VoiceActive && _voice.Transmitting;
        var duckLevel = DspUtil.DbToLinear(mix.DuckingAmountDb);
        _ducker.Configure(mix.DuckingAttackMs, mix.DuckingReleaseMs);

        var musicVol = mix.MusicVolume;
        var padsVol = mix.PadsVolume;
        var toMic = mix.MediaToMicEnabled ? mix.MediaToMicVolume : 0f;
        var monitorVol = mix.MonitorVolume;
        var selfMonitor = mix.ListenToSelf ? monitorVol : 0f;

        float voicePeak = 0, mediaPeak = 0;
        for (var f = 0; f < frames; f++)
        {
            var duck = _ducker.Next(duckActive, duckLevel);
            var v = voiceSpan[f];
            var l = (_music[f * 2] * musicVol + _pads[f * 2] * padsVol) * duck;
            var r = (_music[f * 2 + 1] * musicVol + _pads[f * 2 + 1] * padsVol) * duck;

            voicePeak = MathF.Max(voicePeak, MathF.Abs(v));
            mediaPeak = MathF.Max(mediaPeak, MathF.Max(MathF.Abs(l), MathF.Abs(r)));

            if (!main.IsEmpty)
            {
                main[f * 2] = v + l * toMic;
                main[f * 2 + 1] = v + r * toMic;
            }
            if (!monitor.IsEmpty)
            {
                monitor[f * 2] = l * monitorVol + v * selfMonitor;
                monitor[f * 2 + 1] = r * monitorVol + v * selfMonitor;
            }
        }

        if (!main.IsEmpty)
        {
            _mainLimiter.ProcessStereo(main[..(frames * 2)]);
            Meters.OutputPeak = Peak(main[..(frames * 2)]);
        }
        if (!monitor.IsEmpty)
        {
            _monitorLimiter.ProcessStereo(monitor[..(frames * 2)]);
            Meters.MonitorPeak = Peak(monitor[..(frames * 2)]);
        }

        Meters.MicLevelDb = _voice.InputLevelDb;
        Meters.VoicePeak = voicePeak;
        Meters.MediaPeak = mediaPeak;
        Meters.DuckGain = _ducker.Gain;
        Meters.VoiceActive = _voice.VoiceActive;
        Meters.Transmitting = _voice.Transmitting;
    }

    private static float Peak(ReadOnlySpan<float> data)
    {
        var p = 0f;
        foreach (var s in data) p = MathF.Max(p, MathF.Abs(s));
        return p;
    }

    private void EnsureCapacity(int frames)
    {
        if (_mic.Length < frames)
        {
            _mic = new float[frames];
            _voiceOut = new float[frames];
        }
        if (_music.Length < frames * 2)
        {
            _music = new float[frames * 2];
            _pads = new float[frames * 2];
        }
    }
}
