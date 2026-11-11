using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Tests;

/// <summary>2.5.1: downloaded tools checked against the published SHA-256, renamed preset.</summary>
public sealed class Security251Tests
{
    private const string Hash = "f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d";

    [Fact]
    public void Checksums_FindTheListedFile()
    {
        var sums = $"""
            0000000000000000000000000000000000000000000000000000000000000000  yt-dlp
            {Hash.ToUpperInvariant()}  yt-dlp.exe
            1111111111111111111111111111111111111111111111111111111111111111 *yt-dlp_x86.exe
            """;
        Assert.Equal(Hash, Checksums.Find(sums, "yt-dlp.exe"));
        Assert.Equal(new string('1', 64), Checksums.Find(sums, "yt-dlp_x86.exe"));
        Assert.Null(Checksums.Find(sums, "ffmpeg.zip"));
        Assert.Null(Checksums.Find("not a list", "yt-dlp.exe"));
        Assert.Null(Checksums.Find("zz89d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d  yt-dlp.exe", "yt-dlp.exe"));
    }

    [Fact]
    public async Task Checksums_HashFiles()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await Checksums.FileSha256Async(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Checksums_ArePublished_Online()
    {
        if (Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") != "1") return;
        using var http = new HttpClient();
        var ytdlp = await http.GetStringAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS");
        Assert.NotNull(Checksums.Find(ytdlp, "yt-dlp.exe"));
        var ffmpeg = await http.GetStringAsync("https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256");
        Assert.NotNull(Checksums.Find(ffmpeg, "ffmpeg-master-latest-win64-gpl.zip"));
    }

    [Fact]
    public async Task YtDlp_InstallsOnlyWhenTheHashMatches_Online()
    {
        if (Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") != "1") return;
        var dir = Directory.CreateTempSubdirectory("spoka-ytdlp-");
        try
        {
            var downloader = new MediaDownloader(dir.FullName) { PackagedOverride = false };
            await downloader.EnsureYtDlpAsync(null, CancellationToken.None);
            var exe = Path.Combine(dir.FullName, "tools", "yt-dlp.exe");
            Assert.True(File.Exists(exe));
            Assert.False(File.Exists(exe + ".part"));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Presets_TheDarkLordKeepsItsVoiceUnderANewName()
    {
        Assert.Contains(BuiltInPresets.All, p => p.Name == BuiltInPresets.DarkLordName);
        Assert.DoesNotContain(BuiltInPresets.All, p => p.Name.Contains("Vador"));
        var s = SettingsStore.Deserialize("""{ "SelectedPresetName": "Dark Vador" }""");
        Assert.Equal(BuiltInPresets.DarkLordName, s.SelectedPresetName);
    }
}
