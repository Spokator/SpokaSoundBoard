using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Music;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.ViewModels.Music;

/// <summary>
/// « Morceau » panel of the music page: fix a track's title and artist, or pick another video when the automatic
/// search found the wrong song (a cover, a live, a remix…).
/// </summary>
public sealed partial class TrackInfoViewModel(MusicViewModel music) : ObservableObject
{
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack), nameof(CanChangeVersion))]
    private TrackItem? _track;

    [ObservableProperty] private string _titleDraft = "";
    [ObservableProperty] private string _artistDraft = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _status = "";

    public bool HasTrack => Track != null;

    /// <summary>Only songs that come from the web can be swapped for another video (local files stay as they are).</summary>
    public bool CanChangeVersion => Track != null && (Track.HasSource || Track.SearchQuery != null);

    public ObservableCollection<MusicResultViewModel> Versions { get; } = [];

    public void Open(TrackItem track)
    {
        _searchCts?.Cancel();
        Track = track;
        TitleDraft = track.Title;
        ArtistDraft = track.Artist ?? "";
        Versions.Clear();
        Status = CanChangeVersion
            ? "Mauvaise version (reprise, live, remix…) ? Cherchez la bonne ci-dessous."
            : "Fichier de votre PC : seuls le titre et l'artiste peuvent être modifiés.";
        if (CanChangeVersion) _ = SearchVersions();
    }

    [RelayCommand]
    private void SaveInfo()
    {
        if (Track == null) return;
        music.Library.EditInfo(Track, TitleDraft, ArtistDraft);
        TitleDraft = Track.Title;
        music.Main.ShowToast($"« {Track.Title} » mis à jour.");
    }

    [RelayCommand]
    private async Task SearchVersions()
    {
        if (Track == null) return;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var query = string.IsNullOrWhiteSpace(ArtistDraft) ? TitleDraft : $"{ArtistDraft} {TitleDraft}";
        IsSearching = true;
        Status = "Recherche des versions…";
        try
        {
            var results = await music.Main.Search.SearchAsync(SearchSource.YouTube, query, 10, ct);
            if (ct.IsCancellationRequested) return;
            Versions.Clear();
            var current = Track.SourceUrl == null ? null : MusicLibrary.SourceKey(Track.SourceUrl);
            foreach (var r in results)
                Versions.Add(new MusicResultViewModel(r, current != null && MusicLibrary.SourceKey(r.PageUrl) == current ? "Version actuelle" : null));
            Status = Versions.Count == 0 ? "Aucune version trouvée : modifiez le titre ou l'artiste puis cherchez à nouveau."
                : "Choisissez la bonne version (▶ pour l'écouter dans votre casque).";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = "Recherche impossible : " + Core.FriendlyErrors.Describe(ex);
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsSearching = false;
        }
    }

    [RelayCommand]
    private void ChooseVersion(MusicResultViewModel? version)
    {
        if (Track is not { } track || version == null) return;
        var oldFile = music.Library.ChangeSource(track, version.Result.PageUrl, version.Result.Duration?.TotalSeconds, version.Result.ThumbnailUrl);
        DeleteIfUnused(oldFile);
        foreach (var v in Versions) v.IsChosen = ReferenceEquals(v, version);
        music.Main.ShowToast($"Nouvelle version choisie pour « {track.Title} » : elle sera téléchargée à la prochaine écoute.");
        music.RestartIfCurrent(track);
    }

    /// <summary>The old download is useless now, unless another track still points at it.</summary>
    private void DeleteIfUnused(string? file)
    {
        if (file == null) return;
        var cache = Path.GetFullPath(music.Downloader.CacheDirectory) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(file).StartsWith(cache, StringComparison.OrdinalIgnoreCase)) return;
        if (music.Library.Tracks.Any(t => string.Equals(t.FilePath, file, StringComparison.OrdinalIgnoreCase))) return;
        if (music.Main.Settings.Pads.Any(p => string.Equals(p.FilePath, file, StringComparison.OrdinalIgnoreCase))) return;
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use: the cache cleanup takes it later */ }
    }
}
