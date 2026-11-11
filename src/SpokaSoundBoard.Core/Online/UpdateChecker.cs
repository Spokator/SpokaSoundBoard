using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpokaSoundBoard.Core.Online;

/// <param name="Size">Installer size announced by GitHub (0 = unknown).</param>
/// <param name="Sha256">Installer SHA-256 announced by GitHub (asset "digest"), lowercase hex, or null when absent.</param>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string ReleaseUrl, string Notes, long Size = 0, string? Sha256 = null);

/// <summary>The downloaded installer does not match what GitHub announced: it is deleted and never run.</summary>
public sealed class UpdateIntegrityException(string message) : Exception(message);

/// <summary>Checks the latest GitHub release and downloads its installer.</summary>
public static class UpdateChecker
{
    public const string Repository = "Spokator/SpokaSoundBoard";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement, current);
    }

    internal static UpdateInfo? Parse(JsonElement release, Version current)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Normalize(current)) return null;

        string? url = null;
        long size = 0;
        string? sha256 = null;
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    if (asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number) size = s.GetInt64();
                    sha256 = ParseDigest(asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null);
                    break;
                }
            }
        }
        if (url == null) return null;

        var page = release.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, url, page, notes, size, sha256);
    }

    /// <summary>"sha256:ABCD…" → "abcd…"; anything else (other algorithm, malformed) → null.</summary>
    internal static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>
    /// Checks a downloaded installer against the size and SHA-256 GitHub announced. Returns null when it matches,
    /// otherwise the reason (in French, shown to the user).
    /// </summary>
    internal static string? CheckIntegrity(UpdateInfo update, long length, string sha256Hex)
    {
        if (update.Size > 0 && length != update.Size)
            return $"fichier incomplet ({length:N0} octets reçus sur {update.Size:N0})";
        if (update.Sha256 != null && !string.Equals(update.Sha256, sha256Hex, StringComparison.OrdinalIgnoreCase))
            return "l'empreinte du fichier ne correspond pas à celle publiée sur GitHub";
        return null;
    }

    /// <summary>
    /// Downloads the installer to a ".part" file, checks its size and SHA-256 against the release, and only then
    /// gives it its final name. On any failure the partial file is deleted, so a broken or tampered installer is
    /// never run.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var target = Path.Combine(Path.GetTempPath(), "SpokaSoundBoard", $"SpokaSoundBoard-Setup-{update.Version}.exe");
        var part = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(part))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    hash.AppendData(buffer, 0, n);
                    done += n;
                    if (total > 0) progress?.Report(done * 100.0 / total);
                }
            }
            var problem = CheckIntegrity(update, done, Convert.ToHexString(hash.GetHashAndReset()));
            if (problem != null) throw new UpdateIntegrityException("Installateur refusé : " + problem + ".");
            File.Move(part, target, true);
            return target;
        }
        catch
        {
            try { File.Delete(part); } catch { /* best effort */ }
            throw;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpokaSoundBoard", AppEnvironment.UserAgentVersion));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
