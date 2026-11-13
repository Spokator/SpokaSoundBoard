using System.IO.Compression;
using System.Text.Json;
using SpokaSoundBoard.Core.Music;

namespace SpokaSoundBoard.Core;

/// <summary>What a backup contains (stored as manifest.json at the root of the archive).</summary>
public sealed class BackupManifest
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public string AppVersion { get; set; } = "";
    public string Edition { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int Sounds { get; set; }
    public int Tracks { get; set; }
    public int Playlists { get; set; }
    public int HistoryEntries { get; set; }
    public bool IncludesMusic { get; set; }

    /// <summary>Audio files in the archive: original full path → entry name.</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Result of <see cref="FullBackup.Restore"/>.</summary>
public sealed record RestoreResult(BackupManifest Manifest, int FilesExtracted, int FilesReused, string? SafetyCopy);

/// <summary>
/// One file with everything needed to get SpokaSoundBoard back on another PC or in the other edition (installer /
/// Microsoft Store): settings (soundboard, voice presets, shortcuts, remote deck…), music library, playlists, history,
/// the sound files of the pads and, optionally, the music files. Restoring rewrites the file paths to where the files
/// end up. The archive is a zip (<see cref="Extension"/>). Restoring is done before the settings are loaded, at start.
/// </summary>
public static class FullBackup
{
    public const string Extension = ".spokabackup";
    public const string DialogFilter = "Sauvegarde SpokaSoundBoard (*.spokabackup)|*.spokabackup";

    /// <summary>Folder (in the data directory) for the copies made automatically before a restore.</summary>
    public const string SafetyFolder = "Sauvegardes";

    /// <summary>Folder (in Downloads) for restored files whose original folder does not exist on this PC.</summary>
    public const string RestoredFolder = "Restauré";

    private const string ManifestName = "manifest.json";
    private const string SettingsName = "settings.json";
    private const string LibraryName = "library.json";

    private static readonly JsonSerializerOptions ManifestOptions = new() { WriteIndented = true };

    public static string DefaultFileName(DateTime now) => $"SpokaSoundBoard-sauvegarde-{now:yyyy-MM-dd}{Extension}";

    /// <summary>Sizes before creating a backup: the pads' files are always included, the music files on request.</summary>
    public static (long SoundBytes, long MusicBytes, int Sounds, int Tracks) Estimate(string dataDirectory)
    {
        var (sounds, music) = AudioFiles(dataDirectory);
        return (sounds.Sum(SizeOf), music.Sum(SizeOf), sounds.Count, music.Count);
    }

    /// <summary>
    /// Writes the backup. The caller saves the settings and flushes the library first (the files on disk are read).
    /// </summary>
    public static BackupManifest Create(string dataDirectory, string destination, bool includeMusic, string appVersion, string edition,
        IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var settingsPath = Path.Combine(dataDirectory, SettingsName);
        var libraryPath = Path.Combine(dataDirectory, LibraryName);
        var historyPath = Path.Combine(dataDirectory, HistoryStore.FileName);
        var (sounds, music) = AudioFiles(dataDirectory);

        var manifest = new BackupManifest
        {
            AppVersion = appVersion,
            Edition = edition,
            CreatedAt = DateTime.Now,
            Sounds = sounds.Count,
            IncludesMusic = includeMusic,
        };
        if (File.Exists(libraryPath))
        {
            var library = LibraryStore.Deserialize(File.ReadAllText(libraryPath));
            manifest.Tracks = library.Tracks.Count;
            manifest.Playlists = library.Playlists.Count;
        }
        if (File.Exists(historyPath)) manifest.HistoryEntries = new HistoryStore(historyPath).Load(MusicLibrary.MaxHistory).Count;

        var files = new List<string>(sounds);
        if (includeMusic) files.AddRange(music.Where(m => !sounds.Contains(m, StringComparer.OrdinalIgnoreCase)));
        var total = Math.Max(1, files.Sum(SizeOf));
        long done = 0;

        var temp = destination + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var (name, path) in new[] { (SettingsName, settingsPath), (LibraryName, libraryPath), (HistoryStore.FileName, historyPath) })
                    if (File.Exists(path)) zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);

                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var index = 0;
                foreach (var file in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var folder = sounds.Contains(file, StringComparer.OrdinalIgnoreCase) ? "sons" : "musique";
                    var entry = $"{folder}/{++index:00000}-{SafeName(Path.GetFileName(file))}";
                    if (!used.Add(entry)) continue;
                    // Audio is already compressed: storing is as small and much faster.
                    zip.CreateEntryFromFile(file, entry, CompressionLevel.NoCompression);
                    manifest.Files[Path.GetFullPath(file)] = entry;
                    done += SizeOf(file);
                    progress?.Report(done / (double)total);
                }

                var manifestEntry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using var stream = manifestEntry.Open();
                JsonSerializer.Serialize(stream, manifest, ManifestOptions);
            }
            File.Move(temp, destination, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        progress?.Report(1);
        return manifest;
    }

    /// <summary>Reads what a backup contains, without restoring it. Throws if it is not a SpokaSoundBoard backup.</summary>
    public static BackupManifest Inspect(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return ReadManifest(zip);
    }

    /// <summary>
    /// Restores a backup into <paramref name="dataDirectory"/>: a copy of the current settings and library is made
    /// first (in <see cref="SafetyFolder"/>), audio files are put back at their original place when it is free or
    /// already holds the same file, otherwise next to the data, and every path is updated. Call before loading the
    /// settings (the app does it at start).
    /// </summary>
    public static RestoreResult Restore(string backupPath, string dataDirectory, string appVersion = "", string edition = "")
    {
        using var zip = ZipFile.OpenRead(backupPath);
        var manifest = ReadManifest(zip);
        Directory.CreateDirectory(dataDirectory);

        // 1. Safety copy of what is about to be replaced (no audio files: they are never deleted).
        string? safety = null;
        if (File.Exists(Path.Combine(dataDirectory, SettingsName)) || File.Exists(Path.Combine(dataDirectory, LibraryName)))
        {
            var folder = Path.Combine(dataDirectory, SafetyFolder);
            Directory.CreateDirectory(folder);
            safety = Path.Combine(folder, $"avant-restauration-{DateTime.Now:yyyy-MM-dd-HHmmss}{Extension}");
            CreateDataOnly(dataDirectory, safety, appVersion, edition);
        }

        // 2. Audio files: reuse the original when it is there with the same size, else extract.
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int extracted = 0, reused = 0;
        var restoredFolder = Path.Combine(dataDirectory, "Downloads", RestoredFolder);
        foreach (var (original, entryName) in manifest.Files)
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null) continue;
            if (File.Exists(original) && new FileInfo(original).Length == entry.Length)
            {
                reused++;
                continue;
            }
            // Same PC (file deleted since): back where it was. Another PC: next to the data.
            var target = FolderExists(original) && !File.Exists(original)
                ? original
                : UniquePath(Path.Combine(restoredFolder, entryName.StartsWith("musique/") ? "Musique" : "Sons", OriginalName(entryName)));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
            if (!string.Equals(target, original, StringComparison.OrdinalIgnoreCase)) moved[original] = target;
            extracted++;
        }

        // 3. Settings and library with their paths updated, history as is.
        if (zip.GetEntry(SettingsName) is { } settingsEntry)
        {
            var settings = SettingsStore.Deserialize(ReadText(settingsEntry));
            foreach (var pad in settings.Pads)
                if (moved.TryGetValue(Full(pad.FilePath), out var path)) pad.FilePath = path;
            WriteAtomic(Path.Combine(dataDirectory, SettingsName), SettingsStore.Serialize(settings));
        }
        if (zip.GetEntry(LibraryName) is { } libraryEntry)
        {
            var library = LibraryStore.Deserialize(ReadText(libraryEntry));
            foreach (var track in library.Tracks)
                if (!string.IsNullOrEmpty(track.FilePath) && moved.TryGetValue(Full(track.FilePath), out var path)) track.FilePath = path;
            library.LegacyHistory = null;
            WriteAtomic(Path.Combine(dataDirectory, LibraryName), LibraryStore.Serialize(library));
        }
        var historyPath = Path.Combine(dataDirectory, HistoryStore.FileName);
        if (zip.GetEntry(HistoryStore.FileName) is { } historyEntry) WriteAtomic(historyPath, ReadText(historyEntry));
        else if (File.Exists(historyPath)) File.Delete(historyPath); // the backup had no history: do not mix

        return new RestoreResult(manifest, extracted, reused, safety);
    }

    /// <summary>Settings, library and history only (the copy made before a restore).</summary>
    private static void CreateDataOnly(string dataDirectory, string destination, string appVersion, string edition)
    {
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var name in new[] { SettingsName, LibraryName, HistoryStore.FileName })
        {
            var path = Path.Combine(dataDirectory, name);
            if (File.Exists(path)) zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
        }
        var manifest = new BackupManifest { AppVersion = appVersion, Edition = edition };
        using var stream = zip.CreateEntry(ManifestName).Open();
        JsonSerializer.Serialize(stream, manifest, ManifestOptions);
    }

    /// <summary>Audio files referenced by the pads, and by the music library, that exist on disk.</summary>
    private static (List<string> Sounds, List<string> Music) AudioFiles(string dataDirectory)
    {
        var sounds = new List<string>();
        var music = new List<string>();
        var settingsPath = Path.Combine(dataDirectory, SettingsName);
        if (File.Exists(settingsPath))
            sounds.AddRange(SettingsStore.Deserialize(File.ReadAllText(settingsPath)).Pads
                .Select(p => p.FilePath).Where(File.Exists).Select(Path.GetFullPath));
        var libraryPath = Path.Combine(dataDirectory, LibraryName);
        if (File.Exists(libraryPath))
            music.AddRange(LibraryStore.Deserialize(File.ReadAllText(libraryPath)).Tracks
                .Select(t => t.FilePath).Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Select(Path.GetFullPath));
        return (sounds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), music.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static BackupManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("Ce fichier n'est pas une sauvegarde SpokaSoundBoard.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, ManifestOptions)
                       ?? throw new InvalidDataException("Sauvegarde illisible.");
        if (manifest.Format > BackupManifest.CurrentFormat)
            throw new InvalidDataException("Cette sauvegarde vient d'une version plus récente de SpokaSoundBoard : mettez l'application à jour pour la restaurer.");
        manifest.Files = new Dictionary<string, string>(manifest.Files ?? [], StringComparer.OrdinalIgnoreCase);
        return manifest;
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, true);
    }

    private static string Full(string path)
    {
        try
        {
            return string.IsNullOrEmpty(path) ? "" : Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static bool FolderExists(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder);
        }
        catch
        {
            return false;
        }
    }

    private static string OriginalName(string entryName)
    {
        var name = entryName[(entryName.LastIndexOf('/') + 1)..];
        var dash = name.IndexOf('-');
        return dash > 0 ? name[(dash + 1)..] : name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 120 ? name[^120..] : name;
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}
