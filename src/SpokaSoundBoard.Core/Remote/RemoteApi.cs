using System.Text;
using System.Text.Json;

namespace SpokaSoundBoard.Core.Remote;

public sealed record RemotePad(Guid Id, string Name, string Color, string Category, bool Playing, string? Hotkey, string Icon = "wave-sine");

public sealed record RemotePreset(string Name, string Description);

/// <summary>What rarely changes: sounds, voice effects, phrases and the user's deck. Reloaded when <c>Version</c> changes.</summary>
public sealed record RemoteCatalog(
    IReadOnlyList<RemotePad> Pads,
    IReadOnlyList<RemotePreset> Presets,
    IReadOnlyList<string> QuickPhrases,
    IReadOnlyList<string> History,
    IReadOnlyList<string> Deck,
    string Accent,
    string OnAccent);

public sealed record RemoteMusic(bool HasTrack, string? Title, string? Artist, string? Cover, bool Playing, double Position, double Duration);

public sealed record RemoteMix(double Music, double Pads, double ToMic, double Monitor, bool MediaToMic);

public sealed record RemoteBlindTestPlayer(string Name, int Score, int Rank, bool Title, bool Artist, bool Fast);

public sealed record RemoteBlindTest(
    string Phase, bool Suspended, int Round, int Total, int Countdown, string State, bool Paused,
    string AnswerTitle, string AnswerArtist, string Hint, bool CanHint, IReadOnlyList<RemoteBlindTestPlayer> Players);

/// <summary>What changes all the time: pushed to the phone several times a second.</summary>
public sealed record RemoteState(
    string Version,
    bool MicEnabled,
    bool EffectsEnabled,
    string Preset,
    bool RadioMode,
    bool Talking,
    IReadOnlyList<Guid> Playing,
    RemoteMix Mix,
    RemoteMusic Music,
    RemoteBlindTest? BlindTest,
    bool TtsAvailable,
    bool NoiseSuppression = false,
    string? Profile = null);

/// <summary>Kept for 2.6 integrations (/api/status).</summary>
public sealed record RemoteStatus(bool MicEnabled, bool EffectsEnabled, string Preset, string? Music, bool MusicPlaying);

/// <summary>Everything the remote control can do; implemented by the app on its UI thread.</summary>
public interface IRemoteActions
{
    Task<RemoteCatalog> CatalogAsync();
    Task<RemoteState> StateAsync();

    /// <summary>Same as clicking the sound (its play mode applies). False when no sound has that id or name.</summary>
    Task<bool> PlayPadAsync(string idOrName);

    Task<bool> ApplyPresetAsync(string name);

    /// <summary>A named action (see <see cref="RemoteApi.Actions"/>). False when it does not exist or cannot run now.</summary>
    Task<bool> RunAsync(string action);

    /// <summary>Radio key held on the phone. « Down » must be repeated while held: the PC lets go by itself otherwise.</summary>
    Task PushToTalkAsync(bool down);

    Task<bool> SetMixAsync(string key, double value);
    Task<bool> SayAsync(string text);
    Task<bool> SetDeckAsync(IReadOnlyList<string> items);
    Task<bool> ToggleBlindTestAsync(string player, string field);

    /// <summary>Cover of the current track when it is a file on this PC (web covers are loaded by the phone itself).</summary>
    Task<(byte[] Data, string ContentType)?> CoverAsync();

    /// <summary>App icon (PNG) for the phone's home screen.</summary>
    byte[]? Icon { get; }
}

/// <summary>
/// Routes of the remote control. Every action answers GET and POST alike, so it can be wired in any tool:
/// Stream Deck (« Website » or an HTTP plugin), Touch Portal, an OBS script, a browser bookmark… and the phone page.
/// </summary>
public static class RemoteApi
{
    public const int MaxDeckItems = 60;
    public const int MaxDeckItemLength = 160;
    public const int MaxSayLength = 300;

