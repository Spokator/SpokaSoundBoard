namespace SpokaSoundBoard.Core.Online;

/// <summary>
/// Frees the disk space of downloaded sounds and songs nothing uses any more (track removed, autoplay song pruned,
/// pad deleted). Only files inside the app's own download folders are ever considered: the user's own files are
/// never touched.
/// </summary>
public static class DownloadCache
{
    /// <summary>A file this recent may still be in use (download in progress, track just removed then re-added).</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromDays(2);

    /// <summary>Files under <paramref name="subFolders"/> of the cache that no path in <paramref name="referenced"/> points to.</summary>
    public static List<FileInfo> FindOrphans(string cacheDirectory, IEnumerable<string> subFolders, IEnumerable<string?> referenced,
                                             DateTime nowUtc, TimeSpan? minimumAge = null)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in referenced)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try { used.Add(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        var limit = nowUtc - (minimumAge ?? MinimumAge);
        var orphans = new List<FileInfo>();
        foreach (var sub in subFolders)
        {
            var dir = new DirectoryInfo(Path.Combine(cacheDirectory, sub));
            if (!dir.Exists) continue;
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                if (file.LastWriteTimeUtc < limit && !used.Contains(file.FullName))
                    orphans.Add(file);
        }
        return orphans;
    }

    /// <summary>Deletes the orphans; files in use or protected are skipped. Returns the space freed.</summary>
    public static (int Files, long Bytes) DeleteOrphans(string cacheDirectory, IEnumerable<string> subFolders, IEnumerable<string?> referenced,
                                                       DateTime nowUtc, TimeSpan? minimumAge = null)
    {
        var files = 0;
        long bytes = 0;
        foreach (var file in FindOrphans(cacheDirectory, subFolders, referenced, nowUtc, minimumAge))
        {
            try
            {
                var length = file.Length;
                file.Delete();
                files++;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or read-only: try again next launch.
            }
        }
        return (files, bytes);
    }
}
