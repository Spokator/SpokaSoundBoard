using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.ViewModels;

public partial class VoiceViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public VoiceViewModel(MainViewModel main)
    {
        _main = main;
        _selectedPresetName = main.Settings.SelectedPresetName;
        foreach (var p in BuiltInPresets.All) Presets.Add(p);
        foreach (var p in main.Settings.CustomPresets) Presets.Add(p);
    }

    public VoiceEffectSettings Settings => _main.VoiceSettings;

    public ObservableCollection<EffectPreset> Presets { get; } = [];

    [ObservableProperty] private string? _selectedPresetName;
    [ObservableProperty] private string _newPresetName = "";

    [RelayCommand]
    private void ApplyPreset(EffectPreset? preset)
    {
        if (preset == null) return;
        Settings.CopyEffectsFrom(preset.Settings);
        SelectedPresetName = preset.Name;
        _main.ShowToast($"Effet « {preset.Name} » appliqué");
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = NewPresetName.Trim();
        if (name.Length == 0)
        {
            _main.ShowToast("Donnez un nom à votre preset.");
            return;
        }

        var existing = Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is { IsBuiltIn: true })
        {
            _main.ShowToast("Ce nom est déjà utilisé par un preset intégré.");
            return;
        }
        if (existing != null)
        {
            existing.Settings = Settings.Clone();
            // Refresh the item in the list.
            var index = Presets.IndexOf(existing);
            Presets[index] = existing;
        }
        else
        {
            var preset = new EffectPreset { Name = name, Icon = "\uE734", Description = "Preset personnalisé", Settings = Settings.Clone() };
            Presets.Add(preset);
            _main.Settings.CustomPresets.Add(preset);
        }

        SelectedPresetName = name;
        NewPresetName = "";
        _main.Save();
        _main.ShowToast($"Preset « {name} » enregistré");
    }

    [RelayCommand]
    private void DeletePreset(EffectPreset? preset)
    {
        if (preset is not { IsBuiltIn: false }) return;
        Presets.Remove(preset);
        _main.Settings.CustomPresets.Remove(preset);
        _main.Save();
    }

    /// <summary>Hotkeys "effet suivant / précédent".</summary>
    public void CyclePreset(int direction)
    {
        if (Presets.Count == 0) return;
        var index = Presets.ToList().FindIndex(p => p.Name == SelectedPresetName);
        var next = Presets[((index < 0 ? 0 : index + direction) % Presets.Count + Presets.Count) % Presets.Count];
        ApplyPreset(next);
    }

    public int AddImportedPresets(IEnumerable<EffectPreset> presets)
    {
        var added = 0;
        foreach (var preset in presets)
        {
            if (Presets.Any(p => p.Name.Equals(preset.Name, StringComparison.CurrentCultureIgnoreCase))) continue;
            preset.IsBuiltIn = false;
            Presets.Add(preset);
            _main.Settings.CustomPresets.Add(preset);
            added++;
        }
        return added;
    }

    [RelayCommand]
    private void ResetVoice()
    {
        ApplyPreset(BuiltInPresets.All[0]);
        Settings.InputGainDb = 0;
        Settings.VoiceVolumeDb = 0;
    }
}
