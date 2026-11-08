using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Music;
using SpokaSoundBoard.Services;

namespace SpokaSoundBoard.ViewModels.Music;

public enum MusicSectionKind
{
    Home,
    Tracks,
    Favorites,
    History,
    Stats,
    Playlist,
}

/// <summary>An entry of the music page sidebar.</summary>
public sealed record MusicSection(MusicSectionKind Kind, string Title, string Icon);

public partial class MusicViewModel
{
    private LocalMetadataService? _metadata;
    private bool _refreshPending;

    public IReadOnlyList<MusicSection> Sections { get; } =
    [
        new(MusicSectionKind.Home, "Accueil", ""),
        new(MusicSectionKind.Tracks, "Toutes les musiques", ""),
        new(MusicSectionKind.Favorites, "Favoris", ""),
        new(MusicSectionKind.History, "Historique", ""),
        new(MusicSectionKind.Stats, "Statistiques", ""),
    ];

    public ObservableCollection<MusicPlaylist> Playlists => Library.Playlists;

    /// <summary>Tracks shown in the centre list for the selected section, after the filter.</summary>
    public ObservableCollection<TrackItem> VisibleTracks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsStats), nameof(IsTrackList), nameof(IsPlaylist), nameof(SectionTitle), nameof(SectionIcon))]
    private MusicSection? _selectedSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsStats), nameof(IsTrackList), nameof(IsPlaylist), nameof(SectionTitle), nameof(SectionIcon))]
    private MusicPlaylist? _selectedPlaylist;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _sectionSubtitle = "";
    [ObservableProperty] private TrackItem? _selectedTrack;

    /// <summary>Inline "new playlist / rename" editor.</summary>
    [ObservableProperty] private bool _isEditingPlaylistName;
    [ObservableProperty] private string _playlistNameDraft = "";
    private MusicPlaylist? _renaming;
    private IReadOnlyList<TrackItem>? _pendingPlaylistTracks;

    public MusicSectionKind CurrentKind => SelectedPlaylist != null ? MusicSectionKind.Playlist : SelectedSection?.Kind ?? MusicSectionKind.Home;
    public bool IsHome => CurrentKind == MusicSectionKind.Home;
    public bool IsStats => CurrentKind == MusicSectionKind.Stats;
    public bool IsPlaylist => CurrentKind == MusicSectionKind.Playlist;
    public bool IsTrackList => !IsHome && !IsStats;
    public string SectionTitle => SelectedPlaylist?.Name ?? SelectedSection?.Title ?? "";
    public string SectionIcon => SelectedPlaylist != null ? "" : SelectedSection?.Icon ?? "";
    public bool ListIsEmpty => VisibleTracks.Count == 0;
    public bool LibraryIsEmpty => !Library.LibraryTracks.Any();

    // Home page shelves.
    public ObservableCollection<TrackItem> RecentTracks { get; } = [];
    public ObservableCollection<TrackItem> NewTracks { get; } = [];
    public ObservableCollection<TrackItem> TopTracks { get; } = [];

    [ObservableProperty] private StatsViewModel? _stats;

    private void InitializeLibraryBrowsing()
    {
        VisibleTracks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ListIsEmpty));
        Library.Changed += ScheduleRefresh;
        Library.Tracks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LibraryIsEmpty));
        SelectedSection = Sections[0];
    }

    partial void OnSelectedSectionChanged(MusicSection? value)
    {
        if (value != null) SelectedPlaylist = null;
        Filter = "";
        RefreshVisible();
    }

    partial void OnSelectedPlaylistChanged(MusicPlaylist? value)
    {
        if (value != null) SelectedSection = null;
        else if (SelectedSection == null) SelectedSection = Sections[0];
        Filter = "";
        RefreshVisible();
    }

    partial void OnFilterChanged(string value) => RefreshVisible();

    [RelayCommand]
    private void OpenSection(MusicSection? section)
    {
        if (section != null) SelectedSection = section;
    }

    [RelayCommand]
    private void OpenPlaylist(MusicPlaylist? playlist)
    {
        if (playlist != null) SelectedPlaylist = playlist;
    }

    private void ScheduleRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        _dispatcher.BeginInvoke(() =>
        {
            _refreshPending = false;
            RefreshVisible();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Rebuilds the centre list (and the home shelves) for the current section.</summary>
    public void RefreshVisible()
    {
        IEnumerable<TrackItem> source = CurrentKind switch
        {
            MusicSectionKind.Tracks => Library.LibraryTracks.OrderByDescending(t => t.AddedAt),
            MusicSectionKind.Favorites => Library.Favorites,
            MusicSectionKind.History => Library.RecentlyPlayed(300),
            MusicSectionKind.Playlist when SelectedPlaylist != null => Library.TracksOf(SelectedPlaylist),
            _ => [],
        };
        var list = source.Where(t => MusicLibrary.Matches(t, Filter)).ToList();

        if (!list.SequenceEqual(VisibleTracks))
        {
            VisibleTracks.Clear();
            foreach (var t in list) VisibleTracks.Add(t);
        }

        var duration = Library.DurationOf(list);
        SectionSubtitle = list.Count == 0 ? "" :
            $"{list.Count} morceau{(list.Count > 1 ? "x" : "")}" + (duration.TotalMinutes >= 1
                ? $" · {(duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} h {duration.Minutes:00}" : $"{(int)duration.TotalMinutes} min")}"
                : "");

        if (IsHome) RefreshHome();
        if (IsStats) Stats = new StatsViewModel(Library);
    }

    private void RefreshHome()
    {
        Replace(RecentTracks, Library.RecentlyPlayed(12));
        Replace(NewTracks, Library.RecentlyAdded(12));
        Replace(TopTracks, Library.MostPlayed(12));
    }

    private static void Replace(ObservableCollection<TrackItem> target, IReadOnlyList<TrackItem> items)
    {
        if (items.SequenceEqual(target)) return;
        target.Clear();
        foreach (var t in items) target.Add(t);
    }

    // ---- Playing from the list ---------------------------------------------------------------------------

    /// <summary>Double-click / Enter / ▶ on a row: the visible list becomes the queue, starting at that track.</summary>
    [RelayCommand]
    private void PlayTrack(TrackItem? track)
    {
        if (track == null) return;
        if (track == CurrentTrack && Player.HasTrack)
        {
            PlayPause();
            return;
        }
        var index = VisibleTracks.IndexOf(track);
        if (index < 0)
        {
            PlayNow(track);
            return;
        }
        PlayList(VisibleTracks, index);
    }

    /// <summary>Plays a shelf of the home page (recent, new, most played) from the given track.</summary>
    [RelayCommand]
    private void PlayFromShelf(TrackItem? track)
    {
        if (track == null) return;
        var shelf = new[] { RecentTracks, NewTracks, TopTracks }.FirstOrDefault(s => s.Contains(track)) ?? [track];
        PlayList(shelf, shelf.IndexOf(track));
    }

    [RelayCommand]
    private void PlaySection() => PlayList(VisibleTracks, 0, shuffle: false);

    [RelayCommand]
    private void ShuffleSection() => PlayList(VisibleTracks, -1, shuffle: true);

    [RelayCommand]
    private void QueueSection() => AddToQueue(VisibleTracks.ToList());

    [RelayCommand]
    private void PlayPlaylist(MusicPlaylist? playlist)
    {
        if (playlist != null) PlayList(Library.TracksOf(playlist), 0);
    }

    // ---- Library edits -------------------------------------------------------------------------------------------

    [RelayCommand]
    public void ToggleFavorite(TrackItem? track)
    {
        if (track == null) return;
        Library.ToggleFavorite(track);
        MediaControls?.Update();
    }

    [RelayCommand]
    private void AddToLibrary(TrackItem? track)
    {
        if (track == null) return;
        var t = Library.AddOrGet(track);
        t.IsTransient = false;
        _main.ShowToast($"Ajouté à la bibliothèque : {t.Title}");
        ScheduleRefresh();
    }

    /// <summary>« Retirer » in the list: from the playlist when one is open, otherwise from the library.</summary>
    [RelayCommand]
    private void RemoveTrack(TrackItem? track)
    {
        track ??= SelectedTrack;
        if (track == null) return;
        switch (CurrentKind)
        {
            case MusicSectionKind.Playlist when SelectedPlaylist != null:
                Library.RemoveFromPlaylist(SelectedPlaylist, track);
                break;
            case MusicSectionKind.Favorites:
                if (track.IsFavorite) Library.ToggleFavorite(track);
                break;
            default:
                Library.Remove(track);
                Queue.Remove(track);
                if (track == CurrentTrack) Stop();
                break;
        }
        RefreshVisible();
    }

    [RelayCommand]
    private void DislikeTrack(TrackItem? track)
    {
        if (track == null) return;
        Library.DislikeTrack(track);
        if (!string.IsNullOrWhiteSpace(track.Artist))
            _main.ShowToast($"Noté : l'autoplay ne proposera plus ce morceau.");
        if (track == CurrentTrack) Next();
        Queue.Remove(track);
    }

    [RelayCommand]
    private void DislikeArtist(TrackItem? track)
    {
        if (track?.Artist is not { Length: > 0 } artist) return;
        Library.DislikeArtist(artist);
        _main.ShowToast($"L'autoplay ne proposera plus {artist}.");
    }

    [RelayCommand]
    private void OpenSource(TrackItem? track)
    {
        if (track?.SourceUrl is { } url) WindowsIntegration.Open(url);
        else if (track?.IsDownloaded == true) WindowsIntegration.Open(Path.GetDirectoryName(track.FilePath)!);
    }

    public void MoveInPlaylist(int from, int to)
    {
        if (SelectedPlaylist == null || !string.IsNullOrEmpty(Filter)) return;
        Library.MoveInPlaylist(SelectedPlaylist, from, to);
        RefreshVisible();
    }

    // ---- Playlists -------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void NewPlaylist()
    {
        _renaming = null;
        _pendingPlaylistTracks = null;
        PlaylistNameDraft = "Nouvelle playlist";
        IsEditingPlaylistName = true;
    }

    [RelayCommand]
    private void NewPlaylistWith(TrackItem? track)
    {
        if (track == null) return;
        _renaming = null;
        _pendingPlaylistTracks = [track];
        PlaylistNameDraft = "Nouvelle playlist";
        IsEditingPlaylistName = true;
    }

    [RelayCommand]
    private void RenamePlaylist(MusicPlaylist? playlist)
    {
        playlist ??= SelectedPlaylist;
        if (playlist == null) return;
        _renaming = playlist;
        PlaylistNameDraft = playlist.Name;
        IsEditingPlaylistName = true;
    }

    [RelayCommand]
    private void ConfirmPlaylistName()
    {
        var name = PlaylistNameDraft.Trim();
        IsEditingPlaylistName = false;
        if (name.Length == 0) return;
        if (_renaming != null)
        {
            Library.RenamePlaylist(_renaming, name);
            OnPropertyChanged(nameof(SectionTitle));
        }
        else
        {
            var playlist = Library.CreatePlaylist(name, _pendingPlaylistTracks);
            if (_pendingPlaylistTracks is { Count: > 0 })
                _main.ShowToast($"Ajouté à « {playlist.Name} »");
            else
                SelectedPlaylist = playlist;
        }
        _renaming = null;
        _pendingPlaylistTracks = null;
    }

    [RelayCommand]
    private void CancelPlaylistName()
    {
        IsEditingPlaylistName = false;
        _renaming = null;
        _pendingPlaylistTracks = null;
    }

    [RelayCommand]
    private void DeletePlaylist(MusicPlaylist? playlist)
    {
        playlist ??= SelectedPlaylist;
        if (playlist == null) return;
        var answer = MessageBox.Show($"Supprimer la playlist « {playlist.Name} » ?\n\nLes morceaux restent dans votre bibliothèque.",
            "SpokaSoundBoard", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        Library.DeletePlaylist(playlist);
        if (SelectedPlaylist == playlist) SelectedPlaylist = null;
    }

    /// <summary>Context menu « Ajouter à la playlist » (parameter: object[] { track, playlist }).</summary>
    [RelayCommand]
    private void AddToPlaylist(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } args || args[0] is not TrackItem track || args[1] is not MusicPlaylist playlist) return;
        var added = Library.AddToPlaylist(playlist, [track]);
        _main.ShowToast(added > 0 ? $"Ajouté à « {playlist.Name} »" : $"Déjà dans « {playlist.Name} »");
        if (SelectedPlaylist == playlist) RefreshVisible();
    }

    public void AddTracksToPlaylist(MusicPlaylist playlist, IEnumerable<TrackItem> tracks)
    {
        var added = Library.AddToPlaylist(playlist, tracks);
        _main.ShowToast($"{added} morceau(x) ajouté(s) à « {playlist.Name} »");
        if (SelectedPlaylist == playlist) RefreshVisible();
    }

    [RelayCommand]
    private void ExportPlaylist(MusicPlaylist? playlist)
    {
        playlist ??= SelectedPlaylist;
        if (playlist == null) return;
        var dialog = new SaveFileDialog { Title = "Exporter la playlist", FileName = playlist.Name, Filter = "Playlist M3U (*.m3u8)|*.m3u8" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var lines = new List<string> { "#EXTM3U", $"#PLAYLIST:{playlist.Name}" };
            foreach (var t in Library.TracksOf(playlist))
            {
                lines.Add($"#EXTINF:{(int)(t.DurationSeconds ?? -1)},{(t.Artist != null ? t.Artist + " - " : "")}{t.Title}");
                lines.Add(t.IsDownloaded ? t.FilePath : t.SourceUrl ?? t.SearchQuery ?? t.Title);
            }
            File.WriteAllLines(dialog.FileName, lines);
            _main.ShowToast("Playlist exportée");
        }
        catch (Exception ex)
        {
            _main.ShowToast("Export impossible : " + ex.Message);
        }
    }

    // ---- Adding local files -----------------------------------------------------------------------------------------

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
        if (dialog.ShowDialog() == true) AddPaths([dialog.FolderName]);
    }

    /// <summary>Adds files / folders (drag-and-drop or dialogs) to the library, and to the open playlist.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var files = new List<string>();
        try
        {
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                    files.AddRange(Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories).Where(AudioFileLoader.IsSupported)
                        .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
                else if (AudioFileLoader.IsSupported(p))
                    files.Add(p);
            }
        }
        catch (Exception ex)
        {
            _main.ShowToast("Lecture du dossier impossible : " + ex.Message);
        }

        var added = new List<TrackItem>();
        foreach (var file in files)
        {
            var before = Library.Tracks.Count;
            var track = Library.AddOrGet(new TrackItem { FilePath = file, State = TrackState.Ready });
            track.IsTransient = false;
            added.Add(track);
            if (Library.Tracks.Count > before)
            {
                AnalyzeLoudness(track);
                Metadata.Enqueue(track);
            }
        }
        if (added.Count == 0) return;
        if (SelectedPlaylist != null) Library.AddToPlaylist(SelectedPlaylist, added);
        _main.ShowToast(added.Count == 1 ? "1 musique ajoutée" : $"{added.Count} musiques ajoutées");
        if (CurrentKind == MusicSectionKind.Home) SelectedSection = Sections[1];
        RefreshVisible();
    }

    private LocalMetadataService Metadata => _metadata ??= new LocalMetadataService(Path.Combine(_main.DataDirectory, "Covers"), () =>
    {
        ScheduleLibrarySave();
        ScheduleRefresh();
    });

    /// <summary>Reads tags / covers of local files that have none yet (first start after the migration, new files).</summary>
    public void ReadMissingMetadata()
    {
        foreach (var t in Library.Tracks.Where(t => t.IsDownloaded && t.SourceUrl == null && t.Artist == null && t.ThumbnailUrl == null))
            Metadata.Enqueue(t);
    }
}
