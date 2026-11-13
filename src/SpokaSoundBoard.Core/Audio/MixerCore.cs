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
    public volatile float DiscordPeak;
    public volatile float MonitorPeak;
    public volatile float DuckGain = 1f;
    public volatile bool VoiceActive;
    public volatile bool Transmitting;
}

/// <summary>
/// The mixing graph. Every call to <see cref="Render(Span{float}, Span{float}, Span{float}, int)"/> produces,
/// for the same block of time, the game bus (main virtual mic), the Discord bus (second virtual mic)
/// and the headphone (monitor) bus. Empty spans are skipped.
/// </summary>
public sealed class MixerCore
{
    public const int SampleRate = 48000;

    private readonly MixSettings _mix;
    private readonly VoiceProcessor _voice;
    private readonly NoiseSuppressor _denoiser = new(SampleRate);
    private readonly Ducker _ducker = new(SampleRate);
    private readonly Limiter _gameLimiter = new(SampleRate);
    private readonly Limiter _discordLimiter = new(SampleRate);
    private readonly Limiter _monitorLimiter = new(SampleRate);
    private ISampleProvider? _micSource;
    private float[] _mic = new float[2048];
    private float[] _voiceWet = new float[2048];
    private float[] _voiceDry = new float[2048];
    private float[] _music = new float[4096];
    private float[] _pads = new float[4096];
    private float[] _preview = new float[4096];
    private float[] _privatePads = new float[4096];

    public MixerCore(VoiceEffectSettings voice, MixSettings mix, MusicPlayer music, SoundPadPlayer pads, MusicPlayer? preview = null,
                     SoundPadPlayer? privatePads = null)
    {
        _mix = mix;
        Music = music;
        Pads = pads;
        Preview = preview;
        PrivatePads = privatePads;
        _voice = new VoiceProcessor(SampleRate, voice);
    }

    public MusicPlayer Music { get; }
    public SoundPadPlayer Pads { get; }

    /// <summary>Headphones-only player used to listen to search results before adding them.</summary>
    public MusicPlayer? Preview { get; }

    /// <summary>Soundboard sounds for the headphones only (« Moi seulement », « Tester » in the pad editor).</summary>
    public SoundPadPlayer? PrivatePads { get; }

    /// <summary>Receives the raw microphone while a sound is being recorded (null otherwise).</summary>
    public VoiceRecorder? Recorder
    {
        get => Volatile.Read(ref _recorder);
        set => Volatile.Write(ref _recorder, value);
    }

    private VoiceRecorder? _recorder;
    public EngineMeters Meters { get; } = new();

    /// <summary>False when rnnoise.dll could not be loaded (the option is then shown as unavailable).</summary>
    public bool NoiseSuppressionAvailable => _denoiser.IsAvailable;

    /// <summary>Speech probability of the last 10 ms (0..1) while noise suppression is on.</summary>
    public float NoiseSuppressionVoiceProbability => _denoiser.IsRunning ? _denoiser.VoiceProbability : 0f;

    /// <summary>Radio-mode key state, set by the hotkey service.</summary>
    public volatile bool PushToTalkHeld;

    /// <summary>Radio key held on the phone remote (released by the app if the phone goes silent).</summary>
    public volatile bool RemotePushToTalkHeld;

    public void SetMicSource(ISampleProvider? source) => Volatile.Write(ref _micSource, source);

    public void Render(Span<float> game, Span<float> monitor, int frames) =>
        Render(game, Span<float>.Empty, monitor, frames);

    /// <param name="game">Stereo interleaved output for the game's virtual microphone.</param>
    /// <param name="discord">Stereo interleaved output for Discord's virtual microphone.</param>
    /// <param name="monitor">Stereo interleaved output for the headphones.</param>
    public void Render(Span<float> game, Span<float> discord, Span<float> monitor, int frames)
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

        // Noise removal first: the recording, the effects and every bus get the cleaned voice.
        _denoiser.Process(micSpan, mix.NoiseSuppressionEnabled, mix.NoiseSuppressionAmount);

        Recorder?.Write(micSpan);

        // The natural voice is only needed when some bus actually uses it.
        var needDry = !game.IsEmpty && mix.GameVoice && !mix.GameVoiceEffects
                      || !discord.IsEmpty && mix.DiscordVoice && !mix.DiscordVoiceEffects;
        var wetSpan = _voiceWet.AsSpan(0, frames);
        var drySpan = needDry ? _voiceDry.AsSpan(0, frames) : Span<float>.Empty;
        _voice.Process(micSpan, wetSpan, drySpan, mix.MicEnabled, mix.PushToTalkEnabled, PushToTalkHeld || RemotePushToTalkHeld);

