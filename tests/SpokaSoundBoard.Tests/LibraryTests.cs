using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Music;

namespace SpokaSoundBoard.Tests;

public class PlayQueueTests
{
    private static List<TrackItem> Tracks(int n) =>
        Enumerable.Range(1, n).Select(i => new TrackItem { DisplayTitle = $"T{i}", SourceUrl = $"https://example.com/{i}" }).ToList();

    [Fact]
    public void AdvancesInOrderAndStopsAtEndWithoutRepeat()
    {
        var q = new PlayQueue { Repeat = RepeatMode.Off };
        var t = Tracks(3);
        q.Replace(t);
        Assert.Equal(t[0], q.Current);
        Assert.Equal(t[1], q.PeekNext());
        Assert.Equal(t[1], q.Advance(true));
        Assert.Equal(t[2], q.Advance(true));
        Assert.Null(q.PeekNext());
        Assert.Null(q.Advance(true));
        Assert.Null(q.Current);
    }

    [Fact]
    public void RepeatAllWrapsAndRepeatOneReplays()
    {
        var q = new PlayQueue { Repeat = RepeatMode.All };
        var t = Tracks(2);
        q.Replace(t, 1);
        Assert.Equal(t[0], q.Advance(true));

        q.Repeat = RepeatMode.One;
        Assert.Equal(t[0], q.PeekNext());
        Assert.Equal(t[0], q.Advance(true));
        // The Next button still moves on in repeat-one mode.
        Assert.Equal(t[1], q.Advance(false));
    }

    [Fact]
    public void ManualNextAtEndWrapsEvenWithoutRepeat()
    {
        var q = new PlayQueue { Repeat = RepeatMode.Off };
        var t = Tracks(2);
        q.Replace(t, 1);
        Assert.Equal(t[0], q.Advance(false));
    }

    [Fact]
    public void ShuffleIsAPermutationWithoutRepeatsAndRestoresOrder()
    {
        var q = new PlayQueue(new Random(42)) { Repeat = RepeatMode.Off };
        var t = Tracks(20);
        q.Replace(t);
        q.Shuffle = true;
        Assert.Equal(t[0], q.Current);
        Assert.Equal(20, q.Items.Distinct().Count());
        Assert.NotEqual(t, q.Items.ToList());

        var played = new List<TrackItem> { q.Current! };
        while (q.Advance(true) is { } next) played.Add(next);
        Assert.Equal(20, played.Distinct().Count());

        q.Replace(t, 5);
        q.Shuffle = false;
        q.Shuffle = true;
        q.Shuffle = false;
        Assert.Equal(t, q.Items.ToList());
        Assert.Equal(t[5], q.Current);
    }

    [Fact]
    public void ReplaceWithShuffleStartsWithChosenTrack()
    {
        var q = new PlayQueue(new Random(1)) { Shuffle = true };
        var t = Tracks(10);
        q.Replace(t, 7);
        Assert.Equal(t[7], q.Current);
        Assert.Equal(0, q.CurrentIndex);
        Assert.Equal(10, q.Items.Distinct().Count());
    }

    [Fact]
    public void PlayNextInsertsAfterCurrent()
    {
        var q = new PlayQueue();
        var t = Tracks(3);
        var extra = new TrackItem { DisplayTitle = "X" };
        q.Replace(t);
        q.PlayNext(extra);
        Assert.Equal(extra, q.PeekNext());
        Assert.Equal(extra, q.Advance(false));
        Assert.Equal(t[1], q.Advance(false));
    }

    [Fact]
    public void GoBackFollowsHistoryAfterJumps()
    {
        var q = new PlayQueue();
        var t = Tracks(5);
        q.Replace(t);
        q.JumpTo(3);
        q.Advance(false);
        Assert.Equal(t[4], q.Current);
        Assert.Equal(t[3], q.GoBack());
        Assert.Equal(t[0], q.GoBack());
    }

