using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SpokaSoundBoard.Core.Online;

/// <summary>Shared HTTP plumbing for the online music services: one pooled client, friendly French errors.</summary>
internal static class OnlineHttp
{
    public static readonly string AppUserAgent = $"SpokaSoundBoard/{AppEnvironment.UserAgentVersion} (https://github.com/Spokator/SpokaSoundBoard)";
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 8,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    internal sealed record Page(HttpStatusCode Status, string Body, Uri FinalUri)
    {
        public bool Ok => (int)Status is >= 200 and < 300;
    }

    /// <summary>GET as text. Network failures and timeouts become a <see cref="DownloadException"/> naming <paramref name="service"/>.</summary>
    /// <param name="browser">Send a browser User-Agent (needed by Spotify / Apple pages) instead of the app's own.</param>
    public static async Task<Page> GetAsync(string url, string service, bool browser, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", browser ? BrowserUserAgent : AppUserAgent);
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("fr-FR"));
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("fr", 0.9));
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.7));
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return new Page(response.StatusCode, body, response.RequestMessage?.RequestUri ?? new Uri(url));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DownloadException($"{service} ne répond pas (délai dépassé). Réessayez dans un instant.");
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException($"{service} est inaccessible ({ex.Message}). Vérifiez votre connexion Internet.");
        }
    }

    /// <summary>GET and parse JSON. The element is cloned, so it outlives the document.</summary>
    public static async Task<JsonElement> GetJsonAsync(string url, string service, CancellationToken ct, TimeSpan? timeout = null)
    {
        var page = await GetAsync(url, service, browser: false, ct, timeout);
        if (!page.Ok) throw new DownloadException($"{service} a répondu par une erreur ({(int)page.Status}).");
        try
        {
            using var doc = JsonDocument.Parse(page.Body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new DownloadException($"Réponse illisible de {service}.");
        }
    }
}

/// <summary>Null-tolerant JSON readers (web payloads are never fully reliable).</summary>
internal static class JsonExtensions
{
    public static JsonElement? Get(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static JsonElement? At(this JsonElement e, params string[] names)
    {
        JsonElement? current = e;
        foreach (var n in names)
        {
            current = current?.Get(n);
            if (current == null) return null;
        }
        return current;
    }

    public static string? Str(this JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            var v = e.Get(n);
            if (v is { ValueKind: JsonValueKind.String } s && !string.IsNullOrWhiteSpace(s.GetString())) return s.GetString()!.Trim();
            if (v is { ValueKind: JsonValueKind.Number } num) return num.GetRawText();
        }
        return null;
    }

    public static double? Num(this JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            var v = e.Get(n);
            if (v is { ValueKind: JsonValueKind.Number } num && num.TryGetDouble(out var d)) return d;
            if (v is { ValueKind: JsonValueKind.String } s && double.TryParse(s.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        return null;
    }

    public static bool Bool(this JsonElement e, string name) => e.Get(name) is { ValueKind: JsonValueKind.True };

    public static IEnumerable<JsonElement> Array(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];
}
