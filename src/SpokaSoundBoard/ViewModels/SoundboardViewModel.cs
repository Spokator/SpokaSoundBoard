using System.Collections.ObjectModel;
using System.Windows.Data;
using SpokaSoundBoard.Core;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.ViewModels;

public partial class SoundboardViewModel : ObservableObject
{
    public static readonly string[] Palette =
        ["#7C5CFF", "#3B82F6", "#06B6D4", "#10B981", "#84CC16", "#F59E0B", "#F97316", "#EF4444", "#EC4899", "#A855F7", "#64748B"];

    private readonly MainViewModel _main;

    public SoundboardViewModel(MainViewModel main)
    {
        _main = main;
        Link = new LinkImporter(main, "Sons", false,
            "Rechercher un son (ex. « vine boom », « sirène ») ou coller un lien…",
            [new(SearchSource.MyInstants, "Bruitages (MyInstants)", "\uE8A9"), new(SearchSource.YouTube, "YouTube", "\uE714")],
            AddFromLink);
        Tts = new TextToSpeechViewModel(main, this);
        PadsView = CollectionViewSource.GetDefaultView(Pads);
        PadsView.Filter = o => o is SoundPad p && (string.IsNullOrWhiteSpace(Filter) || p.Name.Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase));
        foreach (var pad in Pads) pad.PropertyChanged += OnPadChanged;
        main.Settings.Appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceSettings.ShowPadHotkeys)) OnPropertyChanged(nameof(ShowHotkeys));
        };
        Pads.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (SoundPad pad in e.NewItems) pad.PropertyChanged += OnPadChanged;
            if (e.OldItems != null)
                foreach (SoundPad pad in e.OldItems) pad.PropertyChanged -= OnPadChanged;
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    public ObservableCollection<SoundPad> Pads => _main.Settings.Pads;

    public LinkImporter Link { get; }

    /// <summary>« Synthèse vocale » panel at the top of the page.</summary>
    public TextToSpeechViewModel Tts { get; }

    public bool ShowHotkeys => _main.Settings.Appearance.ShowPadHotkeys;

    public IReadOnlyList<string> Colors => Palette;

    public bool IsEmpty => Pads.Count == 0;

    /// <summary>Pads filtered by the search box.</summary>
    public ICollectionView PadsView { get; }

    [ObservableProperty] private string _filter = "";

    partial void OnFilterChanged(string value) => PadsView.Refresh();

    public void AnalyzeMissingLoudness()
    {
        foreach (var pad in Pads.Where(p => p.NormalizationDb == null && File.Exists(p.FilePath)).ToList())
            Analyze(pad);
    }

    private void Analyze(SoundPad pad) => _main.Loudness.Enqueue(pad.FilePath, db =>
    {
        pad.NormalizationDb = db;
        _main.Save();
    });

    private float EffectiveVolume(SoundPad pad) => pad.Volume * _main.NormalizationGain(pad.NormalizationDb);

    [RelayCommand]
    private void ExportPack()
    {
        if (Pads.Count == 0)
        {
            _main.ShowToast("Ajoutez d'abord des sons à votre soundboard.");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Exporter ma soundboard",
            Filter = SoundboardPack.DialogFilter,
            FileName = "Ma soundboard" + SoundboardPack.Extension,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var count = SoundboardPack.Export(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), Pads, _main.Settings.CustomPresets);
            _main.ShowToast($"Pack exporté : {count} sons. Envoyez le fichier à vos amis !");
        }
        catch (Exception ex)
        {
            _main.ShowToast("Export impossible : " + ex.Message);
        }
    }

    [RelayCommand]
    private void ImportPack()
    {
        var dialog = new OpenFileDialog { Title = "Importer une soundboard", Filter = SoundboardPack.DialogFilter };
        if (dialog.ShowDialog() == true) ImportPackFile(dialog.FileName);
    }

    public void ImportPackFile(string path)
    {
        try
        {
            var library = Path.Combine(_main.Downloader.CacheDirectory, "Packs");
            var (pads, presets) = SoundboardPack.Import(path, library);
            foreach (var pad in pads)
            {
                Pads.Add(pad);
                if (pad.NormalizationDb == null) Analyze(pad);
            }
            var addedPresets = _main.Voice.AddImportedPresets(presets);
            _main.Save();
            _main.ShowToast($"Pack importé : {pads.Count} sons" + (addedPresets > 0 ? $" et {addedPresets} effets de voix." : "."));
        }
        catch (Exception ex)
        {
            _main.ShowToast("Import impossible : " + ex.Message);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SoundPad? _selectedPad;

    public bool HasSelection => SelectedPad != null;

    [RelayCommand]
    private void AddSounds()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ajouter des sons",
            Filter = AudioFileLoader.DialogFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        var list = paths.ToList();
        foreach (var pack in list.Where(p => p.EndsWith(SoundboardPack.Extension, StringComparison.OrdinalIgnoreCase)))
            ImportPackFile(pack);
        if (list.All(p => p.EndsWith(SoundboardPack.Extension, StringComparison.OrdinalIgnoreCase))) return;
        foreach (var path in list.Where(AudioFileLoader.IsSupported))
        {
            var newPad = new SoundPad
            {
                Name = Path.GetFileNameWithoutExtension(path),
                FilePath = path,
                Color = Palette[Pads.Count % Palette.Length],
            };
            Pads.Add(newPad);
            Analyze(newPad);
            added++;
        }
        if (added > 0)
        {
            SelectedPad = Pads[^1];
            _main.Save();
        }
        else
        {
            _main.ShowToast("Aucun fichier audio compatible.");
        }
    }

    private void AddFromLink(DownloadedMedia media)
    {
        var pad = new SoundPad
        {
            Name = media.Title.Length > 40 ? media.Title[..40].TrimEnd() + "…" : media.Title,
            FilePath = media.FilePath,
            SourceUrl = media.SourceUrl,
            Color = Palette[Pads.Count % Palette.Length],
        };
        Pads.Add(pad);
        Analyze(pad);
        SelectedPad = pad;
    }

    [RelayCommand]
    private async Task PlayPad(SoundPad? pad)
    {
        if (pad == null) return;
        var player = _main.Engine.Pads;

        // Clicking a sound that is already playing stops it.
        if (player.IsPlaying(pad.Id))
        {
            player.Stop(pad.Id);
            pad.IsPlaying = false;
            return;
        }

        if (!File.Exists(pad.FilePath))
        {
            _main.ShowToast($"Fichier introuvable : {pad.FilePath}");
            return;
        }

        try
        {
            pad.IsPlaying = true;
            await player.PlayAsync(pad.Id, pad.FilePath, EffectiveVolume(pad), pad.Loop, pad.StartSeconds, pad.EndSeconds);
        }
        catch (Exception ex)
        {
            pad.IsPlaying = false;
            _main.ShowToast($"Lecture impossible de « {pad.Name} » : {ex.Message}");
        }
    }

    [RelayCommand]
    private void StopAll()
    {
        _main.Engine.Pads.StopAll();
        foreach (var pad in Pads) pad.IsPlaying = false;
    }

    [RelayCommand]
    private void DeletePad(SoundPad? pad)
    {
        pad ??= SelectedPad;
        if (pad == null) return;
        _main.Engine.Pads.Stop(pad.Id);
        Pads.Remove(pad);
        if (SelectedPad == pad) SelectedPad = null;
        _main.Save();
    }

    [RelayCommand]
    private void EditPad(SoundPad? pad) => SelectedPad = pad;

    [RelayCommand]
    private void CloseEditor() => SelectedPad = null;

    [RelayCommand]
    private void SetColor(string color)
    {
        if (SelectedPad != null) SelectedPad.Color = color;
    }

    [RelayCommand]
    private void ChangeFile()
    {
        if (SelectedPad == null) return;
        var dialog = new OpenFileDialog { Title = "Choisir le fichier du son", Filter = AudioFileLoader.DialogFilter };
        if (dialog.ShowDialog() == true) SelectedPad.FilePath = dialog.FileName;
    }

    public void RefreshPlayingState()
    {
        if (Pads.Count == 0) return;
        var playing = _main.Engine.Pads.PlayingIds();
        foreach (var pad in Pads)
        {
            var isPlaying = playing.Contains(pad.Id);
            // Keep the "starting" state while the file is being opened.
            if (pad.IsPlaying && !isPlaying && _startGrace.TryGetValue(pad.Id, out var t) && DateTime.UtcNow - t < TimeSpan.FromMilliseconds(400))
                continue;
            pad.IsPlaying = isPlaying;
        }
    }

    private readonly Dictionary<Guid, DateTime> _startGrace = [];

    private void OnPadChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SoundPad pad) return;
        if (e.PropertyName is nameof(SoundPad.Volume) or nameof(SoundPad.NormalizationDb))
            _main.Engine.Pads.SetVolume(pad.Id, EffectiveVolume(pad));
        else if (e.PropertyName == nameof(SoundPad.IsPlaying) && pad.IsPlaying)
            _startGrace[pad.Id] = DateTime.UtcNow;
    }
}