    /// <summary>Named actions for <c>/api/action/{name}</c> (and the deck's « act: » buttons).</summary>
    public static readonly IReadOnlyList<string> Actions =
    [
        "stop", "mic", "effects", "radio", "noise", "preset-next", "preset-previous",
        "music-playpause", "music-next", "music-previous", "media-to-mic",
        "bt-reveal", "bt-next", "bt-replay", "bt-pause", "bt-hint", "bt-skip", "bt-resume",
    ];

    /// <summary>Paths reachable without the code: the page itself and its home-screen files (no data in them).</summary>
    public static readonly IReadOnlySet<string> PublicPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/manifest.webmanifest", "/icon.png",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // 2.6 paths, kept for existing Stream Deck buttons.
    private static readonly Dictionary<string, string> LegacyActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/api/stop"] = "stop",
        ["/api/mic/toggle"] = "mic",
        ["/api/effects/toggle"] = "effects",
        ["/api/preset/next"] = "preset-next",
        ["/api/preset/previous"] = "preset-previous",
        ["/api/music/playpause"] = "music-playpause",
        ["/api/music/next"] = "music-next",
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static async Task<RemoteResponse> HandleAsync(RemoteRequest request, IRemoteActions actions)
    {
        var path = request.Path;
        var q = request.Query;
        switch (path.ToLowerInvariant())
        {
            case "/":
                return RemoteResponse.Html(RemotePage.Html);
            case "/manifest.webmanifest":
                return new RemoteResponse(200, "application/manifest+json", Encoding.UTF8.GetBytes(RemotePage.Manifest));
            case "/icon.png":
                return actions.Icon is { } icon ? new RemoteResponse(200, "image/png", icon) : RemoteResponse.Error(404, "pas d'icône");
            case "/api/catalog":
                return Ok(await actions.CatalogAsync());
            case "/api/state":
                return Ok(await actions.StateAsync());
            case "/api/events":
                return RemoteResponse.Stream((stream, ct) => StreamStateAsync(stream, actions, ct));
            case "/api/pads":
                var catalog = await actions.CatalogAsync();
                var state = await actions.StateAsync();
                return Ok(catalog.Pads.Select(p => p with { Playing = state.Playing.Contains(p.Id) }));
            case "/api/status":
                var s = await actions.StateAsync();
                return Ok(new RemoteStatus(s.MicEnabled, s.EffectsEnabled, s.Preset, s.Music.Title, s.Music.Playing));
            case "/api/ptt":
                await actions.PushToTalkAsync(!string.Equals(q.GetValueOrDefault("state"), "up", StringComparison.OrdinalIgnoreCase));
                return Ok(new { ok = true });
            case "/api/mix":
                var key = q.GetValueOrDefault("key") ?? "";
                return double.TryParse(q.GetValueOrDefault("value"), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var value) && await actions.SetMixAsync(key, value)
                    ? Ok(new { ok = true })
                    : RemoteResponse.Error(400, "réglage ou valeur invalide");
            case "/api/say":
                var text = (q.GetValueOrDefault("text") ?? "").Trim();
                if (text.Length == 0) return RemoteResponse.Error(400, "phrase vide");
                if (text.Length > MaxSayLength) text = text[..MaxSayLength];
                return await actions.SayAsync(text) ? Ok(new { ok = true }) : RemoteResponse.Error(409, "aucune voix disponible sur le PC");
            case "/api/deck":
                var items = ParseDeck(q.GetValueOrDefault("items"));
                return await actions.SetDeckAsync(items) ? Ok(new { ok = true, count = items.Count }) : RemoteResponse.Error(400, "deck invalide");
            case "/api/blindtest/player":
                return await actions.ToggleBlindTestAsync(q.GetValueOrDefault("name") ?? "", q.GetValueOrDefault("field") ?? "")
                    ? Ok(new { ok = true })
                    : RemoteResponse.Error(404, "joueur ou partie introuvable");
            case "/api/music/cover":
                return await actions.CoverAsync() is { } cover ? new RemoteResponse(200, cover.ContentType, cover.Data) : RemoteResponse.Error(404, "pas de pochette");
        }

        if (LegacyActions.TryGetValue(path, out var legacy)) return await Run(actions, legacy);
        if (path.StartsWith("/api/action/", StringComparison.OrdinalIgnoreCase)) return await Run(actions, path["/api/action/".Length..]);

        if (path.StartsWith("/api/pads/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/play", StringComparison.OrdinalIgnoreCase))
        {
            var pad = path["/api/pads/".Length..^"/play".Length];
            return await actions.PlayPadAsync(pad) ? Ok(new { ok = true }) : RemoteResponse.Error(404, $"aucun son nommé « {pad} »");
        }
        if (path.StartsWith("/api/presets/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/apply", StringComparison.OrdinalIgnoreCase))
        {
            var preset = path["/api/presets/".Length..^"/apply".Length];
            return await actions.ApplyPresetAsync(preset) ? Ok(new { ok = true }) : RemoteResponse.Error(404, $"aucun effet nommé « {preset} »");
        }
        return RemoteResponse.Error(404, "adresse inconnue");
    }

