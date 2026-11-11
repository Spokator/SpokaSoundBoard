using System.Net;
using NAudio.Wave;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Music;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Tests;

/// <summary>2.2.2: hotkeys with modifiers held, unplugged devices, settings recovery, pad fades, duplicates, lyrics, cache.</summary>
public sealed class Fixes222Tests
{
    private const int Rate = 48000;

    private static HotkeyBinding Key(int vk, HotkeyModifiers modifiers = HotkeyModifiers.None)
    {
        var b = new HotkeyBinding();
        b.Set(vk, modifiers);
        return b;
    }

    [Fact]
    public void Hotkeys_StillFireWhileAModifierIsHeld()
    {
        const int f1 = 0x70, f2 = 0x71;
        HotkeyBinding[] bindings = [Key(f1), Key(f2, HotkeyModifiers.Ctrl), new HotkeyBinding()];

        Assert.Equal([0], HotkeyMatching.Select(bindings, f1, HotkeyModifiers.None));
        Assert.Equal([0], HotkeyMatching.Select(bindings, f1, HotkeyModifiers.Shift));                       // sprinting
        Assert.Equal([0], HotkeyMatching.Select(bindings, f1, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift)); // crouching too
        Assert.Empty(HotkeyMatching.Select(bindings, f2, HotkeyModifiers.None));                              // Ctrl is required
        Assert.Equal([1], HotkeyMatching.Select(bindings, f2, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt));
        Assert.Empty(HotkeyMatching.Select(bindings, 0, HotkeyModifiers.None));                               // unset binding
    }

    [Fact]
    public void Hotkeys_TheMostSpecificBindingWins()
    {
        const int f1 = 0x70;
        HotkeyBinding[] bindings = [Key(f1), Key(f1, HotkeyModifiers.Shift), Key(f1, HotkeyModifiers.Shift)];
        Assert.Equal([1, 2], HotkeyMatching.Select(bindings, f1, HotkeyModifiers.Shift));
        Assert.Equal([0], HotkeyMatching.Select(bindings, f1, HotkeyModifiers.Ctrl));
    }

    [Fact]
    public void Devices_AnUnpluggedDeviceIsReplacedOnlyForNow()
    {
        var headset = new AudioDevice("headset", "Casque", false);
        var speakers = new AudioDevice("speakers", "Haut-parleurs", false);

        Assert.Equal((headset, false), DeviceService.Resolve([headset, speakers], "headset", false, () => speakers));
        // Unplugged: the default device plays meanwhile, flagged so the caller does not save it.
        Assert.Equal((speakers, true), DeviceService.Resolve([speakers], "headset", false, () => speakers));
        // Never chosen: the automatic choice is a real one.
        Assert.Equal((speakers, false), DeviceService.Resolve([speakers], null, false, () => speakers));
        // « Aucun » chosen by the user stays « aucun ».
        Assert.Equal((null, false), DeviceService.Resolve([headset, speakers], null, true, () => speakers));
    }

    [Fact]
    public void Settings_OneBrokenSectionDoesNotEraseTheSoundboard()
    {
        const string json = """
            {
              "Voice": { "BeepStyle": "UnknownFutureStyle" },
              "Pads": [ { "Name": "Applaudissements", "FilePath": "C:\\sons\\clap.mp3" } ],
              "Mix": { "MusicVolume": 0.5 },
              "FirstRunDone": true
            }
            """;
        Assert.ThrowsAny<Exception>(() => SettingsStore.Deserialize(json));

        var recovered = SettingsStore.Recover(json, out var lost);
        Assert.NotNull(recovered);
        Assert.Equal(["effets de voix"], lost);
        Assert.Single(recovered.Pads);
        Assert.Equal("Applaudissements", recovered.Pads[0].Name);
        Assert.Equal(0.5f, recovered.Mix.MusicVolume);
        Assert.True(recovered.FirstRunDone);
        Assert.Equal(new VoiceEffectSettings().EchoDelayMs, recovered.Voice.EchoDelayMs);

        Assert.Null(SettingsStore.Recover("not json at all", out _));
    }

