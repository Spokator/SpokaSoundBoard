using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace SpokaSoundBoard.Core.Online;

/// <param name="Url">Directly playable page (YouTube, SoundCloud), or null when the track must be found by search
/// (<see cref="TrackResolver.ResolveAsync"/>) — Spotify, Deezer and Apple Music.</param>
public sealed record ImportedTrack(string Title, string? Artist, TimeSpan? Duration, string? Url, string? ThumbnailUrl);

/// <param name="Name">Original name of the playlist / album on its site.</param>
public sealed record ImportedPlaylist(string Name, string SourceUrl, IReadOnlyList<ImportedTrack> Tracks, string? ThumbnailUrl);

/// <summary>
/// Imports playlists, albums and tracks from Spotify (public embed page, no key), Deezer (public API), Apple Music
/// (page data), YouTube / YouTube Music playlists and SoundCloud sets (yt-dlp, flat listing).
/// </summary>
public sealed partial class PlaylistImporter(MediaDownloader downloader)
{
    public const int MaxTracks = 500;

    /// <summary>True for the links <see cref="ImportAsync"/> understands (Spotify, Deezer, Apple Music, YouTube playlists, SoundCloud sets).</summary>
    public static bool CanImport(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        url = url.Trim();
        return SpotifyRegex().IsMatch(url) || SpotifyUriRegex().IsMatch(url) || SpotifyShortRegex().IsMatch(url)
               || DeezerRegex().IsMatch(url) || DeezerShortRegex().IsMatch(url)
               || AppleRegex().IsMatch(url)
               || IsYouTubePlaylist(url) || SoundCloudSetRegex().IsMatch(url);
    }

    /// <summary>Reads the playlist behind <paramref name="url"/> (at most 500 tracks). Errors are <see cref="DownloadException"/>s.</summary>
    public async Task<ImportedPlaylist> ImportAsync(string url, CancellationToken ct)
    {
        url = url.Trim();
        ImportedPlaylist playlist;
        if (SpotifyRegex().IsMatch(url) || SpotifyUriRegex().IsMatch(url) || SpotifyShortRegex().IsMatch(url)) playlist = await ImportSpotifyAsync(url, ct);
        else if (DeezerRegex().IsMatch(url) || DeezerShortRegex().IsMatch(url)) playlist = await ImportDeezerAsync(url, ct);
        else if (AppleRegex().IsMatch(url)) playlist = await ImportAppleAsync(url, ct);
        else if (IsYouTubePlaylist(url) || SoundCloudSetRegex().IsMatch(url)) playlist = await ImportWithYtDlpAsync(url, ct);
        else throw new DownloadException("Ce lien n'est pas une playlist reconnue (Spotify, Deezer, Apple Music, YouTube ou SoundCloud).");

        if (playlist.Tracks.Count == 0) throw new DownloadException("Aucun titre n'a été trouvé dans cette playlist (elle est peut-être privée).");
        return playlist.Tracks.Count > MaxTracks ? playlist with { Tracks = playlist.Tracks.Take(MaxTracks).ToList() } : playlist;
    }

    // ───────────────────────────── Spotify ─────────────────────────────

    private static async Task<ImportedPlaylist> ImportSpotifyAsync(string url, CancellationToken ct)
    {
        if (SpotifyShortRegex().IsMatch(url)) url = await FollowShortLinkAsync(url, SpotifyRegex(), "Spotify", ct);
        var m = SpotifyRegex().Match(url);
        if (!m.Success) m = SpotifyUriRegex().Match(url);
        if (!m.Success) throw new DownloadException("Lien Spotify invalide.");
        var type = m.Groups["type"].Value.ToLowerInvariant();
        var id = m.Groups["id"].Value;

        var page = await OnlineHttp.GetAsync($"https://open.spotify.com/embed/{type}/{id}", "Spotify", browser: true, ct);
        if (page.Status == HttpStatusCode.NotFound) throw new DownloadException("Spotify : contenu introuvable ou privé.");
        if (!page.Ok) throw new DownloadException($"Spotify a répondu par une erreur ({(int)page.Status}).");
        return ParseSpotifyEmbed(page.Body, $"https://open.spotify.com/{type}/{id}");
    }

