using System.Text.Json;

namespace SpokaSoundBoard.Core.Remote;

public sealed record RemotePad(Guid Id, string Name, string Color, string Category, bool Playing, string? Hotkey);

public sealed record RemoteStatus(bool MicEnabled, bool EffectsEnabled, string Preset, string? Music, bool MusicPlaying);

/// <summary>What the remote control can do; implemented by the app on its UI thread.</summary>
public interface IRemoteActions
{
    Task<IReadOnlyList<RemotePad>> PadsAsync();

    /// <summary>Same as clicking the sound (its play mode applies). False when no sound has that id or name.</summary>
    Task<bool> PlayPadAsync(string idOrName);

    Task StopAllAsync();
    Task ToggleMicAsync();
    Task ToggleEffectsAsync();
    Task CyclePresetAsync(int direction);
    Task MusicPlayPauseAsync();
    Task MusicNextAsync();
    Task<RemoteStatus> StatusAsync();
}

/// <summary>
/// Routes of the remote control. Every action answers GET and POST alike, so it can be wired in any tool:
/// Stream Deck (« Website » or an HTTP plugin), Touch Portal, an OBS script, a browser bookmark…
/// </summary>
public static class RemoteApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<RemoteResponse> HandleAsync(RemoteRequest request, IRemoteActions actions)
    {
        var path = request.Path;
        if (path == "/") return RemoteResponse.Html(RemotePage.Html);
        if (path == "/api/pads") return Ok(await actions.PadsAsync());
        if (path == "/api/status") return Ok(await actions.StatusAsync());

        if (path.StartsWith("/api/pads/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/play", StringComparison.OrdinalIgnoreCase))
        {
            var key = path["/api/pads/".Length..^"/play".Length];
            return await actions.PlayPadAsync(key)
                ? Ok(new { ok = true })
                : RemoteResponse.Error(404, $"aucun son nommé « {key} »");
        }

        Func<Task>? action = path.ToLowerInvariant() switch
        {
            "/api/stop" => actions.StopAllAsync,
            "/api/mic/toggle" => actions.ToggleMicAsync,
            "/api/effects/toggle" => actions.ToggleEffectsAsync,
            "/api/preset/next" => () => actions.CyclePresetAsync(1),
            "/api/preset/previous" => () => actions.CyclePresetAsync(-1),
            "/api/music/playpause" => actions.MusicPlayPauseAsync,
            "/api/music/next" => actions.MusicNextAsync,
            _ => null,
        };
        if (action == null) return RemoteResponse.Error(404, "adresse inconnue");
        await action();
        return Ok(new { ok = true });
    }

    private static RemoteResponse Ok(object value) => RemoteResponse.Json(JsonSerializer.Serialize(value, Json));

    /// <summary>Finds a pad by id, then by exact name, then by name ignoring case and accents.</summary>
    public static T? FindPad<T>(IEnumerable<T> pads, string key, Func<T, Guid> id, Func<T, string> name) where T : class
    {
        var list = pads.ToList();
        if (Guid.TryParse(key, out var guid)) return list.FirstOrDefault(p => id(p) == guid);
        return list.FirstOrDefault(p => name(p) == key)
               ?? list.FirstOrDefault(p => Music.MusicLibrary.Normalize(name(p)) == Music.MusicLibrary.Normalize(key));
    }
}
