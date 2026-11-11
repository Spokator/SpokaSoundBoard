using CommunityToolkit.Mvvm.ComponentModel;

namespace SpokaSoundBoard.Core.Models;

/// <summary>Music player behaviour. Bound by the UI, read by the player.</summary>
public partial class MusicSettings : ObservableObject
{
    /// <summary>Equal-power crossfade between consecutive tracks (0 = gapless, no fade).</summary>
    [ObservableProperty] private double _crossfadeSeconds = 4;

    /// <summary>When the queue runs out, keep playing similar tracks (YouTube "mix" of the last track).</summary>
    [ObservableProperty] private bool _autoplay = true;

    [ObservableProperty] private bool _showLyrics = true;

    /// <summary>Remove the queue entries once they have been played (otherwise the queue is kept and replayed).</summary>
    [ObservableProperty] private bool _consumeQueue;

    public MusicEffectSettings Effects { get; set; } = new();
}

/// <summary>
/// Real-time effects applied to the music bus only (never to the voice, the soundboard or the preview).
/// Read by the audio thread on every block: changes apply instantly, without reloading the track.
/// </summary>
public partial class MusicEffectSettings : ObservableObject
{
    /// <summary>Master switch for every effect below.</summary>
    [ObservableProperty] private bool _enabled = true;

    /// <summary>Name of the built-in preset last applied (null = custom).</summary>
    [ObservableProperty] private string? _presetName;

    /// <summary>Low shelf around 100 Hz, 0 to +15 dB.</summary>
    [ObservableProperty] private float _bassDb;

    /// <summary>High shelf around 8 kHz, -10 to +10 dB.</summary>
    [ObservableProperty] private float _trebleDb;

    // 10-band graphic equalizer (peaking filters, -12 to +12 dB).
    [ObservableProperty] private bool _eqEnabled;
    [ObservableProperty] private float _eq32;
    [ObservableProperty] private float _eq64;
    [ObservableProperty] private float _eq125;
    [ObservableProperty] private float _eq250;
    [ObservableProperty] private float _eq500;
    [ObservableProperty] private float _eq1k;
    [ObservableProperty] private float _eq2k;
    [ObservableProperty] private float _eq4k;
    [ObservableProperty] private float _eq8k;
    [ObservableProperty] private float _eq16k;

    /// <summary>Playback speed, 0.5 to 2.0. The pitch follows the speed (nightcore 1.25, vaporwave 0.8).</summary>
    [ObservableProperty] private float _speed = 1f;

    /// <summary>"8D audio": the sound slowly rotates around the listener.</summary>
    [ObservableProperty] private bool _surround8D;

    /// <summary>Rotation speed of the 8D effect in Hz (0.05 to 0.5).</summary>
    [ObservableProperty] private float _surround8DRate = 0.12f;

    /// <summary>Removes the centred lead vocal (keeps the bass).</summary>
    [ObservableProperty] private bool _karaoke;

    /// <summary>Concert-hall reverb amount (0 = off, 1 = very wet).</summary>
    [ObservableProperty] private float _reverbMix;

    /// <summary>Stereo width: 0 = mono, 1 = unchanged, 2 = very wide.</summary>
    [ObservableProperty] private float _stereoWidth = 1f;

    public float[] EqBands() => [Eq32, Eq64, Eq125, Eq250, Eq500, Eq1k, Eq2k, Eq4k, Eq8k, Eq16k];

    public static readonly float[] EqFrequencies = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public void SetEqBands(IReadOnlyList<float> bands)
    {
        Eq32 = bands[0]; Eq64 = bands[1]; Eq125 = bands[2]; Eq250 = bands[3]; Eq500 = bands[4];
        Eq1k = bands[5]; Eq2k = bands[6]; Eq4k = bands[7]; Eq8k = bands[8]; Eq16k = bands[9];
    }

    /// <summary>Brings every value back into the range of its slider (NaN / infinity → neutral).</summary>
    public void Sanitize()
    {
        BassDb = SettingsMath.Clamp(BassDb, 0, 15, 0);
        TrebleDb = SettingsMath.Clamp(TrebleDb, -10, 10, 0);
        SetEqBands(EqBands().Select(b => SettingsMath.Clamp(b, -12, 12, 0)).ToArray());
        Speed = SettingsMath.Clamp(Speed, 0.5f, 2f, 1f);
        Surround8DRate = SettingsMath.Clamp(Surround8DRate, 0.05f, 0.5f, 0.12f);
        ReverbMix = SettingsMath.Clamp(ReverbMix, 0, 1, 0);
        StereoWidth = SettingsMath.Clamp(StereoWidth, 0, 2, 1f);
    }

    /// <summary>True when every effect is neutral (lets the player skip the processing).</summary>
    public bool IsNeutral =>
        !Enabled || BassDb == 0 && TrebleDb == 0 && (!EqEnabled || EqBands().All(b => b == 0)) && Speed == 1f
        && !Surround8D && !Karaoke && ReverbMix == 0 && StereoWidth == 1f;
}
