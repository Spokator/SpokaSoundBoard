using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using SpokaSoundBoard.Core.Music;

namespace SpokaSoundBoard.Core.Models;

/// <summary>Blind test rules: title +2, artist +1, both +1 bonus, fast answer +1.</summary>
public static class BlindTestScoring
{
    public const int TitlePoints = 2;
    public const int ArtistPoints = 1;
    public const int BothBonus = 1;
    public const int FastBonus = 1;

    /// <summary>An answer given within this delay after the excerpt started earns the fast bonus.</summary>
    public static readonly TimeSpan FastWindow = TimeSpan.FromSeconds(10);

    /// <summary>Points of one answer. The fast bonus only counts when something was found.</summary>
    public static int Points(bool title, bool artist, bool fast) =>
        (title ? TitlePoints : 0)
        + (artist ? ArtistPoints : 0)
        + (title && artist ? BothBonus : 0)
        + (fast && (title || artist) ? FastBonus : 0);

    /// <summary>Competition ranking (1, 1, 3…) of scores, in the order given.</summary>
    public static int[] Ranks(IReadOnlyList<int> scores)
    {
        var ranks = new int[scores.Count];
        for (var i = 0; i < scores.Count; i++)
            ranks[i] = 1 + scores.Count(s => s > scores[i]);
        return ranks;
    }
}

/// <summary>What one player found during the current round.</summary>
public partial class BlindTestAnswer : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Points))]
    private bool _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Points))]
    private bool _artist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Points))]
    private bool _fast;

    public int Points => BlindTestScoring.Points(Title, Artist, Fast);
}

/// <summary>Chooses the songs of a game: no repeats, already downloaded songs first.</summary>
public static class BlindTestPicker
{
    /// <summary>
    /// Every usable candidate in play order: duplicates (same id, or same artist + title) removed, the tracks
    /// <paramref name="isReady"/> accepts first (shuffled), then the others (shuffled). The game plays the first
    /// rounds and keeps the rest as spares for songs that fail to download or are skipped.
    /// </summary>
    public static List<TrackItem> Order(IEnumerable<TrackItem> candidates, Func<TrackItem, bool> isReady, Random random)
    {
        var ids = new HashSet<Guid>();
        var keys = new HashSet<string>();
        var ready = new List<TrackItem>();
        var remote = new List<TrackItem>();
        foreach (var t in candidates)
        {
            if (t == null || !ids.Add(t.Id) || !IsPlayable(t, isReady)) continue;
            var key = MusicLibrary.Normalize(t.Artist) + "|" + MusicLibrary.Normalize(t.Title);
            if (key.Length > 1 && !keys.Add(key)) continue;
            (isReady(t) ? ready : remote).Add(t);
        }
        Shuffle(ready, random);
        Shuffle(remote, random);
        ready.AddRange(remote);
        return ready;
    }

    /// <summary>A track can be played if it is on disk or can be found online (link, or a title to search).</summary>
    public static bool IsPlayable(TrackItem t, Func<TrackItem, bool> isReady) =>
        isReady(t) || !string.IsNullOrEmpty(t.SourceUrl) || !string.IsNullOrWhiteSpace(t.SearchQuery)
        || !string.IsNullOrWhiteSpace(t.DisplayTitle);

    /// <summary>
    /// Start of the excerpt: random point between 20 % and 45 % of the song (past the intro, before the end),
    /// moved earlier when needed so the whole excerpt fits.
    /// </summary>
    public static double ExcerptStart(double durationSeconds, double excerptSeconds, Random random)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return 0;
        var start = durationSeconds * (0.20 + 0.25 * random.NextDouble());
        var latest = durationSeconds - excerptSeconds - 1;
        if (start > latest) start = latest;
        return Math.Max(0, start);
    }

    private static void Shuffle<T>(IList<T> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

/// <summary>Tolerant comparison of a typed guess with a title or an artist.</summary>
public static partial class BlindTestAnswerMatcher
{
    private static readonly HashSet<string> Articles = ["the", "le", "la", "les", "l", "un", "une", "a", "an"];

    /// <summary>
    /// True when <paramref name="guess"/> names <paramref name="expected"/>: accents, case and punctuation ignored,
    /// a leading article and decorations like « (Clip officiel) » or « feat. X » ignored, a few typos allowed
    /// (more for longer names), and the answer may be inside a sentence (« je crois que c'est Daft Punk »).
    /// </summary>
    public static bool IsMatch(string? guess, string? expected)
    {
        var target = Words(Clean(expected));
        var said = Words(MusicLibrary.Normalize(guess));
        if (target.Length == 0 || said.Length == 0) return false;

        var targetText = string.Join(' ', target);
        var tolerance = Tolerance(targetText.Length);

        // Compare with every run of words of the guess around the target's word count.
        for (var size = Math.Max(1, target.Length - 1); size <= Math.Min(said.Length, target.Length + 1); size++)
            for (var start = 0; start + size <= said.Length; start++)
            {
                var window = string.Join(' ', said, start, size);
                if (window == targetText) return true;
                if (tolerance > 0 && Math.Abs(window.Length - targetText.Length) <= tolerance
                                  && Levenshtein(window, targetText, tolerance) <= tolerance)
                    return true;
                // Spaces typed differently (« daftpunk »).
                if (window.Replace(" ", "") == targetText.Replace(" ", "")) return true;
            }
        return false;
    }

    /// <summary>Typos allowed: none for very short names, then about one per five letters (at most 3).</summary>
    public static int Tolerance(int length) => length switch
    {
        <= 3 => 0,
        <= 6 => 1,
        <= 12 => 2,
        _ => 3,
    };

    /// <summary>Edit distance, stopping early once it exceeds <paramref name="max"/> (returns max + 1 then).</summary>
    public static int Levenshtein(string a, string b, int max = int.MaxValue)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > max) return max + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>Normalized title / artist without the parts players are not expected to say.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var normalized = MusicLibrary.Normalize(StripDecorations(text));
        return normalized.Length > 0 ? normalized : MusicLibrary.Normalize(text);
    }

    /// <summary>
    /// The title as people say it: without « (Clip officiel) », « [Lyrics] », « feat. X » or « - Remastered 2011 »
    /// (case and accents kept, e.g. to announce it). Returns the text unchanged if nothing would be left.
    /// </summary>
    public static string StripDecorations(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Brackets().Replace(text, " ");
        t = Featuring().Replace(t, " ");
        t = Suffix().Replace(t, " ");
        t = string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '-', '–', '—', '|');
        return t.Length > 0 ? t : text.Trim();
    }

    private static string[] Words(string normalized)
    {
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 && Articles.Contains(words[0]) ? words[1..] : words;
    }

    [GeneratedRegex(@"[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s(feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"\s[-–—|]\s.*(remaster|version|edit|live|mix|radio|clip|officiel|official|lyrics?|audio).*$", RegexOptions.IgnoreCase)]
    private static partial Regex Suffix();
}
