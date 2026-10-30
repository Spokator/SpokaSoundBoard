using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Services;

namespace SpokaSoundBoard.ViewModels;

/// <summary>"Apparence" section: theme, accent color, zoom, soundboard tile size.</summary>
public partial class AppearanceViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public AppearanceViewModel(MainViewModel main)
    {
        _main = main;
        _customColor = Settings.AccentColor;
        Settings.PropertyChanged += OnSettingsChanged;
        ThemeService.Apply(Settings);
    }

    public AppearanceSettings Settings => _main.Settings.Appearance;

    public IReadOnlyList<AccentPreset> Accents => ThemeService.Accents;

    [ObservableProperty] private string _customColor;

    [RelayCommand]
    private void SetAccent(AccentPreset? preset)
    {
        if (preset == null) return;
        Settings.SecondaryColor = preset.Secondary;
        Settings.AccentColor = preset.Accent;
        CustomColor = preset.Accent;
    }

    [RelayCommand]
    private void ApplyCustomColor()
    {
        var hex = CustomColor.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (!ThemeService.IsValidColor(hex))
        {
            _main.ShowToast("Couleur invalide : utilisez le format #RRGGBB (ex. #FF6600).");
            return;
        }
        Settings.AccentColor = hex.ToUpperInvariant();
        CustomColor = Settings.AccentColor;
    }

    [RelayCommand]
    private void Reset()
    {
        Settings.Theme = AppTheme.Dark;
        Settings.UiScale = 1.0;
        Settings.PadSize = PadSize.Medium;
        Settings.ShowSidebarMeters = true;
        Settings.ShowPadHotkeys = true;
        SetAccent(ThemeService.Accents[0]);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppearanceSettings.Theme) or nameof(AppearanceSettings.AccentColor)
            or nameof(AppearanceSettings.SecondaryColor) or nameof(AppearanceSettings.PadSize))
            ThemeService.Apply(Settings);
        _main.Save();
    }
}
