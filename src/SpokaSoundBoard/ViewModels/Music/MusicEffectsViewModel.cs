using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.ViewModels.Music;

public sealed record MusicEffectPreset(string Name, string Icon, string Description, Action<MusicEffectSettings> Apply);

public sealed partial class EqBandViewModel(MusicEffectSettings settings, int index, string label) : ObservableObject
{
    public string Label { get; } = label;

    public float Value
    {
        get => settings.EqBands()[index];
        set
        {
            var bands = settings.EqBands();
            bands[index] = value;
            settings.SetEqBands(bands);
            settings.EqEnabled = true;
            settings.PresetName = null;
            OnPropertyChanged();
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(Value));
}

/// <summary>« Effets » panel of the music page: presets, bass / treble, 10-band EQ, speed, 8D, karaoke, reverb, width.</summary>
public partial class MusicEffectsViewModel : ObservableObject
{
    private readonly MusicViewModel _music;

    public MusicEffectsViewModel(MusicViewModel music)
    {
        _music = music;
        string[] labels = ["32", "64", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];
        Bands = labels.Select((l, i) => new EqBandViewModel(Settings, i, l)).ToList();
        Settings.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(CurrentPresetName));
            if (e.PropertyName is nameof(MusicEffectSettings.Speed)) OnPropertyChanged(nameof(SpeedText));
            music.Main.Save();
        };
    }

    public MusicEffectSettings Settings => _music.Settings.Effects;
    public MusicSettings Music => _music.Settings;
    public IReadOnlyList<EqBandViewModel> Bands { get; }

    public string CurrentPresetName => Settings.PresetName ?? (Settings.IsNeutral ? "Aucun" : "Personnalisé");
    public string SpeedText => $"× {Settings.Speed:0.00}";

    public IReadOnlyList<MusicEffectPreset> Presets { get; } =
    [
        new("Normal", "", "Aucun effet", s => { }),
        new("Bass boost", "", "Basses renforcées", s => { s.BassDb = 8; }),
        new("Bass boost extrême", "", "Pour faire trembler les murs", s => { s.BassDb = 14; s.EqEnabled = true; s.SetEqBands([6, 5, 3, 0, 0, 0, 0, 0, 0, 0]); }),
        new("Nightcore", "", "Plus rapide et plus aigu", s => { s.Speed = 1.25f; s.TrebleDb = 2; }),
        new("Vaporwave", "", "Ralenti, rêveur", s => { s.Speed = 0.8f; s.ReverbMix = 0.3f; s.TrebleDb = -3; }),
        new("Slowed + reverb", "", "La version TikTok", s => { s.Speed = 0.85f; s.ReverbMix = 0.4f; }),
        new("Audio 8D", "", "Le son tourne autour de vous (au casque)", s => { s.Surround8D = true; s.Surround8DRate = 0.12f; s.ReverbMix = 0.15f; }),
        new("Karaoké", "", "Voix principale retirée", s => { s.Karaoke = true; }),
        new("Salle de concert", "", "Grande réverbération", s => { s.ReverbMix = 0.45f; s.StereoWidth = 1.3f; }),
        new("Soirée", "", "Basses et aigus, son large", s => { s.BassDb = 5; s.TrebleDb = 3; s.StereoWidth = 1.3f; }),
        new("Lo-fi chill", "", "Doux et feutré", s => { s.Speed = 0.92f; s.TrebleDb = -6; s.ReverbMix = 0.2f; }),
        new("Voix claires", "", "Podcasts, vidéos parlées", s => { s.EqEnabled = true; s.SetEqBands([-4, -3, -1, 0, 2, 4, 4, 2, 0, -1]); }),
        new("Radio AM", "", "Vieux poste", s => { s.EqEnabled = true; s.SetEqBands([-12, -12, -6, 2, 5, 6, 4, -6, -12, -12]); s.StereoWidth = 0; }),
    ];

    [RelayCommand]
    private void ApplyPreset(MusicEffectPreset? preset)
    {
        if (preset == null) return;
        Reset();
        preset.Apply(Settings);
        Settings.Enabled = true;
        Settings.PresetName = preset.Name == "Normal" ? null : preset.Name;
        foreach (var b in Bands) b.Refresh();
        _music.Main.ShowToast(preset.Name == "Normal" ? "Effets musique désactivés" : $"Effet : {preset.Name}");
    }

    [RelayCommand]
    private void Reset()
    {
        Settings.BassDb = 0;
        Settings.TrebleDb = 0;
        Settings.EqEnabled = false;
        Settings.SetEqBands(new float[10]);
        Settings.Speed = 1f;
        Settings.Surround8D = false;
        Settings.Karaoke = false;
        Settings.ReverbMix = 0;
        Settings.StereoWidth = 1f;
        Settings.PresetName = null;
        foreach (var b in Bands) b.Refresh();
    }

    [RelayCommand]
    private void ResetSpeed() => Settings.Speed = 1f;
}
