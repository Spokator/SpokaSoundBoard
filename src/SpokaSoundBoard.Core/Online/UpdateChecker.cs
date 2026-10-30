using System.Net.Http.Headers;
using System.Text.Json;

namespace SpokaSoundBoard.Core.Online;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string ReleaseUrl, string Notes);

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
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }
        if (url == null) return null;

        var page = release.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, url, page, notes);
    }

    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var target = Path.Combine(Path.GetTempPath(), "SpokaSoundBoard", $"SpokaSoundBoard-Setup-{update.Version}.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(target))
        {
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(done * 100.0 / total);
            }
        }
        return target;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpokaSoundBoard", "1.4"));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
