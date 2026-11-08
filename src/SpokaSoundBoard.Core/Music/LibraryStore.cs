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

    public MusicLibraryData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var stream = File.OpenRead(FilePath);
                var data = JsonSerializer.Deserialize<MusicLibraryData>(stream, Options);
                if (data != null) return data;
            }
        }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".bak", true); } catch { /* ignore */ }
        }
        return new MusicLibraryData();
    }

    public void Save(MusicLibraryData data)
    {
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
