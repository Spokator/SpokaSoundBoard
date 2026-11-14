using CommunityToolkit.Mvvm.ComponentModel;
using SpokaSoundBoard.Core.Speech;

namespace SpokaSoundBoard.Core.Models;

/// <summary>
/// AI voice changer (2.15). Only the chosen voice is kept: the changer itself always starts off, so the app never
/// takes the graphics card (or changes your voice) by surprise at launch.
/// </summary>
public partial class VoiceChangerSettings : ObservableObject
{
    /// <summary>
    /// Key of the target voice: a built-in voice (<see cref="VoiceChangerCatalog.Voices"/>) or one of « Mes voix »
    /// (<see cref="CustomVoice.KeyPrefix"/> + id; checked against the library when the voice changer starts).
    /// </summary>
    [ObservableProperty] private string _voiceKey = VoiceChangerCatalog.DefaultVoiceKey;

    /// <summary>Shorter delay (default) or a little more faithful.</summary>
    [ObservableProperty] private VoiceChangerMode _mode = VoiceChangerMode.Reactive;

    public void Sanitize()
    {
        if (string.IsNullOrWhiteSpace(VoiceKey) || !VoiceKey.StartsWith(CustomVoice.KeyPrefix, StringComparison.Ordinal))
            VoiceKey = VoiceChangerCatalog.FindVoice(VoiceKey).Key;
        if (!Enum.IsDefined(Mode)) Mode = VoiceChangerMode.Reactive;
    }
}

public enum VoiceChangerMode
{
    /// <summary>About half a second: 200 ms blocks, 3 steps, shorter contexts.</summary>
    Reactive,

    /// <summary>About 0.7 s: Seed-VC's real-time settings (300 ms blocks, 4 steps).</summary>
    Stable,
}
