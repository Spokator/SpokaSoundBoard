using CommunityToolkit.Mvvm.ComponentModel;
using SpokaSoundBoard.Core.Speech;
using SpokaSoundBoard.Services;

namespace SpokaSoundBoard.ViewModels;

/// <summary>One entry of the voice picker: a natural (neural) voice or a Windows voice.</summary>
public sealed partial class TtsVoiceOption : ObservableObject
{
    public const string NaturalGroup = "Voix naturelles";
    public const string WindowsGroup = "Voix Windows";

    private TtsVoiceOption(string id, string group, NeuralVoice? natural, SpeechVoice? windows)
    {
        Id = id;
        Group = group;
        Natural = natural;
        Windows = windows;
    }

    public static TtsVoiceOption FromNatural(NeuralVoice voice, bool installed) =>
        new(voice.Id, NaturalGroup, voice, null) { IsInstalled = installed };

    public static TtsVoiceOption FromWindows(SpeechVoice voice) =>
        new(voice.Id, WindowsGroup, null, voice) { IsInstalled = true };

    /// <summary>Persisted in TextToSpeechSettings.VoiceId.</summary>
    public string Id { get; }

    /// <summary>Picker group header (« Voix naturelles » first, then « Voix Windows »).</summary>
    public string Group { get; }

    public NeuralVoice? Natural { get; }
    public SpeechVoice? Windows { get; }
    public bool IsNatural => Natural != null;

    /// <summary>Natural voice: its model is downloaded. Windows voice: always true.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private bool _isInstalled;

    public string Display => Natural != null
        ? IsInstalled ? Natural.Name : $"{Natural.Name} (à télécharger)"
        : Windows!.Display;

    /// <summary>The voice currently chosen (drawn highlighted in the voice list).</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Name on the voice card (« Femme 1 », « Microsoft Denise »).</summary>
    public string Title => Natural?.Name ?? Windows!.Name;

    /// <summary>Second line of the voice card.</summary>
    public string Subtitle => Natural != null
        ? Natural.Description.Length > 0 ? Natural.Description : Natural.IsFemale ? "Voix féminine" : "Voix masculine"
        : LanguageName(Windows!.Language);

    /// <summary>Segoe icon: a person for natural voices, the Windows logo otherwise.</summary>
    public string Glyph => Natural != null ? "\uE77B" : "\uE770";

    private static string LanguageName(string tag)
    {
        try
        {
            var name = new System.Globalization.CultureInfo(tag).DisplayName;
            return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : tag;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return tag;
        }
    }

    public override string ToString() => Display;
}
