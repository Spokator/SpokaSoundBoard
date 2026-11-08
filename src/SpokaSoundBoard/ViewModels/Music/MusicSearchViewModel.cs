using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.ViewModels.Music;

/// <summary>A web search result shown in the music search panel.</summary>
public sealed partial class MusicResultViewModel(SearchResult result, string? reason = null) : ObservableObject
{
    public SearchResult Result { get; } = result;
    public string Title => Result.Title;
    public string Subtitle => Result.Artist ?? Result.Subtitle;
    public string? Reason { get; } = reason;
    public string? ThumbnailUrl => Result.ThumbnailUrl;
    public bool HasThumbnail => !string.IsNullOrEmpty(Result.ThumbnailUrl);
    public string DurationText => Result.Duration is { } d ? MusicViewModel.Format(d) : "";

    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private bool _isPreviewLoading;
    [ObservableProperty] private bool _isAdded;

    /// <summary>The library / queue entry created from this result (reused by every action).</summary>
    public TrackItem? Track { get; set; }
}

/// <summary>Search bar of the music page: YouTube search with suggestions, links and playlist imports.</summary>
public partial class MusicSearchViewModel : ObservableObject
{
    private const string PreviewFolder = "Apercus";
    private readonly MusicViewModel _music;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _suggestCts;
    private CancellationTokenSource? _importCts;
    private MusicResultViewModel? _previewItem;
    private bool _suppressSuggestions;

