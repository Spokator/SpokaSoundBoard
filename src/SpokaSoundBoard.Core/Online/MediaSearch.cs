using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpokaSoundBoard.Core.Online;

public enum SearchSource
{
    YouTube,
    MyInstants,
    SoundCloud,
}

/// <param name="PageUrl">Web page of the result (opened in the browser, or downloaded with yt-dlp).</param>
/// <param name="Title">Title as published by the source (use <see cref="ArtistTitle.Split"/> for a clean display title).</param>
/// <param name="Subtitle">Channel / uploader as published (e.g. "Daft Punk - Topic"); used by the ranking.</param>
/// <param name="DirectAudioUrl">Direct audio file (MyInstants), downloaded without yt-dlp.</param>
/// <param name="Artist">Artist read from the metadata, the title ("Artist - Title") or the cleaned channel name.</param>
/// <param name="ViewCount">Number of views when the source gives it.</param>
/// <param name="IsLive">Live stream (or upcoming premiere): cannot be downloaded as a file.</param>
public sealed record SearchResult(
    string Title,
    string Subtitle,
    string PageUrl,
    string? DirectAudioUrl,
    TimeSpan? Duration,
    string? ThumbnailUrl,
    SearchSource Source,
    string? Color,
    string? Artist = null,
    long? ViewCount = null,
    bool IsLive = false);

/// <summary>Text search: YouTube (music, clips, sounds), SoundCloud and MyInstants (short meme / sound-effect buttons).</summary>
public sealed partial class MediaSearch(MediaDownloader downloader)
{
    private static readonly HttpClient Http = CreateHttpClient();

    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchSource source, string query, int count, CancellationToken ct) =>
        source switch
        {
            SearchSource.MyInstants => SearchMyInstantsAsync(query, count, ct),
            SearchSource.SoundCloud => downloader.SearchSoundCloudAsync(query, count, ct),
            _ => downloader.SearchYouTubeAsync(query, count, ct),
        };

    /// <summary>
    /// Search-as-you-type suggestions (YouTube flavour of Google Suggest). Short timeout; never throws:
    /// an empty list is returned on any error or cancellation.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SuggestAsync(string prefix, CancellationToken ct)
    {
        var q = prefix?.Trim() ?? "";
        if (q.Length == 0) return [];
        try
        {
            var url = "https://suggestqueries.google.com/complete/search?client=firefox&ds=yt&hl=fr&oe=utf-8&q=" + Uri.EscapeDataString(q);
            var page = await OnlineHttp.GetAsync(url, "Google", browser: false, ct, TimeSpan.FromSeconds(3));
            return page.Ok ? ParseSuggestions(page.Body) : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Reads Google Suggest's <c>["query", ["s1", "s2", …]]</c> answer.</summary>
    internal static IReadOnlyList<string> ParseSuggestions(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2) return [];
            var list = doc.RootElement[1];
            if (list.ValueKind != JsonValueKind.Array) return [];
            return list.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchMyInstantsAsync(string query, int count, CancellationToken ct)
    {
        var url = "https://www.myinstants.com/fr/search/?name=" + Uri.EscapeDataString(query.Trim());
        string html;
        try
        {
            html = await Http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException($"MyInstants est inaccessible ({ex.Message}).");
        }
        return ParseMyInstants(html).Take(count).ToList();
    }

    internal static List<SearchResult> ParseMyInstants(string html)
    {
        var results = new List<SearchResult>();
        var seen = new HashSet<string>();
        foreach (var block in html.Split("<div class=\"instant\">").Skip(1))
        {
            var sound = SoundRegex().Match(block);
            var link = LinkRegex().Match(block);
            if (!sound.Success || !link.Success) continue;
            var mp3 = sound.Groups["mp3"].Value;
            if (!seen.Add(mp3)) continue;
            var title = WebUtility.HtmlDecode(link.Groups["title"].Value).Trim();
            var page = "https://www.myinstants.com" + link.Groups["page"].Value;
            var colorMatch = ColorRegex().Match(block);
            var color = colorMatch.Success ? NormalizeColor(colorMatch.Groups["color"].Value) : null;
            results.Add(new SearchResult(title, "MyInstants", page, "https://www.myinstants.com" + mp3, null, null, SearchSource.MyInstants, color));
        }
        return results;
    }

    private static string? NormalizeColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        return hex.Length == 6 ? "#" + hex.ToUpperInvariant() : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SpokaSoundBoard/1.3");
        return client;
    }

    [GeneratedRegex(@"play\('(?<mp3>/media/sounds/[^']+)'")]
    private static partial Regex SoundRegex();

    [GeneratedRegex("""<a href="(?<page>/[^"]+)" class="instant-link[^"]*">(?<title>[^<]+)</a>""")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"background-color:\s*(?<color>#[0-9A-Fa-f]{3,6})")]
    private static partial Regex ColorRegex();
}
