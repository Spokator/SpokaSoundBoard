using System.Security.Cryptography;

namespace SpokaSoundBoard.Core.Online;

/// <summary>Published SHA-256 lists (« SHA2-256SUMS », « checksums.sha256 ») and file hashing.</summary>
public static class Checksums
{
    /// <summary>
    /// The SHA-256 (64 lowercase hex characters) listed for <paramref name="fileName"/>, or null. Lines look like
    /// « hash  name » or « hash *name » (binary mode).
    /// </summary>
    public static string? Find(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space != 64) continue;
            var name = line[space..].Trim().TrimStart('*');
            if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) continue;
            var hash = line[..64].ToLowerInvariant();
            return hash.All(Uri.IsHexDigit) ? hash : null;
        }
        return null;
    }

    public static async Task<string> FileSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
