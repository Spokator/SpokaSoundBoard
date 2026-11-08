using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpokaSoundBoard.Core.Online;

/// <summary>One synchronised lyrics line.</summary>
public sealed record LyricLine(TimeSpan Time, string Text);

/// <param name="Plain">Plain lyrics (null when only synced lyrics exist, or for an instrumental).</param>
/// <param name="Synced">Time-stamped lines sorted by time (empty when the lyrics are not synchronised).</param>
public sealed record Lyrics(string? Plain, IReadOnlyList<LyricLine> Synced, bool IsInstrumental)
{
    public bool HasSynced => Synced.Count > 0;
}

/// <summary>
/// Lyrics from lrclib.net (free, no key): exact lookup with the duration, then a looser search. Results are kept in
/// an in-memory LRU cache (~300 entries) and a small JSON cache on disk; misses are only remembered in memory.
/// </summary>
public sealed partial class LyricsService
{
    private const int MemoryCapacity = 300;
    private const int DiskCapacity = 600;
    private const string Api = "https://lrclib.net/api";

    private readonly string _cacheDirectory;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Lyrics? Value)>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, Lyrics? Value)> _lru = new();

    /// <param name="cacheDirectory">Folder for the on-disk cache (created on demand).</param>
    public LyricsService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>Test hook: replaces the HTTP GET (url → status + body).</summary>
    internal Func<string, CancellationToken, Task<(HttpStatusCode Status, string Body)>>? FetchOverride { get; set; }

    /// <summary>
    /// Lyrics of a track, or null when lrclib has none. <paramref name="title"/> may be a raw web title
    /// ("Artist - Title (Clip officiel)"); <paramref name="artist"/> may be a channel name. Network failures throw a
    /// <see cref="DownloadException"/>.
    /// </summary>
    public async Task<Lyrics?> GetAsync(string title, string? artist, TimeSpan? duration, CancellationToken ct)
    {
        var (cleanArtist, cleanTitle) = ArtistTitle.Split(title ?? "", artist);
        cleanTitle = CleanForLyrics(cleanTitle);
        cleanArtist = cleanArtist == null ? null : CleanForLyrics(cleanArtist);
        if (cleanTitle.Length == 0) return null;

        var key = $"{ArtistTitle.Normalize(cleanArtist)}|{ArtistTitle.Normalize(cleanTitle)}";
        if (TryGetMemory(key, out var cached)) return cached;
        if (ReadDisk(key) is { } fromDisk)
        {
            Remember(key, fromDisk);
            return fromDisk;
        }

        var result = await FetchAsync(cleanTitle, cleanArtist, duration, ct);
        Remember(key, result);
        if (result != null) WriteDisk(key, result);
        return result;
    }

    private async Task<Lyrics?> FetchAsync(string title, string? artist, TimeSpan? duration, CancellationToken ct)
    {
        if (artist != null)
        {
            var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            if (duration is { TotalSeconds: > 0 } d) query += "&duration=" + Math.Round(d.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            var (status, body) = await GetAsync($"{Api}/get?{query}", ct);
            if (status == HttpStatusCode.OK && ParseRecord(body) is { } exact) return exact;
        }

        // Looser search (titles read from YouTube are rarely exact).
        var q = ArtistTitle.Normalize($"{artist} {title}");
        var (searchStatus, searchBody) = await GetAsync($"{Api}/search?q={Uri.EscapeDataString(q)}", ct);
        if (searchStatus != HttpStatusCode.OK) return null;
        return PickFromSearch(searchBody, title, duration);
    }

    private async Task<(HttpStatusCode, string)> GetAsync(string url, CancellationToken ct)
    {
        if (FetchOverride != null) return await FetchOverride(url, ct);
        var page = await OnlineHttp.GetAsync(url, "Le service de paroles (lrclib.net)", browser: false, ct, TimeSpan.FromSeconds(12));
        return (page.Status, page.Body);
    }

    /// <summary>One lrclib record (<c>/api/get</c> answer or search item).</summary>
    internal static Lyrics? ParseRecord(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromRecord(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Lyrics? FromRecord(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.Bool("instrumental")) return new Lyrics(null, [], true);
        var plain = e.Str("plainLyrics");
        var synced = e.Str("syncedLyrics") is { } lrc ? ParseLrc(lrc) : [];
        if (plain == null && synced.Count == 0) return null;
        plain ??= string.Join("\n", synced.Select(l => l.Text));
        return new Lyrics(plain, synced, false);
    }

    /// <summary>Best item of an <c>/api/search</c> answer: title match, close duration, synced lyrics preferred.</summary>
    internal static Lyrics? PickFromSearch(string json, string title, TimeSpan? duration)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var wanted = ArtistTitle.Normalize(title);
            var best = doc.RootElement.EnumerateArray()
                .Select(item =>
                {
                    var name = ArtistTitle.Normalize(item.Str("trackName", "name"));
                    var titleMatch = wanted.Length > 0 && name.Length > 0 && (name.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(name, StringComparison.Ordinal));
                    var close = duration is { TotalSeconds: > 0 } d && item.Num("duration") is { } s && Math.Abs(s - d.TotalSeconds) <= 15;
                    var far = duration is { TotalSeconds: > 0 } d2 && item.Num("duration") is { } s2 && Math.Abs(s2 - d2.TotalSeconds) > 45;
                    var score = (titleMatch ? 4 : 0) + (close ? 3 : 0) + (item.Str("syncedLyrics") != null ? 2 : 0) + (item.Str("plainLyrics") != null ? 1 : 0);
                    return (Item: item, Score: score, Acceptable: (titleMatch || close) && !far);
                })
                .Where(x => x.Acceptable)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            return best.Acceptable ? FromRecord(best.Item) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses LRC text: "[mm:ss.xx]", "[mm:ss]", "[mm:ss:xx]", several time tags per line and the "[offset:±ms]" tag.
    /// Metadata tags ([ar:], [ti:]…) are ignored; lines are sorted by time.
    /// </summary>
    public static IReadOnlyList<LyricLine> ParseLrc(string lrc)
    {
        if (string.IsNullOrEmpty(lrc)) return [];
        var offset = TimeSpan.Zero;
        var lines = new List<LyricLine>();
        foreach (var rawLine in lrc.Split('\n'))
        {
            var line = rawLine.Trim();
            var offsetMatch = OffsetRegex().Match(line);
            if (offsetMatch.Success)
            {
                // A positive offset shows the lyrics earlier.
                offset = TimeSpan.FromMilliseconds(int.Parse(offsetMatch.Groups["ms"].Value, CultureInfo.InvariantCulture));
                continue;
            }

            var times = new List<TimeSpan>();
            var position = 0;
            Match m;
            while ((m = TimeTagRegex().Match(line, position)).Success && m.Index == position)
            {
                var minutes = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
                var fraction = m.Groups["f"].Success ? m.Groups["f"].Value : "";
                var ms = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
                times.Add(new TimeSpan(0, 0, minutes, seconds, ms));
                position = m.Index + m.Length;
                var next = position;
                while (next < line.Length && char.IsWhiteSpace(line[next])) next++;
                if (next < line.Length && line[next] == '[') position = next;
            }
            if (times.Count == 0) continue;
            var text = line[position..].Trim();
            foreach (var t in times) lines.Add(new LyricLine(t, text));
        }

        if (offset != TimeSpan.Zero)
            lines = lines.Select(l => l with { Time = l.Time - offset < TimeSpan.Zero ? TimeSpan.Zero : l.Time - offset }).ToList();
        return lines.OrderBy(l => l.Time).ToList();
    }

    /// <summary>Index of the line being sung at <paramref name="position"/> (last line whose time is ≤ position), or -1 before the first line.</summary>
    public static int LineAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    /// <summary>Removes what lrclib does not know: brackets, "official", "lyrics", "feat."…</summary>
    internal static string CleanForLyrics(string s)
    {
        s = BracketsRegex().Replace(s, " ");
        s = NoiseRegex().Replace(s, " ");
        return MultiSpaceRegex().Replace(s, " ").Trim(' ', '-', '–', '|', ':');
    }

    // ───── caches ─────

    private bool TryGetMemory(string key, out Lyrics? value)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private void Remember(string key, Lyrics? value)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var existing)) _lru.Remove(existing);
            var node = _lru.AddFirst((key, value));
            _index[key] = node;
            while (_lru.Count > MemoryCapacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _index.Remove(last.Value.Key);
            }
        }
    }

    private sealed record DiskEntry(string Key, string? Plain, List<DiskLine> Synced, bool Instrumental);

    private sealed record DiskLine(double Ms, string Text);

    private string DiskPath(string key) =>
        Path.Combine(_cacheDirectory, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20] + ".json");

    private Lyrics? ReadDisk(string key)
    {
        try
        {
            var path = DiskPath(key);
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<DiskEntry>(File.ReadAllText(path, Encoding.UTF8));
            if (entry == null || entry.Key != key) return null;
            return new Lyrics(entry.Plain, entry.Synced.Select(l => new LyricLine(TimeSpan.FromMilliseconds(l.Ms), l.Text)).ToList(), entry.Instrumental);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private void WriteDisk(string key, Lyrics lyrics)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var entry = new DiskEntry(key, lyrics.Plain, lyrics.Synced.Select(l => new DiskLine(l.Time.TotalMilliseconds, l.Text)).ToList(), lyrics.IsInstrumental);
            File.WriteAllText(DiskPath(key), JsonSerializer.Serialize(entry), Encoding.UTF8);
            TrimDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The disk cache is optional.
        }
    }

    private void TrimDisk()
    {
        var files = new DirectoryInfo(_cacheDirectory).GetFiles("*.json");
        if (files.Length <= DiskCapacity) return;
        foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - DiskCapacity * 3 / 4))
        {
            try { old.Delete(); } catch (IOException) { /* in use */ }
        }
    }

    [GeneratedRegex(@"^\s*\[offset:\s*(?<ms>[+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"\[(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]")]
    private static partial Regex TimeTagRegex();

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex BracketsRegex();

    [GeneratedRegex(@"\b(?:official|video|lyrics?|audio|hd|4k|clip|officiel(?:le)?|paroles|visuali[sz]er)\b|\s(?:ft|feat|featuring)\b\.?.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();
}
