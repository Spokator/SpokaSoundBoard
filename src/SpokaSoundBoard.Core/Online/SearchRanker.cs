using System.Text.RegularExpressions;

namespace SpokaSoundBoard.Core.Online;

/// <summary>
/// Re-orders search results for listening: the song rather than the talkative clip, the reaction video or the one-hour
/// compilation. What the query explicitly asks for ("live", "clip", "remix"…) wins: its penalty becomes a bonus.
/// </summary>
public static partial class SearchRanker
{
    // Words describing a format rather than the searched title: ignored for the title match.
    private static readonly HashSet<string> FormatWords = new(
        "clip clips officiel officielle official video videoclip audio son musique music live concert remix edit version lyrics paroles karaoke instrumental cover reprise full complet album single feat ft the a de du des le la les et and or"
            .Split(' '), StringComparer.Ordinal);

    /// <summary>What the query asks for explicitly.</summary>
    internal readonly record struct Wants(bool Clip, bool Live, bool Remix, bool Cover, bool Compilation, bool Stream, bool Lyrics);

    /// <summary>Results sorted from best to worst for listening (stable for equal scores).</summary>
    /// <param name="expectedDuration">Known duration of the original (Spotify, Deezer…): the closest version wins.</param>
    public static IReadOnlyList<SearchResult> Rank(string query, IReadOnlyList<SearchResult> results, TimeSpan? expectedDuration = null)
    {
        if (results.Count < 2) return results;
        var context = CreateContext(query, results, expectedDuration);
        return results
            .Select((r, i) => (Result: r, Score: Score(r, i, context)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Result)
            .ToList();
    }

    internal sealed record RankContext(int Total, long MaxViews, IReadOnlyList<string> Words, Wants Wants, TimeSpan? ExpectedDuration);

    internal static RankContext CreateContext(string query, IReadOnlyList<SearchResult> results, TimeSpan? expectedDuration) =>
        new(results.Count, results.Max(r => r.ViewCount ?? 0), UsefulWords(query), WantsFromQuery(query), expectedDuration);

    internal static Wants WantsFromQuery(string query)
    {
        var q = " " + ArtistTitle.Normalize(query);
        bool Has(params string[] words) => words.Any(w => q.Contains(" " + w, StringComparison.Ordinal));
        return new Wants(
            Clip: Has("clip", "video", "videoclip"),
            Live: Has("live", "concert", "unplugged", "session"),
            Remix: Has("remix", "mashup", "sped up", "slowed", "nightcore", "8d", "bass boost"),
            Cover: Has("cover", "reprise", "karaoke", "instrumental", "acoustique", "acoustic"),
            Compilation: Has("mix", "compilation", "best of", "album", "playlist", "full album", "heures", "hour"),
            Stream: Has("radio", "stream", "direct", "24 7", "lofi"),
            Lyrics: Has("lyrics", "paroles"));
    }

    internal static IReadOnlyList<string> UsefulWords(string query) =>
        ArtistTitle.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !FormatWords.Contains(w)).Distinct().ToList();

