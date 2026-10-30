using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SpokaSoundBoard.Core.Audio;

namespace SpokaSoundBoard.Core.Online;

public sealed record DownloadedMedia(string FilePath, string Title, string SourceUrl);

public sealed record DownloadProgress(double Percent, string Message);

public sealed class DownloadException(string message) : Exception(message);

/// <summary>
/// Imports audio from web links (YouTube, SoundCloud, MyInstants, TikTok, direct MP3 links…) with yt-dlp.
/// yt-dlp is downloaded on first use; ffmpeg is only fetched when a site delivers a format NAudio cannot read.
/// </summary>
public sealed partial class MediaDownloader
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string FilePrefix = "[SPKFILE]";
    private const string ProgressPrefix = "[SPKPROG]";
    private const string Separator = "|#|";

    private static readonly HttpClient Http = CreateHttpClient();
    private readonly SemaphoreSlim _toolsLock = new(1, 1);
    private bool _updatedThisSession;

    /// <param name="baseDirectory">Writable data folder (tools are kept up to date there).</param>
    /// <param name="bundledToolsDirectory">Optional read-only folder shipped by the installer, used as a seed.</param>
    public MediaDownloader(string baseDirectory, string? bundledToolsDirectory = null)
    {
        ToolsDirectory = Path.Combine(baseDirectory, "tools");
        CacheDirectory = Path.Combine(baseDirectory, "Downloads");
        BundledToolsDirectory = bundledToolsDirectory;
    }

    public string? BundledToolsDirectory { get; }

    /// <summary>Test hook: forces a yt-dlp format selector.</summary>
    internal string? FormatOverride { get; set; }

    public string ToolsDirectory { get; }
    public string CacheDirectory { get; }
    public string YtDlpPath => Path.Combine(ToolsDirectory, "yt-dlp.exe");
    public string FfmpegPath => Path.Combine(ToolsDirectory, "ffmpeg.exe");

    public static bool LooksLikeUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Downloads the audio behind <paramref name="url"/> into <paramref name="subFolder"/> of the cache.</summary>
    /// <param name="allowPlaylist">Import every item of a playlist link instead of a single video.</param>
    /// <param name="onItem">Called as soon as each file is ready (playlists are added progressively).</param>
    public async Task<IReadOnlyList<DownloadedMedia>> DownloadAsync(
        string url, string subFolder, bool allowPlaylist, Action<DownloadedMedia>? onItem,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        url = url.Trim();
        if (!LooksLikeUrl(url)) throw new DownloadException("Ce n'est pas un lien valide (il doit commencer par http:// ou https://).");

        await EnsureYtDlpAsync(progress, ct);
        var outputDir = Path.Combine(CacheDirectory, subFolder);
        Directory.CreateDirectory(outputDir);

        var result = await RunYtDlpAsync(url, outputDir, allowPlaylist, onItem, progress, ct);
        if (result.Items.Count == 0 && !_updatedThisSession)
        {
            // Sites change often: an outdated yt-dlp is the most common cause of failure.
            progress?.Report(new DownloadProgress(0, "Mise à jour de yt-dlp…"));
            _updatedThisSession = true;
            await UpdateYtDlpAsync(ct);
            result = await RunYtDlpAsync(url, outputDir, allowPlaylist, onItem, progress, ct);
        }

        if (result.Items.Count == 0)
            throw new DownloadException(FriendlyError(result.LastError));
        return result.Items;
    }

    private async Task<(List<DownloadedMedia> Items, string? LastError)> RunYtDlpAsync(
        string url, string outputDir, bool allowPlaylist, Action<DownloadedMedia>? onItem,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var args = new List<string>
        {
            "--no-config", "--encoding", "utf-8", "--ignore-errors", "--newline", "--progress", "--color", "never", "--windows-filenames", "--no-mtime",
            // MP3 / M4A / MP4 are played natively; other formats are converted afterwards.
            "-f", FormatOverride ?? "bestaudio[ext=m4a]/bestaudio[ext=mp3]/bestaudio[acodec^=mp4a]/bestaudio/best",
            "-P", outputDir,
            "-o", "%(title).80B [%(id)s].%(ext)s",
            "--progress-template", $"download:{ProgressPrefix}%(progress._percent_str)s{Separator}%(info.title)s{Separator}%(info.playlist_index)s{Separator}%(info.n_entries)s",
            "--print", $"after_move:{FilePrefix}%(filepath)s{Separator}%(title)s{Separator}%(webpage_url)s",
        };
        if (allowPlaylist) args.AddRange(["--yes-playlist", "--playlist-items", "1:200"]);
        else args.Add("--no-playlist");
        if (File.Exists(FfmpegPath)) args.AddRange(["--ffmpeg-location", ToolsDirectory]);
        args.Add("--");
        args.Add(url);

        var items = new List<DownloadedMedia>();
        string? lastError = null;
        progress?.Report(new DownloadProgress(0, "Recherche du média…"));

        var psi = CreateStartInfo(YtDlpPath, args);
        using var process = Process.Start(psi) ?? throw new DownloadException("Impossible de lancer yt-dlp.");
        using var registration = ct.Register(() => TryKill(process));

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) lastError = line;
        });

        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            {
                var parts = line[ProgressPrefix.Length..].Split(Separator);
                var percent = ParsePercent(parts[0]);
                var title = parts.Length > 1 ? parts[1] : "";
                var position = parts.Length > 3 && parts[2] != "NA" && parts[3] != "NA" ? $" ({parts[2]}/{parts[3]})" : "";
                progress?.Report(new DownloadProgress(percent, $"Téléchargement{position} : {title}"));
            }
            else if (line.StartsWith(FilePrefix, StringComparison.Ordinal))
            {
                var parts = line[FilePrefix.Length..].Split(Separator);
                var path = parts[0].Trim();
                var title = parts.Length > 1 ? parts[1] : Path.GetFileNameWithoutExtension(path);
                var source = parts.Length > 2 && LooksLikeUrl(parts[2]) ? parts[2] : url;
                if (!File.Exists(path)) continue;

                try
                {
                    path = await EnsurePlayableAsync(path, progress, ct);
                    var item = new DownloadedMedia(path, title, source);
                    items.Add(item);
                    onItem?.Invoke(item);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex.Message;
                }
            }
        }

        await process.WaitForExitAsync(ct);
        await stderrTask;
        return (items, lastError);
    }

    /// <summary>YouTube search through yt-dlp ("ytsearchN:"), without downloading anything.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchYouTubeAsync(string query, int count, CancellationToken ct)
    {
        await EnsureYtDlpAsync(null, ct);
        var args = new[]
        {
            "--no-config", "--encoding", "utf-8", "--flat-playlist", "--ignore-errors",
            "--print", $"%(id)s{Separator}%(title)s{Separator}%(channel,uploader)s{Separator}%(duration)s{Separator}%(live_status)s",
            "--", $"ytsearch{Math.Clamp(count, 1, 30)}:{query.Trim()}",
        };
        var psi = CreateStartInfo(YtDlpPath, args);
        using var process = Process.Start(psi) ?? throw new DownloadException("Impossible de lancer yt-dlp.");
        using var registration = ct.Register(() => TryKill(process));
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        var results = new List<SearchResult>();
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            var parts = line.Split(Separator);
            if (parts.Length < 4 || string.IsNullOrWhiteSpace(parts[0]) || parts[0] == "NA") continue;
            var id = parts[0];
            TimeSpan? duration = double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
            var isLive = parts.Length > 4 && parts[4].Contains("live", StringComparison.OrdinalIgnoreCase) && parts[4] != "not_live" && parts[4] != "was_live";
            if (isLive) continue;
            var channel = parts[2] == "NA" ? "YouTube" : parts[2];
            results.Add(new SearchResult(parts[1], channel, $"https://www.youtube.com/watch?v={id}", null, duration,
                $"https://i.ytimg.com/vi/{id}/mqdefault.jpg", SearchSource.YouTube, null));
        }
        await process.WaitForExitAsync(ct);
        if (results.Count == 0)
        {
            var error = (await errorTask).Split('\n').LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));
            if (error != null) throw new DownloadException(FriendlyError(error));
        }
        return results;
    }

    /// <summary>Downloads a direct audio file (e.g. a MyInstants MP3) without yt-dlp.</summary>
    public async Task<DownloadedMedia> DownloadDirectAsync(string audioUrl, string title, string pageUrl, string subFolder,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var outputDir = Path.Combine(CacheDirectory, subFolder);
        Directory.CreateDirectory(outputDir);
        var extension = Path.GetExtension(new Uri(audioUrl).AbsolutePath);
        if (string.IsNullOrEmpty(extension) || extension.Length > 5) extension = ".mp3";
        var target = Path.Combine(outputDir, SafeFileName(title) + extension);
        if (!File.Exists(target))
            await DownloadFileAsync(audioUrl, target, progress, "Téléchargement", ct);
        target = await EnsurePlayableAsync(target, progress, ct);
        return new DownloadedMedia(target, title, pageUrl);
    }

    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 80) clean = clean[..80];
        return clean.Length == 0 ? "son" : clean;
    }

    /// <summary>Converts formats NAudio cannot read (Opus, WebM…) to MP3 with ffmpeg.</summary>
    private async Task<string> EnsurePlayableAsync(string path, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (await Task.Run(() => CanOpen(path), ct)) return path;

        await EnsureFfmpegAsync(progress, ct);
        progress?.Report(new DownloadProgress(100, "Conversion en MP3…"));
        var target = Path.ChangeExtension(path, ".mp3");
        var psi = CreateStartInfo(FfmpegPath, ["-y", "-loglevel", "error", "-i", path, "-vn", "-c:a", "libmp3lame", "-q:a", "2", target]);
        using var process = Process.Start(psi) ?? throw new DownloadException("Impossible de lancer ffmpeg.");
        using var registration = ct.Register(() => TryKill(process));
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || !File.Exists(target))
            throw new DownloadException("Conversion impossible : " + (await error).Trim());

        TryDelete(path);
        return target;
    }

    private static bool CanOpen(string path)
    {
        if (!AudioFileLoader.IsSupported(path)) return false;
        try
        {
            var (reader, _) = AudioFileLoader.Open(path, MixerCore.SampleRate);
            var ok = reader.TotalTime > TimeSpan.Zero || reader.Length > 0;
            reader.Dispose();
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public async Task EnsureYtDlpAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(YtDlpPath)) return;
        await _toolsLock.WaitAsync(ct);
        try
        {
            if (File.Exists(YtDlpPath)) return;

            var bundled = BundledToolsDirectory != null ? Path.Combine(BundledToolsDirectory, "yt-dlp.exe") : null;
            if (bundled != null && File.Exists(bundled))
            {
                Directory.CreateDirectory(ToolsDirectory);
                File.Copy(bundled, YtDlpPath, true);
                return;
            }

            progress?.Report(new DownloadProgress(0, "Première utilisation : installation de yt-dlp…"));
            await DownloadFileAsync(YtDlpUrl, YtDlpPath, progress, "Installation de yt-dlp", ct);
            _updatedThisSession = true;
        }
        finally
        {
            _toolsLock.Release();
        }
    }

    private async Task UpdateYtDlpAsync(CancellationToken ct)
    {
        await _toolsLock.WaitAsync(ct);
        try
        {
            var psi = CreateStartInfo(YtDlpPath, ["-U"]);
            using var process = Process.Start(psi);
            if (process == null) return;
            using var registration = ct.Register(() => TryKill(process));
            _ = process.StandardError.ReadToEndAsync(ct);
            await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Update failures are not fatal; the original error will be reported.
        }
        finally
        {
            _toolsLock.Release();
        }
    }

    private async Task EnsureFfmpegAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(FfmpegPath)) return;
        await _toolsLock.WaitAsync(ct);
        try
        {
            if (File.Exists(FfmpegPath)) return;
            var zip = Path.Combine(ToolsDirectory, "ffmpeg.zip");
            await DownloadFileAsync(FfmpegUrl, zip, progress, "Installation du convertisseur audio (une seule fois)", ct);
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zip);
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new DownloadException("ffmpeg.exe introuvable dans l'archive.");
                entry.ExtractToFile(FfmpegPath, true);
            }, ct);
            TryDelete(zip);
        }
        finally
        {
            _toolsLock.Release();
        }
    }

    private async Task DownloadFileAsync(string url, string target, IProgress<DownloadProgress>? progress, string label, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".part";
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report(new DownloadProgress(done * 100.0 / total.Value, $"{label}… {done / 1048576} / {total.Value / 1048576} Mo"));
                }
            }
            File.Move(temp, target, true);
        }
        catch (HttpRequestException ex)
        {
            TryDelete(temp);
            throw new DownloadException($"Téléchargement impossible ({ex.Message}). Vérifiez votre connexion Internet.");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        return psi;
    }

    internal static double ParsePercent(string text)
    {
        var m = PercentRegex().Match(text);
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 100) : 0;
    }

    internal static string FriendlyError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "Aucun son n'a pu être récupéré depuis ce lien.";
        var e = error.Replace("ERROR:", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (e.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return "Ce site n'est pas pris en charge.";
        if (e.Contains("Private video", StringComparison.OrdinalIgnoreCase)) return "Cette vidéo est privée.";
        if (e.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase)) return "Cette vidéo est soumise à une limite d'âge.";
        if (e.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)) return "Cette vidéo n'est pas disponible.";
        if (e.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase) || e.Contains("Unable to download webpage", StringComparison.OrdinalIgnoreCase))
            return "Impossible d'accéder au site. Vérifiez votre connexion Internet.";
        return e.Length > 220 ? e[..220] + "…" : e;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { /* already gone */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpokaSoundBoard/1.1");
        return client;
    }

    [GeneratedRegex(@"([\d.]+)\s*%")]
    private static partial Regex PercentRegex();
}