    [Fact]
    public void Settings_LoadKeepsACopyAndExplains()
    {
        var dir = Directory.CreateTempSubdirectory("spoka-settings-");
        try
        {
            var store = new SettingsStore(dir.FullName);
            File.WriteAllText(store.FilePath, """{ "Hotkeys": { "PlayPause": { "VirtualKey": "abc" } }, "Pads": [ { "Name": "ok" } ] }""");
            var settings = store.Load();
            Assert.Single(settings.Pads);
            Assert.Contains("raccourcis", store.LoadProblem);
            Assert.Single(dir.GetFiles("settings-*.bak.json"));

            store.Save(settings);
            store.Load();
            Assert.Null(store.LoadProblem);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static string WriteTone(DirectoryInfo dir, double seconds, float level = 0.5f)
    {
        var wav = Path.Combine(dir.FullName, $"tone-{Guid.NewGuid():N}.wav");
        using var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        for (var i = 0; i < (int)(Rate * seconds); i++) { w.WriteSample(level); w.WriteSample(level); }
        return wav;
    }

    [Fact]
    public async Task SoundPads_FadeInAndOutWithoutClicks()
    {
        var dir = Directory.CreateTempSubdirectory("spoka-fades-");
        try
        {
            var wav = WriteTone(dir, 1);
            using var player = new SoundPadPlayer(Rate);
            var id = Guid.NewGuid();
            await player.PlayAsync(id, wav, 1f, loop: false);

            var block = new float[960 * 2]; // 20 ms
            player.Read(block, 0, block.Length);
            Assert.True(block[0] < 0.01f, "starts from silence");
            Assert.Equal(0.5f, block[^1], 3);
            for (var i = 2; i < block.Length; i += 2) Assert.True(block[i] - block[i - 2] < 0.01f, "no jump while fading in");

            player.Stop(id);
            Assert.False(player.IsPlaying(id));
            Assert.Equal(0, player.ActiveCount);
            player.Read(block, 0, block.Length);
            Assert.True(block[0] > 0.45f, "still audible right after the stop");
            Assert.Equal(0f, block[^1]);
            for (var i = 2; i < block.Length; i += 2) Assert.True(block[i - 2] - block[i] < 0.01f, "no jump while fading out");

            player.Read(block, 0, block.Length);
            Assert.All(block, x => Assert.Equal(0f, x));
        }
        finally
        {
            await Task.Delay(200);
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Library_ReimportingAPlaylistDoesNotDuplicateItsSongs()
    {
        static List<TrackItem> Import() =>
        [
            new() { DisplayTitle = "Mona Lisa", Artist = "Booba", SearchQuery = "Booba - Mona Lisa", State = TrackState.Remote },
            new() { DisplayTitle = "Bande organisée", Artist = "Jul", SearchQuery = "Jul - Bande organisée", State = TrackState.Remote },
        ];
        var library = new MusicLibrary();
        library.CreatePlaylist("Spotify", Import(), "https://open.spotify.com/playlist/x");
        // The first song was played meanwhile: it now has a YouTube link.
        library.Tracks[0].SourceUrl = "https://www.youtube.com/watch?v=abcdefghijk";
        library.CreatePlaylist("Spotify", Import(), "https://open.spotify.com/playlist/x");

        Assert.Equal(2, library.Tracks.Count);
        Assert.Equal(library.Playlists[0].TrackIds, library.Playlists[1].TrackIds);
    }

    [Fact]
    public async Task Lyrics_RetryAsksAgainAndServerErrorsAreNotRemembered()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spoka-lyrics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var status = HttpStatusCode.ServiceUnavailable;
            var calls = 0;
            Task<(HttpStatusCode, string)> Fake(string url, CancellationToken _)
            {
                calls++;
                if (url.Contains("/api/get")) return Task.FromResult((HttpStatusCode.NotFound, "{}"));
                return Task.FromResult((status, status == HttpStatusCode.OK ? "[]" : ""));
            }
            var service = new LyricsService(dir) { FetchOverride = Fake };

            await Assert.ThrowsAsync<DownloadException>(() => service.GetAsync("Titre", "Artiste", null, CancellationToken.None));
            status = HttpStatusCode.OK;
            Assert.Null(await service.GetAsync("Titre", "Artiste", null, CancellationToken.None)); // asked again, not a cached error
            var afterMiss = calls;

            Assert.Null(await service.GetAsync("Titre", "Artiste", null, CancellationToken.None));
            Assert.Equal(afterMiss, calls); // an ordinary miss is remembered…
            await service.GetAsync("Titre", "Artiste", null, CancellationToken.None, retry: true);
            Assert.True(calls > afterMiss); // …but « Réessayer » asks lrclib again
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void DownloadCache_DeletesOnlyUnusedOldDownloads()
    {
        var root = Directory.CreateTempSubdirectory("spoka-cache-");
        try
        {
            string Make(string folder, string name, int daysOld)
            {
                var path = Path.Combine(root.FullName, folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
                return path;
            }
            var used = Make("Musique", "used.mp3", 30);
            var orphan = Make("Musique", "removed.mp3", 30);
            var recent = Make("Musique", "just-downloaded.mp3", 0);
            var padOrphan = Make("Sons", "deleted-pad.mp3", 10);
            var elsewhere = Make("Apercus", "preview.mp3", 30);

            var (files, _) = DownloadCache.DeleteOrphans(root.FullName, ["Musique", "Sons"], [used, null, ""], DateTime.UtcNow);

            Assert.Equal(2, files);
            Assert.True(File.Exists(used));
            Assert.False(File.Exists(orphan));
            Assert.True(File.Exists(recent));
            Assert.False(File.Exists(padOrphan));
            Assert.True(File.Exists(elsewhere));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Downloads_SameTitleDifferentLinkGetsItsOwnFile()
    {
        Assert.NotEqual(MediaDownloader.ShortHash("https://www.myinstants.com/media/sounds/a.mp3"),
                        MediaDownloader.ShortHash("https://www.myinstants.com/media/sounds/b.mp3"));
        Assert.Equal(8, MediaDownloader.ShortHash("x").Length);
    }
}
