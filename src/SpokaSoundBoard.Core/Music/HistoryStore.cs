using System.Text;
using System.Text.Json;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Music;

/// <summary>
/// The listening history in its own file, one JSON object per line (history.jsonl). New listenings and updates are
/// appended (a line whose id is already known replaces the earlier one), so saving costs a few hundred bytes instead
/// of rewriting thousands of entries. The file is compacted from time to time. A line cut by a crash is skipped.
/// Not thread-safe: <see cref="LibraryStore"/> calls it from its single writer.
/// </summary>
public sealed class HistoryStore(string path)
{
    public const string FileName = "history.jsonl";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public string FilePath { get; } = path;

    /// <summary>Lines currently in the file (to know when compacting is worth it).</summary>
    public int LineCount { get; private set; }

    public bool Exists => File.Exists(FilePath);

    /// <summary>Entries in listening order (the latest version of each), at most <paramref name="max"/>, newest kept.</summary>
    public List<HistoryEntry> Load(int max)
    {
        LineCount = 0;
        if (!File.Exists(FilePath)) return [];
        var byId = new Dictionary<Guid, HistoryEntry>();
        var order = new List<Guid>();
        foreach (var line in File.ReadLines(FilePath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            LineCount++;
            HistoryEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<HistoryEntry>(line, Options);
            }
            catch (JsonException)
            {
                continue; // cut by a crash or edited by hand: skip that line only
            }
            if (entry == null) continue;
            if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
            if (!byId.ContainsKey(entry.Id)) order.Add(entry.Id);
            byId[entry.Id] = entry;
        }
        var entries = order.Select(id => byId[id]).ToList();
        if (entries.Count > max) entries.RemoveRange(0, entries.Count - max);
        return entries;
    }

    public static string Serialize(HistoryEntry entry) => JsonSerializer.Serialize(entry, Options);

    /// <summary>Adds lines prepared by <see cref="Serialize"/>.</summary>
    public void Append(IReadOnlyCollection<string> lines)
    {
        if (lines.Count == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // A previous crash may have left a line without its end: start on a new line.
        var newLine = File.Exists(FilePath) && LastByte() != '\n';
        using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            if (newLine) writer.Write('\n');
            foreach (var line in lines) writer.Write(line + "\n");
        }
        LineCount += lines.Count;
    }

    /// <summary>Replaces the whole file (after a clear, a migration, or to compact it). Atomic.</summary>
    public void Rewrite(IReadOnlyCollection<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
            foreach (var line in lines) writer.Write(line + "\n");
        File.Move(temp, FilePath, true);
        LineCount = lines.Count;
    }

    /// <summary>Keeps one line per entry and only the newest <paramref name="max"/> ones.</summary>
    public void Compact(int max) => Rewrite(Load(max).Select(Serialize).ToList());

    private int LastByte()
    {
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return '\n';
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte();
    }
}
