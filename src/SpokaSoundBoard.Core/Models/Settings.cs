using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SpokaSoundBoard.Core.Models;

/// <summary>Sound played by the radio effect around a transmission.</summary>
public enum RadioBeepStyle
{
    /// <summary>Two-tone beep when you stop talking.</summary>
    Roger,

    /// <summary>NASA "Quindar" tones: 2525 Hz when you start, 2475 Hz when you stop.</summary>
    Nasa,

    /// <summary>Short single beep when you stop talking.</summary>
    Simple,
}

/// <summary>
/// All voice-processing parameters. Bound directly by the UI and read by the audio thread.
/// </summary>
public partial class VoiceEffectSettings : ObservableObject
{
    [ObservableProperty] private bool _effectsEnabled = true;
    [ObservableProperty] private float _inputGainDb;
    [ObservableProperty] private float _voiceVolumeDb;

    [ObservableProperty] private bool _gateEnabled;
    [ObservableProperty] private float _voiceThresholdDb = -45f;

    [ObservableProperty] private float _pitchSemitones;

    [ObservableProperty] private bool _radioEnabled;
    [ObservableProperty] private float _radioLowCutHz = 400f;
    [ObservableProperty] private float _radioHighCutHz = 3000f;
    [ObservableProperty] private float _radioDrive = 0.4f;
    [ObservableProperty] private float _radioNoise = 0.15f;
    [ObservableProperty] private bool _squelchEnabled = true;
    [ObservableProperty] private bool _rogerBeepEnabled;
    [ObservableProperty] private RadioBeepStyle _beepStyle;

    [ObservableProperty] private bool _robotEnabled;
    [ObservableProperty] private float _robotFrequency = 60f;
    [ObservableProperty] private float _robotMix = 0.8f;

    [ObservableProperty] private bool _echoEnabled;
    [ObservableProperty] private float _echoDelayMs = 250f;
    [ObservableProperty] private float _echoFeedback = 0.35f;
    [ObservableProperty] private float _echoMix = 0.3f;

    [ObservableProperty] private bool _reverbEnabled;
    [ObservableProperty] private float _reverbSize = 0.6f;
    [ObservableProperty] private float _reverbMix = 0.25f;

    [ObservableProperty] private bool _compressorEnabled = true;
    [ObservableProperty] private float _compressorAmount = 0.4f;

    [ObservableProperty] private bool _eqEnabled;
    [ObservableProperty] private float _eqLowDb;
    [ObservableProperty] private float _eqMidDb;
    [ObservableProperty] private float _eqHighDb;

    [ObservableProperty] private bool _chorusEnabled;
    [ObservableProperty] private float _chorusRate = 0.8f;
    [ObservableProperty] private float _chorusDepth = 0.5f;
    [ObservableProperty] private float _chorusMix = 0.5f;

    [ObservableProperty] private bool _lofiEnabled;
    [ObservableProperty] private float _lofiDrive = 0.3f;
    [ObservableProperty] private float _lofiBits = 10f;
    [ObservableProperty] private float _lofiDownsample = 2f;

    [ObservableProperty] private bool _muffleEnabled;
    [ObservableProperty] private float _muffleHz = 900f;

