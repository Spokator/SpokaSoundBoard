using CommunityToolkit.Mvvm.ComponentModel;
using SpokaSoundBoard.Core.Speech;

namespace SpokaSoundBoard.Core.Models;

/// <summary>
/// AI voice changer (2.15). Only the chosen voice is kept: the changer itself always starts off, so the app never
/// takes the graphics card (or changes your voice) by surprise at launch.
/// </summary>
public partial class VoiceChangerSettings : ObservableObject
{
    /// <summary>Key of the target voice (<see cref="VoiceChangerCatalog.Voices"/>).</summary>
    [ObservableProperty] private string _voiceKey = VoiceChangerCatalog.DefaultVoiceKey;

    public void Sanitize() => VoiceKey = VoiceChangerCatalog.FindVoice(VoiceKey).Key;
}
