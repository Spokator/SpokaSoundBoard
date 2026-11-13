using System.Text.Json;
using System.Text.Json.Serialization;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Music;

/// <summary>
/// Loads and saves library.json (atomic write, backup of a corrupted file, migration from 1.x) and the listening
/// history next to it (<see cref="HistoryStore"/>). Day-to-day saves run on a background writer.
/// </summary>
public sealed class LibraryStore
{
    public const string MigratedPlaylistName = "Ma playlist";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public LibraryStore(string directory)
    {
        Directory = directory;
        FilePath = Path.Combine(directory, "library.json");
        History = new HistoryStore(Path.Combine(directory, HistoryStore.FileName));
    }

    public string Directory { get; }
    public string FilePath { get; }
    public HistoryStore History { get; }

    public bool Exists => File.Exists(FilePath);

    /// <summary>Raised on the writer thread when a background save fails.</summary>
    public event Action<Exception>? SaveFailed;

    // Background writer: one save at a time, in order.
    private readonly object _writeLock = new();
    private Task _writer = Task.CompletedTask;
    private byte[]? _lastWritten;
    private bool _historyRewritePending;

    /// <summary>Saves done in the background so far (for the tests and the diagnostics).</summary>
    public int BackgroundSaves { get; private set; }

    /// <summary>Loads library.json and the history (moved out of library.json the first time).</summary>
    public MusicLibraryData Load()
    {
        var data = LoadLibrary();
        try
        {
            if (History.Exists)
            {
                data.History = History.Load(MusicLibrary.MaxHistory);
            }
            else if (data.LegacyHistory is { Count: > 0 } legacy)
            {
                data.History = legacy;
                _historyRewritePending = true; // written to history.jsonl by the next save
            }
        }
        catch (Exception)
        {
            // Unreadable history: the library itself still works; the file is kept for a later attempt.
        }
        data.LegacyHistory = null;
        foreach (var e in data.History)
            if (e.Id == Guid.Empty) e.Id = Guid.NewGuid();
        return data;
    }

    /// <summary>
    /// True when library.json exists but could not be read: saving is then refused for this session so a
    /// transient lock (sync tool, antivirus) or a newer format never replaces the user's library with an empty one.
    /// </summary>
    public bool LoadFailed { get; private set; }

    private MusicLibraryData LoadLibrary()
    {
        LoadFailed = false;
        if (!File.Exists(FilePath)) return new MusicLibraryData();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = File.OpenRead(FilePath);
                return JsonSerializer.Deserialize<MusicLibraryData>(stream, Options) ?? throw new JsonException("empty");
            }
            catch (IOException) when (attempt < 4)
            {
                // Locked by another process for a moment: try again.
                Thread.Sleep(250);
            }
            catch
            {
                LoadFailed = true;
                try
                {
                    File.Copy(FilePath, $"{FilePath}.{DateTime.Now:yyyyMMdd-HHmmss}.bak", false);
                }
                catch
                {
                    // Keep going: the original file is left untouched anyway.
                }
                return new MusicLibraryData();
            }
        }
    }

    /// <summary>Saves everything now, synchronously (history file rewritten whole). Waits for background saves first.</summary>
    public void Save(MusicLibraryData data)
    {
        if (LoadFailed) throw LoadFailedError();
        Flush();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, Options);
        var lines = data.History.Select(HistoryStore.Serialize).ToList();
        lock (_writeLock)
        {
            WriteLibrary(bytes);
            History.Rewrite(lines);
            _historyRewritePending = false;
        }
    }

    /// <summary>
    /// Saves without blocking the caller: only a copy of the objects is taken here (about a millisecond for thousands
    /// of tracks); serializing and writing happen on a background writer, which skips library.json when it did not
    /// change and only appends the history entries added or updated since the last save.
    /// </summary>
    public void SaveInBackground(MusicLibrary library)
    {
        if (LoadFailed) throw LoadFailedError();
        var snapshot = library.Data.SnapshotForSave();
        var (reset, changed) = library.TakeHistoryChanges();
        List<HistoryEntry>? rewrite = null;
        if (reset || _historyRewritePending)
        {
            rewrite = library.Data.History.Select(e => e.SnapshotCopy()).ToList();
            _historyRewritePending = false;
        }
        var append = rewrite == null ? changed.Select(e => e.SnapshotCopy()).ToList() : [];

        lock (_writeLock)
        {
            _writer = _writer.ContinueWith(_ =>
            {
                try
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Options);
                    lock (_writeLock)
                    {
                        WriteLibrary(bytes);
                        if (rewrite != null) History.Rewrite(rewrite.Select(HistoryStore.Serialize).ToList());
                        else History.Append(append.Select(HistoryStore.Serialize).ToList());
                        // Updates pile up lines for the same entries: compact once the file holds twice the history.
                        if (History.LineCount > 2 * MusicLibrary.MaxHistory) History.Compact(MusicLibrary.MaxHistory);
                        BackgroundSaves++;
                    }
                }
                catch (Exception ex)
                {
                    SaveFailed?.Invoke(ex);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Waits for the background saves (on exit, before a backup).</summary>
    public void Flush(TimeSpan? timeout = null)
    {
        Task writer;
        lock (_writeLock) writer = _writer;
        try
        {
            writer.Wait(timeout ?? TimeSpan.FromSeconds(15));
        }
        catch (AggregateException)
        {
            // Reported through SaveFailed.
        }
    }

    private void WriteLibrary(byte[] bytes)
    {
        if (_lastWritten != null && bytes.AsSpan().SequenceEqual(_lastWritten) && File.Exists(FilePath)) return;
        System.IO.Directory.CreateDirectory(Directory);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, FilePath, true);
        _lastWritten = bytes;
    }

    private static IOException LoadFailedError() =>
        new("La bibliothèque n'a pas pu être lue au démarrage : elle n'est pas écrasée. Redémarrez SpokaSoundBoard.");

    public static string Serialize(MusicLibraryData data) => JsonSerializer.Serialize(data, Options);

    public static MusicLibraryData Deserialize(string json) =>
        JsonSerializer.Deserialize<MusicLibraryData>(json, Options) ?? new MusicLibraryData();

    /// <summary>
    /// 1.x kept a single playlist in settings.json. Moves it into the library as « Ma playlist » and makes it the queue.
    /// Returns true when something was migrated (the caller then clears <see cref="AppSettings.Playlist"/> and saves both files).
    /// </summary>
    public static bool Migrate(AppSettings settings, MusicLibrary library)
    {
        if (settings.Playlist.Count == 0) return false;
        var tracks = settings.Playlist.Select(library.AddOrGet).ToList();
        if (!library.Playlists.Any(p => p.Name == MigratedPlaylistName))
            library.CreatePlaylist(MigratedPlaylistName, tracks);
        if (library.Data.SavedQueue.Count == 0)
        {
            library.Data.SavedQueue = tracks.Select(t => t.Id).ToList();
            library.Data.SavedQueueIndex = 0;
        }
        return true;
    }
}