    [Fact]
    public void RemoveAndMoveKeepCurrent()
    {
        var q = new PlayQueue();
        var t = Tracks(4);
        q.Replace(t, 2);
        q.RemoveAt(0);
        Assert.Equal(t[2], q.Current);
        Assert.Equal(1, q.CurrentIndex);
        q.Move(2, 0);
        Assert.Equal(t[2], q.Current);
        Assert.Equal(t[3], q.Items[0]);
        q.Remove(t[2]);
        Assert.Null(q.Current);
    }

    [Fact]
    public void ConsumeRemovesPlayedTracks()
    {
        var q = new PlayQueue { Consume = true, Repeat = RepeatMode.Off };
        var t = Tracks(3);
        q.Replace(t);
        Assert.Equal(t[1], q.Advance(true));
        Assert.Equal(2, q.Count);
        Assert.Equal(t[2], q.Advance(true));
        Assert.Null(q.Advance(true));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void PeekNextAlwaysMatchesAdvanceWithShuffleAndRepeatAll()
    {
        var q = new PlayQueue(new Random(7)) { Repeat = RepeatMode.All };
        q.Replace(Tracks(6));
        q.Shuffle = true;
        for (var i = 0; i < 40; i++)
        {
            var peeked = q.PeekNext();
            Assert.Same(peeked, q.Advance(true));
        }
    }

    [Fact]
    public void ConsumeWithDuplicatesPlaysTheFollowingItem()
    {
        var q = new PlayQueue { Consume = true, Repeat = RepeatMode.Off };
        var t = Tracks(2);
        q.Replace([t[0], t[1], t[0]]);
        Assert.Equal(t[1], q.PeekNext());
        Assert.Equal(t[1], q.Advance(true));
        Assert.Equal(t[0], q.Advance(true));
    }

    [Fact]
    public void RemoveDuplicatesKeepsFirstAndCurrent()
    {
        var q = new PlayQueue();
        var t = Tracks(2);
        q.Replace([t[0], t[1], t[0], t[1]], 2);
        var removed = q.RemoveDuplicates();
        Assert.Equal(2, removed);
        Assert.Equal(t[0], q.Current);
        Assert.Equal(2, q.Count);
    }

    [Fact]
    public void ClearUpcomingKeepsPlayedAndCurrent()
    {
        var q = new PlayQueue();
        var t = Tracks(5);
        q.Replace(t, 2);
        q.ClearUpcoming();
        Assert.Equal(3, q.Count);
        Assert.Equal(t[2], q.Current);
        Assert.Null(q.Advance(true) == t[3] ? t[3] : null);
    }
}

public class MusicLibraryTests
{
    [Fact]
    public void DeduplicatesYouTubeLinksByVideoId()
    {
        var lib = new MusicLibrary();
        var a = lib.AddOrGet(new TrackItem { SourceUrl = "https://www.youtube.com/watch?v=abc123&t=10s" });
        var b = lib.AddOrGet(new TrackItem { SourceUrl = "https://youtu.be/abc123", Artist = "Daft Punk" });
        Assert.Same(a, b);
        Assert.Single(lib.Tracks);
        Assert.Equal("Daft Punk", a.Artist);
        Assert.Equal("yt:xyz", MusicLibrary.SourceKey("https://music.youtube.com/watch?v=xyz&list=RDxyz"));
        Assert.Equal("yt:sh0rt", MusicLibrary.SourceKey("https://www.youtube.com/shorts/sh0rt"));
    }

    [Fact]
    public void PlaylistsAddRemoveMoveAndUniqueNames()
    {
        var lib = new MusicLibrary();
        var t1 = new TrackItem { DisplayTitle = "A", FilePath = @"C:\a.mp3" };
        var t2 = new TrackItem { DisplayTitle = "B", FilePath = @"C:\b.mp3" };
        var p = lib.CreatePlaylist("Soirée", [t1]);
        var p2 = lib.CreatePlaylist("soirée");
        Assert.Equal("soirée (2)", p2.Name);
        Assert.Equal(1, lib.AddToPlaylist(p, [t1, t2]));
        Assert.Equal([t1, t2], lib.TracksOf(p));
        lib.MoveInPlaylist(p, 1, 0);
        Assert.Equal([t2, t1], lib.TracksOf(p));
        lib.Remove(t2);
        Assert.Equal([t1], lib.TracksOf(p));
    }

