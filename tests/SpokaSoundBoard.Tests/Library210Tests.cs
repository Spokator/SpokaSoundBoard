using System.Diagnostics;
using System.IO.Compression;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Music;
using Xunit.Abstractions;

namespace SpokaSoundBoard.Tests;

/// <summary>2.10: history in its own append-only file, background saves, indexed lookups, full backup.</summary>
public sealed class Library210Tests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("spoka210-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static MusicLibrary BigLibrary(int tracks, int plays)
    {
        var lib = new MusicLibrary();
        for (var i = 0; i < tracks; i++)
            lib.Tracks.Add(new TrackItem
            {
                DisplayTitle = $"Titre {i}", Artist = $"Artiste {i % 300}",
                SourceUrl = $"https://www.youtube.com/watch?v=v{i:D10}", ThumbnailUrl = $"https://i.ytimg.com/vi/v{i:D10}/hqdefault.jpg",
                DurationSeconds = 180 + i % 120,
            });
        lib.CreatePlaylist("Grosse playlist", lib.Tracks.Take(1000));
        var start = new DateTime(2025, 1, 1);
        for (var i = 0; i < plays; i++)
        {
            var entry = lib.RecordPlay(lib.Tracks[i % tracks], start.AddMinutes(i * 4));
            lib.AddListened(entry, 150);
        }
        return lib;
    }

    // ---- History file ---------------------------------------------------------------------------------------

    [Fact]
    public void History_LaterLinesUpdateEarlierOnes_AndACutLineIsSkipped()
    {
        var store = new HistoryStore(Path.Combine(_dir, HistoryStore.FileName));
        var a = new HistoryEntry { Title = "A" };
        var b = new HistoryEntry { Title = "B" };
        store.Append([HistoryStore.Serialize(a), HistoryStore.Serialize(b)]);
        a.ListenedSeconds = 42;
        store.Append([HistoryStore.Serialize(a)]);
        File.AppendAllText(store.FilePath, "{\"Id\":\"0000"); // crash in the middle of a line
        var c = new HistoryEntry { Title = "C" };
        store.Append([HistoryStore.Serialize(c)]);

        var loaded = store.Load(100);
        Assert.Equal(["A", "B", "C"], loaded.Select(e => e.Title));
        Assert.Equal(42, loaded[0].ListenedSeconds);
        Assert.Equal(2, store.Load(2).Count);
        Assert.Equal(["B", "C"], store.Load(2).Select(e => e.Title));

        store.Compact(100);
        Assert.Equal(3, File.ReadAllLines(store.FilePath).Length);
        Assert.Equal(3, store.LineCount);
    }

    [Fact]
    public void Migration_MovesTheHistoryOutOfLibraryJson()
    {
        // A 2.9 library.json, history inside.
        var old = new MusicLibrary();
        var track = old.AddOrGet(new TrackItem { DisplayTitle = "Zombie", SourceUrl = "https://youtu.be/6Ejga4kJUts" });
        old.RecordPlay(track, new DateTime(2026, 1, 2));
        var json = LibraryStore.Serialize(old.Data).Replace("\"Tracks\"", "\"History\":" +
            System.Text.Json.JsonSerializer.Serialize(old.Data.History) + ",\"Tracks\"");
        File.WriteAllText(Path.Combine(_dir, "library.json"), json);

        var store = new LibraryStore(_dir);
        var lib = new MusicLibrary(store.Load());
        Assert.Single(lib.History);
        Assert.Equal("Zombie", lib.History[0].Title);

        store.SaveInBackground(lib);
        store.Flush();
        Assert.True(File.Exists(Path.Combine(_dir, HistoryStore.FileName)));
        Assert.DoesNotContain("\"History\"", File.ReadAllText(store.FilePath));

        var again = new MusicLibrary(new LibraryStore(_dir).Load());
        Assert.Single(again.History);
        Assert.Equal(lib.History[0].Id, again.History[0].Id);
    }

    [Fact]
    public void BackgroundSave_AppendsOnlyWhatChanged_AndSkipsAnUnchangedLibrary()
    {
        var store = new LibraryStore(_dir);
        var lib = new MusicLibrary(store.Load());
        var t = lib.AddOrGet(new TrackItem { DisplayTitle = "Bella", SourceUrl = "https://youtu.be/aaaaaaaaaaa" });
        var entry = lib.RecordPlay(t);
        store.SaveInBackground(lib);
        store.Flush();
        Assert.Single(File.ReadAllLines(store.History.FilePath));

        lib.AddListened(entry, 30);
        lib.RecordPlay(t);
        store.SaveInBackground(lib);
        store.Flush();
        Assert.Equal(3, File.ReadAllLines(store.History.FilePath).Length); // the update + the new play

        var written = File.GetLastWriteTimeUtc(store.FilePath);
        Thread.Sleep(30);
        store.SaveInBackground(lib); // nothing changed
        store.Flush();
        Assert.Equal(written, File.GetLastWriteTimeUtc(store.FilePath));
        Assert.Equal(3, File.ReadAllLines(store.History.FilePath).Length);

        var back = new MusicLibrary(new LibraryStore(_dir).Load());
        Assert.Equal(2, back.History.Count);
        Assert.Equal(30, back.History[0].ListenedSeconds);
        Assert.Equal(2, back.Tracks[0].PlayCount);

        lib.ClearHistory();
        store.SaveInBackground(lib);
        store.Flush();
        Assert.Empty(new MusicLibrary(new LibraryStore(_dir).Load()).History);
    }

    [Fact]
    public void BackgroundSave_KeepsTheUiThreadFree_OnABigLibrary()
    {
        var lib = BigLibrary(5000, 10000);
        var store = new LibraryStore(_dir);
        store.Save(lib.Data); // first full write, like after the migration

        // Before 2.10: every save serialized library + history (same options) and wrote them on the UI thread.
        var legacy = new MusicLibraryData { Tracks = lib.Data.Tracks, Playlists = lib.Data.Playlists, LegacyHistory = lib.Data.History };
        var bytes = Array.Empty<byte>();
        var oldWay = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(LibraryStore.Serialize(legacy));
            File.WriteAllBytes(Path.Combine(_dir, "old-way.json"), bytes);
        }
        oldWay.Stop();

        var ui = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            lib.RecordPlay(lib.Tracks[7 + i]);
            store.SaveInBackground(lib);
        }
        ui.Stop();
        store.Flush();
        output.WriteLine($"ancienne sauvegarde (fil UI) : {oldWay.Elapsed.TotalMilliseconds / 3:0.0} ms, {bytes.Length / 1024} Ko ; " +
                         $"nouvelle, part du fil UI : {ui.Elapsed.TotalMilliseconds / 3:0.0} ms, library.json {new FileInfo(store.FilePath).Length / 1024} Ko");
        Assert.True(ui.Elapsed < oldWay.Elapsed / 3, "the UI thread only copies the objects");
        Assert.True(new FileInfo(store.FilePath).Length < bytes.Length / 2, "the history is no longer in library.json");
        // 10 003 plays: the oldest ones fall out of the 10 000 kept.
        Assert.Equal(MusicLibrary.MaxHistory, new MusicLibrary(new LibraryStore(_dir).Load()).History.Count);
    }

    [Fact]
    public void Snapshot_SavesExactlyWhatTheLiveObjectsHold()
    {
        var lib = BigLibrary(50, 20);
        lib.Tracks[3].IsFavorite = true;
        lib.Tracks[4].NormalizationDb = -3.5;
        lib.Tracks[5].SearchQuery = "Zaz - Éclair";
        lib.DislikeArtist("Quelqu'un");
        lib.Data.SavedQueue = [lib.Tracks[1].Id];
        lib.Data.SavedPositionSeconds = 42.5;

        var live = LibraryStore.Serialize(lib.Data);
        var snapshot = lib.Data.SnapshotForSave();
        Assert.Equal(live, LibraryStore.Serialize(snapshot));

        // Later edits of the live objects do not leak into a snapshot being written.
        lib.Tracks[0].DisplayTitle = "Changé";
        lib.Playlists[0].TrackIds.Clear();
        Assert.Equal(live, LibraryStore.Serialize(snapshot));
    }

    // ---- Index --------------------------------------------------------------------------------------------------

    [Fact]
    public void Index_GivesTheSameAnswersAsAScan_AndFollowsChanges()
    {
        var lib = new MusicLibrary();
        var a = lib.AddOrGet(new TrackItem { FilePath = @"C:\Music\A.mp3" });
        var b = lib.AddOrGet(new TrackItem { DisplayTitle = "B", SourceUrl = "https://www.youtube.com/watch?v=bbbbbbbbbbb&list=x" });
        var c = lib.AddOrGet(new TrackItem { DisplayTitle = "C", SearchQuery = "Jul - Bande organisée" });

        Assert.Same(a, lib.FindExisting(@"c:\music\a.MP3", null));
        Assert.Same(b, lib.FindExisting(null, "https://youtu.be/bbbbbbbbbbb"));
        Assert.Same(c, lib.FindExisting(null, null, "jul - bande organisée"));
        Assert.Same(b, lib.Find(b.Id));

        // Edits and removals are followed.
        a.FilePath = @"C:\Music\A2.mp3";
        Assert.Null(lib.FindExisting(@"C:\Music\A.mp3", null));
        Assert.Same(a, lib.FindExisting(@"C:\Music\A2.mp3", null));
        c.SearchQuery = "Jul - Tchikita";
        Assert.Same(c, lib.FindExisting(null, null, "Jul - Tchikita"));
        lib.Remove(b);
        Assert.Null(lib.Find(b.Id));
        Assert.Null(lib.FindExisting(null, "https://youtu.be/bbbbbbbbbbb"));

        // Two tracks with the same link (hand-edited file): the first one wins, as before.
        var first = new TrackItem { SourceUrl = "https://youtu.be/ddddddddddd" };
        var second = new TrackItem { SourceUrl = "https://youtu.be/ddddddddddd" };
        lib.Tracks.Add(first);
        lib.Tracks.Add(second);
        Assert.Same(first, lib.FindExisting(null, "https://youtu.be/ddddddddddd"));
    }

    [Fact]
    public void Index_ImportingABigPlaylist_IsFast()
    {
        var lib = BigLibrary(5000, 0);
        var imported = Enumerable.Range(4000, 2000) // half already known, half new
            .Select(i => new TrackItem { DisplayTitle = $"Titre {i}", SourceUrl = $"https://youtu.be/v{i:D10}" }).ToList();
        var watch = Stopwatch.StartNew();
        var playlist = lib.CreatePlaylist("Import", imported);
        watch.Stop();
        output.WriteLine($"import de 2 000 morceaux dans 5 000 : {watch.Elapsed.TotalMilliseconds:0.0} ms");
        Assert.Equal(2000, playlist.TrackIds.Count);
        Assert.Equal(6000, lib.Tracks.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    // ---- Full backup ------------------------------------------------------------------------------------------

    private (string Data, string Sound, string Song) MakeData(string name)
    {
        var data = Path.Combine(_dir, name);
        var media = Path.Combine(_dir, name + "-media");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(media);
        var sound = Path.Combine(media, "boom.wav");
        var song = Path.Combine(media, "chanson.mp3");
        File.WriteAllBytes(sound, Enumerable.Repeat((byte)1, 1000).ToArray());
        File.WriteAllBytes(song, Enumerable.Repeat((byte)2, 5000).ToArray());

        var settings = new AppSettings();
        settings.Pads.Add(new SoundPad { Name = "Boom", FilePath = sound });
        new SettingsStore(data).Save(settings);
        var lib = new MusicLibrary();
        var t = lib.AddOrGet(new TrackItem { DisplayTitle = "Chanson", FilePath = song });
        lib.CreatePlaylist("Soirée", [t]);
        lib.RecordPlay(t);
        new LibraryStore(data).Save(lib.Data);
        return (data, sound, song);
    }

    [Fact]
    public void Backup_ToAnotherPc_PutsTheFilesNextToTheDataAndFixesThePaths()
    {
        var (data, sound, song) = MakeData("pc1");
        var backup = Path.Combine(_dir, "sauvegarde" + FullBackup.Extension);
        var manifest = FullBackup.Create(data, backup, includeMusic: true, "2.10.0", "Installeur");
        Assert.Equal(1, manifest.Sounds);
        Assert.Equal(1, manifest.Tracks);
        Assert.Equal(1, manifest.Playlists);
        Assert.Equal(1, manifest.HistoryEntries);
        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(1, FullBackup.Inspect(backup).Sounds);

        // « Another PC »: the original media folder does not exist there.
        Directory.Delete(Path.GetDirectoryName(sound)!, true);
        var target = Path.Combine(_dir, "pc2");
        var result = FullBackup.Restore(backup, target);
        Assert.Equal(2, result.FilesExtracted);
        Assert.Null(result.SafetyCopy); // nothing to protect on a fresh install

        var settings = new SettingsStore(target).Load();
        var pad = settings.Pads.Single();
        Assert.True(File.Exists(pad.FilePath));
        Assert.StartsWith(Path.Combine(target, "Downloads", FullBackup.RestoredFolder), pad.FilePath);
        Assert.Equal(1000, new FileInfo(pad.FilePath).Length);

        var lib = new MusicLibrary(new LibraryStore(target).Load());
        Assert.True(File.Exists(lib.Tracks.Single().FilePath));
        Assert.Equal("Soirée", lib.Playlists.Single().Name);
        Assert.Single(lib.History);
    }

    [Fact]
    public void Backup_OnTheSamePc_ReusesTheFiles_AndKeepsACopyOfTheCurrentState()
    {
        var (data, sound, song) = MakeData("pc");
        var backup = Path.Combine(_dir, "sauvegarde" + FullBackup.Extension);
        FullBackup.Create(data, backup, includeMusic: false, "2.10.0", "Installeur");
        using (var zip = ZipFile.OpenRead(backup))
            Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("musique/"));

        // Changes made after the backup, then undone by restoring it.
        var store = new SettingsStore(data);
        var settings = store.Load();
        settings.Pads.Clear();
        store.Save(settings);

        var result = FullBackup.Restore(backup, data);
        Assert.Equal(0, result.FilesExtracted);
        Assert.Equal(1, result.FilesReused);
        Assert.NotNull(result.SafetyCopy);
        Assert.True(File.Exists(result.SafetyCopy));
        Assert.Equal(sound, new SettingsStore(data).Load().Pads.Single().FilePath);
        Assert.Equal(song, new MusicLibrary(new LibraryStore(data).Load()).Tracks.Single().FilePath);
    }

    [Fact]
    public void Backup_RefusesOtherFiles_AndNewerFormats()
    {
        var notABackup = Path.Combine(_dir, "pas-une-sauvegarde.zip");
        using (var zip = ZipFile.Open(notABackup, ZipArchiveMode.Create)) zip.CreateEntry("x.txt");
        Assert.Throws<InvalidDataException>(() => FullBackup.Inspect(notABackup));

        var newer = Path.Combine(_dir, "future" + FullBackup.Extension);
        using (var zip = ZipFile.Open(newer, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write("{\"Format\":99}");
        var error = Assert.Throws<InvalidDataException>(() => FullBackup.Inspect(newer));
        Assert.Contains("plus récente", error.Message);
    }
}