        Music.Read(_music, 0, frames * 2);
        Pads.Read(_pads, 0, frames * 2);
        var preview = Preview is { IsActive: true } && !monitor.IsEmpty;
        if (preview) Preview!.Read(_preview, 0, frames * 2);
        // Always read, so these sounds end on time even without headphones.
        var privatePads = PrivatePads != null;
        if (privatePads) PrivatePads!.Read(_privatePads, 0, frames * 2);

        var duckActive = mix.DuckingEnabled && _voice.VoiceActive && _voice.Transmitting;
        var duckLevel = DspUtil.DbToLinear(mix.DuckingAmountDb);
        _ducker.Configure(mix.DuckingAttackMs, mix.DuckingReleaseMs);

        var musicVol = mix.MusicVolume;
        var padsVol = mix.PadsVolume;
        var toMic = mix.MediaToMicEnabled ? mix.MediaToMicVolume : 0f;
        var monitorVol = mix.MonitorVolume;
        var selfMonitor = mix.ListenToSelf ? monitorVol : 0f;
        var previewVol = preview ? MathF.Max(monitorVol, 0.4f) : 0f;
        var privateVol = MathF.Max(monitorVol, 0.4f);

        var gameMusic = mix.GameMusic ? toMic : 0f;
        var gameSounds = mix.GameSounds ? toMic : 0f;
        var gameVoice = mix.GameVoice;
        var gameWet = mix.GameVoiceEffects;
        var discordMusic = mix.DiscordMusic ? toMic : 0f;
        var discordSounds = mix.DiscordSounds ? toMic : 0f;
        var discordVoice = mix.DiscordVoice;
        var discordWet = mix.DiscordVoiceEffects;

        float voicePeak = 0, mediaPeak = 0;
        for (var f = 0; f < frames; f++)
        {
            var duck = _ducker.Next(duckActive, duckLevel);
            var wet = wetSpan[f];
            var ml = _music[f * 2] * musicVol * duck;
            var mr = _music[f * 2 + 1] * musicVol * duck;
            var pl = _pads[f * 2] * padsVol * duck;
            var pr = _pads[f * 2 + 1] * padsVol * duck;

            voicePeak = MathF.Max(voicePeak, MathF.Abs(wet));
            mediaPeak = MathF.Max(mediaPeak, MathF.Max(MathF.Abs(ml + pl), MathF.Abs(mr + pr)));

            if (!game.IsEmpty)
            {
                var v = gameVoice ? (gameWet ? wet : drySpan[f]) : 0f;
                game[f * 2] = v + ml * gameMusic + pl * gameSounds;
                game[f * 2 + 1] = v + mr * gameMusic + pr * gameSounds;
            }
            if (!discord.IsEmpty)
            {
                var v = discordVoice ? (discordWet ? wet : drySpan[f]) : 0f;
                discord[f * 2] = v + ml * discordMusic + pl * discordSounds;
                discord[f * 2 + 1] = v + mr * discordMusic + pr * discordSounds;
            }
            if (!monitor.IsEmpty)
            {
                monitor[f * 2] = (ml + pl) * monitorVol + wet * selfMonitor;
                monitor[f * 2 + 1] = (mr + pr) * monitorVol + wet * selfMonitor;
                if (preview)
                {
                    monitor[f * 2] += _preview[f * 2] * previewVol;
                    monitor[f * 2 + 1] += _preview[f * 2 + 1] * previewVol;
                }
                if (privatePads)
                {
                    monitor[f * 2] += _privatePads[f * 2] * padsVol * privateVol;
                    monitor[f * 2 + 1] += _privatePads[f * 2 + 1] * padsVol * privateVol;
                }
            }
        }

        Meters.OutputPeak = Finish(_gameLimiter, game, frames);
        Meters.DiscordPeak = Finish(_discordLimiter, discord, frames);
        Meters.MonitorPeak = Finish(_monitorLimiter, monitor, frames);
        Meters.MicLevelDb = _voice.InputLevelDb;
        Meters.VoicePeak = voicePeak;
        Meters.MediaPeak = mediaPeak;
        Meters.DuckGain = _ducker.Gain;
        Meters.VoiceActive = _voice.VoiceActive;
        Meters.Transmitting = _voice.Transmitting;
    }

    private static float Finish(Limiter limiter, Span<float> bus, int frames)
    {
        if (bus.IsEmpty) return 0f;
        var data = bus[..(frames * 2)];
        limiter.ProcessStereo(data);
        var p = 0f;
        foreach (var s in data) p = MathF.Max(p, MathF.Abs(s));
        return p;
    }

    private void EnsureCapacity(int frames)
    {
        if (_mic.Length < frames)
        {
            _mic = new float[frames];
            _voiceWet = new float[frames];
            _voiceDry = new float[frames];
        }
        if (_music.Length < frames * 2)
        {
            _music = new float[frames * 2];
            _pads = new float[frames * 2];
            _preview = new float[frames * 2];
            _privatePads = new float[frames * 2];
        }
    }
}