    public MusicSearchViewModel(MusicViewModel music)
    {
        _music = music;
        Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResults));
        music.Main.Engine.Preview.TrackEnded += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_previewItem != null) _previewItem.IsPreviewing = false;
            _previewItem = null;
        });
    }

    public ObservableCollection<MusicResultViewModel> Results { get; } = [];
    public ObservableCollection<string> Suggestions { get; } = [];
    public bool HasResults => Results.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUrl), nameof(SubmitLabel))]
    private string _query = "";

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _showSuggestions;
    [ObservableProperty] private string? _resultsTitle;

    public bool IsUrl => MediaDownloader.LooksLikeUrl(Query);
    public string SubmitLabel => !IsUrl ? "Rechercher" : PlaylistImporter.CanImport(Query.Trim()) ? "Importer la playlist" : "Ajouter";

    partial void OnQueryChanged(string value)
    {
        if (_suppressSuggestions) return;
        _ = SuggestAsync(value);
    }

    private async Task SuggestAsync(string prefix)
    {
        _suggestCts?.Cancel();
        _suggestCts = new CancellationTokenSource();
        var ct = _suggestCts.Token;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Trim().Length < 2 || MediaDownloader.LooksLikeUrl(prefix))
        {
            Suggestions.Clear();
            ShowSuggestions = false;
            return;
        }
        try
        {
            await Task.Delay(180, ct);
            var list = await MediaSearch.SuggestAsync(prefix, ct);
            if (ct.IsCancellationRequested) return;
            Suggestions.Clear();
            foreach (var s in list.Take(8)) Suggestions.Add(s);
            ShowSuggestions = Suggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private void Toggle()
    {
        IsOpen = !IsOpen;
        if (!IsOpen) StopPreview();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        ShowSuggestions = false;
        StopPreview();
    }

    [RelayCommand]
    private Task PickSuggestion(string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion)) return Task.CompletedTask;
        _suppressSuggestions = true;
        Query = suggestion;
        _suppressSuggestions = false;
        return SearchAsync(suggestion);
    }

    [RelayCommand]
    private Task Submit()
    {
        ShowSuggestions = false;
        var q = Query.Trim();
        if (q.Length == 0) return Task.CompletedTask;
        if (!MediaDownloader.LooksLikeUrl(q)) return SearchAsync(q);
        return PlaylistImporter.CanImport(q) ? ImportPlaylistAsync(q) : AddLinkAsync(q);
    }

    /// <summary>Any link dropped on the page or pasted in the bar.</summary>
    public Task HandleUrlAsync(string url)
    {
        IsOpen = true;
        _suppressSuggestions = true;
        Query = url;
        _suppressSuggestions = false;
        return Submit();
    }

    public async Task SearchAsync(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        IsOpen = true;
        IsBusy = true;
        HasError = false;
        ShowSuggestions = false;
        ResultsTitle = $"Résultats pour « {query} »";
        Status = $"Recherche de « {query} »…";
        StopPreview();
        Results.Clear();
        try
        {
            var results = await _music.Main.Search.SearchAsync(SearchSource.YouTube, query, 15, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var r in results) Results.Add(new MusicResultViewModel(r));
            Status = results.Count == 0 ? "Aucun résultat. Essayez d'autres mots." : null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HasError = true;
            Status = ex is DownloadException ? ex.Message : "Recherche impossible : " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsBusy = false;
        }
    }

    /// <summary>Shows recommendations in the results area (« Pour vous », « Similaires »).</summary>
    public void ShowRecommendations(string title, IEnumerable<Recommendation> recommendations)
    {
        IsOpen = true;
        StopPreview();
        Results.Clear();
        ResultsTitle = title;
        foreach (var r in recommendations) Results.Add(new MusicResultViewModel(r.Result, r.Reason));
        Status = Results.Count == 0 ? "Pas encore de recommandation : écoutez quelques morceaux d'abord." : null;
    }

    private TrackItem TrackFor(MusicResultViewModel item)
    {
        if (item.Track != null) return item.Track;
        var r = item.Result;
        var existing = _music.Library.FindExisting(null, r.PageUrl);
        item.Track = existing ?? new TrackItem
        {
            DisplayTitle = r.Title,
            Artist = r.Artist,
            SourceUrl = r.PageUrl,
            ThumbnailUrl = r.ThumbnailUrl,
            DurationSeconds = r.Duration?.TotalSeconds,
            State = TrackState.Remote,
            IsTransient = true,
        };
        if (existing == null && item.PreviewLoaded is { } file && File.Exists(file))
        {
            item.Track.FilePath = MoveFromPreview(file);
            item.Track.State = TrackState.Ready;
        }
        return item.Track;
    }

    [RelayCommand]
    private void Play(MusicResultViewModel? item)
    {
        if (item == null) return;
        StopPreview();
        _music.PlayNowCommand.Execute(TrackFor(item));
    }

    [RelayCommand]
    private void PlayNext(MusicResultViewModel? item)
    {
        if (item != null) _music.PlayNextInQueueCommand.Execute(TrackFor(item));
    }

    [RelayCommand]
    private void Queue(MusicResultViewModel? item)
    {
        if (item == null) return;
        var t = TrackFor(item);
        _music.AddToQueueCommand.Execute(t);
        _ = _music.EnsureLocalAsync(t);
    }

    [RelayCommand]
    private void Add(MusicResultViewModel? item)
    {
        if (item == null || item.IsAdded) return;
        var track = _music.Library.AddOrGet(TrackFor(item));
        track.IsTransient = false;
        item.Track = track;
        item.IsAdded = true;
        if (_music.SelectedPlaylist is { } playlist) _music.AddTracksToPlaylist(playlist, [track]);
        else _music.Main.ShowToast($"Ajouté à la bibliothèque : {track.Title}");
        _music.RefreshVisible();
        // Download right away so it plays instantly later.
        _ = _music.EnsureLocalAsync(track);
    }

    /// <summary>Ajouter à une playlist depuis un résultat (parameter: object[] { result, playlist }).</summary>
    [RelayCommand]
    private void AddToPlaylist(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } args || args[0] is not MusicResultViewModel item || args[1] is not Core.Models.MusicPlaylist playlist) return;
        var track = TrackFor(item);
        _music.AddTracksToPlaylist(playlist, [track]);
        item.IsAdded = true;
        _ = _music.EnsureLocalAsync(track);
    }

    [RelayCommand]
    private void Similar(MusicResultViewModel? item)
    {
        if (item != null) _ = _music.ShowSimilarAsync(item.Result.PageUrl, item.Title);
    }

    [RelayCommand]
    private void OpenResult(MusicResultViewModel? item)
    {
        if (item != null) Services.WindowsIntegration.Open(item.Result.PageUrl);
    }

    // ---- Preview in the headphones ------------------------------------------------------------------------

    [RelayCommand]
    private async Task Preview(MusicResultViewModel? item)
    {
        if (item == null) return;
        if (item.IsPreviewing)
        {
            StopPreview();
            return;
        }
        StopPreview();
        if (_music.Main.Settings.MonitorDeviceId == null)
        {
            _music.Main.ShowToast("Choisissez un « Retour casque » dans Configuration pour écouter les aperçus.");
            return;
        }
        item.IsPreviewLoading = true;
        try
        {
            if (item.Track is { IsDownloaded: true } t) item.PreviewLoaded = t.FilePath;
            if (item.PreviewLoaded == null || !File.Exists(item.PreviewLoaded))
                item.PreviewLoaded = (await _music.Downloader.DownloadTrackAsync(item.Result.PageUrl, PreviewFolder, null, CancellationToken.None)).FilePath;
            if (_previewItem != null) StopPreview();
            _music.Main.Engine.Preview.Load(item.PreviewLoaded);
            // Start a little into the track: intros are often silent.
            var d = _music.Main.Engine.Preview.Duration;
            if (d > TimeSpan.FromSeconds(60)) _music.Main.Engine.Preview.Seek(TimeSpan.FromSeconds(d.TotalSeconds * 0.3));
            _previewItem = item;
            item.IsPreviewing = true;
        }
        catch (Exception ex)
        {
            HasError = true;
            Status = "Aperçu impossible : " + ex.Message;
        }
        finally
        {
            item.IsPreviewLoading = false;
        }
    }

    public void StopPreview()
    {
        if (_previewItem == null) return;
        _music.Main.Engine.Preview.Stop();
        _previewItem.IsPreviewing = false;
        _previewItem = null;
    }

    private string MoveFromPreview(string previewFile)
    {
        var dir = Path.Combine(_music.Downloader.CacheDirectory, MusicViewModel.DownloadFolder);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(previewFile));
        try
        {
            if (!File.Exists(target)) File.Move(previewFile, target);
            return target;
        }
        catch
        {
            return previewFile;
        }
    }

    // ---- Links and playlist imports ---------------------------------------------------------------------------

    private async Task AddLinkAsync(string url)
    {
        IsBusy = true;
        HasError = false;
        Status = "Ajout du morceau…";
        try
        {
            var track = _music.Library.AddOrGet(new TrackItem { SourceUrl = url, State = TrackState.Remote });
            track.IsTransient = false;
            if (_music.SelectedPlaylist is { } playlist) _music.Library.AddToPlaylist(playlist, [track]);
            var ok = await _music.EnsureLocalAsync(track);
            if (!ok)
            {
                HasError = true;
                Status = $"Import impossible : {track.StateMessage}";
                _music.Library.Remove(track);
                return;
            }
            Status = $"Ajouté : {track.Title}";
            _suppressSuggestions = true;
            Query = "";
            _suppressSuggestions = false;
            _music.RefreshVisible();
            if (!_music.Player.HasTrack) _music.PlayNowCommand.Execute(track);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportPlaylistAsync(string url)
    {
        _importCts?.Cancel();
        _importCts = new CancellationTokenSource();
        IsBusy = true;
        HasError = false;
        Status = "Lecture de la playlist…";
        try
        {
            var imported = await _music.Importer.ImportAsync(url, _importCts.Token);
            if (imported.Tracks.Count == 0)
            {
                HasError = true;
                Status = "Cette playlist est vide ou privée.";
                return;
            }
            var tracks = imported.Tracks.Select(t => new TrackItem
            {
                DisplayTitle = t.Title,
                Artist = t.Artist,
                SourceUrl = t.Url,
                SearchQuery = string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Artist} - {t.Title}",
                ThumbnailUrl = t.ThumbnailUrl,
                DurationSeconds = t.Duration?.TotalSeconds,
                State = TrackState.Remote,
            }).ToList();
            var playlist = _music.Library.CreatePlaylist(imported.Name, tracks, imported.SourceUrl);
            Status = $"Playlist « {playlist.Name} » importée : {playlist.TrackIds.Count} morceaux. Ils seront téléchargés au moment de les écouter.";
            _suppressSuggestions = true;
            Query = "";
            _suppressSuggestions = false;
            _music.SelectedPlaylist = playlist;
        }
        catch (OperationCanceledException)
        {
            Status = "Import annulé";
        }
        catch (Exception ex)
        {
            HasError = true;
            Status = ex is DownloadException ? ex.Message : "Import impossible : " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _importCts?.Cancel();
}

public sealed partial class MusicResultViewModel
{
    /// <summary>File downloaded for the headphone preview, reused when the result is played or added.</summary>
    public string? PreviewLoaded { get; set; }
}
