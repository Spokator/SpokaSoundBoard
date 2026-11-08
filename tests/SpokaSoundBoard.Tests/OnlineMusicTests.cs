using System.Net;
using System.Text.Json;
using NAudio.Wave;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Tests;

/// <summary>Offline tests of the online music services (parsers and pure functions).</summary>
public class OnlineMusicTests
{
    private static SearchResult Yt(string title, string channel, int seconds, long? views = null, string id = "aaaaaaaaaaa", bool live = false) =>
        new(title, channel, $"https://www.youtube.com/watch?v={id}", null, seconds > 0 ? TimeSpan.FromSeconds(seconds) : null,
            null, SearchSource.YouTube, null, ArtistTitle.Split(title, channel).Artist, views, live);

    // ───────────── ArtistTitle ─────────────

    [Theory]
    [InlineData("Booba - Mona Lisa (Clip officiel)", "Booba", "Booba", "Mona Lisa")]
    [InlineData("Daft Punk - One More Time (Official Video) [Lyrics] ft. Romanthony", "Daft Punk", "Daft Punk", "One More Time")]
    [InlineData("Mona Lisa", "Booba - Topic", "Booba", "Mona Lisa")]
    [InlineData("Bohemian Rhapsody (Remastered 2011)", "Queen Official", "Queen", "Bohemian Rhapsody")]
    [InlineData("Booba feat. Damso - Pitbull", null, "Booba", "Pitbull")]
    [InlineData("Get Lucky - Daft Punk", "Daft Punk", "Daft Punk", "Get Lucky")]
    [InlineData("Stromae - Alors on danse - Clip Officiel", "StromaeVEVO", "Stromae", "Alors on danse")]
    [InlineData("Angèle - \"Balance ton quoi\" (Vidéo officielle)", null, "Angèle", "Balance ton quoi")]
    [InlineData("Céline Dion - Pour que tu m'aimes encore | Lyrics", null, "Céline Dion", "Pour que tu m'aimes encore")]
    public void ArtistTitle_SplitsWebTitles(string raw, string? channel, string? artist, string title)
    {
        var (a, t) = ArtistTitle.Split(raw, channel);
        Assert.Equal(artist, a);
        Assert.Equal(title, t);
    }

    [Fact]
    public void ArtistTitle_KeepsRealVersionsAndIgnoresGenericChannels()
    {
        Assert.Equal("Song (Remix)", ArtistTitle.Split("Artist - Song (Remix)", null).Title);
        Assert.Equal("Song (Live at Wembley)", ArtistTitle.Split("Artist - Song (Live at Wembley)", null).Title);
        Assert.Null(ArtistTitle.Split("Vine boom", "YouTube").Artist);
        Assert.Null(ArtistTitle.Split("Vine boom", null).Artist);
        Assert.Equal("Sans titre", ArtistTitle.Split("", null).Title);
    }

    [Fact]
    public void ArtistTitle_NormalizesAndBuildsKeys()
    {
        Assert.Equal("pour que tu m aimes encore", ArtistTitle.Normalize("Pour que tu m'aimes encore !"));
        Assert.Equal("celine dion", ArtistTitle.Normalize("  Céline   DION "));
        Assert.Equal("", ArtistTitle.Normalize(null));
        Assert.Equal("booba", ArtistTitle.ArtistKey("BoobaOfficiel"));
        Assert.Equal("booba", ArtistTitle.ArtistKey("Booba - Topic"));
        Assert.Equal("booba", ArtistTitle.ArtistKey("BoobaVEVO"));
        Assert.Equal("daftpunk", ArtistTitle.ArtistKey("Daft Punk"));
        Assert.Equal("Booba", ArtistTitle.PrimaryArtist("Booba & Damso"));
        Assert.Equal("Pitbull", ArtistTitle.PrimaryArtist("Pitbull, Ne-Yo"));
        Assert.Equal("Daft Punk", ArtistTitle.CleanChannel("Daft Punk - Topic"));
        Assert.Contains("booba", ArtistTitle.ArtistKeys("Booba feat. Damso - Pitbull", "Rap FR"));
        Assert.Contains("rapfr", ArtistTitle.ArtistKeys("Booba feat. Damso - Pitbull", "Rap FR"));
    }

    // ───────────── SearchRanker ─────────────

