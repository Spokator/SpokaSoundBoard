using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SpokaSoundBoard.Core.Models;

/// <summary>Where the blind test takes its songs from.</summary>
public enum BlindTestSource
{
    Library,
    Favorites,
    History,
    Playlist,
}

/// <summary>Blind test options, players and all-time scores (persisted in settings.json).</summary>
public partial class BlindTestSettings : ObservableObject
{
    public const int MinRounds = 5;
    public const int MaxRounds = 50;
    public const int MinExcerptSeconds = 10;
    public const int MaxExcerptSeconds = 45;
    public const int MaxPlayers = 16;
    public const int MaxNameLength = 24;
    public const int MaxRecords = 50;

    [ObservableProperty] private BlindTestSource _source = BlindTestSource.Library;

    /// <summary>Playlist used when <see cref="Source"/> is <see cref="BlindTestSource.Playlist"/>.</summary>
    [ObservableProperty] private Guid? _playlistId;

    [ObservableProperty] private int _rounds = 10;
    [ObservableProperty] private int _excerptSeconds = 20;

    /// <summary>Announce the answer with the text-to-speech voice (« C'était … de … ») into the microphone.</summary>
    [ObservableProperty] private bool _announceAnswer = true;

    public ObservableCollection<string> Players { get; set; } = [];

    /// <summary>All-time scores, best total first.</summary>
    public List<BlindTestRecord> Records { get; set; } = [];

    /// <summary>Adds a player (trimmed, unique ignoring case). Returns false when the name is empty, taken or the list is full.</summary>
    public bool AddPlayer(string? name)
    {
        var clean = CleanName(name);
        if (clean.Length == 0 || Players.Count >= MaxPlayers) return false;
        if (Players.Any(p => string.Equals(p, clean, StringComparison.CurrentCultureIgnoreCase))) return false;
        Players.Add(clean);
        return true;
    }

    /// <summary>Records the final scores of a game: totals, best score, wins (every top scorer with points wins).</summary>
    public void RecordGame(IReadOnlyCollection<(string Name, int Score)> results, DateTime? when = null)
    {
        if (results.Count == 0) return;
        var at = when ?? DateTime.Now;
        var best = results.Max(r => r.Score);
        foreach (var (name, score) in results)
        {
            var clean = CleanName(name);
            if (clean.Length == 0) continue;
            var record = Records.FirstOrDefault(r => string.Equals(r.Name, clean, StringComparison.CurrentCultureIgnoreCase));
            if (record == null)
            {
                record = new BlindTestRecord { Name = clean };
                Records.Add(record);
            }
            record.GamesPlayed++;
            record.TotalPoints += Math.Max(0, score);
            record.BestScore = Math.Max(record.BestScore, score);
            if (score == best && best > 0 && results.Count > 1) record.Wins++;
            record.LastPlayedAt = at;
        }
        SortRecords();
    }

    /// <summary>Normalizes values read from an older or hand-edited settings file.</summary>
    public void Sanitize()
    {
        Rounds = Math.Clamp(Rounds, MinRounds, MaxRounds);
        ExcerptSeconds = Math.Clamp(ExcerptSeconds, MinExcerptSeconds, MaxExcerptSeconds);
        if (!Enum.IsDefined(Source)) Source = BlindTestSource.Library;
        Players ??= [];
        var players = Players.Select(CleanName).Where(n => n.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(MaxPlayers).ToList();
        if (!players.SequenceEqual(Players))
        {
            Players.Clear();
            foreach (var p in players) Players.Add(p);
        }
        Records ??= [];
        Records.RemoveAll(r => r == null || CleanName(r.Name).Length == 0);
        SortRecords();
    }

    public static string CleanName(string? name)
    {
        var clean = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > MaxNameLength ? clean[..MaxNameLength].TrimEnd() : clean;
    }

    private void SortRecords()
    {
        Records.Sort((a, b) => b.TotalPoints != a.TotalPoints ? b.TotalPoints.CompareTo(a.TotalPoints) : b.Wins.CompareTo(a.Wins));
        if (Records.Count > MaxRecords) Records.RemoveRange(MaxRecords, Records.Count - MaxRecords);
    }
}

/// <summary>All-time statistics of one blind test player.</summary>
public sealed class BlindTestRecord
{
    public string Name { get; set; } = "";
    public int TotalPoints { get; set; }
    public int GamesPlayed { get; set; }
    public int Wins { get; set; }
    public int BestScore { get; set; }
    public DateTime LastPlayedAt { get; set; } = DateTime.Now;
}

/// <summary>Text-to-speech panel of the soundboard (persisted in settings.json).</summary>
public partial class TextToSpeechSettings : ObservableObject
{
    public const int MaxHistory = 10;

    /// <summary>WinRT voice id (null = a French voice if installed, otherwise the system default).</summary>
    [ObservableProperty] private string? _voiceId;

    /// <summary>Speaking rate, 0.5 to 3 (1 = normal).</summary>
    [ObservableProperty] private double _rate = 1;

    /// <summary>Voice pitch, 0.3 to 2 (1 = normal).</summary>
    [ObservableProperty] private double _pitch = 1;

    /// <summary>Playback volume, 0 to 2.</summary>
    [ObservableProperty] private double _volume = 1;

    /// <summary>Voice, speed and phrase shortcuts shown (the text box is always visible).</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Last phrases spoken, most recent first.</summary>
    public List<string> History { get; set; } = [];

    /// <summary>Puts <paramref name="text"/> at the top of the history (no duplicates, at most <see cref="MaxHistory"/>).</summary>
    public void Remember(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        History.RemoveAll(h => string.Equals(h, text, StringComparison.CurrentCultureIgnoreCase));
        History.Insert(0, text);
        if (History.Count > MaxHistory) History.RemoveRange(MaxHistory, History.Count - MaxHistory);
    }

    public void Sanitize()
    {
        Rate = double.IsFinite(Rate) ? Math.Clamp(Rate, 0.5, 3) : 1;
        Pitch = double.IsFinite(Pitch) ? Math.Clamp(Pitch, 0.3, 2) : 1;
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 2) : 1;
        History ??= [];
        History.RemoveAll(string.IsNullOrWhiteSpace);
        if (History.Count > MaxHistory) History.RemoveRange(MaxHistory, History.Count - MaxHistory);
    }
}
