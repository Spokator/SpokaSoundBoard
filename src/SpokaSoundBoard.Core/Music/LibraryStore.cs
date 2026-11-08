using System.Text.Json;
using System.Text.Json.Serialization;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Music;

/// <summary>Loads and saves library.json (atomic write, backup of a corrupted file, migration from 1.x).</summary>
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
    }

    public string Directory { get; }
    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// True when library.json exists but could not be read: saving is then refused for this session so a
    /// transient lock (sync tool, antivirus) or a newer format never replaces the user's library with an empty one.
    /// </summary>
    public bool LoadFailed { get; private set; }

    public MusicLibraryData Load()
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

    public void Save(MusicLibraryData data)
    {
        if (LoadFailed) throw new IOException("La bibliothèque n'a pas pu être lue au démarrage : elle n'est pas écrasée. Redémarrez SpokaSoundBoard.");
        System.IO.Directory.CreateDirectory(Directory);
        var temp = FilePath + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, data, Options);
        File.Move(temp, FilePath, true);
    }

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
