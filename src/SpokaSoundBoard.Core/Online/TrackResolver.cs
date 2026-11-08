using System.Text.RegularExpressions;

namespace SpokaSoundBoard.Core.Online;

/// <summary>
/// Finds a playable page for a track known by its metadata only (Spotify, Deezer, Apple imports, recommendations):
/// YouTube first (ranked against the original duration), then SoundCloud when YouTube has nothing convincing.
/// </summary>
public sealed partial class TrackResolver(MediaDownloader downloader)
{
    private const double YouTubeDurationTolerance = 0.25;
    private const double FallbackDurationTolerance = 0.15;
    private const double MinKeywordRatio = 0.6;
    private const double LastResortKeywordRatio = 0.34;

    private static readonly HashSet<string> NoiseWords = new(
        "official video audio clip officiel officielle lyrics paroles hd hq remastered remaster version feat ft the and de du des le la les topic".Split(' '),
        StringComparer.Ordinal);

    /// <summary>
    /// Best playable result for "artist - title": the first ranked YouTube result that really matches (keywords, duration
    /// ±25 %), otherwise a validated SoundCloud result (keywords ≥ 60 %, duration ±15 %, no unrequested remix / cover / live),
    /// otherwise the top YouTube result when it is still loosely related. Null when nothing fits.
    /// </summary>
    public async Task<SearchResult?> ResolveAsync(string? artist, string title, TimeSpan? duration, CancellationToken ct)
    {
        artist = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();
        title = title?.Trim() ?? "";
        if (title.Length == 0) return null;
        var query = artist != null ? $"{artist} - {title}" : title;

        IReadOnlyList<SearchResult> youTube = [];
        DownloadException? youTubeError = null;
        try
        {
            youTube = await downloader.SearchYouTubeAsync(query, 6, duration, ct);
        }
        catch (DownloadException ex)
        {
            youTubeError = ex;
        }

        var match = youTube.FirstOrDefault(r => Matches(r, artist, title, duration, YouTubeDurationTolerance));
        if (match != null) return match;

        var fallback = await SearchSoundCloudAsync(artist, title, duration, ct);
        if (fallback != null) return fallback;

        if (youTube.Count > 0 && KeywordRatio(youTube[0], artist, title) >= LastResortKeywordRatio && !IsUnrequestedDerivative(youTube[0], artist, title))
            return youTube[0];
        if (youTubeError != null) throw youTubeError;
        return null;
    }

    /// <summary>
    /// Same track elsewhere when the download of <paramref name="failed"/> is refused (YouTube anti-bot check, removed or
    /// restricted video): SoundCloud first, then another YouTube upload. Only convincing matches are returned.
    /// </summary>
    public async Task<SearchResult?> FindAlternativeAsync(SearchResult failed, CancellationToken ct)
    {
        var (artist, title) = ArtistTitle.Split(failed.Title, failed.Artist ?? failed.Subtitle);
        var soundCloud = await SearchSoundCloudAsync(artist, title, failed.Duration, ct);
        if (soundCloud != null && soundCloud.PageUrl != failed.PageUrl) return soundCloud;
        if (failed.Source == SearchSource.SoundCloud) return null;

        try
        {
            var failedId = RecommendationService.YouTubeId(failed.PageUrl);
            var query = artist != null ? $"{artist} - {title}" : title;
            var others = await downloader.SearchYouTubeAsync(query, 6, failed.Duration, ct);
            return others.FirstOrDefault(r => RecommendationService.YouTubeId(r.PageUrl) != failedId && r.PageUrl != failed.PageUrl
                                              && Matches(r, artist, title, failed.Duration, FallbackDurationTolerance));
        }
        catch (DownloadException)
        {
            return null;
        }
    }

    private async Task<SearchResult?> SearchSoundCloudAsync(string? artist, string title, TimeSpan? duration, CancellationToken ct)
    {
        var query = string.Join(" ", new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (query.Length > 120) query = query[..120];
        try
        {
            var results = await downloader.SearchSoundCloudAsync(query, 5, ct);
            return results.FirstOrDefault(r => Matches(r, artist, title, duration, FallbackDurationTolerance));
        }
        catch (DownloadException)
        {
            return null;
        }
    }

    /// <summary>
    /// Does <paramref name="candidate"/> look like the wanted track? (port of PinoyBot's fallback check)
    /// At least 60 % of the wanted keywords, no remix / cover / live unless asked for, duration within the tolerance.
    /// </summary>
    internal static bool Matches(SearchResult candidate, string? artist, string title, TimeSpan? duration, double durationTolerance)
    {
        if (KeywordRatio(candidate, artist, title) < MinKeywordRatio) return false;
        if (IsUnrequestedDerivative(candidate, artist, title)) return false;
        if (duration is { TotalSeconds: > 0 } wanted && candidate.Duration is { TotalSeconds: > 0 } actual)
        {
            var gap = Math.Abs(actual.TotalSeconds - wanted.TotalSeconds) / wanted.TotalSeconds;
            if (gap > durationTolerance) return false;
        }
        return true;
    }

    /// <summary>Share of the wanted keywords (artist + title, brackets ignored) found in the candidate's channel, artist and title.</summary>
    internal static double KeywordRatio(SearchResult candidate, string? artist, string title)
    {
        var wanted = Keywords($"{artist} {title}");
        if (wanted.Count == 0) return 0;
        // Brackets are kept on the candidate side: "(remix)" counts.
        var haystack = " " + ArtistTitle.Normalize($"{candidate.Subtitle} {candidate.Artist} {candidate.Title}") + " ";
        var found = wanted.Count(w => haystack.Contains(w, StringComparison.Ordinal));
        return (double)found / wanted.Count;
    }

    private static bool IsUnrequestedDerivative(SearchResult candidate, string? artist, string title) =>
        DerivativeRegex().IsMatch($"{candidate.Title} {candidate.Artist}") && !DerivativeRegex().IsMatch($"{title} {artist}");

    private static List<string> Keywords(string text)
    {
        var words = ArtistTitle.Normalize(BracketsRegex().Replace(text, " ")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var useful = words.Where(w => w.Length >= 3 && !NoiseWords.Contains(w)).Distinct().ToList();
        // Short words only (non-Latin scripts, "U2 - One"): keep them rather than matching nothing.
        return useful.Count > 0 ? useful : words.Where(w => !NoiseWords.Contains(w)).Distinct().ToList();
    }

    // Remixes, covers and altered versions are not the requested track, unless asked for. "Radio Edit" stays accepted.
    [GeneratedRegex(@"\b(remix|vip|bootleg|mashup|flip|cover|reprise|live|concert|instrumental|karaok[ée]|sped\s*up|slowed|nightcore|8d|tribute|acapella|a\s+cappella|intro|loop)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DerivativeRegex();

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex BracketsRegex();
}