    [Fact]
    public void Ranker_PrefersOfficialAudioOverReactionsLyricsAndLives()
    {
        var results = new[]
        {
            Yt("JE RÉAGIS À BOOBA - MONA LISA (reaction)", "Le Rappeur Fou", 900, 2_000_000, "aaaaaaaaaa1"),
            Yt("Booba - Mona Lisa (Paroles/Lyrics)", "Rap Lyrics FR", 220, 5_000_000, "aaaaaaaaaa2"),
            Yt("Booba - Mona Lisa (Live à Bercy)", "Booba", 260, 1_000_000, "aaaaaaaaaa3"),
            Yt("Mona Lisa", "Booba - Topic", 214, 3_000_000, "aaaaaaaaaa4"),
        };
        var ranked = SearchRanker.Rank("booba mona lisa", results);
        Assert.Equal("aaaaaaaaaa4", RecommendationService.YouTubeId(ranked[0].PageUrl));
        Assert.Equal("aaaaaaaaaa1", RecommendationService.YouTubeId(ranked[^1].PageUrl));
    }

    [Fact]
    public void Ranker_TurnsRequestedFormatIntoBonus()
    {
        var results = new[]
        {
            Yt("Booba - Mona Lisa", "Booba", 214, 3_000_000, "aaaaaaaaaa1"),
            Yt("Booba - Mona Lisa (Live à Bercy)", "Booba", 260, 1_000_000, "aaaaaaaaaa2"),
        };
        Assert.Equal("aaaaaaaaaa1", RecommendationService.YouTubeId(SearchRanker.Rank("booba mona lisa", results)[0].PageUrl));
        Assert.Equal("aaaaaaaaaa2", RecommendationService.YouTubeId(SearchRanker.Rank("booba mona lisa live", results)[0].PageUrl));
    }

    [Fact]
    public void Ranker_UsesExpectedDurationAndPenalizesCompilations()
    {
        var results = new[]
        {
            Yt("Daft Punk - One More Time", "Uploader A", 400, 1_000_000, "aaaaaaaaaa1"),
            Yt("Daft Punk - One More Time", "Uploader B", 320, 1_000_000, "aaaaaaaaaa2"),
            Yt("Daft Punk - One More Time 1 HOUR", "Loops", 3600, 500_000, "aaaaaaaaaa3"),
        };
        var ranked = SearchRanker.Rank("daft punk one more time", results, TimeSpan.FromSeconds(320));
        Assert.Equal("aaaaaaaaaa2", RecommendationService.YouTubeId(ranked[0].PageUrl));
        Assert.Equal("aaaaaaaaaa3", RecommendationService.YouTubeId(ranked[^1].PageUrl));

        Assert.Single(SearchRanker.Rank("x", [results[0]]));
        Assert.Empty(SearchRanker.Rank("x", []));
    }

    // ───────────── LRC ─────────────

    [Fact]
    public void Lrc_ParsesFormatsMultipleTagsAndOffset()
    {
        const string lrc = "[ar:Daft Punk]\n[ti:One More Time]\n[00:01.50]One more time\r\n[00:10][01:10.250]We're gonna celebrate\n" +
                           "[00:05:20] Oh yeah\n[02:00.00]\nnot a lyric line";
        var lines = LyricsService.ParseLrc(lrc);
        Assert.Equal(5, lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), lines[0].Time);
        Assert.Equal("One more time", lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(5200), lines[1].Time);
        Assert.Equal("Oh yeah", lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(10), lines[2].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(70250), lines[3].Time);
        Assert.Equal("We're gonna celebrate", lines[3].Text);
        Assert.Equal("", lines[4].Text);

        var shifted = LyricsService.ParseLrc("[offset:+500]\n[00:01.00]a\n[00:00.20]b");
        Assert.Equal(TimeSpan.Zero, shifted[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(500), shifted[1].Time);
        Assert.Empty(LyricsService.ParseLrc(""));
    }