    private static async Task<RemoteResponse> Run(IRemoteActions actions, string name)
    {
        if (!Actions.Contains(name, StringComparer.OrdinalIgnoreCase)) return RemoteResponse.Error(404, $"action inconnue « {name} »");
        return await actions.RunAsync(name.ToLowerInvariant()) ? Ok(new { ok = true }) : RemoteResponse.Error(409, "impossible pour l'instant");
    }

    private static RemoteResponse Ok(object value) => RemoteResponse.Json(JsonSerializer.Serialize(value, Json));

    /// <summary>Deck items separated by « | » (their own « | » are not allowed); invalid or extra items are dropped.</summary>
    public static List<string> ParseDeck(string? items) =>
        (items ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsDeckItem)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxDeckItems)
            .ToList();

    /// <summary>« pad:{id} », « preset:{name} », « say:{text} » or « act:{action} ».</summary>
    public static bool IsDeckItem(string item)
    {
        if (item.Length > MaxDeckItemLength) return false;
        var colon = item.IndexOf(':');
        if (colon <= 0 || colon == item.Length - 1) return false;
        var value = item[(colon + 1)..];
        return item[..colon] switch
        {
            "pad" => Guid.TryParse(value, out _),
            "preset" or "say" => true,
            "act" => Actions.Contains(value) || value == "ptt",
            _ => false,
        };
    }

    /// <summary>Server-sent events: the state as soon as it changes (checked 4 times a second), and a keep-alive.</summary>
    private static async Task StreamStateAsync(Stream stream, IRemoteActions actions, CancellationToken ct)
    {
        string? last = null;
        var lastSent = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            var json = Serialize(await actions.StateAsync());
            string? message = null;
            if (json != last) message = "data: " + json + "\n\n";
            else if (DateTime.UtcNow - lastSent > TimeSpan.FromSeconds(15)) message = ": ping\n\n";
            if (message != null)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(message), ct);
                await stream.FlushAsync(ct);
                last = json;
                lastSent = DateTime.UtcNow;
            }
            await Task.Delay(250, ct);
        }
    }

    /// <summary>Finds a pad by id, then by exact name, then by name ignoring case and accents.</summary>
    public static T? FindPad<T>(IEnumerable<T> pads, string key, Func<T, Guid> id, Func<T, string> name) where T : class
    {
        var list = pads.ToList();
        if (Guid.TryParse(key, out var guid)) return list.FirstOrDefault(p => id(p) == guid);
        return list.FirstOrDefault(p => name(p) == key)
               ?? list.FirstOrDefault(p => Music.MusicLibrary.Normalize(name(p)) == Music.MusicLibrary.Normalize(key));
    }
}
