using System.Text.RegularExpressions;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Online;

/// <param name="Reason">Why it is proposed, in French (« Parce que vous écoutez Daft Punk »).</param>
public sealed record Recommendation(SearchResult Result, string Reason);

/// <summary>
/// "In the style of what you listen to", without any paid API: YouTube "RD" mixes of the current track and of a
/// favorite, plus the most listened artists of the last 30 days. Dislikes (artists, urls) are applied on every call,
/// so a new 👎 takes effect immediately even when the pool comes from the cache.
/// </summary>
public sealed partial class RecommendationService(MediaDownloader downloader)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxTrackDuration = TimeSpan.FromMinutes(12);
    private const int MaxMixEntries = 50;
    private const int MaxCachedMixes = 32;

    private readonly SemaphoreSlim _parallel = new(3, 3);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTime At, FlatPlaylist List)> _mixCache = new(StringComparer.Ordinal);
    private (string Key, DateTime At, IReadOnlyList<Recommendation> Pool)? _forYou;

    /// <summary>Test hook: replaces "now" (history windows and cache expiry).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>YouTube video id of a link (watch, youtu.be, shorts, embed, live, music.youtube), or null.</summary>
    public static string? YouTubeId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = YouTubeIdRegex().Match(url.Trim());
        return m.Success ? m.Groups["id"].Value : null;
    }

    /// <summary>Forgets the cached pools (e.g. after clearing the history).</summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _mixCache.Clear();
            _forYou = null;
        }
    }

    /// <summary>
    /// Tracks related to a seed (a YouTube link, or a title searched on YouTube first), from YouTube's "RD" mix
    /// (at most 50 entries), minus <paramref name="excludeUrls"/> (already played, in the queue, disliked urls) and
    /// <paramref name="dislikedArtists"/> (names as displayed; compared with <see cref="ArtistTitle.ArtistKey"/>).
    /// </summary>
    public async Task<IReadOnlyList<Recommendation>> GetRelatedAsync(string seedUrlOrTitle, int count, IReadOnlyCollection<string> excludeUrls,
        IReadOnlyCollection<string> dislikedArtists, CancellationToken ct)
    {
        var seed = seedUrlOrTitle?.Trim() ?? "";
        if (seed.Length == 0 || count <= 0) return [];

        var id = YouTubeId(seed);
        string? label = null;
        if (id == null)
        {
            if (MediaDownloader.LooksLikeUrl(seed)) return []; // Not a YouTube link: no mix to read.
            var found = await downloader.SearchYouTubeAsync(seed, 1, ct);
            if (found.Count == 0) return [];
            id = YouTubeId(found[0].PageUrl);
            label = ArtistTitle.Split(found[0].Title, found[0].Artist ?? found[0].Subtitle).Title;
            if (id == null) return [];
        }

        var mix = await GetMixAsync(id, ct);
        var seedEntry = mix.Entries.FirstOrDefault(e => YouTubeId(e.PageUrl) == id);
        label ??= seedEntry != null ? ArtistTitle.Split(seedEntry.Title, seedEntry.Artist ?? seedEntry.Subtitle).Title : ArtistTitle.Label(seed);
        var reason = $"Parce que vous écoutez « {ArtistTitle.Label(label)} »";

        var exclude = new List<string>(excludeUrls) { $"https://www.youtube.com/watch?v={id}" };
        return Filter(mix.Entries.Select(e => new Recommendation(e, reason)), exclude, dislikedArtists).Take(count).ToList();
    }

    /// <summary>
    /// "For you" mix: the style of the last played track, the top 3 artists of the last 30 days and a random favorite,
    /// interleaved by reason. The pool is cached 10 minutes (per last track / top artists); exclusions and dislikes are
    /// applied on every call. Tracks played in the last 7 days are left out automatically.
    /// </summary>
    public async Task<IReadOnlyList<Recommendation>> GetForYouAsync(IReadOnlyList<HistoryEntry> history, IReadOnlyList<TrackItem> favorites, int count,
        IReadOnlyCollection<string> excludeUrls, IReadOnlyCollection<string> dislikedArtists, CancellationToken ct)
    {
        if (count <= 0) return [];
        var now = Clock();
        var seeds = PickSeeds(history, favorites, dislikedArtists, now);
        if (seeds.Count == 0) return [];

        var key = string.Join("|", seeds.Where(s => !s.IsFavorite).Select(s => s.Key));
        IReadOnlyList<Recommendation>? pool = null;
        lock (_cacheLock)
        {
            if (_forYou is { } cached && cached.Key == key && now - cached.At < CacheDuration) pool = cached.Pool;
        }

        if (pool == null)
        {
            pool = await BuildPoolAsync(seeds, ct);
            lock (_cacheLock) _forYou = (key, now, pool);
        }

        var recent = history.Where(h => now - h.PlayedAt < TimeSpan.FromDays(7) && !string.IsNullOrEmpty(h.SourceUrl)).Select(h => h.SourceUrl!);
        var filtered = Filter(pool, excludeUrls.Concat(recent).ToList(), dislikedArtists);
        return Interleave(filtered).Take(count).ToList();
    }

    internal sealed record Seed(string Key, string Reason, bool IsMix, bool IsFavorite, int Limit, bool SkipFirst);

    /// <summary>Seeds of the "for you" mix (pure; exposed for tests).</summary>
    internal static List<Seed> PickSeeds(IReadOnlyList<HistoryEntry> history, IReadOnlyList<TrackItem> favorites,
        IReadOnlyCollection<string> dislikedArtists, DateTime now, Random? random = null)
    {
        var seeds = new List<Seed>();
        var disliked = DislikedKeys(dislikedArtists);

        // 1. The style of the last played (YouTube) track.
        var current = history.OrderByDescending(h => h.PlayedAt).FirstOrDefault();
        if (current != null && YouTubeId(current.SourceUrl) is { } currentId)
            seeds.Add(new Seed(currentId, $"Dans le style de « {ArtistTitle.Label(ArtistTitle.Split(current.Title, current.Artist).Title)} »",
                IsMix: true, IsFavorite: false, Limit: 30, SkipFirst: true));

        // 2. The most listened artists of the last 30 days.
        var topArtists = history
            .Where(h => now - h.PlayedAt < TimeSpan.FromDays(30))
            .Select(h => ArtistTitle.Split(h.Title, h.Artist).Artist)
            .Where(a => a != null)
            .Select(a => ArtistTitle.PrimaryArtist(a!))
            .Select(a => (Name: a, Key: ArtistTitle.ArtistKey(a)))
            .Where(a => a.Key.Length >= 2 && !disliked.Contains(a.Key))
            .GroupBy(a => a.Key)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(g => g.GroupBy(x => x.Name).OrderByDescending(n => n.Count()).First().Key);
        foreach (var artist in topArtists)
            seeds.Add(new Seed(artist, $"Parce que vous écoutez {artist}", IsMix: false, IsFavorite: false, Limit: 8, SkipFirst: false));

        // 3. A random favorite.
        var withId = favorites.Where(f => YouTubeId(f.SourceUrl) != null).ToList();
        if (withId.Count > 0)
        {
            var fav = withId[(random ?? Random.Shared).Next(withId.Count)];
            var favId = YouTubeId(fav.SourceUrl)!;
            if (seeds.Count == 0 || seeds[0].Key != favId)
                seeds.Add(new Seed(favId, $"Proche de votre favori « {ArtistTitle.Label(ArtistTitle.Split(fav.Title, fav.Artist).Title)} »",
                    IsMix: true, IsFavorite: true, Limit: 12, SkipFirst: true));
        }
        return seeds;
    }

    private async Task<IReadOnlyList<Recommendation>> BuildPoolAsync(IReadOnlyList<Seed> seeds, CancellationToken ct)
    {
        var errors = new List<Exception>();
        var jobs = seeds.Select(async seed =>
        {
            try
            {
                IEnumerable<SearchResult> results;
                if (seed.IsMix)
                {
                    var mix = await GetMixAsync(seed.Key, ct);
                    results = mix.Entries.Where(e => !seed.SkipFirst || YouTubeId(e.PageUrl) != seed.Key).Take(seed.Limit);
                }
                else
                {
                    await _parallel.WaitAsync(ct);
                    try
                    {
                        var listing = await downloader.GetFlatPlaylistAsync($"ytsearch12:{seed.Key}", 12, true, TimeSpan.FromSeconds(45), ct);
                        results = SearchRanker.Rank(seed.Key, listing.Entries.Where(e => !e.IsLive).ToList()).Take(seed.Limit);
                    }
                    finally
                    {
                        _parallel.Release();
                    }
                }
                return results.Select(r => new Recommendation(r, seed.Reason)).ToList();
            }
            catch (DownloadException ex)
            {
                lock (errors) errors.Add(ex);
                return new List<Recommendation>();
            }
        }).ToList();

        var lists = await Task.WhenAll(jobs);
        var pool = lists.SelectMany(l => l).ToList();
        if (pool.Count == 0 && errors.Count > 0) throw errors[0];
        return pool;
    }

    private async Task<FlatPlaylist> GetMixAsync(string id, CancellationToken ct)
    {
        var now = Clock();
        lock (_cacheLock)
        {
            if (_mixCache.TryGetValue(id, out var cached) && now - cached.At < CacheDuration)
                return cached.List;
        }

        FlatPlaylist mix;
        await _parallel.WaitAsync(ct);
        try
        {
            mix = await downloader.GetFlatPlaylistAsync($"https://www.youtube.com/watch?v={id}&list=RD{id}", MaxMixEntries, true,
                TimeSpan.FromSeconds(60), ct);
        }
        finally
        {
            _parallel.Release();
        }

        lock (_cacheLock)
        {
            if (_mixCache.Count >= MaxCachedMixes)
                _mixCache.Remove(_mixCache.MinBy(kv => kv.Value.At).Key);
            _mixCache[id] = (now, mix);
        }
        return mix;
    }

    /// <summary>Drops excluded urls, disliked artists, live streams, over-long videos, compilations and duplicates.</summary>
    internal static IEnumerable<Recommendation> Filter(IEnumerable<Recommendation> items, IReadOnlyCollection<string> excludeUrls,
        IReadOnlyCollection<string> dislikedArtists)
    {
        var excluded = new HashSet<string>(excludeUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(UrlKey), StringComparer.OrdinalIgnoreCase);
        var disliked = DislikedKeys(dislikedArtists);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSongs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var r = item.Result;
            var urlKey = UrlKey(r.PageUrl);
            if (excluded.Contains(urlKey) || !seenUrls.Add(urlKey)) continue;
            if (r.IsLive || r.Duration > MaxTrackDuration || CompilationRegex().IsMatch(r.Title)) continue;

            var keys = ArtistTitle.ArtistKeys(r.Title, r.Subtitle).Append(ArtistTitle.ArtistKey(r.Artist));
            if (disliked.Count > 0 && keys.Any(disliked.Contains)) continue;

            var (artist, title) = ArtistTitle.Split(r.Title, r.Artist ?? r.Subtitle);
            var song = ArtistTitle.ArtistKey(artist != null ? ArtistTitle.PrimaryArtist(artist) : "") + "|" + ArtistTitle.Normalize(title);
            if (!seenSongs.Add(song)) continue;
            yield return item;
        }
    }

    /// <summary>Round-robin over the reasons, so the list does not show ten tracks of the same seed in a row.</summary>
    internal static IEnumerable<Recommendation> Interleave(IEnumerable<Recommendation> items)
    {
        var groups = items.GroupBy(i => i.Reason).Select(g => g.ToList()).ToList();
        for (var i = 0; groups.Any(g => i < g.Count); i++)
            foreach (var g in groups)
                if (i < g.Count) yield return g[i];
    }

    private static HashSet<string> DislikedKeys(IReadOnlyCollection<string> dislikedArtists) =>
        new(dislikedArtists.Select(ArtistTitle.ArtistKey).Where(k => k.Length >= 2), StringComparer.Ordinal);

    private static string UrlKey(string url) => YouTubeId(url) is { } id ? "yt:" + id : url.Trim().TrimEnd('/');

    [GeneratedRegex(@"^(?:https?://)?(?:(?:www|m|music)\.)?(?:youtube(?:-nocookie)?\.com/(?:watch\?(?:[^#]*?&)?v=|shorts/|embed/|live/|v/)|youtu\.be/)(?<id>[A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YouTubeIdRegex();

    [GeneratedRegex(@"\b(mix|best\s+of|playlist|full\s+album|album\s+complet|compilation|\d+\s*hours?|\d+\s*heures?|greatest\s+hits|mega\s*mix|non\s*stop)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompilationRegex();
}