    [Fact]
    public void Lrc_LineAtFindsTheCurrentLine()
    {
        var lines = LyricsService.ParseLrc("[00:01.00]a\n[00:05.00]b\n[00:09.00]c");
        Assert.Equal(-1, LyricsService.LineAt(lines, TimeSpan.FromSeconds(0.5)));
        Assert.Equal(0, LyricsService.LineAt(lines, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, LyricsService.LineAt(lines, TimeSpan.FromSeconds(4.99)));
        Assert.Equal(1, LyricsService.LineAt(lines, TimeSpan.FromSeconds(5)));
        Assert.Equal(2, LyricsService.LineAt(lines, TimeSpan.FromMinutes(10)));
        Assert.Equal(-1, LyricsService.LineAt([], TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Lyrics_FallsBackToSearchAndCachesInMemoryAndOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spoka_lyrics_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var calls = new List<string>();
            Task<(HttpStatusCode, string)> Fake(string url, CancellationToken _)
            {
                calls.Add(url);
                if (url.Contains("/api/get")) return Task.FromResult((HttpStatusCode.NotFound, "{\"code\":404}"));
                return Task.FromResult((HttpStatusCode.OK, """
                    [
                      {"trackName":"Something else","artistName":"X","duration":100,"plainLyrics":"nope","syncedLyrics":null,"instrumental":false},
                      {"trackName":"Mona Lisa","artistName":"Booba","duration":214,"plainLyrics":"ligne 1\nligne 2","syncedLyrics":"[00:12.00]ligne 1\n[00:15.00]ligne 2","instrumental":false}
                    ]
                    """));
            }

            var service = new LyricsService(dir) { FetchOverride = Fake };
            var lyrics = await service.GetAsync("Booba - Mona Lisa (Clip officiel)", "Booba", TimeSpan.FromSeconds(215), CancellationToken.None);
            Assert.NotNull(lyrics);
            Assert.True(lyrics.HasSynced);
            Assert.Equal("ligne 2", lyrics.Synced[1].Text);
            Assert.Contains(calls, c => c.Contains("track_name=Mona%20Lisa") && c.Contains("artist_name=Booba") && c.Contains("duration=215"));
            Assert.Equal(2, calls.Count);

            await service.GetAsync("Mona Lisa", "Booba", null, CancellationToken.None);
            Assert.Equal(2, calls.Count); // memory cache

            var fresh = new LyricsService(dir) { FetchOverride = Fake };
            var fromDisk = await fresh.GetAsync("Mona Lisa", "Booba - Topic", null, CancellationToken.None);
            Assert.Equal(2, calls.Count); // disk cache
            Assert.Equal(lyrics.Plain, fromDisk!.Plain);
            Assert.Equal(TimeSpan.FromSeconds(12), fromDisk.Synced[0].Time);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Lyrics_ParsesInstrumentalAndRejectsUnrelatedSearchResults()
    {
        var instrumental = LyricsService.ParseRecord("""{"trackName":"Intro","instrumental":true,"plainLyrics":null,"syncedLyrics":null}""");
        Assert.True(instrumental!.IsInstrumental);
        Assert.Null(LyricsService.PickFromSearch("""[{"trackName":"Autre chose","duration":500,"plainLyrics":"x"}]""", "Mona Lisa", TimeSpan.FromSeconds(214)));
        Assert.Null(LyricsService.PickFromSearch("[]", "Mona Lisa", null));
        Assert.Equal("Mona Lisa", LyricsService.CleanForLyrics("Mona Lisa (Clip officiel) [HD]"));
    }

    // ───────────── YouTube ids, yt-dlp output ─────────────

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?feature=share&v=dQw4w9WgXcQ&t=10", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RDAMVM", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/playlist?list=PL123", null)]
    [InlineData("https://soundcloud.com/a/b", null)]
    [InlineData("https://www.youtube.com/watch?v=short", null)]
    [InlineData(null, null)]
    public void YouTubeId_ReadsEveryLinkShape(string? url, string? id) => Assert.Equal(id, RecommendationService.YouTubeId(url));

    [Fact]
    public void YtDlp_ParsesFlatSearchAndPlaylistEntries()
    {
        const string json = """
            {"_type":"playlist","title":"Ma playlist","thumbnails":[{"url":"https://i.ytimg.com/pl/big.jpg","width":1280},{"url":"https://i.ytimg.com/pl/mid.jpg","width":480}],
             "entries":[
              {"ie_key":"Youtube","id":"dQw4w9WgXcQ","url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","title":"Rick Astley - Never Gonna Give You Up (Official Music Video)","channel":"Rick Astley","duration":213.0,"view_count":1600000000,"live_status":null},
              {"ie_key":"Youtube","id":"aaaaaaaaaaa","url":"https://www.youtube.com/watch?v=aaaaaaaaaaa","title":"[Private video]","duration":null},
              {"ie_key":"Youtube","id":"bbbbbbbbbbb","url":"https://www.youtube.com/watch?v=bbbbbbbbbbb","title":"Radio 24/7","channel":"Lofi","live_status":"is_live"},
              {"ie_key":"Youtube","id":"dQw4w9WgXcQ","url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","title":"duplicate"}
             ]}
            """;
        using var doc = JsonDocument.Parse(json);
        var listing = MediaDownloader.ParseFlatPlaylist(doc.RootElement, 500);
        Assert.Equal("Ma playlist", listing.Title);
        Assert.Equal("https://i.ytimg.com/pl/mid.jpg", listing.ThumbnailUrl);
        Assert.Equal(2, listing.Entries.Count);
        var first = listing.Entries[0];
        Assert.Equal("Rick Astley", first.Artist);
        Assert.Equal(1_600_000_000, first.ViewCount);
        Assert.Equal(TimeSpan.FromSeconds(213), first.Duration);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", first.ThumbnailUrl);
        Assert.True(listing.Entries[1].IsLive);

        using var sc = JsonDocument.Parse("""
            {"entries":[{"ie_key":"Soundcloud","id":"123","url":"https://soundcloud.com/daftpunk/one-more-time","title":"One More Time","uploader":"Daft Punk","duration":320.5,
              "thumbnails":[{"url":"https://i1.sndcdn.com/a-t50x50.jpg","width":50},{"url":"https://i1.sndcdn.com/a-t500x500.jpg","width":500}]},
              {"ie_key":"Soundcloud","url":"https://soundcloud.com/someone/my-cool-track"}]}
            """);
        var scList = MediaDownloader.ParseFlatPlaylist(sc.RootElement, 10);
        Assert.Equal(SearchSource.SoundCloud, scList.Entries[0].Source);
        Assert.Equal("Daft Punk", scList.Entries[0].Artist);
        Assert.Equal("https://i1.sndcdn.com/a-t500x500.jpg", scList.Entries[0].ThumbnailUrl);
        Assert.Equal("my cool track", scList.Entries[1].Title);
    }

    [Fact]
    public void YtDlp_ParsesTheFileLineWithMetadata()
    {
        var media = MediaDownloader.ParseFileLine(
            @"C:\cache\Musique\Mona Lisa [abcdefghijk].m4a|#|Mona Lisa|#|https://www.youtube.com/watch?v=abcdefghijk|#|NA|#|Booba - Topic|#|214.0|#|abcdefghijk|#|https://i.ytimg.com/vi_webp/abcdefghijk/maxresdefault.webp|#|Youtube",
            "https://fallback");
        Assert.Equal(@"C:\cache\Musique\Mona Lisa [abcdefghijk].m4a", media.FilePath);
        Assert.Equal("Booba", media.Artist);
        Assert.Equal(214, media.DurationSeconds);
        Assert.Equal("https://i.ytimg.com/vi/abcdefghijk/hqdefault.jpg", media.ThumbnailUrl);

        var sc = MediaDownloader.ParseFileLine(@"C:\x\t [1].mp3|#|Track|#|NA|#|Daft Punk, Daft Punk|#|DP|#|NA|#|1|#|https://i1.sndcdn.com/x.jpg|#|Soundcloud", "https://soundcloud.com/x/y");
        Assert.Equal("https://soundcloud.com/x/y", sc.SourceUrl);
        Assert.Equal("Daft Punk", sc.Artist);
        Assert.Null(sc.DurationSeconds);
        Assert.Equal("https://i1.sndcdn.com/x.jpg", sc.ThumbnailUrl);
    }

    [Fact]
    public void Suggestions_ParseGoogleAnswer()
    {
        var list = MediaSearch.ParseSuggestions("""["booba",["booba mona lisa","booba","Booba","booba dkr"]]""");
        Assert.Equal(["booba mona lisa", "booba", "booba dkr"], list);
        Assert.Empty(MediaSearch.ParseSuggestions("not json"));
        Assert.Empty(MediaSearch.ParseSuggestions("""{"a":1}"""));
    }

    // ───────────── Fallback validation ─────────────

    [Fact]
    public void Resolver_ValidatesFallbackCandidates()
    {
        SearchResult Sc(string title, string uploader, int seconds) =>
            new(title, uploader, "https://soundcloud.com/x/y", null, TimeSpan.FromSeconds(seconds), null, SearchSource.SoundCloud, null, uploader);
        var wanted = TimeSpan.FromSeconds(320);

        Assert.True(TrackResolver.Matches(Sc("One More Time", "Daft Punk", 322), "Daft Punk", "One More Time", wanted, 0.15));
        Assert.True(TrackResolver.Matches(Sc("One More Time (Radio Edit)", "Daft Punk", 300), "Daft Punk", "One More Time", wanted, 0.15));
        Assert.False(TrackResolver.Matches(Sc("One More Time (Remix)", "DJ Someone", 330), "Daft Punk", "One More Time", wanted, 0.15));
        Assert.True(TrackResolver.Matches(Sc("One More Time (Remix)", "Daft Punk", 330), "Daft Punk", "One More Time (Remix)", wanted, 0.15));
        Assert.False(TrackResolver.Matches(Sc("One More Time", "Daft Punk", 600), "Daft Punk", "One More Time", wanted, 0.15));
        Assert.False(TrackResolver.Matches(Sc("Around the World", "Daft Punk", 320), "Daft Punk", "One More Time", wanted, 0.15));
        Assert.False(TrackResolver.Matches(Sc("One More Time cover", "Daft Punk", 320), "Daft Punk", "One More Time", null, 0.15));
        Assert.True(TrackResolver.Matches(Sc("Мона Лиза", "Буба", 200), "Буба", "Мона Лиза", null, 0.15));
    }

    // ───────────── Playlist importers ─────────────

    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=x", true)]
    [InlineData("https://open.spotify.com/intl-fr/album/4aawyAB9vmqN3uQ7FjRGTy", true)]
    [InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT", true)]
    [InlineData("https://www.deezer.com/fr/playlist/908622995", true)]
    [InlineData("https://link.deezer.com/s/31abc", true)]
    [InlineData("https://music.apple.com/fr/album/random-access-memories/617154241", true)]
    [InlineData("https://www.youtube.com/playlist?list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG", true)]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_abc", true)]
    [InlineData("https://soundcloud.com/daftpunk/sets/discovery", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("https://soundcloud.com/daftpunk/one-more-time", false)]
    [InlineData("https://www.myinstants.com/fr/instant/x/", false)]
    [InlineData("pas un lien", false)]
    public void Importer_RecognizesLinks(string url, bool expected) => Assert.Equal(expected, PlaylistImporter.CanImport(url));

    private const string SpotifyPlaylistHtml = """
        <html><head></head><body><div id="__next"></div>
        <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{
          "type":"playlist","name":"Today’s Top Hits","title":"Today’s Top Hits","subtitle":"Spotify",
          "coverArt":{"sources":[{"height":null,"width":null,"url":"https://i.scdn.co/image/cover"}]},
          "visualIdentity":{"image":[{"url":"https://img/64","maxHeight":64,"maxWidth":64},{"url":"https://img/640","maxHeight":640,"maxWidth":640},{"url":"https://img/300","maxHeight":300,"maxWidth":300}]},
          "trackList":[
            {"uri":"spotify:track:1","title":"Stop The Wedding!","subtitle":"Ashe","duration":198840,"entityType":"track"},
            {"uri":"spotify:track:2","title":"Give Me Everything (feat. Nayer)","subtitle":"Pitbull,\u00a0AFROJACK,\u00a0Ne-Yo","duration":252306,"entityType":"track"},
            {"uri":"spotify:track:3","subtitle":"Nobody"}
          ]}}}}}}</script></body></html>
        """;

    [Fact]
    public void Spotify_ParsesEmbedPlaylist()
    {
        var playlist = PlaylistImporter.ParseSpotifyEmbed(SpotifyPlaylistHtml, "https://open.spotify.com/playlist/x");
        Assert.Equal("Today’s Top Hits", playlist.Name);
        Assert.Equal("https://img/640", playlist.ThumbnailUrl);
        Assert.Equal(2, playlist.Tracks.Count);
        Assert.Equal("Pitbull, AFROJACK, Ne-Yo", playlist.Tracks[1].Artist);
        Assert.Equal(TimeSpan.FromMilliseconds(198840), playlist.Tracks[0].Duration);
        Assert.Null(playlist.Tracks[0].Url);
        Assert.Null(playlist.Tracks[0].ThumbnailUrl);
    }

    [Fact]
    public void Spotify_ParsesEmbedTrackAndAlbum()
    {
        const string track = """
            <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{"type":"track","name":"Never Gonna Give You Up",
            "artists":[{"name":"Rick Astley"}],"duration":213573,"visualIdentity":{"image":[{"url":"https://img/300","maxWidth":300}]}}}}}}}</script>
            """;
        var single = PlaylistImporter.ParseSpotifyEmbed(track, "https://open.spotify.com/track/x");
        var t = Assert.Single(single.Tracks);
        Assert.Equal("Rick Astley", t.Artist);
        Assert.Equal("https://img/300", t.ThumbnailUrl);
        Assert.Equal("Never Gonna Give You Up", single.Name);

        const string album = """
            <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"state":{"data":{"entity":{"type":"album","name":"Global Warming","subtitle":"Pitbull",
            "visualIdentity":{"image":[{"url":"https://img/640","maxWidth":640}]},"trackList":[{"title":"Global Warming (feat. Sensato)","subtitle":"Pitbull,\u00a0Sensato","duration":85400}]}}}}}}</script>
            """;
        var albumList = PlaylistImporter.ParseSpotifyEmbed(album, "https://open.spotify.com/album/x");
        Assert.Equal("Global Warming", albumList.Name);
        Assert.Equal("https://img/640", albumList.Tracks[0].ThumbnailUrl);

        Assert.Throws<DownloadException>(() => PlaylistImporter.ParseSpotifyEmbed("<html>nothing</html>", "x"));
    }

    [Fact]
    public void Deezer_ParsesTracksAndErrors()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[
              {"id":116348632,"readable":true,"title":"Hey Jude (Remastered 2015)","title_short":"Hey Jude","duration":429,
               "artist":{"id":1,"name":"The Beatles"},"album":{"title":"1","cover_medium":"https://c/250.jpg","cover_big":"https://c/500.jpg"}},
              {"id":3135553,"title":"One More Time","duration":320}
            ],"next":"https://api.deezer.com/playlist/1/tracks?index=25"}
            """);
        var tracks = PlaylistImporter.DeezerTracks(doc.RootElement.GetProperty("data").EnumerateArray(), "Daft Punk", "https://album/cover.jpg");
        Assert.Equal(2, tracks.Count);
        Assert.Equal("Hey Jude (Remastered 2015)", tracks[0].Title);
        Assert.Equal("The Beatles", tracks[0].Artist);
        Assert.Equal(TimeSpan.FromSeconds(429), tracks[0].Duration);
        Assert.Equal("https://c/500.jpg", tracks[0].ThumbnailUrl);
        Assert.Equal("Daft Punk", tracks[1].Artist);
        Assert.Equal("https://album/cover.jpg", tracks[1].ThumbnailUrl);

        using var error = JsonDocument.Parse("""{"error":{"type":"DataException","message":"no data","code":800}}""");
        var ex = Assert.Throws<DownloadException>(() => PlaylistImporter.ThrowIfDeezerError(error.RootElement));
        Assert.Contains("introuvable", ex.Message);
    }

    private const string AppleAlbumHtml = """
        <html><head>
        <meta property="og:title" content="Random Access Memories par Daft Punk sur Apple&nbsp;Music">
        <meta property="og:image" content="https://is1-ssl.mzstatic.com/cover/1200x630wp.jpg">
        </head><body>
        <script type="application/json" id="serialized-server-data">[{"intent":{},"data":{"sections":[
          {"items":[{"title":"Random Access Memories","trackCount":13,"subtitleLinks":[{"title":"Daft Punk"}]}]},
          {"items":[
            {"id":"track-lockup - 617154241 - 617154353","title":"Give Life Back to Music","duration":274403,"artistName":"Daft Punk","artwork":null,
             "contentDescriptor":{"kind":"song","identifiers":{"storeAdamID":"617154353"}}},
            {"id":"track-lockup - 617154241 - 617154366","title":"Get Lucky (feat. Pharrell Williams & Nile Rodgers)","duration":369626,"subtitleLinks":null,
             "artwork":{"dictionary":{"url":"https://is1-ssl.mzstatic.com/x/{w}x{h}bb.{f}"}},
             "contentDescriptor":{"kind":"song","identifiers":{"storeAdamID":"617154366"}}}
          ]}]}}]</script>
        </body></html>
        """;

    [Fact]
    public void Apple_ParsesServerDataAndSelectsSong()
    {
        var album = PlaylistImporter.ParseApplePage(AppleAlbumHtml, "https://music.apple.com/fr/album/random-access-memories/617154241");
        Assert.Equal("Random Access Memories", album.Name);
        Assert.Equal(2, album.Tracks.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(274403), album.Tracks[0].Duration);
        Assert.Equal("Daft Punk", album.Tracks[1].Artist); // from the album header
        Assert.Equal("https://is1-ssl.mzstatic.com/cover/1200x630wp.jpg", album.Tracks[0].ThumbnailUrl);
        Assert.Equal("https://is1-ssl.mzstatic.com/x/600x600bb.jpg", album.Tracks[1].ThumbnailUrl);

        var song = PlaylistImporter.ParseApplePage(AppleAlbumHtml, "https://music.apple.com/fr/album/get-lucky/617154241?i=617154366");
        var only = Assert.Single(song.Tracks);
        Assert.StartsWith("Get Lucky", only.Title);
        Assert.Equal(only.Title, song.Name);
    }

    [Fact]
    public void Apple_FallsBackToJsonLdThenOpenGraph()
    {
        const string ld = """
            <meta property="og:title" content="Tubes du moment sur Apple Music">
            <script type="application/ld+json">{"@context":"http://schema.org","@type":"MusicPlaylist","name":"Tubes du moment",
              "track":[{"@type":"MusicRecording","name":"D'or et d'étain","url":"https://music.apple.com/fr/song/dor-et-detain/6792063320","duration":"PT3M16S","byArtist":{"name":"Vianney"}},
                       {"@type":"MusicRecording","name":"STORM II","duration":"PT2M53S"}]}</script>
            """;
        var playlist = PlaylistImporter.ParseApplePage(ld, "https://music.apple.com/fr/playlist/tubes-du-moment/pl.f4d106fed2bd41149aaacabb233eb5eb");
        Assert.Equal("Tubes du moment", playlist.Name);
        Assert.Equal(2, playlist.Tracks.Count);
        Assert.Equal(TimeSpan.FromSeconds(196), playlist.Tracks[0].Duration);
        Assert.Equal("Vianney", playlist.Tracks[0].Artist);

        const string og = """<meta content="Get Lucky par Daft Punk sur Apple Music" property="og:title"><meta property="og:image" content="https://img">""";
        var single = PlaylistImporter.ParseApplePage(og, "https://music.apple.com/fr/song/get-lucky/617154366");
        var t = Assert.Single(single.Tracks);
        Assert.Equal("Get Lucky", t.Title);
        Assert.Equal("Daft Punk", t.Artist);
        Assert.Equal("https://img", t.ThumbnailUrl);

        Assert.Throws<DownloadException>(() => PlaylistImporter.ParseApplePage("<html></html>", "https://music.apple.com/fr/album/x/1"));
    }

    [Fact]
    public void YtDlpPlaylist_BecomesImportedPlaylistWithPlayableUrls()
    {
        using var doc = JsonDocument.Parse("""
            {"title":"Daft Punk - Discovery","entries":[{"ie_key":"Youtube","id":"FGBhQbmPwH8","title":"Daft Punk - One More Time (Official Video)","channel":"Daft Punk","duration":320}]}
            """);
        var playlist = PlaylistImporter.FromFlatPlaylist(MediaDownloader.ParseFlatPlaylist(doc.RootElement, 500), "https://www.youtube.com/playlist?list=x");
        Assert.Equal("Daft Punk - Discovery", playlist.Name);
        var t = Assert.Single(playlist.Tracks);
        Assert.Equal("One More Time", t.Title);
        Assert.Equal("Daft Punk", t.Artist);
        Assert.Equal("https://www.youtube.com/watch?v=FGBhQbmPwH8", t.Url);
    }

    // ───────────── Recommendations ─────────────

    [Fact]
    public void Recommendations_FilterExclusionsDislikesAndCompilations()
    {
        var items = new[]
        {
            new Recommendation(Yt("Daft Punk - Around the World", "Daft Punk", 430, id: "aaaaaaaaaa1"), "A"),
            new Recommendation(Yt("Booba - Pitbull", "BoobaOfficiel", 200, id: "aaaaaaaaaa2"), "A"),
            new Recommendation(Yt("Best of Daft Punk 2024 Mix", "Mixes", 3000, id: "aaaaaaaaaa3"), "A"),
            new Recommendation(Yt("Justice - D.A.N.C.E.", "Justice", 240, id: "aaaaaaaaaa4"), "B"),
            new Recommendation(Yt("Daft Punk - Around the World (Official Audio)", "Some Channel", 430, id: "aaaaaaaaaa5"), "B"),
            new Recommendation(Yt("Cassius - 1999", "Cassius", 300, id: "aaaaaaaaaa6"), "B"),
        };
        var kept = RecommendationService.Filter(items, ["https://youtu.be/aaaaaaaaaa6"], ["Booba"]).ToList();
        Assert.Equal(["aaaaaaaaaa1", "aaaaaaaaaa4"], kept.Select(k => RecommendationService.YouTubeId(k.Result.PageUrl)));

        var mixed = RecommendationService.Interleave([
            new Recommendation(Yt("a", "x", 200), "A"), new Recommendation(Yt("b", "x", 200), "A"), new Recommendation(Yt("c", "x", 200), "A"),
            new Recommendation(Yt("d", "x", 200), "B"),
        ]).Select(r => r.Result.Title);
        Assert.Equal(["a", "d", "b", "c"], mixed);
    }

    [Fact]
    public void Recommendations_PickSeedsFromHistoryAndFavorites()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0);
        var history = new List<HistoryEntry>
        {
            new() { Title = "Daft Punk - One More Time", SourceUrl = "https://www.youtube.com/watch?v=FGBhQbmPwH8", PlayedAt = now.AddMinutes(-5) },
            new() { Title = "Around the World", Artist = "Daft Punk", PlayedAt = now.AddDays(-1) },
            new() { Title = "Justice - Genesis", PlayedAt = now.AddDays(-2) },
            new() { Title = "Justice - D.A.N.C.E.", PlayedAt = now.AddDays(-3) },
            new() { Title = "Booba - Pitbull", PlayedAt = now.AddDays(-3) },
            new() { Title = "Cassius - 1999", PlayedAt = now.AddDays(-60) },
        };
        var favorites = new List<TrackItem> { new() { DisplayTitle = "Get Lucky", Artist = "Daft Punk", SourceUrl = "https://youtu.be/5NV6Rdv1a3I" } };

        var seeds = RecommendationService.PickSeeds(history, favorites, ["Booba"], now, new Random(1));
        Assert.Equal("FGBhQbmPwH8", seeds[0].Key);
        Assert.StartsWith("Dans le style de « One More Time »", seeds[0].Reason);
        Assert.Equal(["Daft Punk", "Justice"], seeds.Where(s => !s.IsMix).Select(s => s.Key));
        Assert.Contains(seeds, s => s.Reason == "Parce que vous écoutez Daft Punk");
        Assert.Equal("5NV6Rdv1a3I", seeds[^1].Key);
        Assert.True(seeds[^1].IsFavorite);
        Assert.Empty(RecommendationService.PickSeeds([], [], [], now));
    }

    // ───────────── MediaDownloader: packaged mode and cache ─────────────

    [Fact]
    public async Task Downloader_PackagedModeUsesShippedToolsAndNeverDownloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "spoka_packaged_test_" + Guid.NewGuid().ToString("N"));
        var shipped = Path.Combine(root, "package", "tools");
        Directory.CreateDirectory(shipped);
        try
        {
            var downloader = new MediaDownloader(Path.Combine(root, "data")) { PackagedOverride = true, PackagedToolsOverride = shipped };
            Assert.Equal(Path.Combine(shipped, "yt-dlp.exe"), downloader.YtDlpPath);
            Assert.Equal(Path.Combine(shipped, "ffmpeg.exe"), downloader.FfmpegPath);
            var ex = await Assert.ThrowsAsync<DownloadException>(() => downloader.EnsureYtDlpAsync(null, CancellationToken.None));
            Assert.Contains("Microsoft Store", ex.Message);
            Assert.False(File.Exists(Path.Combine(root, "data", "tools", "yt-dlp.exe")));

            var classic = new MediaDownloader(Path.Combine(root, "data")) { PackagedOverride = false };
            Assert.Equal(Path.Combine(root, "data", "tools", "yt-dlp.exe"), classic.YtDlpPath);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Downloader_ReturnsCachedTrackWithoutYtDlp()
    {
        var root = Path.Combine(Path.GetTempPath(), "spoka_cache_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            // No yt-dlp anywhere: any attempt to run it would throw.
            var downloader = new MediaDownloader(root) { PackagedOverride = true, PackagedToolsOverride = Path.Combine(root, "none") };
            var folder = Path.Combine(downloader.CacheDirectory, "Musique");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "Never Gonna Give You Up [dQw4w9WgXcQ].wav");
            using (var writer = new WaveFileWriter(file, new WaveFormat(8000, 16, 1)))
                writer.Write(new byte[16000], 0, 16000);
            File.WriteAllText(Path.Combine(folder, "Other [dQw4w9WgXcQ].wav.part"), "partial");

            var media = await downloader.DownloadTrackAsync("https://youtu.be/dQw4w9WgXcQ", "Autre", null, CancellationToken.None);
            Assert.Equal(file, media.FilePath);
            Assert.Equal("Never Gonna Give You Up", media.Title);
            Assert.Equal(1.0, media.DurationSeconds!.Value, 2);
            Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", media.ThumbnailUrl);

            await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadTrackAsync("https://youtu.be/aaaaaaaaaaa", "Autre", null, CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Downloader_ClassifiesBlockedErrors()
    {
        Assert.Equal("YouTube bloque temporairement le téléchargement (vérification anti-robot).",
            MediaDownloader.FriendlyError("ERROR: [youtube] abc: Sign in to confirm you’re not a bot."));
        Assert.Equal("Cette vidéo est soumise à une limite d'âge.", MediaDownloader.FriendlyError("ERROR: Sign in to confirm your age"));
        Assert.True(MediaDownloader.IsBlockedOrUnavailable(new DownloadException(MediaDownloader.FriendlyError("ERROR: not a bot"))));
        Assert.True(MediaDownloader.IsBlockedOrUnavailable(new DownloadException(MediaDownloader.FriendlyError("ERROR: Video unavailable"))));
        Assert.False(MediaDownloader.IsBlockedOrUnavailable(new DownloadException("Ce site n'est pas pris en charge.")));
        Assert.False(MediaDownloader.IsBlockedOrUnavailable(new InvalidOperationException("x")));
    }
}