    /// <summary>Reads the <c>__NEXT_DATA__</c> JSON of an open.spotify.com/embed page.</summary>
    internal static ImportedPlaylist ParseSpotifyEmbed(string html, string sourceUrl)
    {
        var m = NextDataRegex().Match(html);
        if (!m.Success) throw new DownloadException("Spotify : impossible de lire la page (son format a peut-être changé).");
        JsonElement entity;
        try
        {
            using var doc = JsonDocument.Parse(m.Groups["json"].Value);
            entity = doc.RootElement.At("props", "pageProps", "state", "data", "entity")?.Clone()
                     ?? throw new DownloadException("Spotify : format de page inconnu.");
        }
        catch (JsonException)
        {
            throw new DownloadException("Spotify : format de page inconnu.");
        }

        var type = entity.Str("type") ?? "";
        var name = Clean(entity.Str("name", "title")) ?? "Spotify";
        var cover = LargestImage(entity.At("visualIdentity")?.Array("image") ?? [], "url", "maxWidth")
                    ?? LargestImage(entity.At("coverArt")?.Array("sources") ?? [], "url", "width");

        var tracks = new List<ImportedTrack>();
        if (entity.Get("trackList") is { ValueKind: JsonValueKind.Array } list)
        {
            // Album tracks share the album cover; playlist / artist covers are not the tracks' covers.
            var trackCover = type == "album" ? cover : null;
            foreach (var t in list.EnumerateArray())
            {
                var title = Clean(t.Str("title", "name"));
                if (title == null) continue;
                var artist = Clean(t.Str("subtitle")) ?? JoinNames(t.Array("artists")) ?? (type == "album" ? Clean(entity.Str("subtitle")) : null);
                tracks.Add(new ImportedTrack(title, artist, Milliseconds(t.Num("duration", "duration_ms")), null, trackCover));
            }
        }
        else if (type == "track" || entity.Get("duration") != null)
        {
            tracks.Add(new ImportedTrack(name, JoinNames(entity.Array("artists")) ?? Clean(entity.Str("subtitle")),
                Milliseconds(entity.Num("duration", "duration_ms")), null, cover));
        }

        if (type == "artist") name = $"{name} — Titres populaires";
        return new ImportedPlaylist(name, sourceUrl, tracks, cover);
    }

    // ───────────────────────────── Deezer ─────────────────────────────

    private static async Task<ImportedPlaylist> ImportDeezerAsync(string url, CancellationToken ct)
    {
        if (DeezerShortRegex().IsMatch(url)) url = await FollowShortLinkAsync(url, DeezerRegex(), "Deezer", ct);
        var m = DeezerRegex().Match(url);
        if (!m.Success) throw new DownloadException("Lien Deezer invalide.");
        var type = m.Groups["type"].Value.ToLowerInvariant();
        var id = m.Groups["id"].Value;
        var sourceUrl = $"https://www.deezer.com/{type}/{id}";

        switch (type)
        {
            case "track":
            {
                var t = await DeezerApiAsync($"/track/{id}", ct);
                return new ImportedPlaylist(DeezerTrack(t, null, null)?.Title ?? "Deezer", sourceUrl, DeezerTracks([t], null, null),
                    t.At("album")?.Str("cover_big", "cover_medium"));
            }
            case "album":
            {
                var album = await DeezerApiAsync($"/album/{id}", ct);
                var items = await DeezerPagesAsync($"/album/{id}/tracks?limit=100", ct);
                var cover = album.Str("cover_big", "cover_medium", "cover");
                var artist = album.At("artist")?.Str("name");
                return new ImportedPlaylist(album.Str("title") ?? "Album Deezer", sourceUrl, DeezerTracks(items, artist, cover), cover);
            }
            case "playlist":
            {
                var playlist = await DeezerApiAsync($"/playlist/{id}", ct);
                var items = await DeezerPagesAsync($"/playlist/{id}/tracks?limit=100", ct);
                return new ImportedPlaylist(playlist.Str("title") ?? "Playlist Deezer", sourceUrl, DeezerTracks(items, null, null),
                    playlist.Str("picture_big", "picture_medium", "picture"));
            }
            case "artist":
            {
                var artist = await DeezerApiAsync($"/artist/{id}", ct);
                var items = await DeezerPagesAsync($"/artist/{id}/top?limit=50", ct, 50);
                var name = artist.Str("name") ?? "Artiste";
                return new ImportedPlaylist($"{name} — Titres populaires", sourceUrl, DeezerTracks(items, name, null),
                    artist.Str("picture_big", "picture_medium"));
            }
            default:
                throw new DownloadException("Ce type de lien Deezer n'est pas pris en charge.");
        }
    }

