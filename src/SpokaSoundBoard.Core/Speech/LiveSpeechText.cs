using System.Globalization;
using System.Text;

namespace SpokaSoundBoard.Core.Speech;

/// <summary>Text rules of « Parler avec cette voix »: what is worth saying, and what is only an echo.</summary>
public static class LiveSpeechText
{
    // Hesitations said alone are not worth a synthetic voice.
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "euh", "heu", "hum", "hmm", "mmh", "mh", "ah", "oh", "eh", "bah", "ben", "uh", "um", "erm",
    };

    /// <summary>Recognized text ready to be spoken, or null when there is nothing to say.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        var words = Words(t);
        if (words.Count == 0) return null;
        if (words.All(Fillers.Contains)) return null;
        return t;
    }

    /// <summary>
    /// True when <paramref name="text"/> is mostly a phrase the voice just said: with speakers instead of headphones,
    /// the microphone hears the synthetic voice, which must not be repeated in a loop.
    /// </summary>
    public static bool IsEcho(string text, IEnumerable<string> recentlySpoken)
    {
        var heard = Words(text);
        if (heard.Count == 0) return false;
        foreach (var spoken in recentlySpoken)
        {
            var said = Words(spoken).ToHashSet(StringComparer.Ordinal);
            if (said.Count == 0) continue;
            var common = heard.Count(said.Contains);
            if (common >= Math.Max(1, (int)Math.Ceiling(heard.Count * 0.6))) return true;
        }
        return false;
    }

    /// <summary>Lower-case words without accents nor punctuation (« Bien joué ! » → bien, joue).</summary>
    internal static List<string> Words(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