    [Fact]
    public void HistoryFavoritesAndRecentlyPlayed()
    {
        var lib = new MusicLibrary();
        var t1 = lib.AddOrGet(new TrackItem { DisplayTitle = "A", FilePath = @"C:\a.mp3" });
        var t2 = lib.AddOrGet(new TrackItem { DisplayTitle = "B", FilePath = @"C:\b.mp3" });
        lib.RecordPlay(t1);
        lib.RecordPlay(t2);
        lib.RecordPlay(t1);
        Assert.Equal([t1, t2], lib.RecentlyPlayed(10));
        Assert.Equal(2, t1.PlayCount);
        Assert.Equal(t1, lib.MostPlayed(1)[0]);
        lib.ToggleFavorite(t2);
        Assert.Equal([t2], lib.Favorites.ToList());
    }

    [Fact]
    public void SearchIgnoresAccentsAndCase()
    {
        var t = new TrackItem { DisplayTitle = "Désenchantée", Artist = "Mylène Farmer" };
        Assert.True(MusicLibrary.Matches(t, "mylene desenchantee"));
        Assert.False(MusicLibrary.Matches(t, "mylene libertine"));
        Assert.Equal("ac dc back in black", MusicLibrary.Normalize("AC/DC — Back in Black!"));
    }

    [Fact]
    public void DislikesAreNormalized()
    {
        var lib = new MusicLibrary();
        lib.DislikeArtist("Jul ");
        Assert.True(lib.IsDisliked("JUL", null));
        lib.DislikeTrack(new TrackItem { SourceUrl = "https://youtu.be/zz" });
        Assert.True(lib.IsDisliked(null, "https://www.youtube.com/watch?v=zz"));
    }

