using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SpokaSoundBoard.Core.Remote;

/// <summary>One HTTP request received by the remote control (path without the query string, query parameters decoded).</summary>
public sealed record RemoteRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query, IPAddress Client);

public sealed record RemoteResponse(int Status, string ContentType, byte[] Body)
{
    /// <summary>Set for a streamed answer (server-sent events): written until the client leaves.</summary>
    public Func<Stream, CancellationToken, Task>? Streamer { get; init; }

    public static RemoteResponse Stream(Func<Stream, CancellationToken, Task> streamer) =>
        new(200, "text/event-stream; charset=utf-8", []) { Streamer = streamer };

    public static RemoteResponse Json(string json, int status = 200) => new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
    public static RemoteResponse Html(string html) => new(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
    public static RemoteResponse Error(int status, string message) =>
        Json("{\"error\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}", status);
}

/// <summary>
/// Tiny HTTP/1.1 server for the remote control (Stream Deck, Touch Portal, OBS, a phone on the same Wi-Fi). One request
/// per connection, no body, no keep-alive: enough for buttons, and nothing more to attack. Every request must carry the
/// access code (« ?code= » or the « X-Spoka-Code » header); an address that keeps guessing wrong is shut out for a while.
/// </summary>
public sealed class RemoteServer : IAsyncDisposable
{
    public const int DefaultPort = 8737;
    private const int MaxRequestBytes = 8192;
    private const int MaxFailures = 10;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<RemoteRequest, Task<RemoteResponse>> _handler;
    private readonly ConcurrentDictionary<IPAddress, (int Count, DateTime Until)> _failures = new();
    private readonly ConcurrentDictionary<IPAddress, DateTime> _clients = new();
    private int _streams;

    /// <summary>Live connections at once (each phone keeps one open); beyond that, clients fall back to polling.</summary>
    public const int MaxStreams = 8;

    /// <summary>Raised (on a worker thread) the first time an address uses the code in this session.</summary>
    public event Action<IPAddress>? ClientConnected;

    /// <summary>Addresses that used the code within <paramref name="window"/> (a phone with the page open counts).</summary>
    public IReadOnlyList<IPAddress> ActiveClients(TimeSpan window)
    {
        var limit = DateTime.UtcNow - window;
        return _clients.Where(c => c.Value >= limit).Select(c => c.Key).ToList();
    }
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public RemoteServer(Func<RemoteRequest, Task<RemoteResponse>> handler) => _handler = handler;

    /// <summary>Code every request must carry. Changing it takes effect immediately.</summary>
    public string Code { get; set; } = NewCode();

    public bool IsRunning => _listener != null;
    public int Port { get; private set; }
    public bool AcceptsNetwork { get; private set; }

    /// <param name="network">False: this PC only (127.0.0.1). True: also the local network (phone on the same Wi-Fi).</param>
    public void Start(int port, bool network)
    {
        if (IsRunning) Stop();
        var listener = new TcpListener(network ? IPAddress.Any : IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        AcceptsNetwork = network;
        _cts = new CancellationTokenSource();
        _loop = AcceptLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        var loop = _loop;
        Stop();
        if (loop != null) try { await loop.ConfigureAwait(false); } catch { /* stopping */ }
    }

    /// <summary>10 characters without look-alikes (no 0/O, 1/l/I): about 50 bits, typed once or scanned as a QR code.</summary>
    public static string NewCode()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        var sb = new StringBuilder(10);
        foreach (var b in random) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReadTimeout);
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
            var response = head == null ? RemoteResponse.Error(400, "requête invalide") : await HandleAsync(head, remote).ConfigureAwait(false);
            if (response.Streamer == null)
            {
                await WriteAsync(stream, response, timeout.Token).ConfigureAwait(false);
                return;
            }
            if (Interlocked.Increment(ref _streams) > MaxStreams)
            {
                Interlocked.Decrement(ref _streams);
                await WriteAsync(stream, RemoteResponse.Error(503, "trop de connexions"), timeout.Token).ConfigureAwait(false);
                return;
            }
            try
            {
                await WriteHeaderAsync(stream, response, ct).ConfigureAwait(false);
                // The stream lives until the phone leaves; the address stays « connected » meanwhile.
                using var touch = new Timer(_ => _clients[remote] = DateTime.UtcNow, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
                await response.Streamer(stream, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _streams);
            }
        }
        catch (Exception)
        {
            // A client that disconnects or sends garbage only loses its own request.
        }
    }

    internal async Task<RemoteResponse> HandleAsync(string head, IPAddress remote)
    {
        var parsed = Parse(head);
        if (parsed == null) return RemoteResponse.Error(400, "requête invalide");
        var (method, path, query, headers) = parsed.Value;

        if (_failures.TryGetValue(remote, out var f) && f.Count >= MaxFailures && f.Until > DateTime.UtcNow)
            return RemoteResponse.Error(429, "trop d'essais avec un mauvais code : réessayez dans quelques minutes");

        if (RemoteApi.PublicPaths.Contains(path))
            return await _handler(new RemoteRequest(method, path, query, remote)).ConfigureAwait(false);

        var code = query.GetValueOrDefault("code") ?? headers.GetValueOrDefault("x-spoka-code");
        if (!CodeMatches(code))
        {
            _failures.AddOrUpdate(remote, _ => (1, DateTime.UtcNow + Lockout), (_, old) => (old.Count + 1, DateTime.UtcNow + Lockout));
            return RemoteResponse.Error(401, "code d'accès manquant ou incorrect (Configuration → Télécommande)");
        }
        _failures.TryRemove(remote, out _);
        if (_clients.TryAdd(remote, DateTime.UtcNow)) ClientConnected?.Invoke(remote);
        else _clients[remote] = DateTime.UtcNow;
        try
        {
            return await _handler(new RemoteRequest(method, path, query, remote)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return RemoteResponse.Error(500, FriendlyErrors.Describe(ex));
        }
    }

    private bool CodeMatches(string? code) =>
        code != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(Code));

    /// <summary>Request line and headers of a raw HTTP request (null when malformed).</summary>
    internal static (string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers)? Parse(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var method = parts[0].ToUpperInvariant();
        if (method is not ("GET" or "POST" or "HEAD")) return null;
        var target = parts[1];
        if (!target.StartsWith('/')) return null;

        var question = target.IndexOf('?');
        var path = Uri.UnescapeDataString(question < 0 ? target : target[..question]).TrimEnd('/');
        if (path.Length == 0) path = "/";
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (question >= 0)
            foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                query[key] = value;
            }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return (method, path, query, headers);
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n <= 0) return null;
            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return Encoding.UTF8.GetString(buffer, 0, end);
        }
        return null;
    }

    private static async Task WriteAsync(NetworkStream stream, RemoteResponse response, CancellationToken ct)
    {
        await WriteHeaderAsync(stream, response, ct).ConfigureAwait(false);
        await stream.WriteAsync(response.Body, ct).ConfigureAwait(false);
    }

    private static async Task WriteHeaderAsync(NetworkStream stream, RemoteResponse response, CancellationToken ct)
    {
        var reason = response.Status switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 409 => "Conflict",
            429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Error",
        };
        var length = response.Streamer == null ? $"Content-Length: {response.Body.Length}\r\n" : "";
        var header = $"HTTP/1.1 {response.Status} {reason}\r\nContent-Type: {response.ContentType}\r\n{length}" +
                     "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