    /// <summary>Additive score of one candidate (higher is better).</summary>
    internal static int Score(SearchResult r, int index, RankContext ctx)
    {
        var title = r.Title ?? "";
        var t = ArtistTitle.Normalize(title);
        var channel = r.Subtitle ?? "";
        var seconds = r.Duration?.TotalSeconds ?? 0;
        var w = ctx.Wants;
        double score = 0;

        // 1. The site's own order stays the base: it knows relevance better than us.
        score += (ctx.Total - index) * 2;

        // 2. Popularity relative to the best of the list: the version everybody listens to.
        if (ctx.MaxViews > 0 && r.ViewCount is > 0)
            score += Math.Round(24 * Math.Pow((double)r.ViewCount.Value / ctx.MaxViews, 0.35));

        // 3. Does the title really match the query?
        if (ctx.Words.Count > 0)
        {
            var haystack = t + " " + ArtistTitle.Normalize(channel);
            var found = ctx.Words.Count(word => haystack.Contains(word, StringComparison.Ordinal));
            score += Math.Round(26.0 * found / ctx.Words.Count);
        }

        // 4. Who publishes: the YouTube-generated "Topic" channel (official audio) or the artist's channel.
        var channelArtist = ChannelArtist(channel);
        if (TopicRegex().IsMatch(channel)) score += 22;
        else if (channelArtist.Length > 0 && (t.StartsWith(channelArtist, StringComparison.Ordinal) || ArtistTitle.Normalize(channel).EndsWith("vevo", StringComparison.Ordinal)))
            score += 18;
        else if (r.Artist != null && channelArtist.Length > 0 && ArtistTitle.Normalize(r.Artist) == channelArtist)
            score += 12;

        // 5. Kind of content.
        if (AudioRegex().IsMatch(title)) score += 14;
        if (ClipRegex().IsMatch(title)) score += w.Clip ? 12 : -7;
        if (LiveRegex().IsMatch(title) || r.IsLive) score += w.Live ? 20 : w.Stream ? 0 : -26;
        if (RemixRegex().IsMatch(title)) score += w.Remix ? 16 : -20;
        if (CoverRegex().IsMatch(title)) score += w.Cover ? 16 : -26;
        if (CompilationRegex().IsMatch(title)) score += w.Compilation ? 10 : -18;
        if (LyricsRegex().IsMatch(title)) score += w.Lyrics ? 8 : -12;
        if (ReactionRegex().IsMatch(title)) score -= 32;
        if (CartoonRegex().IsMatch(title)) score -= 32;

        // 6. Duration: a song lasts between one and ten minutes.
        if (!w.Stream && !r.IsLive && !w.Compilation)
        {
            if (seconds <= 0) score -= 20;
            else if (seconds < 60) score -= 25;
            else if (seconds > 900) score -= 30;
            else if (seconds > 600) score -= 12;
        }

        // 7. Duration known in advance (track from Spotify, Deezer, Apple): stick to it.
        if (ctx.ExpectedDuration is { TotalSeconds: > 0 } expected && seconds > 0)
        {
            var gap = Math.Abs(seconds - expected.TotalSeconds);
            if (gap <= 5) score += 22;
            else if (gap <= 20) score += 12;
            else if (gap > 90) score -= 20;
        }

        return (int)Math.Round(score);
    }

    /// <summary>"Queen Official" → "queen", "Daft Punk - Topic" → "daft punk".</summary>
    private static string ChannelArtist(string channel) =>
        MultiSpaceRegex().Replace(ChannelWordsRegex().Replace(ArtistTitle.Normalize(channel), " "), " ").Trim();

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"-\s*topic\s*$", Options)]
    private static partial Regex TopicRegex();

    [GeneratedRegex(@"\b(official\s+audio|audio\s+officiel|son\s+officiel|audio\s+only|full\s+song|version\s+audio|\(audio\)|\[audio\])", Options)]
    private static partial Regex AudioRegex();

    [GeneratedRegex(@"\b(official\s+(music\s+)?video|clip\s+officiel|music\s+video|videoclip|vid[ée]o\s+officielle?|visuali[sz]er)", Options)]
    private static partial Regex ClipRegex();

    [GeneratedRegex(@"\b(live|en\s+concert|concert|unplugged|session\s+live|tour|bercy|accorhotels|festival|au\s+z[ée]nith|aux?\s+victoires)\b", Options)]
    private static partial Regex LiveRegex();

    [GeneratedRegex(@"\b(reaction|react|r[ée]action|interview|making\s+of|behind\s+the\s+scenes|teaser|trailer|analyse|explained|breakdown|documentaire|documentary|podcast|freestyle\s+radio|clash|r[ée]ponse\s+[àa])\b", Options)]
    private static partial Regex ReactionRegex();

    [GeneratedRegex(@"\b(cover|reprise|the\s+voice|nouvelle\s+star|karaok[ée]|instrumental|tutoriel|tutorial|how\s+to\s+play|flashmob|parodie|parody|ia\s+cover|ai\s+cover)\b", Options)]
    private static partial Regex CoverRegex();

    [GeneratedRegex(@"\b(remix|mashup|sped\s+up|slowed|nightcore|8d\s+audio|bass\s*boost(ed)?|screwed|edit\s+by|mix\s+by|dj\s+set)\b", Options)]
    private static partial Regex RemixRegex();

    [GeneratedRegex(@"\b(best\s+of|greatest\s+hits|compilation|mega\s*mix|non\s*stop|full\s+album|album\s+complet|playlist|\d+\s*(h|hours?|heures?)\b|hour\s+long|mix\s+\d{4}|top\s+\d+)\b", Options)]
    private static partial Regex CompilationRegex();

    [GeneratedRegex(@"\b(lyrics?|paroles|letra|testo)\b", Options)]
    private static partial Regex LyricsRegex();

    [GeneratedRegex(@"\b(dessin\s+anim[ée]|cartoon|for\s+kids|pour\s+enfants|[ée]pisode)\b", Options)]
    private static partial Regex CartoonRegex();

    [GeneratedRegex(@"\b(official|officiel|officielle|vevo|music|musique|records|tv|channel|hd|topic)\b", Options)]
    private static partial Regex ChannelWordsRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();
}
