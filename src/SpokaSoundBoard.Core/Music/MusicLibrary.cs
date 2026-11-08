using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Music;

/// <summary>
/// The music library: every known track, the playlists, the favourites and the listening history.
/// Call from the UI thread only.
/// </summary>
public sealed class MusicLibrary
{
    public const int MaxHistory = 10000;

    public MusicLibrary(MusicLibraryData? data = null)
    {
        Data = data ?? new MusicLibraryData();
        Sanitize(Data);
    }

    public MusicLibraryData Data { get; }

    public ObservableCollection<TrackItem> Tracks => Data.Tracks;
    public ObservableCollection<MusicPlaylist> Playlists => Data.Playlists;
    public IReadOnlyList<HistoryEntry> History => Data.History;

    /// <summary>Raised after any change worth saving.</summary>
    public event Action? Changed;

    public TrackItem? Find(Guid id) => Tracks.FirstOrDefault(t => t.Id == id);

    /// <summary>Finds a track by file path or source URL (YouTube links are compared by video id).</summary>
    public TrackItem? FindExisting(string? filePath, string? sourceUrl)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            var byFile = Tracks.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (byFile != null) return byFile;
        }
        if (!string.IsNullOrEmpty(sourceUrl))
        {
            var key = SourceKey(sourceUrl);
            return Tracks.FirstOrDefault(t => t.SourceUrl != null && SourceKey(t.SourceUrl) == key);
        }
        return null;
    }

    /// <summary>Adds the track unless the same file / link is already known; returns the library instance.</summary>
    public TrackItem AddOrGet(TrackItem track)
    {
        if (Tracks.Contains(track)) return track;
        var existing = FindExisting(track.FilePath, track.SourceUrl);
        if (existing != null)
        {
            // Complete the existing entry with what the new one knows.
            var changed = false;
            if (!existing.IsDownloaded && track.IsDownloaded) { existing.FilePath = track.FilePath; changed = true; }
            if (existing.Artist == null && track.Artist != null) { existing.Artist = track.Artist; changed = true; }
            if (existing.ThumbnailUrl == null && track.ThumbnailUrl != null) { existing.ThumbnailUrl = track.ThumbnailUrl; changed = true; }
            if (existing.DurationSeconds == null && track.DurationSeconds != null) { existing.DurationSeconds = track.DurationSeconds; changed = true; }
            if (string.IsNullOrWhiteSpace(existing.DisplayTitle) && !string.IsNullOrWhiteSpace(track.DisplayTitle)) { existing.DisplayTitle = track.DisplayTitle; changed = true; }
            if (!track.IsTransient && existing.IsTransient) { existing.IsTransient = false; changed = true; }
            if (changed) Changed?.Invoke();
            return existing;
        }
        CompleteWebMetadata(track);
        Tracks.Add(track);
        Changed?.Invoke();
        return track;
    }

    /// <summary>Removes a track from the library and from every playlist.</summary>
    public void Remove(TrackItem track)
    {
        Tracks.Remove(track);
        foreach (var p in Playlists)
            while (p.TrackIds.Remove(track.Id)) { }
        Changed?.Invoke();
    }

    // ---- Playlists -------------------------------------------------------------------------------------

    public MusicPlaylist CreatePlaylist(string name, IEnumerable<TrackItem>? tracks = null, string? sourceUrl = null)
    {
        var playlist = new MusicPlaylist { Name = UniquePlaylistName(name), SourceUrl = sourceUrl, Color = NextColor() };
        if (tracks != null)
            foreach (var t in tracks)
            {
                var item = AddOrGet(t);
                item.IsTransient = false;
                if (!playlist.TrackIds.Contains(item.Id)) playlist.TrackIds.Add(item.Id);
            }
        Playlists.Add(playlist);
        Changed?.Invoke();
        return playlist;
    }

    public void DeletePlaylist(MusicPlaylist playlist)
    {
        Playlists.Remove(playlist);
        Changed?.Invoke();
    }

    public void RenamePlaylist(MusicPlaylist playlist, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == playlist.Name) return;
        playlist.Name = UniquePlaylistName(name, playlist);
        Changed?.Invoke();
    }

    /// <summary>Adds tracks at the end of the playlist (tracks already in it are skipped). Returns the number added.</summary>
    public int AddToPlaylist(MusicPlaylist playlist, IEnumerable<TrackItem> tracks)
    {
        var added = 0;
        foreach (var t in tracks)
        {
            var item = AddOrGet(t);
            item.IsTransient = false;
            var id = item.Id;
            if (playlist.TrackIds.Contains(id)) continue;
            playlist.TrackIds.Add(id);
            added++;
        }
        if (added > 0) Changed?.Invoke();
        return added;
    }

    public void RemoveFromPlaylist(MusicPlaylist playlist, TrackItem track)
    {
        if (playlist.TrackIds.Remove(track.Id)) Changed?.Invoke();
    }

    public void MoveInPlaylist(MusicPlaylist playlist, int from, int to)
    {
        if (from < 0 || to < 0 || from >= playlist.TrackIds.Count || to >= playlist.TrackIds.Count || from == to) return;
        playlist.TrackIds.Move(from, to);
        Changed?.Invoke();
    }

    public List<TrackItem> TracksOf(MusicPlaylist playlist)
    {
        var byId = Tracks.ToDictionary(t => t.Id);
        return playlist.TrackIds.Select(id => byId.GetValueOrDefault(id)).OfType<TrackItem>().ToList();
    }

    public TimeSpan DurationOf(IEnumerable<TrackItem> tracks) =>
        TimeSpan.FromSeconds(tracks.Sum(t => t.DurationSeconds ?? 0));

    // ---- Favourites, dislikes ----------------------------------------------------------------------------

    public IEnumerable<TrackItem> Favorites => Tracks.Where(t => t.IsFavorite).OrderByDescending(t => t.LastPlayedAt ?? t.AddedAt);

    public void ToggleFavorite(TrackItem track)
    {
        track = AddOrGet(track);
        track.IsFavorite = !track.IsFavorite;
        if (track.IsFavorite) track.IsTransient = false;
        Changed?.Invoke();
    }

    public void DislikeArtist(string artist)
    {
        var key = Normalize(artist);
        if (key.Length == 0 || Data.DislikedArtists.Contains(key)) return;
        Data.DislikedArtists.Add(key);
        Changed?.Invoke();
    }

    public void DislikeTrack(TrackItem track)
    {
        if (track.SourceUrl is { } url && !Data.DislikedUrls.Contains(SourceKey(url)))
        {
            Data.DislikedUrls.Add(SourceKey(url));
            Changed?.Invoke();
        }
    }

    public bool IsDisliked(string? artist, string? sourceUrl) =>
        artist != null && Data.DislikedArtists.Contains(Normalize(artist))
        || sourceUrl != null && Data.DislikedUrls.Contains(SourceKey(sourceUrl));

    // ---- History -------------------------------------------------------------------------------------------

    /// <summary>Records that <paramref name="track"/> started playing. Update the returned entry's listened time later.</summary>
    public HistoryEntry RecordPlay(TrackItem track, DateTime? at = null)
    {
        var when = at ?? DateTime.Now;
        track.PlayCount++;
        track.LastPlayedAt = when;
        var entry = new HistoryEntry
        {
            TrackId = track.Id,
            Title = track.Title,
            Artist = track.Artist,
            SourceUrl = track.SourceUrl,
            PlayedAt = when,
        };
        Data.History.Add(entry);
        if (Data.History.Count > MaxHistory) Data.History.RemoveRange(0, Data.History.Count - MaxHistory);
        Changed?.Invoke();
        return entry;
    }

    public void ClearHistory()
    {
        Data.History.Clear();
        Changed?.Invoke();
    }

    /// <summary>Distinct tracks, most recently played first.</summary>
    public List<TrackItem> RecentlyPlayed(int count)
    {
        var result = new List<TrackItem>();
        var seen = new HashSet<Guid>();
        for (var i = Data.History.Count - 1; i >= 0 && result.Count < count; i--)
        {
            var id = Data.History[i].TrackId;
            if (!seen.Add(id)) continue;
            if (Find(id) is { } t) result.Add(t);
        }
        return result;
    }

    public List<TrackItem> MostPlayed(int count) =>
        Tracks.Where(t => t.PlayCount > 0).OrderByDescending(t => t.PlayCount).ThenByDescending(t => t.LastPlayedAt).Take(count).ToList();

    public List<TrackItem> RecentlyAdded(int count) => LibraryTracks.OrderByDescending(t => t.AddedAt).Take(count).ToList();

    /// <summary>Tracks the user added (excludes transient autoplay / direct-play tracks).</summary>
    public IEnumerable<TrackItem> LibraryTracks => Tracks.Where(t => !t.IsTransient);

    /// <summary>Forgets transient tracks that are in no playlist, not favourite and not played for <paramref name="days"/> days.</summary>
    public int PruneTransient(int days, IReadOnlyCollection<Guid> keep, DateTime? now = null)
    {
        var limit = (now ?? DateTime.Now).AddDays(-days);
        var inPlaylists = Playlists.SelectMany(p => p.TrackIds).ToHashSet();
        var stale = Tracks.Where(t => t.IsTransient && !t.IsFavorite && !inPlaylists.Contains(t.Id) && !keep.Contains(t.Id)
                                      && (t.LastPlayedAt ?? t.AddedAt) < limit).ToList();
        foreach (var t in stale) Tracks.Remove(t);
        if (stale.Count > 0) Changed?.Invoke();
        return stale.Count;
    }

    // ---- Search ----------------------------------------------------------------------------------------------

    /// <summary>Accent- and case-insensitive search on title, artist and source; every word must match.</summary>
    public static bool Matches(TrackItem track, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var haystack = Normalize($"{track.Title} {track.Artist} {track.Folder}");
        return Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(haystack.Contains);
    }

    public IEnumerable<TrackItem> Search(string query) => LibraryTracks.Where(t => Matches(t, query));

    /// <summary>Lowercase, no accents, punctuation turned into spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space && sb.Length > 0)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Stable key of a web source: "yt:&lt;id&gt;" for YouTube, else the URL without query noise.</summary>
    public static string SourceKey(string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("youtu.be")) return "yt:" + uri.AbsolutePath.Trim('/');
        if (host.Contains("youtube.com"))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            if (query["v"] is { Length: > 0 } v) return "yt:" + v;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed") return "yt:" + segments[1];
        }
        return (host.Replace("www.", "") + uri.AbsolutePath.TrimEnd('/')).ToLowerInvariant();
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private static readonly string[] PlaylistColors = ["#7C5CFF", "#22D3EE", "#F472B6", "#34D399", "#FBBF24", "#F87171", "#60A5FA", "#A78BFA"];

    private string NextColor() => PlaylistColors[Playlists.Count % PlaylistColors.Length];

    private string UniquePlaylistName(string name, MusicPlaylist? except = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Nouvelle playlist" : name.Trim();
        var candidate = name;
        for (var i = 2; Playlists.Any(p => p != except && string.Equals(p.Name, candidate, StringComparison.CurrentCultureIgnoreCase)); i++)
            candidate = $"{name} ({i})";
        return candidate;
    }

    private static void Sanitize(MusicLibraryData data)
    {
        data.Tracks ??= [];
        data.Playlists ??= [];
        data.History ??= [];
        data.DislikedArtists ??= [];
        data.DislikedUrls ??= [];
        data.SavedQueue ??= [];

        // Duplicate ids (hand-edited file) would break playlists: give them new ids.
        var ids = new HashSet<Guid>();
        foreach (var t in data.Tracks)
            if (t.Id == Guid.Empty || !ids.Add(t.Id))
            {
                t.Id = Guid.NewGuid();
                ids.Add(t.Id);
            }
        foreach (var p in data.Playlists)
        {
            p.TrackIds ??= [];
            for (var i = p.TrackIds.Count - 1; i >= 0; i--)
                if (!ids.Contains(p.TrackIds[i])) p.TrackIds.RemoveAt(i);
        }
        var upgrade = data.Version < MusicLibraryData.CurrentVersion;
        foreach (var t in data.Tracks)
        {
            t.State = t.IsDownloaded ? TrackState.Ready : TrackState.Remote;
            // Derived once (it rewrites titles): later edits by the user are never touched again.
            if (upgrade) CompleteWebMetadata(t);
        }
        data.Version = MusicLibraryData.CurrentVersion;
    }

    /// <summary>
    /// Fills what can be derived offline for web tracks imported by 1.x (or downloaded from a cache hit):
    /// the YouTube thumbnail from the video id, and the artist from titles like « Artiste - Titre (Clip officiel) ».
    /// </summary>
    public static void CompleteWebMetadata(TrackItem t)
    {
        if (t.SourceUrl == null) return;
        if (string.IsNullOrEmpty(t.ThumbnailUrl) && Online.RecommendationService.YouTubeId(t.SourceUrl) is { } id)
            t.ThumbnailUrl = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
        if (string.IsNullOrWhiteSpace(t.Artist) && !string.IsNullOrWhiteSpace(t.DisplayTitle))
        {
            var (artist, title) = Online.ArtistTitle.Split(t.DisplayTitle, null);
            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
            {
                t.Artist = artist;
                t.DisplayTitle = title;
            }
        }
    }
}
