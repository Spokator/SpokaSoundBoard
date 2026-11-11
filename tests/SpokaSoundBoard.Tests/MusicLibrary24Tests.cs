using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Music;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Tests;

/// <summary>2.4: sorting, playlist sync, another version, track info, freeing space.</summary>
public sealed class MusicLibrary24Tests
{
    private static TrackItem T(string title, string? artist = null, double? seconds = null, int plays = 0) =>
        new() { DisplayTitle = title, Artist = artist, DurationSeconds = seconds, PlayCount = plays };

    [Fact]
    public void Sort_FollowsFrenchOrder()
    {
        TrackItem[] tracks = [T("Zombie", "The Cranberries", 300, 1), T("éclair", "Zaz", 200, 5), T("Alors on danse", null, null, 2), T("Bella", "Gims", 250, 5)];

        Assert.Equal(["Alors on danse", "Bella", "éclair", "Zombie"], MusicLibrary.Sort(tracks, TrackSort.Title).Select(t => t.Title));
        Assert.Equal(["Bella", "Zombie", "éclair", "Alors on danse"], MusicLibrary.Sort(tracks, TrackSort.Artist).Select(t => t.Title)); // no artist last
        Assert.Equal(["éclair", "Bella", "Zombie", "Alors on danse"], MusicLibrary.Sort(tracks, TrackSort.Duration).Select(t => t.Title)); // unknown last
        Assert.Equal(5, MusicLibrary.Sort(tracks, TrackSort.MostPlayed).First().PlayCount);
        Assert.Equal(tracks, MusicLibrary.Sort(tracks, TrackSort.Default));
    }

    [Fact]
    public void SyncPlaylist_MirrorsTheSource()
    {
        static TrackItem Remote(string q) => new() { DisplayTitle = q, SearchQuery = q, State = TrackState.Remote };
        var library = new MusicLibrary();
        var playlist = library.CreatePlaylist("Spotify", [Remote("A"), Remote("B"), Remote("C")], "https://open.spotify.com/playlist/x");

        var (added, removed) = library.SyncPlaylist(playlist, [Remote("C"), Remote("A"), Remote("D")]);

        Assert.Equal((1, 1), (added, removed));
        Assert.Equal(["C", "A", "D"], library.TracksOf(playlist).Select(t => t.Title));
        Assert.Equal(4, library.Tracks.Count); // « B » left the playlist, not the library
        Assert.Equal((0, 0), library.SyncPlaylist(playlist, [Remote("C"), Remote("A"), Remote("D")]));
    }

    [Fact]
    public void ChangeSource_ForgetsTheOldDownload()
    {
        var library = new MusicLibrary();
        var track = library.AddOrGet(new TrackItem { DisplayTitle = "Mona Lisa", SourceUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa", FilePath = @"C:\cache\old.mp3", NormalizationDb = -3 });
        var changed = 0;
        library.Changed += () => changed++;

        var old = library.ChangeSource(track, "https://www.youtube.com/watch?v=bbbbbbbbbbb", 215, "https://i.ytimg.com/vi/bbbbbbbbbbb/hqdefault.jpg");

        Assert.Equal(@"C:\cache\old.mp3", old);
        Assert.Equal(("", TrackState.Remote, (double?)null, 215.0), (track.FilePath, track.State, track.NormalizationDb, track.DurationSeconds ?? 0));
        Assert.Contains("bbbbbbbbbbb", track.SourceUrl);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void EditInfo_RenamesAndClearsTheArtist()
    {
        var library = new MusicLibrary();
        var track = library.AddOrGet(new TrackItem { DisplayTitle = "Booba - Mona Lisa (Clip officiel)", SourceUrl = "https://www.youtube.com/watch?v=ccccccccccc" });
        library.EditInfo(track, "  Mona Lisa ", " Booba ");
        Assert.Equal(("Mona Lisa", "Booba"), (track.Title, track.Artist));
        library.EditInfo(track, "", "");
        Assert.Equal(("Mona Lisa", (string?)null), (track.Title, track.Artist)); // an empty title is ignored
    }

    [Fact]
    public void ReleasableDownloads_KeepsWhatTheUserCaresAbout()
    {
        var cache = Path.Combine(Path.GetTempPath(), "spoka-cache-test", "Musique");
        TrackItem Downloaded(string name, bool favorite = false, string? url = "https://www.youtube.com/watch?v=") => new()
        {
            DisplayTitle = name, FilePath = Path.Combine(cache, name + ".mp3"), IsFavorite = favorite,
            SourceUrl = url == null ? null : url + name.PadRight(11, 'x'),
        };
        var library = new MusicLibrary();
        var free = library.AddOrGet(Downloaded("free"));
        library.AddOrGet(Downloaded("fav", favorite: true));
        var inPlaylist = library.AddOrGet(Downloaded("listed"));
        library.CreatePlaylist("P", [inPlaylist]);
        var queued = library.AddOrGet(Downloaded("queued"));
        library.AddOrGet(Downloaded("nolink", url: null));
        library.AddOrGet(new TrackItem { DisplayTitle = "mine", FilePath = @"D:\Musique\mine.mp3", SourceUrl = "https://www.youtube.com/watch?v=mineeeeeeee" });

        var releasable = library.ReleasableDownloads(cache, [queued.Id]);
        Assert.Equal([free], releasable);

        library.ForgetDownload(free);
        Assert.Equal(("", TrackState.Remote), (free.FilePath, free.State));
    }

    [Fact]
    public void Storage_SizesReadLikeWindows()
    {
        Assert.Equal("512 o", DownloadCache.FormatSize(512));
        Assert.Equal("12 Ko", DownloadCache.FormatSize(12 * 1024));
        Assert.Equal("340 Mo", DownloadCache.FormatSize(340L << 20));
        Assert.Equal("1,5 Go", DownloadCache.FormatSize(3L << 29));
        Assert.Equal(0, DownloadCache.FolderSize(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())));
    }

    [Fact]
    public void Settings_KeepTheSortAndRejectUnknownValues()
    {
        var s = new AppSettings();
        s.Music.TrackSort = TrackSort.Artist;
        Assert.Equal(TrackSort.Artist, SettingsStore.Deserialize(SettingsStore.Serialize(s)).Music.TrackSort);
        Assert.Equal(TrackSort.Default, SettingsStore.Deserialize("""{ "Music": { "TrackSort": 42 } }""").Music.TrackSort);
    }
}
