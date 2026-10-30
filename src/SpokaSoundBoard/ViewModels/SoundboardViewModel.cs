using System.Collections.ObjectModel;
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
        foreach (var pad in Pads) pad.PropertyChanged += OnPadChanged;
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

    public IReadOnlyList<string> Colors => Palette;

    public bool IsEmpty => Pads.Count == 0;

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
        foreach (var path in paths.Where(AudioFileLoader.IsSupported))
        {
            Pads.Add(new SoundPad
            {
                Name = Path.GetFileNameWithoutExtension(path),
                FilePath = path,
                Color = Palette[Pads.Count % Palette.Length],
            });
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
            await player.PlayAsync(pad.Id, pad.FilePath, pad.Volume, pad.Loop, pad.StartSeconds, pad.EndSeconds);
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
        if (e.PropertyName == nameof(SoundPad.Volume))
            _main.Engine.Pads.SetVolume(pad.Id, pad.Volume);
        else if (e.PropertyName == nameof(SoundPad.IsPlaying) && pad.IsPlaying)
            _startGrace[pad.Id] = DateTime.UtcNow;
    }
}
