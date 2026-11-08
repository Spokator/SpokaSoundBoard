using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.ViewModels.Music;

public partial class MusicViewModel
{
    private TrackResolver? _resolver;
    private PlaylistImporter? _importer;
    private RecommendationService? _recommendations;

    internal TrackResolver Resolver => _resolver ??= new TrackResolver(Downloader);
    internal PlaylistImporter Importer => _importer ??= new PlaylistImporter(Downloader);
    internal RecommendationService Recommendations => _recommendations ??= new RecommendationService(Downloader);

    [ObservableProperty] private bool _isLoadingForYou;

    /// <summary>
    /// Autoplay: appends a few tracks similar to the last one (YouTube mix), skipping what was played recently
    /// and what the user disliked. Returns true when something was added.
    /// </summary>
    private async Task<bool> AppendAutoplayAsync()
    {
        if (_fetchingAutoplay) return false;
        var seed = Queue.Items.LastOrDefault() ?? CurrentTrack ?? Library.RecentlyPlayed(1).FirstOrDefault();
        if (seed == null) return false;
        _fetchingAutoplay = true;
        try
        {
            var exclude = RecentUrls();
            var related = await Recommendations.GetRelatedAsync(seed.SourceUrl ?? SeedQuery(seed), 5, exclude,
                Library.Data.DislikedArtists, CancellationToken.None);
            var tracks = related.Where(r => !Library.IsDisliked(r.Result.Artist, r.Result.PageUrl)).Select(r => FromResult(r.Result)).ToList();
            if (tracks.Count == 0) return false;
            Queue.Enqueue(tracks);
            _main.ShowToast($"Autoplay : {tracks.Count} morceaux similaires ajoutés à la file");
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _fetchingAutoplay = false;
        }
    }

    /// <summary>« Pour vous » on the home page.</summary>
    [RelayCommand]
    private async Task LoadForYou()
    {
        if (IsLoadingForYou) return;
        IsLoadingForYou = true;
        try
        {
            var favorites = Library.Favorites.ToList();
            var list = await Recommendations.GetForYouAsync(Library.History.ToList(), favorites, 20, RecentUrls(),
                Library.Data.DislikedArtists, CancellationToken.None);
            Search.ShowRecommendations("Pour vous", list);
        }
        catch (Exception ex)
        {
            _main.ShowToast("Recommandations indisponibles : " + (ex is DownloadException ? ex.Message : ex.Message));
        }
        finally
        {
            IsLoadingForYou = false;
        }
    }

    [RelayCommand]
    private Task ShowSimilar(TrackItem? track) =>
        track == null ? Task.CompletedTask : ShowSimilarAsync(track.SourceUrl ?? SeedQuery(track), track.Title);

    public async Task ShowSimilarAsync(string seed, string title)
    {
        try
        {
            Search.IsBusy = true;
            Search.IsOpen = true;
            Search.Status = "Recherche de morceaux similaires…";
            var list = await Recommendations.GetRelatedAsync(seed, 20, RecentUrls(), Library.Data.DislikedArtists, CancellationToken.None);
            Search.ShowRecommendations($"Similaires à « {title} »", list);
        }
        catch (Exception ex)
        {
            Search.HasError = true;
            Search.Status = "Recherche impossible : " + ex.Message;
        }
        finally
        {
            Search.IsBusy = false;
        }
    }

    /// <summary>Starts a "radio" from a track: the track, then an endless stream of similar music.</summary>
    [RelayCommand]
    private async Task StartRadio(TrackItem? track)
    {
        if (track == null) return;
        Queue.Replace([track]);
        _ = StartAsync(track);
        var previous = Settings.Autoplay;
        Settings.Autoplay = true;
        if (!previous) _main.ShowToast("Autoplay activé pour la radio");
        await AppendAutoplayAsync();
    }

    private TrackItem FromResult(SearchResult r) =>
        Library.FindExisting(null, r.PageUrl) ?? new TrackItem
        {
            DisplayTitle = r.Title,
            Artist = r.Artist,
            SourceUrl = r.PageUrl,
            ThumbnailUrl = r.ThumbnailUrl,
            DurationSeconds = r.Duration?.TotalSeconds,
            State = TrackState.Remote,
            IsTransient = true,
        };

    private static string SeedQuery(TrackItem t) => string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Artist} - {t.Title}";

    /// <summary>Links played in the last 7 days or already queued (not proposed again).</summary>
    private HashSet<string> RecentUrls()
    {
        var limit = DateTime.Now.AddDays(-7);
        var urls = Library.History.Where(h => h.PlayedAt >= limit && h.SourceUrl != null).Select(h => h.SourceUrl!).ToHashSet();
        foreach (var t in Queue.Items)
            if (t.SourceUrl != null) urls.Add(t.SourceUrl);
        // Dislikes are stored as source keys ("yt:<id>"): give the recommender real links.
        foreach (var key in Library.Data.DislikedUrls)
            urls.Add(key.StartsWith("yt:", StringComparison.Ordinal) ? "https://www.youtube.com/watch?v=" + key[3..] : "https://" + key);
        return urls;
    }
}
