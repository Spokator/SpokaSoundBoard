using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Tests;

[Collection("Online")]
public class SearchAndGuardTests
{
    private const string MyInstantsSample = """
        <div class="instant">
          <div class="circle small-button-background" style="background-color:#FF0000;"></div>
          <button class="small-button" onclick="play('/media/sounds/vine-boom.mp3', 'loader-81126', 'vine-boom-sound-70972')" title="Play" type="button"></button>
          <a href="/en/instant/vine-boom-sound-70972/" class="instant-link link-secondary">VINE BOOM SOUND</a>
        </div>
        <div class="instant">
          <button class="small-button" onclick="play('/media/sounds/bruh.mp3', 'loader-2', 'bruh-2')" type="button"></button>
          <a href="/en/instant/bruh-2/" class="instant-link link-secondary">Bruh &amp; co</a>
        </div>
        """;

    [Fact]
    public void MyInstants_ParsesTitlesMp3AndColors()
    {
        var results = MediaSearch.ParseMyInstants(MyInstantsSample);
        Assert.Equal(2, results.Count);
        Assert.Equal("VINE BOOM SOUND", results[0].Title);
        Assert.Equal("https://www.myinstants.com/media/sounds/vine-boom.mp3", results[0].DirectAudioUrl);
        Assert.Equal("https://www.myinstants.com/en/instant/vine-boom-sound-70972/", results[0].PageUrl);
        Assert.Equal("#FF0000", results[0].Color);
        Assert.Equal("Bruh & co", results[1].Title);
        Assert.Null(results[1].Color);
    }

    [Fact]
    public void SafeFileName_RemovesInvalidCharacters()
    {
        Assert.Equal("a_b_c", MediaDownloader.SafeFileName("a/b:c"));
        Assert.Equal("son", MediaDownloader.SafeFileName("..."));
    }

    [Fact]
    public async Task YouTubeSearch_FindsTheRightSong_Online()
    {
        if (Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") != "1") return;
        var downloader = new MediaDownloader(Path.Combine(Path.GetTempPath(), "spoka_online_test"));
        var results = await new MediaSearch(downloader).SearchAsync(SearchSource.YouTube, "booba mona lisa", 8, CancellationToken.None);
        Assert.NotEmpty(results);
        Assert.Contains("Mona Lisa", results[0].Title, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(results[0].Duration);
        Assert.StartsWith("https://www.youtube.com/watch?v=", results[0].PageUrl);
    }

    [Fact]
    public async Task YouTubeSearch_KeepsAccents_Online()
    {
        if (Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") != "1") return;
        var downloader = new MediaDownloader(Path.Combine(Path.GetTempPath(), "spoka_online_test"));
        var results = await downloader.SearchYouTubeAsync("céline dion pour que tu m'aimes encore", 5, CancellationToken.None);
        Assert.DoesNotContain(results, r => r.Title.Contains('\uFFFD') || r.Subtitle.Contains('\uFFFD'));
        Assert.Contains(results, r => r.Title.Contains("é", StringComparison.Ordinal) || r.Title.Contains("É", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MyInstantsSearch_ReturnsPlayableSounds_Online()
    {
        if (Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") != "1") return;
        var downloader = new MediaDownloader(Path.Combine(Path.GetTempPath(), "spoka_online_test"));
        var results = await new MediaSearch(downloader).SearchAsync(SearchSource.MyInstants, "vine boom", 10, CancellationToken.None);
        Assert.True(results.Count >= 3);
        var first = results[0];
        var media = await downloader.DownloadDirectAsync(first.DirectAudioUrl!, first.Title, first.PageUrl, "Sons", null, CancellationToken.None);
        var (reader, _) = AudioFileLoader.Open(media.FilePath, MixerCore.SampleRate);
        using (reader) Assert.True(reader.TotalTime > TimeSpan.FromSeconds(0.2));
    }

    /// <summary>Simulates Discord's attenuation on our own session and checks it is undone.</summary>
    [Fact]
    public void SessionGuard_RestoresAttenuatedVolume()
    {
        var deviceId = DeviceService.GetDefaultId(DataFlow.Render, Role.Multimedia);
        if (deviceId == null) return;

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        output.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).ToSampleProvider());
        output.Play();
        Thread.Sleep(200);
        try
        {
            Assert.True(AudioSessionGuard.SetOwnSessionVolume(deviceId, 0.2f));
            Assert.True(AudioSessionGuard.Protect(deviceId) >= 1);
            Assert.Equal(0, AudioSessionGuard.Protect(deviceId));
        }
        finally
        {
            output.Stop();
        }
    }
}