    public VoiceEffectSettings Clone()
    {
        var copy = new VoiceEffectSettings();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Copies the "sound design" values. Gain and detection threshold are personal and kept.</summary>
    public void CopyEffectsFrom(VoiceEffectSettings o)
    {
        EffectsEnabled = true;
        PitchSemitones = o.PitchSemitones;
        RadioEnabled = o.RadioEnabled;
        RadioLowCutHz = o.RadioLowCutHz;
        RadioHighCutHz = o.RadioHighCutHz;
        RadioDrive = o.RadioDrive;
        RadioNoise = o.RadioNoise;
        SquelchEnabled = o.SquelchEnabled;
        RogerBeepEnabled = o.RogerBeepEnabled;
        BeepStyle = o.BeepStyle;
        RobotEnabled = o.RobotEnabled;
        RobotFrequency = o.RobotFrequency;
        RobotMix = o.RobotMix;
        EchoEnabled = o.EchoEnabled;
        EchoDelayMs = o.EchoDelayMs;
        EchoFeedback = o.EchoFeedback;
        EchoMix = o.EchoMix;
        ReverbEnabled = o.ReverbEnabled;
        ReverbSize = o.ReverbSize;
        ReverbMix = o.ReverbMix;
        CompressorEnabled = o.CompressorEnabled;
        CompressorAmount = o.CompressorAmount;
        EqEnabled = o.EqEnabled;
        EqLowDb = o.EqLowDb;
        EqMidDb = o.EqMidDb;
        EqHighDb = o.EqHighDb;
        ChorusEnabled = o.ChorusEnabled;
        ChorusRate = o.ChorusRate;
        ChorusDepth = o.ChorusDepth;
        ChorusMix = o.ChorusMix;
        LofiEnabled = o.LofiEnabled;
        LofiDrive = o.LofiDrive;
        LofiBits = o.LofiBits;
        LofiDownsample = o.LofiDownsample;
        MuffleEnabled = o.MuffleEnabled;
        MuffleHz = o.MuffleHz;
    }

    public void CopyFrom(VoiceEffectSettings o)
    {
        CopyEffectsFrom(o);
        EffectsEnabled = o.EffectsEnabled;
        InputGainDb = o.InputGainDb;
        VoiceVolumeDb = o.VoiceVolumeDb;
        GateEnabled = o.GateEnabled;
        VoiceThresholdDb = o.VoiceThresholdDb;
    }

    /// <summary>
    /// Brings every value back into the range of its slider (NaN / infinity → default), so a hand-edited or
    /// corrupted settings file can never feed the audio thread values that break the effects.
    /// </summary>
    public void Sanitize()
    {
        var d = new VoiceEffectSettings();
        InputGainDb = SettingsMath.Clamp(InputGainDb, -12, 24, d.InputGainDb);
        VoiceVolumeDb = SettingsMath.Clamp(VoiceVolumeDb, -24, 12, d.VoiceVolumeDb);
        VoiceThresholdDb = SettingsMath.Clamp(VoiceThresholdDb, -60, 0, d.VoiceThresholdDb);
        PitchSemitones = SettingsMath.Clamp(PitchSemitones, -12, 12, d.PitchSemitones);
        RadioLowCutHz = SettingsMath.Clamp(RadioLowCutHz, 100, 1200, d.RadioLowCutHz);
        RadioHighCutHz = SettingsMath.Clamp(RadioHighCutHz, 1500, 6000, d.RadioHighCutHz);
        RadioDrive = SettingsMath.Clamp(RadioDrive, 0, 1, d.RadioDrive);
        RadioNoise = SettingsMath.Clamp(RadioNoise, 0, 1, d.RadioNoise);
        if (!Enum.IsDefined(BeepStyle)) BeepStyle = d.BeepStyle;
        RobotFrequency = SettingsMath.Clamp(RobotFrequency, 20, 300, d.RobotFrequency);
        RobotMix = SettingsMath.Clamp(RobotMix, 0, 1, d.RobotMix);
        EchoDelayMs = SettingsMath.Clamp(EchoDelayMs, 40, 1000, d.EchoDelayMs);
        EchoFeedback = SettingsMath.Clamp(EchoFeedback, 0, 0.9f, d.EchoFeedback);
        EchoMix = SettingsMath.Clamp(EchoMix, 0, 1, d.EchoMix);
        ReverbSize = SettingsMath.Clamp(ReverbSize, 0, 1, d.ReverbSize);
        ReverbMix = SettingsMath.Clamp(ReverbMix, 0, 1, d.ReverbMix);
        CompressorAmount = SettingsMath.Clamp(CompressorAmount, 0, 1, d.CompressorAmount);
        EqLowDb = SettingsMath.Clamp(EqLowDb, -12, 12, d.EqLowDb);
        EqMidDb = SettingsMath.Clamp(EqMidDb, -12, 12, d.EqMidDb);
        EqHighDb = SettingsMath.Clamp(EqHighDb, -12, 12, d.EqHighDb);
        ChorusRate = SettingsMath.Clamp(ChorusRate, 0.1f, 8, d.ChorusRate);
        ChorusDepth = SettingsMath.Clamp(ChorusDepth, 0, 1, d.ChorusDepth);
        ChorusMix = SettingsMath.Clamp(ChorusMix, 0, 1, d.ChorusMix);
        LofiDrive = SettingsMath.Clamp(LofiDrive, 0, 1, d.LofiDrive);
        LofiBits = SettingsMath.Clamp(LofiBits, 3, 16, d.LofiBits);
        LofiDownsample = SettingsMath.Clamp(LofiDownsample, 1, 10, d.LofiDownsample);
        MuffleHz = SettingsMath.Clamp(MuffleHz, 200, 8000, d.MuffleHz);
    }
}

/// <summary>Range checks shared by the settings Sanitize methods.</summary>
public static class SettingsMath
{
    /// <summary>Clamps <paramref name="value"/> to [min, max]; NaN and infinities become <paramref name="fallback"/>.</summary>
    public static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public partial class MixSettings : ObservableObject
{
    /// <summary>Send the (processed) voice to the virtual microphone.</summary>
    [ObservableProperty] private bool _micEnabled = true;

    /// <summary>Send music and sounds to the virtual microphone (off = only you hear them).</summary>
    [ObservableProperty] private bool _mediaToMicEnabled = true;

    [ObservableProperty] private float _musicVolume = 0.8f;
    [ObservableProperty] private float _padsVolume = 0.8f;
    [ObservableProperty] private float _mediaToMicVolume = 0.6f;
    [ObservableProperty] private float _monitorVolume = 0.5f;
    [ObservableProperty] private bool _listenToSelf;

    [ObservableProperty] private bool _duckingEnabled = true;
    [ObservableProperty] private float _duckingAmountDb = -14f;
    [ObservableProperty] private float _duckingAttackMs = 60f;
    [ObservableProperty] private float _duckingReleaseMs = 700f;

    /// <summary>Plays every sound and track at the same perceived loudness.</summary>
    [ObservableProperty] private bool _normalizeLoudness = true;

    /// <summary>Radio mode: the voice only goes through while the push-to-talk key is held.</summary>
    [ObservableProperty] private bool _pushToTalkEnabled;

    // Routing matrix "who hears what". The game bus is the main virtual mic, the Discord bus a second one.
    [ObservableProperty] private bool _gameVoice = true;
    [ObservableProperty] private bool _gameVoiceEffects = true;
    [ObservableProperty] private bool _gameMusic = true;
    [ObservableProperty] private bool _gameSounds = true;

    [ObservableProperty] private bool _discordVoice = true;
    [ObservableProperty] private bool _discordVoiceEffects;
    [ObservableProperty] private bool _discordMusic;
    [ObservableProperty] private bool _discordSounds;

    /// <summary>Brings every value back into the range of its slider (NaN / infinity → default).</summary>
    public void Sanitize()
    {
        var d = new MixSettings();
        MusicVolume = SettingsMath.Clamp(MusicVolume, 0, 1.5f, d.MusicVolume);
        PadsVolume = SettingsMath.Clamp(PadsVolume, 0, 1.5f, d.PadsVolume);
        MediaToMicVolume = SettingsMath.Clamp(MediaToMicVolume, 0, 1.5f, d.MediaToMicVolume);
        MonitorVolume = SettingsMath.Clamp(MonitorVolume, 0, 1.5f, d.MonitorVolume);
        DuckingAmountDb = SettingsMath.Clamp(DuckingAmountDb, -40, 0, d.DuckingAmountDb);
        DuckingAttackMs = SettingsMath.Clamp(DuckingAttackMs, 10, 500, d.DuckingAttackMs);
        DuckingReleaseMs = SettingsMath.Clamp(DuckingReleaseMs, 100, 3000, d.DuckingReleaseMs);
    }
}

public class EffectPreset
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "\uE720";
    public string Description { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public VoiceEffectSettings Settings { get; set; } = new();

    [JsonIgnore]
    public bool IsCustom => !IsBuiltIn;
}
