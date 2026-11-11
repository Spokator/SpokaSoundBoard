using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpokaSoundBoard.Core.Audio;

namespace SpokaSoundBoard.Core.Online;

/// <param name="Title">Title as published by the source (use <see cref="ArtistTitle.Split"/> for a clean display title).</param>
/// <param name="Artist">Clean artist (metadata, "Artist - Title" or channel without " - Topic"/VEVO), when known.</param>
/// <param name="ThumbnailUrl">Cover / thumbnail (https://i.ytimg.com/vi/&lt;id&gt;/hqdefault.jpg for YouTube).</param>
public sealed record DownloadedMedia(
    string FilePath,
    string Title,
    string SourceUrl,
    string? Artist = null,
    double? DurationSeconds = null,
    string? ThumbnailUrl = null);

/// <summary>A flat (not downloaded) listing of a web playlist, mix or search.</summary>
internal sealed record FlatPlaylist(string? Title, string? ThumbnailUrl, IReadOnlyList<SearchResult> Entries);

public sealed record DownloadProgress(double Percent, string Message);

public sealed class DownloadException(string message) : Exception(message);

/// <summary>
/// Imports audio from web links (YouTube, SoundCloud, MyInstants, TikTok, direct MP3 links…) with yt-dlp.
/// yt-dlp is downloaded on first use; ffmpeg is only fetched when a site delivers a format NAudio cannot read.
/// Packaged (Microsoft Store) mode: the tools shipped in <see cref="AppEnvironment.BundledToolsDirectory"/> are used
/// as they are; nothing is ever downloaded or updated at runtime.
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

    /// <summary>Test hook: forces packaged mode on or off for this instance (null = <see cref="AppEnvironment.IsPackaged"/>).</summary>
    internal bool? PackagedOverride { get; set; }

    /// <summary>Test hook: folder holding the packaged tools (null = <see cref="AppEnvironment.BundledToolsDirectory"/>).</summary>
    internal string? PackagedToolsOverride { get; set; }

    private bool IsPackaged => PackagedOverride ?? AppEnvironment.IsPackaged;

    /// <summary>Writable tools folder (classic install). Packaged mode reads the tools from the package instead.</summary>
    public string ToolsDirectory { get; }
    public string CacheDirectory { get; }

    /// <summary>Folder the executables are actually run from.</summary>
    public string ActiveToolsDirectory => IsPackaged ? PackagedToolsOverride ?? AppEnvironment.BundledToolsDirectory : ToolsDirectory;
    public string YtDlpPath => Path.Combine(ActiveToolsDirectory, "yt-dlp.exe");
    public string FfmpegPath => Path.Combine(ActiveToolsDirectory, "ffmpeg.exe");

    public static bool LooksLikeUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Downloads one track (never a playlist) and returns it with its metadata. A file already in the cache for the
    /// same YouTube video id is returned immediately, without running yt-dlp.
    /// </summary>
    public async Task<DownloadedMedia> DownloadTrackAsync(string pageUrl, string subFolder, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        pageUrl = pageUrl.Trim();
        var cached = await Task.Run(() => FindCached(pageUrl), ct);
        if (cached != null) return cached;

        var items = await DownloadAsync(pageUrl, subFolder, allowPlaylist: false, null, progress, ct);
        return items[0];
    }

    /// <summary>Cached file for a YouTube link ("Title [id].ext" in any cache sub-folder), or null.</summary>
    internal DownloadedMedia? FindCached(string pageUrl)
    {
        var id = RecommendationService.YouTubeId(pageUrl);
        if (id == null || !Directory.Exists(CacheDirectory)) return null;
        try
        {
            var marker = $" [{id}]";
            foreach (var file in Directory.EnumerateFiles(CacheDirectory, $"*[{id}].*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.EndsWith(marker, StringComparison.Ordinal) || !AudioFileLoader.IsSupported(file)) continue;
                var info = new FileInfo(file);
                if (info.Length == 0) continue;
                var duration = TryGetDuration(file);
                if (duration == null) continue;
                return new DownloadedMedia(file, name[..^marker.Length], $"https://www.youtube.com/watch?v={id}", null,
                    duration, $"https://i.ytimg.com/vi/{id}/hqdefault.jpg");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable cache: download again.
        }
        return null;
    }

    private static double? TryGetDuration(string path)
    {
        try
        {
            var (reader, _) = AudioFileLoader.Open(path, MixerCore.SampleRate);
            using (reader)
                return reader.TotalTime > TimeSpan.Zero ? reader.TotalTime.TotalSeconds : null;
        }
        catch
        {
            return null;
        }
    }

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
        if (result.Items.Count == 0 && !_updatedThisSession && !IsPackaged)
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
            "--print", $"after_move:{FilePrefix}{FileTemplate}",
        };
        if (allowPlaylist) args.AddRange(["--yes-playlist", "--playlist-items", "1:200"]);
        else args.Add("--no-playlist");
        if (File.Exists(FfmpegPath)) args.AddRange(["--ffmpeg-location", ActiveToolsDirectory]);
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
                var parsed = ParseFileLine(line[FilePrefix.Length..], url);
                if (!File.Exists(parsed.FilePath)) continue;

                try
                {
                    var path = await EnsurePlayableAsync(parsed.FilePath, progress, ct);
                    var item = parsed with { FilePath = path };
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

    // Printed for every finished file: path, title, page, metadata artist, channel, duration, id, thumbnail, extractor.
    private const string FileTemplate =
        "%(filepath)s" + Separator + "%(title)s" + Separator + "%(webpage_url)s" + Separator + "%(artist,creator)s" + Separator +
        "%(channel,uploader)s" + Separator + "%(duration)s" + Separator + "%(id)s" + Separator + "%(thumbnail)s" + Separator + "%(extractor_key)s";

    /// <summary>Reads one <see cref="FileTemplate"/> line ("NA" marks missing fields).</summary>
    internal static DownloadedMedia ParseFileLine(string payload, string fallbackUrl)
    {
        var parts = payload.Split(Separator);
        string? Part(int i) => i < parts.Length && parts[i].Trim() is { Length: > 0 } v && v != "NA" ? v : null;

        var path = Part(0) ?? "";
        var title = Part(1) ?? Path.GetFileNameWithoutExtension(path);
        var source = Part(2) is { } page && LooksLikeUrl(page) ? page : fallbackUrl;
        var metadataArtist = Part(3);
        var channel = Part(4);
        double? duration = double.TryParse(Part(5), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
        var id = Part(6);
        var thumbnail = Part(7) is { } thumb && LooksLikeUrl(thumb) ? thumb : null;
        var youTubeId = RecommendationService.YouTubeId(source)
                        ?? (Part(8)?.StartsWith("Youtube", StringComparison.OrdinalIgnoreCase) == true && id is { Length: 11 } ? id : null);
        if (youTubeId != null) thumbnail = YouTubeThumbnail(youTubeId);

        var artist = metadataArtist != null ? FirstCredit(metadataArtist) : ArtistTitle.Split(title, channel).Artist;
        return new DownloadedMedia(path, title, source, artist, duration, thumbnail);
    }

    internal static string YouTubeThumbnail(string id) => $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";

    /// <summary>yt-dlp lists several artists as "A, B, C": keep them, but drop duplicates.</summary>
    private static string FirstCredit(string artists)
    {
        var names = artists.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == 0 ? artists.Trim() : string.Join(", ", names);
    }

    /// <summary>YouTube search through yt-dlp ("ytsearchN:"), without downloading anything. Results are ranked for listening.</summary>
    public Task<IReadOnlyList<SearchResult>> SearchYouTubeAsync(string query, int count, CancellationToken ct) =>
        SearchYouTubeAsync(query, count, null, ct);

    /// <summary>
    /// YouTube search ranked with <see cref="SearchRanker"/>: a few more results than requested are fetched, ranked, then
    /// the best <paramref name="count"/> are kept. Live streams are left out (they cannot be downloaded as files).
    /// </summary>
    /// <param name="expectedDuration">Duration of the original track when known (imported playlists): the closest version wins.</param>
    public async Task<IReadOnlyList<SearchResult>> SearchYouTubeAsync(string query, int count, TimeSpan? expectedDuration, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        count = Math.Clamp(count, 1, 30);
        var fetch = Math.Min(40, Math.Max(count + 4, 8));
        var listing = await GetFlatPlaylistAsync($"ytsearch{fetch}:{query}", fetch, false, TimeSpan.FromSeconds(45), ct);
        var candidates = listing.Entries.Where(r => !r.IsLive).ToList();
        return SearchRanker.Rank(query, candidates, expectedDuration).Take(count).ToList();
    }

    /// <summary>SoundCloud search through yt-dlp ("scsearchN:"), in SoundCloud's order.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchSoundCloudAsync(string query, int count, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        count = Math.Clamp(count, 1, 30);
        var listing = await GetFlatPlaylistAsync($"scsearch{count}:{query}", count, false, TimeSpan.FromSeconds(45), ct);
        return listing.Entries.Where(r => !r.IsLive).Take(count).ToList();
    }

    /// <summary>Lists a playlist / mix / search without downloading anything (<c>yt-dlp --flat-playlist -J</c>).</summary>
    /// <param name="lowPriority">Background work (recommendations): the process runs below normal priority.</param>
    internal async Task<FlatPlaylist> GetFlatPlaylistAsync(string target, int limit, bool lowPriority, TimeSpan timeout, CancellationToken ct)
    {
        var root = await RunYtDlpJsonAsync(
            ["--flat-playlist", "-J", "--playlist-end", Math.Clamp(limit, 1, 1000).ToString(CultureInfo.InvariantCulture), "--", target],
            timeout, lowPriority, ct);
        return ParseFlatPlaylist(root, limit);
    }

    internal static FlatPlaylist ParseFlatPlaylist(JsonElement root, int limit)
    {
        var entries = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var isList = root.Get("entries") is { ValueKind: JsonValueKind.Array };
        if (isList)
        {
            foreach (var entry in root.Get("entries")!.Value.EnumerateArray())
            {
                if (FromYtDlpEntry(entry) is not { } r || !seen.Add(r.PageUrl)) continue;
                entries.Add(r);
                if (entries.Count >= limit) break;
            }
        }
        else if (FromYtDlpEntry(root) is { } single)
        {
            entries.Add(single);
        }

        var thumbnail = (isList ? BestThumbnail(root) : null) ?? entries.FirstOrDefault()?.ThumbnailUrl;
        var title = isList ? root.Str("title") : null;
        return new FlatPlaylist(title, thumbnail, entries);
    }

    /// <summary>Converts one yt-dlp info dict (flat or full) to a search result, or null for private / deleted items.</summary>
    internal static SearchResult? FromYtDlpEntry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = e.Str("id");
        var extractor = e.Str("ie_key", "extractor_key", "extractor") ?? "";
        var url = e.Str("webpage_url", "url", "original_url");
        var isYouTube = extractor.StartsWith("Youtube", StringComparison.OrdinalIgnoreCase) || (url != null && RecommendationService.YouTubeId(url) != null);
        var isSoundCloud = extractor.Contains("soundcloud", StringComparison.OrdinalIgnoreCase)
                           || url?.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase) == true;

        var youTubeId = isYouTube ? (id is { Length: 11 } ? id : RecommendationService.YouTubeId(url)) : null;
        var page = youTubeId != null ? $"https://www.youtube.com/watch?v={youTubeId}" : url != null && LooksLikeUrl(url) ? url : null;
        if (page == null) return null;

        var title = e.Str("title", "fulltitle", "track") ?? TitleFromSlug(page);
        if (title == null || title is "[Private video]" or "[Deleted video]" or "[Vidéo privée]" or "[Vidéo supprimée]") return null;

        var channel = e.Str("channel", "uploader", "creator");
        var metadataArtist = e.Str("artist");
        var artist = metadataArtist != null ? FirstCredit(metadataArtist) : ArtistTitle.Split(title, channel).Artist;

        TimeSpan? duration = null;
        if (e.Num("duration") is { } seconds and > 0)
            duration = TimeSpan.FromSeconds(seconds > 200_000 ? seconds / 1000 : seconds);
        long? views = e.Num("view_count") is { } v and >= 0 ? (long)v : null;
        var isLive = e.Bool("is_live") || e.Str("live_status") is "is_live" or "is_upcoming";

        var thumbnail = youTubeId != null ? YouTubeThumbnail(youTubeId) : BestThumbnail(e);
        var source = isSoundCloud ? SearchSource.SoundCloud : SearchSource.YouTube;
        var subtitle = channel ?? (isSoundCloud ? "SoundCloud" : isYouTube ? "YouTube" : new Uri(page).Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase));
        return new SearchResult(title, subtitle, page, null, duration, thumbnail, source, null, artist, views, isLive);
    }

    private static string? BestThumbnail(JsonElement e)
    {
        if (e.Str("thumbnail") is { } t && LooksLikeUrl(t)) return t;
        string? best = null;
        double bestScore = -1;
        foreach (var thumb in e.Array("thumbnails"))
        {
            var url = thumb.Str("url");
            if (url == null || !LooksLikeUrl(url)) continue;
            var width = thumb.Num("width") ?? 0;
            // Prefer a medium-size picture (250-800 px) over huge originals; otherwise the largest / last one.
            var score = width is >= 250 and <= 800 ? 10_000 + width : width;
            if (score >= bestScore)
            {
                best = url;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>"https://soundcloud.com/artist/my-track-name" → "my track name".</summary>
    private static string? TitleFromSlug(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var slug = uri.Segments.LastOrDefault()?.Trim('/');
        if (string.IsNullOrEmpty(slug)) return null;
        slug = Uri.UnescapeDataString(slug).Replace('-', ' ').Replace('_', ' ').Trim();
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>Runs yt-dlp with JSON output (-J) and returns the parsed root. Failures become a friendly <see cref="DownloadException"/>.</summary>
    private async Task<JsonElement> RunYtDlpJsonAsync(IReadOnlyList<string> extraArgs, TimeSpan timeout, bool lowPriority, CancellationToken ct)
    {
        await EnsureYtDlpAsync(null, ct);
        var args = new List<string> { "--no-config", "--encoding", "utf-8", "--ignore-errors", "--no-warnings", "--color", "never" };
        args.AddRange(extraArgs);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        Process process;
        try
        {
            process = Process.Start(CreateStartInfo(YtDlpPath, args)) ?? throw new DownloadException("Impossible de lancer yt-dlp.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new DownloadException($"Impossible de lancer yt-dlp ({ex.Message}).");
        }

        using (process)
        {
            if (lowPriority)
            {
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* already gone or not allowed */ }
            }
            using var registration = timeoutCts.Token.Register(() => TryKill(process));
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            string stdout;
            try
            {
                stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DownloadException("yt-dlp ne répond pas (délai dépassé). Vérifiez votre connexion Internet.");
            }
            var stderr = await stderrTask;

            var json = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
            if (json != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    return doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Reported below.
                }
            }
            var error = stderr.Split('\n').LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));
            throw new DownloadException(FriendlyError(error ?? "Aucun résultat n'a été trouvé."));
        }
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

    /// <summary>Makes sure yt-dlp is available. Packaged mode only checks the shipped copy (never downloads it).</summary>
    public async Task EnsureYtDlpAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(YtDlpPath)) return;
        if (IsPackaged) throw new DownloadException(MissingPackagedTool("yt-dlp"));
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

    private static string MissingPackagedTool(string tool) =>
        $"Le composant {tool} est introuvable dans l'installation de SpokaSoundBoard. Réparez ou réinstallez l'application depuis le Microsoft Store.";

    private async Task UpdateYtDlpAsync(CancellationToken ct)
    {
        if (IsPackaged) return; // The Store package updates its tools itself.
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
        if (IsPackaged) throw new DownloadException(MissingPackagedTool("ffmpeg"));
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

    private const string BlockedMessage = "YouTube bloque temporairement le téléchargement (vérification anti-robot).";

    /// <summary>
    /// True when a download error means YouTube refused this video (anti-bot check, rate limit, unavailable, private,
    /// age restriction): worth trying <see cref="TrackResolver.FindAlternativeAsync"/>.
    /// </summary>
    public static bool IsBlockedOrUnavailable(Exception error) =>
        error is DownloadException && (error.Message == BlockedMessage
            || error.Message.Contains("limite temporairement", StringComparison.Ordinal)
            || error.Message.Contains("n'est pas disponible", StringComparison.Ordinal)
            || error.Message.Contains("est privée", StringComparison.Ordinal)
            || error.Message.Contains("limite d'âge", StringComparison.Ordinal));

    internal static string FriendlyError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "Aucun son n'a pu être récupéré depuis ce lien.";
        var e = error.Replace("ERROR:", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (e.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return "Ce site n'est pas pris en charge.";
        // Checked before "Sign in to confirm your age": this one is YouTube's anti-bot check.
        if (e.Contains("not a bot", StringComparison.OrdinalIgnoreCase)
            || (e.Contains("confirm you", StringComparison.OrdinalIgnoreCase) && !e.Contains("your age", StringComparison.OrdinalIgnoreCase)))
            return BlockedMessage;
        if (e.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase) || e.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
            return "YouTube limite temporairement les requêtes (trop de demandes). Réessayez dans quelques minutes.";
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd(OnlineHttp.AppUserAgent);
        return client;
    }

    [GeneratedRegex(@"([\d.]+)\s*%")]
    private static partial Regex PercentRegex();
}