    private static async Task<JsonElement> DeezerApiAsync(string pathOrUrl, CancellationToken ct)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : "https://api.deezer.com" + pathOrUrl;
        var json = await OnlineHttp.GetJsonAsync(url, "Deezer", ct);
        ThrowIfDeezerError(json);
        return json;
    }

    internal static void ThrowIfDeezerError(JsonElement json)
    {
        if (json.Get("error") is not { } error) return;
        var code = error.Num("code");
        throw new DownloadException(code == 800
            ? "Deezer : contenu introuvable ou privé."
            : $"Deezer : {error.Str("message") ?? "erreur inconnue"}.");
    }

    private static async Task<List<JsonElement>> DeezerPagesAsync(string path, CancellationToken ct, int limit = MaxTracks)
    {
        var items = new List<JsonElement>();
        string? next = path;
        for (var guard = 0; next != null && items.Count < limit && guard < 20; guard++)
        {
            var page = await DeezerApiAsync(next, ct);
            items.AddRange(page.Array("data"));
            next = page.Str("next");
        }
        return items.Take(limit).ToList();
    }

    /// <summary>Converts Deezer API track objects (album / playlist / top "data" items).</summary>
    internal static List<ImportedTrack> DeezerTracks(IEnumerable<JsonElement> items, string? defaultArtist, string? defaultCover) =>
        items.Select(t => DeezerTrack(t, defaultArtist, defaultCover)).OfType<ImportedTrack>().ToList();

    private static ImportedTrack? DeezerTrack(JsonElement t, string? defaultArtist, string? defaultCover)
    {
        if (t.Get("readable") is { ValueKind: JsonValueKind.False } && t.Str("title") == null) return null;
        var title = Clean(t.Str("title", "title_short"));
        if (title == null) return null;
        var artist = Clean(t.At("artist")?.Str("name")) ?? defaultArtist;
        var cover = t.At("album")?.Str("cover_big", "cover_medium") ?? defaultCover;
        TimeSpan? duration = t.Num("duration") is { } s and > 0 ? TimeSpan.FromSeconds(s) : null;
        return new ImportedTrack(title, artist, duration, null, cover);
    }

    // ───────────────────────────── Apple Music ─────────────────────────────

    private static async Task<ImportedPlaylist> ImportAppleAsync(string url, CancellationToken ct)
    {
        var page = await OnlineHttp.GetAsync(url, "Apple Music", browser: true, ct);
        if (page.Status == HttpStatusCode.NotFound) throw new DownloadException("Apple Music : contenu introuvable.");
        if (!page.Ok) throw new DownloadException($"Apple Music a répondu par une erreur ({(int)page.Status}).");
        return ParseApplePage(page.Body, url);
    }

    /// <summary>
    /// Reads an Apple Music page: the "serialized-server-data" JSON (internal, may change), then the schema.org
    /// JSON-LD, then OpenGraph tags as a last resort (single entry searched by name).
    /// </summary>
    internal static ImportedPlaylist ParseApplePage(string html, string url)
    {
        var m = AppleRegex().Match(url);
        var kind = m.Success ? m.Groups["type"].Value.ToLowerInvariant() : "";
        var songId = m.Success && m.Groups["song"].Success ? m.Groups["song"].Value : kind == "song" && m.Success ? m.Groups["id"].Value : null;

        var cover = MetaContent(html, "og:image");
        string? headerTitle = null, headerArtist = null;
        var tracks = new List<(ImportedTrack Track, string? Id)>();

        var server = AppleServerDataRegex().Match(html);
        if (server.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(server.Groups["json"].Value));
                WalkApple(doc.RootElement, 0, tracks, ref headerTitle, ref headerArtist);
            }
            catch (JsonException)
            {
                // Try the next format.
            }
        }

        if (tracks.Count == 0)
        {
            foreach (Match ld in JsonLdRegex().Matches(html))
            {
                try
                {
                    using var doc = JsonDocument.Parse(ld.Groups["json"].Value);
                    ReadAppleJsonLd(doc.RootElement, tracks, ref headerTitle, ref headerArtist);
                }
                catch (JsonException)
                {
                    // Ignore broken blocks.
                }
            }
        }

        // Album tracks carry no artwork nor artist of their own.
        var list = tracks
            .Select(t => (Track: t.Track with { Artist = t.Track.Artist ?? (kind == "album" ? headerArtist : null), ThumbnailUrl = t.Track.ThumbnailUrl ?? cover }, t.Id))
            .DistinctBy(t => (t.Track.Title.ToLowerInvariant(), t.Track.Artist?.ToLowerInvariant()))
            .ToList();

        if (songId != null)
        {
            var one = list.FirstOrDefault(t => t.Id == songId);
            if (one.Track == null && list.Count > 0)
            {
                var slug = SongSlugRegex().Match(url) is { Success: true } s ? s.Groups["slug"].Value : "";
                one = list.FirstOrDefault(t => slug.Length > 0 && Slug(t.Track.Title) is { Length: > 0 } ts && slug.StartsWith(ts, StringComparison.Ordinal));
                if (one.Track == null && list.Count == 1) one = list[0];
            }
            if (one.Track != null) list = [one];
        }

        var ogTitle = MetaContent(html, "og:title") ?? MetaContent(html, "apple:title");
        ogTitle = ogTitle == null ? null : AppleSuffixRegex().Replace(ogTitle.Replace(' ', ' '), "").Trim();
        if (ogTitle != null && GenericAppleTitleRegex().IsMatch(ogTitle)) ogTitle = null;

        if (list.Count == 0 && ogTitle != null)
        {
            var parts = AppleBySplitRegex().Split(ogTitle, 2);
            list = [(new ImportedTrack(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null, null, null, cover), null)];
        }
        if (list.Count == 0) throw new DownloadException("Apple Music : impossible d'extraire les titres de cette page.");

        var name = headerTitle ?? (ogTitle != null ? AppleBySplitRegex().Split(ogTitle, 2)[0].Trim() : null) ?? list[0].Track.Title;
        if (songId != null && list.Count == 1) name = list[0].Track.Title;
        if (kind == "artist") name = $"{name} — Titres populaires";
        return new ImportedPlaylist(name, url, list.Select(t => t.Track).ToList(), cover);
    }

    private static void WalkApple(JsonElement node, int depth, List<(ImportedTrack, string?)> tracks, ref string? headerTitle, ref string? headerArtist)
    {
        if (depth > 18) return;
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray()) WalkApple(child, depth + 1, tracks, ref headerTitle, ref headerArtist);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;

        var title = node.Get("title") is { ValueKind: JsonValueKind.String } t ? t.GetString() : null;
        if (title != null && node.Get("trackCount") != null)
        {
            headerTitle ??= Clean(title);
            headerArtist ??= Clean(node.Array("subtitleLinks").FirstOrDefault().Str("title"));
            return;
        }
        if (title != null && node.Get("duration") is { ValueKind: JsonValueKind.Number } d)
        {
            var artist = Clean(node.Str("artistName")) ?? Clean(node.Array("subtitleLinks").FirstOrDefault().Str("title"));
            var artwork = node.At("artwork", "dictionary")?.Str("url") ?? node.At("artwork")?.Str("url");
            var thumb = artwork?.Replace("{w}", "600").Replace("{h}", "600").Replace("{f}", "jpg");
            var id = node.At("contentDescriptor", "identifiers")?.Str("storeAdamID") ?? node.Str("id")?.Split(" - ").Last().Trim();
            var raw = d.GetDouble();
            TimeSpan? duration = raw <= 0 ? null : raw > 20_000 ? TimeSpan.FromMilliseconds(raw) : TimeSpan.FromSeconds(raw);
            if (Clean(title) is { } clean) tracks.Add((new ImportedTrack(clean, artist, duration, null, thumb), id));
            return;
        }
        foreach (var property in node.EnumerateObject()) WalkApple(property.Value, depth + 1, tracks, ref headerTitle, ref headerArtist);
    }

    private static void ReadAppleJsonLd(JsonElement root, List<(ImportedTrack, string?)> tracks, ref string? headerTitle, ref string? headerArtist)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in root.EnumerateArray()) ReadAppleJsonLd(child, tracks, ref headerTitle, ref headerArtist);
            return;
        }
        var type = root.Str("@type");
        if (type is "MusicAlbum" or "MusicPlaylist")
        {
            headerTitle ??= Clean(root.Str("name"));
            headerArtist ??= ByArtist(root);
            foreach (var t in root.Array("tracks").Concat(root.Array("track")))
                AddLdRecording(t, headerArtist, tracks);
        }
        else if (type == "MusicRecording")
        {
            AddLdRecording(root, null, tracks);
        }
    }

    private static void AddLdRecording(JsonElement t, string? defaultArtist, List<(ImportedTrack, string?)> tracks)
    {
        if (Clean(t.Str("name")) is not { } name) return;
        var id = t.Str("url") is { } u && SongIdRegex().Match(u) is { Success: true } m ? m.Groups["id"].Value : null;
        tracks.Add((new ImportedTrack(name, ByArtist(t) ?? defaultArtist, IsoDuration(t.Str("duration")), null, t.At("audio")?.Str("thumbnailUrl")), id));
    }

    private static string? ByArtist(JsonElement e) => e.Get("byArtist") switch
    {
        { ValueKind: JsonValueKind.Array } a => JoinNames(a.EnumerateArray()),
        { ValueKind: JsonValueKind.Object } o => Clean(o.Str("name")),
        _ => null,
    };

    // ───────────────────────────── YouTube / SoundCloud (yt-dlp) ─────────────────────────────

    private async Task<ImportedPlaylist> ImportWithYtDlpAsync(string url, CancellationToken ct)
    {
        // YouTube "Mix" (list=RD…): generated endlessly, capped at 50.
        var limit = MixRegex().IsMatch(url) ? 50 : MaxTracks;
        var target = YouTubeListRegex().Match(url) is { Success: true } list && !MixRegex().IsMatch(url)
            ? $"https://www.youtube.com/playlist?list={list.Groups["list"].Value}"
            : url;
        var listing = await downloader.GetFlatPlaylistAsync(target, limit, false, TimeSpan.FromMinutes(3), ct);
        return FromFlatPlaylist(listing, url);
    }

    internal static ImportedPlaylist FromFlatPlaylist(FlatPlaylist listing, string sourceUrl)
    {
        var tracks = listing.Entries.Select(e =>
        {
            var (artist, title) = ArtistTitle.Split(e.Title, e.Artist ?? e.Subtitle);
            return new ImportedTrack(title, e.Artist ?? artist, e.Duration, e.PageUrl, e.ThumbnailUrl);
        }).ToList();
        var name = Clean(listing.Title) ?? (tracks.Count == 1 ? tracks[0].Title : "Playlist importée");
        return new ImportedPlaylist(name, sourceUrl, tracks, listing.ThumbnailUrl);
    }

    private static bool IsYouTubePlaylist(string url) =>
        YouTubeHostRegex().IsMatch(url) && (YouTubeListRegex().IsMatch(url) || url.Contains("/playlist", StringComparison.OrdinalIgnoreCase));

    // ───────────────────────────── Helpers ─────────────────────────────

    /// <summary>Follows a short link (spotify.link, deezer.page.link…) to the real page URL.</summary>
    private static async Task<string> FollowShortLinkAsync(string url, Regex target, string service, CancellationToken ct)
    {
        var page = await OnlineHttp.GetAsync(url, service, browser: true, ct);
        var final = page.FinalUri.ToString();
        if (target.IsMatch(final)) return final;
        // Some short-link services redirect with JavaScript: look for the target in the page.
        var m = target.Match(WebUtility.HtmlDecode(page.Body).Replace("\\/", "/"));
        if (m.Success) return m.Value;
        throw new DownloadException($"{service} : ce lien court ne mène à aucune playlist.");
    }

    private static string? LargestImage(IEnumerable<JsonElement> images, string urlKey, string widthKey) =>
        images.Select(i => (Url: i.Str(urlKey), Width: i.Num(widthKey) ?? 0)).Where(i => i.Url != null).OrderByDescending(i => i.Width).FirstOrDefault().Url;

    private static string? JoinNames(IEnumerable<JsonElement> artists)
    {
        var names = artists.Select(a => Clean(a.Str("name"))).OfType<string>().Distinct().ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static TimeSpan? Milliseconds(double? ms) => ms is > 0 ? TimeSpan.FromMilliseconds(ms.Value) : null;

    private static TimeSpan? IsoDuration(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        try
        {
            var d = XmlConvert.ToTimeSpan(iso);
            return d > TimeSpan.Zero ? d : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var c = MultiSpaceRegex().Replace(WebUtility.HtmlDecode(s).Replace(' ', ' '), " ").Trim();
        return c.Length == 0 ? null : c;
    }

    private static string? MetaContent(string html, string property)
    {
        foreach (Match tag in MetaRegex().Matches(html))
        {
            string? name = null, content = null;
            foreach (Match attribute in AttributeRegex().Matches(tag.Value))
            {
                var key = attribute.Groups["key"].Value.ToLowerInvariant();
                if (key is "property" or "name") name = attribute.Groups["value"].Value;
                else if (key == "content") content = attribute.Groups["value"].Value;
            }
            if (string.Equals(name, property, StringComparison.OrdinalIgnoreCase)) return Clean(content);
        }
        return null;
    }

    private static string Slug(string s)
    {
        var n = ArtistTitle.Normalize(s);
        return n.Replace(' ', '-');
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"open\.spotify\.com/(?:intl-[a-z]{2}(?:-[a-z]{2})?/)?(?:embed/)?(?<type>track|album|playlist|artist)/(?<id>[A-Za-z0-9]+)", Options)]
    private static partial Regex SpotifyRegex();

    [GeneratedRegex(@"^spotify:(?<type>track|album|playlist|artist):(?<id>[A-Za-z0-9]+)", Options)]
    private static partial Regex SpotifyUriRegex();

    [GeneratedRegex(@"^https?://(?:spotify\.link|spoti\.fi)/", Options)]
    private static partial Regex SpotifyShortRegex();

    [GeneratedRegex(@"deezer\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?(?<type>track|album|playlist|artist)/(?<id>\d+)", Options)]
    private static partial Regex DeezerRegex();

    [GeneratedRegex(@"^https?://(?:deezer\.page\.link|link\.deezer\.com|dzr\.page\.link)/", Options)]
    private static partial Regex DeezerShortRegex();

    [GeneratedRegex(@"music\.apple\.com/(?<country>[a-z]{2})/(?<type>album|playlist|song|artist)/(?:[^/?#]+/)?(?<id>[A-Za-z0-9.\-]+)(?:\?(?:[^#]*&)?i=(?<song>\d+))?", Options)]
    private static partial Regex AppleRegex();

    [GeneratedRegex(@"/(?:album|song)/(?<slug>[^/?#]+)", Options)]
    private static partial Regex SongSlugRegex();

    [GeneratedRegex(@"(?:/song/[^/?#]+/|[?&]i=)(?<id>\d+)", Options)]
    private static partial Regex SongIdRegex();

    [GeneratedRegex(@"^https?://(?:(?:www|m|music)\.)?(?:youtube\.com|youtu\.be)/", Options)]
    private static partial Regex YouTubeHostRegex();

    [GeneratedRegex(@"[?&]list=(?<list>[A-Za-z0-9_-]+)", Options)]
    private static partial Regex YouTubeListRegex();

    [GeneratedRegex(@"[?&](?:list=RD|start_radio=1)", Options)]
    private static partial Regex MixRegex();

    [GeneratedRegex(@"^https?://(?:www\.|m\.)?soundcloud\.com/[^/?#]+/(?:sets|albums)(?:/[^/?#]+)?", Options)]
    private static partial Regex SoundCloudSetRegex();

    [GeneratedRegex("""<script id="__NEXT_DATA__" type="application/json"[^>]*>(?<json>.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();

    [GeneratedRegex("""<script[^>]*\bid="serialized-server-data"[^>]*>(?<json>.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex AppleServerDataRegex();

    [GeneratedRegex("""<script[^>]*type="application/ld\+json"[^>]*>(?<json>.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex JsonLdRegex();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRegex();

    [GeneratedRegex("""\b(?<key>[a-zA-Z:-]+)\s*=\s*"(?<value>[^"]*)"|\b(?<key>[a-zA-Z:-]+)\s*=\s*'(?<value>[^']*)'""")]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"\s+(?:sur|on|dans)\s+Apple\s*Music\s*$", Options)]
    private static partial Regex AppleSuffixRegex();

    [GeneratedRegex(@"^(?:Lecteur web|Apple Music(?: Web)?)\b", Options)]
    private static partial Regex GenericAppleTitleRegex();

    [GeneratedRegex(@"\s+(?:[-–—]|par|by|de|von)\s+", Options)]
    private static partial Regex AppleBySplitRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();
}