    [Fact]
    public void StoreRoundTripAndMigrationFrom1x()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spoka-lib-" + Guid.NewGuid());
        try
        {
            var settings = new AppSettings();
            settings.Playlist.Add(new TrackItem { FilePath = @"C:\music\one.mp3" });
            settings.Playlist.Add(new TrackItem { FilePath = @"C:\music\two.mp3", DisplayTitle = "Deux" });
            var lib = new MusicLibrary();
            Assert.True(LibraryStore.Migrate(settings, lib));
            Assert.Equal(2, lib.Tracks.Count);
            Assert.Equal(LibraryStore.MigratedPlaylistName, lib.Playlists.Single().Name);
            Assert.Equal(2, lib.Data.SavedQueue.Count);

            var store = new LibraryStore(dir);
            store.Save(lib.Data);
            var back = new MusicLibrary(store.Load());
            Assert.Equal("Deux", back.Tracks[1].Title);
            Assert.Equal(back.Tracks.Select(t => t.Id), back.Playlists[0].TrackIds);
            // Files do not exist on disk: tracks come back as remote.
            Assert.Equal(TrackState.Remote, back.Tracks[0].State);

            File.WriteAllText(store.FilePath, "{ corrupted");
            Assert.Empty(store.Load().Tracks);
            Assert.True(store.LoadFailed);
            Assert.NotEmpty(Directory.GetFiles(dir, "library.json.*.bak"));
            // An unreadable library is never overwritten by an empty one.
            Assert.Throws<IOException>(() => store.Save(new MusicLibraryData()));
            Assert.Equal("{ corrupted", File.ReadAllText(store.FilePath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CompletesArtistAndThumbnailOfMigratedWebTracks()
    {
        var data = new MusicLibraryData { Version = 1 };
        data.Tracks.Add(new TrackItem { DisplayTitle = "SDM - Yakalelo (Clip officiel)", SourceUrl = "https://www.youtube.com/watch?v=abc123XYZ_0" });
        data.Tracks.Add(new TrackItem { DisplayTitle = "Mr. Ocho", SourceUrl = "https://youtu.be/def456" });
        var lib = new MusicLibrary(data);
        Assert.Equal("SDM", lib.Tracks[0].Artist);
        Assert.Equal("Yakalelo", lib.Tracks[0].Title);
        Assert.Equal("https://i.ytimg.com/vi/abc123XYZ_0/hqdefault.jpg", lib.Tracks[0].ThumbnailUrl);
        Assert.Null(lib.Tracks[1].Artist);
        Assert.Equal("Mr. Ocho", lib.Tracks[1].Title);

        // Done once: a title the user keeps is not split again at the next start.
        lib.Tracks[1].DisplayTitle = "Queen - Live";
        var again = new MusicLibrary(lib.Data);
        Assert.Null(again.Tracks[1].Artist);
    }

    [Fact]
    public void SanitizeFixesDuplicateIdsAndDanglingPlaylistEntries()
    {
        var id = Guid.NewGuid();
        var data = new MusicLibraryData();
        data.Tracks.Add(new TrackItem { Id = id, DisplayTitle = "A" });
        data.Tracks.Add(new TrackItem { Id = id, DisplayTitle = "B" });
        data.Playlists.Add(new MusicPlaylist { Name = "P", TrackIds = [id, Guid.NewGuid()] });
        var lib = new MusicLibrary(data);
        Assert.Equal(2, lib.Tracks.Select(t => t.Id).Distinct().Count());
        Assert.Equal([id], lib.Playlists[0].TrackIds);
    }
}

public class ListeningStatsTests
{
    [Fact]
    public void ComputesRankingsStreaksAndPeaks()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var day = new DateTime(2026, 3, 2, 21, 0, 0); // a Monday
        var history = new List<HistoryEntry>
        {
            new() { TrackId = a, Title = "One More Time", Artist = "Daft Punk", PlayedAt = day, ListenedSeconds = 300 },
            new() { TrackId = a, Title = "One More Time", Artist = "Daft Punk", PlayedAt = day.AddDays(1), ListenedSeconds = 300 },
            new() { TrackId = b, Title = "Alors on danse", Artist = "Stromae", PlayedAt = day.AddDays(2), ListenedSeconds = 200 },
            // A skip: listed in the totals but not in the rankings.
            new() { TrackId = b, Title = "Alors on danse", Artist = "Stromae", PlayedAt = day.AddDays(2).AddMinutes(5), ListenedSeconds = 5 },
            new() { TrackId = b, Title = "Alors on danse", Artist = "Stromae", PlayedAt = day.AddDays(10), ListenedSeconds = 200 },
        };
        var r = ListeningStats.ForYear(history, 2026);
        Assert.Equal(5, r.Plays);
        Assert.Equal(2, r.DistinctTracks);
        Assert.Equal(2, r.DistinctArtists);
        Assert.Equal(3, r.LongestStreakDays);
        Assert.Equal(21, r.PeakHour);
        Assert.Equal("Daft Punk", r.TopArtists[0].Name);
        Assert.Equal(2, r.TopTracks.First(t => t.Name == "Alors on danse").Plays);
        Assert.Equal(day.Date, r.BiggestDay);
        Assert.Equal(5, r.MinutesByWeekday[0]);
        Assert.Equal("One More Time", r.First!.Title);
        Assert.True(ListeningStats.ForYear(history, 2025).IsEmpty);
    }

    [Fact]
    public void UsesTrackDurationWhenListenedTimeUnknown()
    {
        var id = Guid.NewGuid();
        var history = new[] { new HistoryEntry { TrackId = id, Title = "X", PlayedAt = new DateTime(2026, 1, 5) } };
        var r = ListeningStats.ForYear(history, 2026, _ => 120);
        Assert.Equal(2, r.TotalMinutes, 3);
    }
}
