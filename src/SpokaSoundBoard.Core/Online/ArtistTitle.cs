using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SpokaSoundBoard.Core.Online;

/// <summary>
/// Reads "Artist - Title (Official Video) [Lyrics] ft. X" style titles found on YouTube and similar sites,
/// and builds comparable keys for de-duplication and dislikes.
/// </summary>
public static partial class ArtistTitle
{
    private static readonly HashSet<string> GenericArtists = new(StringComparer.Ordinal)
    {
        "inconnu", "unknown", "variousartists", "various", "youtube", "topic", "va", "artist", "na", "soundcloud",
    };

    /// <summary>Splits a raw web title into artist and clean title. The channel is used when the title has no artist part.</summary>
    /// <returns>Artist (null when unknown) and the title without "(Official Video)", "[Lyrics]", "feat. X"…</returns>
    public static (string? Artist, string Title) Split(string rawTitle, string? channel)
    {
        var raw = (rawTitle ?? "").Replace(' ', ' ').Trim();
        var text = CleanNoise(raw);
        var channelArtist = CleanChannel(channel);

        string? artist = null;
        var title = text;
        var m = SeparatorRegex().Match(text);
        if (m.Success)
        {
            var left = StripFeat(m.Groups["left"].Value).Trim();
            var right = m.Groups["right"].Value.Trim();
            // "Title - Artist" posted on the artist's own channel: swap.
            if (channelArtist != null && Normalize(right) == Normalize(channelArtist) && Normalize(left) != Normalize(channelArtist))
                (left, right) = (channelArtist, left);
            if (left.Length is >= 1 and <= 60 && right.Length > 0)
            {
                artist = left;
                title = right;
            }
        }
        else
        {
            var pipe = title.IndexOf(" | ", StringComparison.Ordinal);
            if (pipe > 0) title = title[..pipe];
        }

        title = StripTrailingNoise(StripFeat(title));
        title = Unquote(title).Trim(' ', '-', '–', '—', '|', ':', '.').Trim();
        artist = artist == null ? channelArtist : Unquote(artist).Trim(' ', '-', '–', '—', '|').Trim();
        if (string.IsNullOrWhiteSpace(artist) || GenericArtists.Contains(ArtistKey(artist))) artist = null;
        if (title.Length == 0) title = raw.Length > 0 ? raw : "Sans titre";
        return (artist, title);
    }

    /// <summary>Channel name reduced to the artist: "Daft Punk - Topic" → "Daft Punk", "BoobaVEVO" → "Booba", "Queen Official" → "Queen".</summary>
    public static string? CleanChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return null;
        var c = channel.Replace(' ', ' ').Trim();
        c = TopicSuffixRegex().Replace(c, "");
        c = OfficialSuffixRegex().Replace(c, "").Trim(' ', '-', '–', '—', '|');
        if (c.Length == 0 || GenericArtists.Contains(ArtistKey(c))) return null;
        return c;
    }

    /// <summary>First artist of a credit: "Booba feat. Damso" / "Booba & Damso" / "Booba, Damso" → "Booba".</summary>
    public static string PrimaryArtist(string artist)
    {
        var first = ArtistListRegex().Split(artist ?? "").FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        return (first ?? artist ?? "").Trim();
    }

    /// <summary>Lowercase, no accents, no punctuation, single spaces. "Pour que tu m'aimes encore !" → "pour que tu m aimes encore".</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = true;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// Comparable artist key: no accents, spaces, punctuation nor channel suffix ("BoobaOfficiel", "Booba - Topic"
    /// and "booba" share the key "booba"). Used to match dislikes against channels and titles.
    /// </summary>
    public static string ArtistKey(string? s)
    {
        var plain = Normalize(s);
        plain = ChannelKeySuffixRegex().Replace(plain, "");
        return plain.Replace(" ", "");
    }

    /// <summary>Every artist key a result can be known by (artist read in the title, primary artist, channel).</summary>
    public static IReadOnlyList<string> ArtistKeys(string title, string? channel)
    {
        var keys = new List<string>(3);
        var (artist, _) = Split(title, channel);
        foreach (var raw in new[] { artist != null ? PrimaryArtist(artist) : null, artist, CleanChannel(channel) })
        {
            var k = ArtistKey(raw);
            if (k.Length >= 2 && !GenericArtists.Contains(k) && !keys.Contains(k)) keys.Add(k);
        }
        return keys;
    }

    /// <summary>Short label for a title (no brackets, nothing after "|"), at most 60 characters.</summary>
    internal static string Label(string? title)
    {
        var t = BracketsRegex().Replace(title ?? "", "");
        var pipe = t.IndexOf('|');
        if (pipe > 0) t = t[..pipe];
        t = t.Trim();
        return t.Length > 60 ? t[..60].TrimEnd() + "…" : t;
    }

    private static string CleanNoise(string s)
    {
        s = NoiseBracketRegex().Replace(s, " ");
        return MultiSpaceRegex().Replace(s, " ").Trim();
    }

    private static string StripFeat(string s) => FeatRegex().Replace(s, "").Trim();

    private static string StripTrailingNoise(string s)
    {
        string previous;
        do
        {
            previous = s;
            s = TrailingNoiseRegex().Replace(s, "").Trim();
        } while (s != previous && s.Length > 0);
        return s.Length == 0 ? previous : s;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '«' && s[^1] == '»') || (s[0] == '“' && s[^1] == '”')))
            s = s[1..^1].Trim();
        return s;
    }

    // "(Official Video)", "[Lyrics]", "(Clip officiel)", "(feat. X)", "【MV】"… Brackets mentioning a real version (remix, live) are kept.
    [GeneratedRegex(@"\s*[\(\[【]\s*[^\)\]】]*?\b(?:official|officiel(?:le)?|video|vid[ée]o|clip|audio|lyrics?|paroles|visuali[sz]er|hd|hq|4k|mv|m/v|explicit|feat\.?|ft\.?|featuring|prod\.?|remaster(?:ed)?|color\s+coded)\b[^\)\]】]*[\)\]】]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseBracketRegex();

    [GeneratedRegex(@"\s+(?:\(|\[)?\b(?:feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeatRegex();

    [GeneratedRegex(@"(?:\s+[-–—|]\s*(?:official\s+(?:music\s+)?(?:video|audio|clip|visuali[sz]er)|clip\s+officiel|vid[ée]o\s+officielle|audio\s+officiel|son\s+officiel|lyrics?(?:\s+video)?|paroles|visuali[sz]er|hd|hq|4k)|\s+(?:official\s+(?:music\s+)?(?:video|audio|clip|visuali[sz]er)|clip\s+officiel|vid[ée]o\s+officielle|audio\s+officiel|son\s+officiel|lyrics?\s+video))\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNoiseRegex();

    [GeneratedRegex(@"^\s*(?<left>.+?)\s+[-–—]\s+(?<right>.+)$")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s*[-–—]\s*Topic\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TopicSuffixRegex();

    [GeneratedRegex(@"\s*(?:VEVO|Officiel(?:le)?|Official(?:\s+(?:Channel|Music))?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OfficialSuffixRegex();

    [GeneratedRegex(@"(?:\s*\b(?:topic|music|musique|tv|channel|records?)|\s*(?:vevo|officiel(?:le)?|official))+$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelKeySuffixRegex();

    [GeneratedRegex(@"\s*(?:,|&|\+|\bfeat\.?|\bft\.?|\bfeaturing\b|\bx\b|\bet\b|\band\b|\bvs\.?)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtistListRegex();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex BracketsRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();
}
