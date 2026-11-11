using System.Text;
using SpokaSoundBoard.Core.Music;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Core.Models;

/// <summary>Blind test on a theme: turns YouTube results into songs worth guessing.</summary>
public static class BlindTestThemes
{
    // Compilations, long mixes and lyric loops make poor rounds: the answer is not one song.
    private static readonly string[] Excluded =
    [
        "mix", "compilation", "playlist", "best of", "medley", "full album", "album complet", "nonstop", "non stop",
        "heure", "hour", "hits 20", "top 50", "top 100", "karaoke", "karaoké", "instrumental", "cover", "reprise", "remix",
        "live", "lyrics video", "paroles 1h", "10 hours", "8d",
    ];

    public const double MinSeconds = 90;
    public const double MaxSeconds = 480;

    /// <summary>The searches run for a theme: the theme itself and its typical hits.</summary>
    public static IReadOnlyList<string> Queries(string theme)
    {
        var clean = theme.Trim();
        return [$"{clean} clip officiel", $"{clean} tubes", clean];
    }

    /// <summary>One song per result that is a single track of normal length, with an artist to guess.</summary>
    public static List<TrackItem> ToTracks(IEnumerable<SearchResult> results)
    {
        var tracks = new List<TrackItem>();
        var seen = new HashSet<string>();
        foreach (var r in results)
        {
            if (r.Duration is not { } d || d.TotalSeconds < MinSeconds || d.TotalSeconds > MaxSeconds) continue;
            var lower = r.Title.ToLowerInvariant();
            if (Excluded.Any(w => ContainsWord(lower, w))) continue;
            var (artist, title) = ArtistTitle.Split(r.Title, r.Subtitle);
            artist = r.Artist ?? artist;
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) continue;
            if (!seen.Add(r.PageUrl) || !seen.Add(MusicLibrary.Normalize(artist) + "|" + MusicLibrary.Normalize(title))) continue;
            tracks.Add(new TrackItem
            {
                DisplayTitle = title,
                Artist = artist,
                SourceUrl = r.PageUrl,
                ThumbnailUrl = r.ThumbnailUrl,
                DurationSeconds = d.TotalSeconds,
                State = TrackState.Remote,
                IsTransient = true,
            });
        }
        return tracks;
    }

    private static bool ContainsWord(string text, string word)
    {
        var i = text.IndexOf(word, StringComparison.Ordinal);
        while (i >= 0)
        {
            var before = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            var end = i + word.Length;
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after) return true;
            i = text.IndexOf(word, i + 1, StringComparison.Ordinal);
        }
        return false;
    }
}

/// <summary>Progressive hints for the round being played.</summary>
public static class BlindTestHints
{
    public const int MaxLevel = 3;

    /// <summary>
    /// Hint text for <paramref name="level"/> (1 to <see cref="MaxLevel"/>): 1 = shape of the title, 2 = first letter
    /// of each word of the title, 3 = the artist's initials too.
    /// </summary>
    public static string For(string title, string? artist, int level)
    {
        if (level <= 0) return "";
        var hint = "Titre : " + Mask(title, firstLetters: level >= 2);
        if (level >= 3 && !string.IsNullOrWhiteSpace(artist)) hint += "\nArtiste : " + Mask(artist, firstLetters: true);
        return hint;
    }

    /// <summary>Letters become « • », spaces and punctuation stay; optionally the first letter of each word shows.</summary>
    public static string Mask(string text, bool firstLetters)
    {
        var sb = new StringBuilder(text.Length);
        var startOfWord = true;
        foreach (var c in text.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(firstLetters && startOfWord ? char.ToUpper(c) : '•');
                startOfWord = false;
            }
            else
            {
                sb.Append(c);
                startOfWord = c is ' ' or '-' or '\'' or '’';
            }
        }
        return sb.ToString();
    }
}
