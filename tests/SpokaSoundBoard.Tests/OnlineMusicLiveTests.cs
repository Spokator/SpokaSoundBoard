using System.Diagnostics;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Tests;

/// <summary>Real network tests of the online music services; only run with SPOKA_ONLINE_TESTS=1.</summary>
[Collection("Online")]
public class OnlineMusicLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") == "1";

    private static MediaDownloader CreateDownloader()
    {
        // Reuse the app's own yt-dlp when it is installed, to avoid a download.
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpokaSoundBoard", "tools");
        return new MediaDownloader(Path.Combine(Path.GetTempPath(), "spoka_online_music_test"),
            File.Exists(Path.Combine(installed, "yt-dlp.exe")) ? installed : null) { PackagedOverride = false };
    }

    [Fact]
    public async Task Spotify_ImportsPublicPlaylist_Online()
    {
        if (!Enabled) return;
        var playlist = await new PlaylistImporter(CreateDownloader()).ImportAsync("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(playlist.Name));
        Assert.True(playlist.Tracks.Count >= 10);
        Assert.All(playlist.Tracks, t => Assert.False(string.IsNullOrWhiteSpace(t.Artist)));
        Assert.All(playlist.Tracks, t => Assert.NotNull(t.Duration));
    }

    [Fact]
    public async Task Deezer_ImportsAlbum_Online()
    {
        if (!Enabled) return;
        var album = await new PlaylistImporter(CreateDownloader()).ImportAsync("https://www.deezer.com/fr/album/302127", CancellationToken.None);
        Assert.Equal("Discovery", album.Name);
        Assert.Equal(14, album.Tracks.Count);
        Assert.Equal("Daft Punk", album.Tracks[0].Artist);
    }

    [Fact]
    public async Task Apple_ImportsAlbum_Online()
    {
        if (!Enabled) return;
        var album = await new PlaylistImporter(CreateDownloader()).ImportAsync("https://music.apple.com/fr/album/random-access-memories/617154241", CancellationToken.None);
        Assert.Contains("Random Access Memories", album.Name);
        Assert.True(album.Tracks.Count >= 10);
        Assert.Contains(album.Tracks, t => t.Title.StartsWith("Get Lucky", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task YouTube_ImportsMixAndRecommendsRelated_Online()
    {
        if (!Enabled) return;
        var downloader = CreateDownloader();
        var mix = await new PlaylistImporter(downloader).ImportAsync("https://www.youtube.com/watch?v=FGBhQbmPwH8&list=RDFGBhQbmPwH8", CancellationToken.None);
        Assert.InRange(mix.Tracks.Count, 5, 50);
        Assert.All(mix.Tracks, t => Assert.StartsWith("https://www.youtube.com/watch?v=", t.Url));

        var related = await new RecommendationService(downloader).GetRelatedAsync("https://www.youtube.com/watch?v=FGBhQbmPwH8", 10, [], ["Daft Punk"], CancellationToken.None);
        Assert.True(related.Count >= 3);
        Assert.All(related, r => Assert.DoesNotContain("daftpunk", ArtistTitle.ArtistKeys(r.Result.Title, r.Result.Subtitle)));
        Assert.All(related, r => Assert.StartsWith("Parce que vous écoutez", r.Reason));
    }

    [Fact]
    public async Task Resolver_FindsSpotifyTrackOnYouTube_Online()
    {
        if (!Enabled) return;
        var downloader = CreateDownloader();
        var result = await new TrackResolver(downloader).ResolveAsync("Daft Punk", "One More Time", TimeSpan.FromSeconds(320), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Contains("One More Time", result.Title, StringComparison.OrdinalIgnoreCase);

        var soundCloud = await downloader.SearchSoundCloudAsync("daft punk one more time", 5, CancellationToken.None);
        Assert.NotEmpty(soundCloud);
        Assert.All(soundCloud, r => Assert.Equal(SearchSource.SoundCloud, r.Source));
    }

    [Fact]
    public async Task Lyrics_AndSuggestions_Online()
    {
        if (!Enabled) return;
        var dir = Path.Combine(Path.GetTempPath(), "spoka_online_lyrics_" + Guid.NewGuid().ToString("N"));
        var lyrics = await new LyricsService(dir).GetAsync("Daft Punk - One More Time (Official Video)", "Daft Punk", TimeSpan.FromSeconds(320), CancellationToken.None);
        Assert.NotNull(lyrics);
        Assert.True(lyrics.HasSynced || !string.IsNullOrEmpty(lyrics.Plain));

        var suggestions = await MediaSearch.SuggestAsync("daft pun", CancellationToken.None);
        Assert.NotEmpty(suggestions);
    }

    [Fact]
    public async Task DownloadTrack_ReturnsMetadataThenUsesCache_Online()
    {
        if (!Enabled) return;
        var downloader = CreateDownloader();
        const string url = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
        var media = await downloader.DownloadTrackAsync(url, "Musique", null, CancellationToken.None);
        Assert.True(File.Exists(media.FilePath));
        Assert.Equal("https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg", media.ThumbnailUrl);
        Assert.InRange(media.DurationSeconds ?? 0, 15, 25);
        Assert.False(string.IsNullOrWhiteSpace(media.Artist));

        var watch = Stopwatch.StartNew();
        var again = await downloader.DownloadTrackAsync("https://youtu.be/jNQXAC9IVRw", "Autre", null, CancellationToken.None);
        Assert.Equal(media.FilePath, again.FilePath);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }
}
