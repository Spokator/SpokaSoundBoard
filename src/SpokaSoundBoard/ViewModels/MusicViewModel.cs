using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.ViewModels;

public partial class MusicViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Random _random = new();

    public MusicViewModel(MainViewModel main)
    {
        _main = main;
        _repeat = main.Settings.Repeat;
        _shuffle = main.Settings.Shuffle;
        Link = new LinkImporter(main, "Musique", true,
            "Rechercher une musique (ex. « booba mona lisa ») ou coller un lien YouTube, SoundCloud…",
            [new(SearchSource.YouTube, "YouTube", "\uE714")],
            AddFromLink);
        Player.TrackEnded += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(OnTrackEnded);
        Playlist.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        main.Mix.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MixSettings.NormalizeLoudness)) ApplyGain();
        };
    }

    private MusicPlayer Player => _main.Engine.Music;

    public ObservableCollection<TrackItem> Playlist => _main.Settings.Playlist;

    public bool IsEmpty => Playlist.Count == 0;

    public void AnalyzeMissingLoudness()
    {
        foreach (var track in Playlist.Where(t => t.NormalizationDb == null && File.Exists(t.FilePath)).ToList())
            Analyze(track);
    }

    private void Analyze(TrackItem track) => _main.Loudness.Enqueue(track.FilePath, db =>
    {
        track.NormalizationDb = db;
        if (track == CurrentTrack) ApplyGain();
        _main.Save();
    });

    private void ApplyGain() => Player.Gain = _main.NormalizationGain(CurrentTrack?.NormalizationDb);

    public LinkImporter Link { get; }

    private void AddFromLink(DownloadedMedia media)
    {
        if (Playlist.Any(t => string.Equals(t.FilePath, media.FilePath, StringComparison.OrdinalIgnoreCase))) return;
        var track = new TrackItem { FilePath = media.FilePath, DisplayTitle = media.Title, SourceUrl = media.SourceUrl };
        Playlist.Add(track);
        Analyze(track);
        // Start right away when nothing is playing, so a pasted link "just plays".
        if (!_main.Engine.Music.HasTrack) PlayTrack(track);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTitle), nameof(HasTrack))]
    private TrackItem? _currentTrack;

    [ObservableProperty] private TrackItem? _selectedTrack;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatIcon), nameof(RepeatTooltip))]
    private RepeatMode _repeat;

    [ObservableProperty] private bool _shuffle;

    /// <summary>True while the user drags the seek bar (position updates are paused).</summary>
    public bool IsSeeking { get; set; }

    public string CurrentTitle => CurrentTrack?.Title ?? "Aucune musique";
    public bool HasTrack => CurrentTrack != null;
    public string RepeatIcon => Repeat == RepeatMode.One ? "\uE8ED" : "\uE8EE";
    public string RepeatTooltip => Repeat switch
    {
        RepeatMode.Off => "Répétition : désactivée",
        RepeatMode.All => "Répétition : toute la liste",
        _ => "Répétition : ce morceau",
    };

    public void RestoreState()
    {
        foreach (var t in Playlist) t.IsCurrent = false;
    }

    [RelayCommand]
    private void AddFiles()
    {
        var dialog = new OpenFileDialog { Title = "Ajouter des musiques", Filter = AudioFileLoader.DialogFilter, Multiselect = true };
        if (dialog.ShowDialog() == true) AddPaths(dialog.FileNames);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Ajouter un dossier de musiques" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var files = Directory.EnumerateFiles(dialog.FolderName, "*.*", SearchOption.AllDirectories)
                .Where(AudioFileLoader.IsSupported)
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase);
            AddPaths(files);
        }
        catch (Exception ex)
        {
            _main.ShowToast("Lecture du dossier impossible : " + ex.Message);
        }
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        var existing = Playlist.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                AddPaths(Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories).Where(AudioFileLoader.IsSupported));
                continue;
            }
            if (!AudioFileLoader.IsSupported(p) || !existing.Add(p)) continue;
            var track = new TrackItem { FilePath = p };
            Playlist.Add(track);
            Analyze(track);
            added++;
        }
        if (added > 0)
        {
            _main.ShowToast(added == 1 ? "1 musique ajoutée" : $"{added} musiques ajoutées");
            _main.Save();
        }
    }

    [RelayCommand]
    private void PlayTrack(TrackItem? track)
    {
        if (track == null) return;
        if (!File.Exists(track.FilePath))
        {
            _main.ShowToast($"Fichier introuvable : {track.Title}");
            return;
        }

        try
        {
            Player.Load(track.FilePath);
            if (CurrentTrack != null) CurrentTrack.IsCurrent = false;
            CurrentTrack = track;
            track.IsCurrent = true;
            ApplyGain();
            IsPlaying = true;
            Duration = Math.Max(1, Player.Duration.TotalSeconds);
            DurationText = Format(Player.Duration);
        }
        catch (Exception ex)
        {
            _main.ShowToast($"Lecture impossible de « {track.Title} » : {ex.Message}");
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (Player.HasTrack)
        {
            if (Player.IsPlaying) Player.Pause(); else Player.Play();
            IsPlaying = Player.IsPlaying;
            return;
        }
        PlayTrack(SelectedTrack ?? CurrentTrack ?? Playlist.FirstOrDefault());
    }

    [RelayCommand]
    private void Stop()
    {
        Player.Stop();
        IsPlaying = false;
        Position = 0;
        PositionText = "0:00";
    }

    [RelayCommand]
    private void Next()
    {
        var next = PickNext(manual: true);
        if (next != null) PlayTrack(next);
    }

    [RelayCommand]
    private void Previous()
    {
        if (Player.HasTrack && Player.Position.TotalSeconds > 3)
        {
            Player.Seek(TimeSpan.Zero);
            return;
        }
        if (Playlist.Count == 0) return;
        var index = CurrentTrack != null ? Playlist.IndexOf(CurrentTrack) : 0;
        PlayTrack(Playlist[(index - 1 + Playlist.Count) % Playlist.Count]);
    }

    [RelayCommand]
    private void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    [RelayCommand]
    private void RemoveTrack(TrackItem? track)
    {
        track ??= SelectedTrack;
        if (track == null) return;
        if (track == CurrentTrack)
        {
            Stop();
            CurrentTrack = null;
        }
        Playlist.Remove(track);
        _main.Save();
    }

    [RelayCommand]
    private void ClearPlaylist()
    {
        Stop();
        CurrentTrack = null;
        Playlist.Clear();
        _main.Save();
    }

    public void SeekTo(double seconds)
    {
        Player.Seek(TimeSpan.FromSeconds(seconds));
        Position = seconds;
        PositionText = Format(TimeSpan.FromSeconds(seconds));
    }

    public void RefreshPosition()
    {
        IsPlaying = Player.IsPlaying;
        if (IsSeeking || !Player.HasTrack) return;
        var pos = Player.Position;
        Position = pos.TotalSeconds;
        PositionText = Format(pos);
    }

    private void OnTrackEnded()
    {
        if (Repeat == RepeatMode.One && CurrentTrack != null)
        {
            PlayTrack(CurrentTrack);
            return;
        }
        var next = PickNext(manual: false);
        if (next != null) PlayTrack(next);
        else IsPlaying = false;
    }

    private TrackItem? PickNext(bool manual)
    {
        if (Playlist.Count == 0) return null;
        if (Shuffle && Playlist.Count > 1)
        {
            TrackItem candidate;
            do candidate = Playlist[_random.Next(Playlist.Count)];
            while (candidate == CurrentTrack);
            return candidate;
        }

        var index = CurrentTrack != null ? Playlist.IndexOf(CurrentTrack) : -1;
        var nextIndex = index + 1;
        if (nextIndex >= Playlist.Count)
        {
            if (!manual && Repeat == RepeatMode.Off) return null;
            nextIndex = 0;
        }
        return Playlist[nextIndex];
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
