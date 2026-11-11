using System.Net;
using System.Text.Json;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Remote;

namespace SpokaSoundBoard.Tests;

/// <summary>2.6 / 2.7: remote control (HTTP server, access code, routes, live state, deck).</summary>
public sealed class RemoteTests
{
    private sealed class FakeActions : IRemoteActions
    {
        public readonly List<string> Calls = [];
        public List<string> Deck = [];
        public bool Mic = true;
        public string Preset = "Voix normale";

        public readonly List<RemotePad> Pads =
        [
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Applaudissements", "#7C5CFF", "Réactions", false, "F1"),
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Sirène", "#EF4444", "", false, null),
        ];

        public byte[]? Icon => [1, 2, 3];

        public Task<RemoteCatalog> CatalogAsync() => Task.FromResult(new RemoteCatalog(Pads, [new("Robot", "Voix de robot")],
            ["GG !"], ["Bonsoir"], Deck, "#7c5cff", "#ffffff"));

        public Task<RemoteState> StateAsync() => Task.FromResult(new RemoteState("v1", Mic, true, Preset, false, false,
            [Pads[1].Id], new RemoteMix(0.8, 0.8, 0.6, 0.5, true), new RemoteMusic(true, "Mona Lisa", "Booba", null, true, 12, 215),
            null, true));

        public Task<bool> PlayPadAsync(string idOrName)
        {
            var pad = RemoteApi.FindPad(Pads, idOrName, p => p.Id, p => p.Name);
            if (pad != null) Calls.Add("play " + pad.Name);
            return Task.FromResult(pad != null);
        }

        public Task<bool> ApplyPresetAsync(string name)
        {
            Calls.Add("preset " + name);
            if (name != "Robot") return Task.FromResult(false);
            Preset = name;
            return Task.FromResult(true);
        }

        public Task<bool> RunAsync(string action)
        {
            Calls.Add(action);
            if (action == "mic") Mic = !Mic;
            return Task.FromResult(!action.StartsWith("bt-"));
        }

        public Task PushToTalkAsync(bool down) { Calls.Add(down ? "ptt down" : "ptt up"); return Task.CompletedTask; }
        public Task<bool> SetMixAsync(string key, double value) { Calls.Add($"mix {key} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"); return Task.FromResult(key == "music"); }
        public Task<bool> SayAsync(string text) { Calls.Add("say " + text); return Task.FromResult(true); }
        public Task<bool> SetDeckAsync(IReadOnlyList<string> items) { Deck = items.ToList(); return Task.FromResult(true); }
        public Task<bool> ToggleBlindTestAsync(string player, string field) => Task.FromResult(false);
        public Task<(byte[] Data, string ContentType)?> CoverAsync() => Task.FromResult<(byte[], string)?>(null);
    }

    private static RemoteRequest Req(string path, Dictionary<string, string>? query = null) =>
        new("POST", path, query ?? new Dictionary<string, string>(), IPAddress.Loopback);

    [Fact]
    public async Task Server_AnswersWithTheRightCodeOnly()
    {
        var actions = new FakeActions();
        await using var server = new RemoteServer(r => RemoteApi.HandleAsync(r, actions)) { Code = "abcdefghjk" };
        server.Start(0, network: false);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        var ok = await http.PostAsync("/api/pads/Applaudissements/play?code=abcdefghjk", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(["play Applaudissements"], actions.Calls);

        var header = new HttpRequestMessage(HttpMethod.Get, "/api/stop");
        header.Headers.Add("X-Spoka-Code", "abcdefghjk");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(header)).StatusCode);

        var refused = await http.GetAsync("/api/stop?code=wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(["play Applaudissements", "stop"], actions.Calls);

        // The page and its home-screen files open without the code (the page then reads it from the address).
        var page = await http.GetAsync("/");
        Assert.Contains("Télécommande SpokaSoundBoard", await page.Content.ReadAsStringAsync());
        Assert.StartsWith("text/html", page.Content.Headers.ContentType!.ToString());
        Assert.Contains("standalone", await http.GetStringAsync("/manifest.webmanifest"));
        Assert.Equal([1, 2, 3], await http.GetByteArrayAsync("/icon.png"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/catalog")).StatusCode);

        Assert.Single(server.ActiveClients(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Server_StreamsTheStateLive()
    {
        var actions = new FakeActions();
        await using var server = new RemoteServer(r => RemoteApi.HandleAsync(r, actions)) { Code = "abcdefghjk" };
        server.Start(0, network: false);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        using var response = await http.GetAsync("/api/events?code=abcdefghjk", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        async Task<JsonElement> NextState()
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line != null && line.StartsWith("data: ")) return JsonDocument.Parse(line[6..]).RootElement;
            }
        }

        Assert.True((await NextState()).GetProperty("micEnabled").GetBoolean());
        await http.PostAsync("/api/action/mic?code=abcdefghjk", null);
        // The change arrives by itself, without asking again.
        Assert.False((await NextState()).GetProperty("micEnabled").GetBoolean());
    }

    [Fact]
    public async Task Server_ShutsOutGuessers()
    {
        var server = new RemoteServer(_ => Task.FromResult(RemoteResponse.Json("{}"))) { Code = "abcdefghjk" };
        var guesser = IPAddress.Parse("192.168.1.50");
        for (var i = 0; i < 10; i++)
            Assert.Equal(401, (await server.HandleAsync("GET /api/stop?code=nope HTTP/1.1\r\nHost: x", guesser)).Status);
        // Even the right code is refused for a while after too many wrong ones…
        Assert.Equal(429, (await server.HandleAsync("GET /api/stop?code=abcdefghjk HTTP/1.1", guesser)).Status);
        // …but only for that address.
        Assert.Equal(200, (await server.HandleAsync("GET /api/stop?code=abcdefghjk HTTP/1.1", IPAddress.Parse("192.168.1.51"))).Status);
    }

    [Fact]
    public void Parse_ReadsRequestLineQueryAndHeaders()
    {
        var parsed = RemoteServer.Parse("POST /api/pads/Sir%C3%A8ne/play/?code=a+b&x HTTP/1.1\r\nX-Spoka-Code: zz\r\nHost: 127.0.0.1");
        Assert.NotNull(parsed);
        var (method, path, query, headers) = parsed.Value;
        Assert.Equal(("POST", "/api/pads/Sirène/play"), (method, path));
        Assert.Equal("a b", query["code"]);
        Assert.Equal("", query["x"]);
        Assert.Equal("zz", headers["x-spoka-code"]);
        Assert.Null(RemoteServer.Parse("DELETE / HTTP/1.1"));
        Assert.Null(RemoteServer.Parse("GET http://evil/ HTTP/1.1"));
        Assert.Null(RemoteServer.Parse("garbage"));
    }

    [Fact]
    public async Task Api_RoutesEveryAction()
    {
        var actions = new FakeActions();
        async Task<int> Status(string path, Dictionary<string, string>? q = null) => (await RemoteApi.HandleAsync(Req(path, q), actions)).Status;

        // 2.6 paths still work for existing Stream Deck buttons.
        foreach (var path in new[] { "/api/stop", "/api/mic/toggle", "/api/effects/toggle", "/api/preset/next", "/api/preset/previous",
                     "/api/music/playpause", "/api/music/next" })
            Assert.Equal(200, await Status(path));
        Assert.Equal(["stop", "mic", "effects", "preset-next", "preset-previous", "music-playpause", "music-next"], actions.Calls);
        actions.Calls.Clear();

        Assert.Equal(200, await Status("/api/action/radio"));
        Assert.Equal(200, await Status("/api/action/media-to-mic"));
        Assert.Equal(404, await Status("/api/action/format-c"));
        Assert.Equal(409, await Status("/api/action/bt-reveal")); // no game running
        Assert.Equal(200, await Status("/api/pads/sirene/play"));
        Assert.Equal(404, await Status("/api/pads/inconnu/play"));
        Assert.Equal(200, await Status("/api/presets/Robot/apply"));
        Assert.Equal(404, await Status("/api/presets/Inconnu/apply"));
        Assert.Equal(200, await Status("/api/ptt", new() { ["state"] = "down" }));
        Assert.Equal(200, await Status("/api/ptt", new() { ["state"] = "up" }));
        Assert.Equal(200, await Status("/api/mix", new() { ["key"] = "music", ["value"] = "0.5" }));
        Assert.Equal(400, await Status("/api/mix", new() { ["key"] = "music", ["value"] = "beaucoup" }));
        Assert.Equal(200, await Status("/api/say", new() { ["text"] = "  Bonjour  " }));
        Assert.Equal(400, await Status("/api/say", new() { ["text"] = " " }));
        Assert.Equal(404, await Status("/api/nope"));
        Assert.Equal(["radio", "media-to-mic", "bt-reveal", "play Sirène", "preset Robot", "preset Inconnu", "ptt down", "ptt up",
            "mix music 0.5", "say Bonjour"], actions.Calls);

        var longText = new string('a', 500);
        await RemoteApi.HandleAsync(Req("/api/say", new() { ["text"] = longText }), actions);
        Assert.Equal("say " + new string('a', RemoteApi.MaxSayLength), actions.Calls[^1]);
    }

    [Fact]
    public async Task Api_GivesCatalogStateAndLegacyLists()
    {
        var actions = new FakeActions();
        using var catalog = JsonDocument.Parse((await RemoteApi.HandleAsync(Req("/api/catalog"), actions)).Body);
        Assert.Equal("Robot", catalog.RootElement.GetProperty("presets")[0].GetProperty("name").GetString());
        Assert.Equal("#7c5cff", catalog.RootElement.GetProperty("accent").GetString());

        using var state = JsonDocument.Parse((await RemoteApi.HandleAsync(Req("/api/state"), actions)).Body);
        Assert.Equal(215, state.RootElement.GetProperty("music").GetProperty("duration").GetDouble());

        using var pads = JsonDocument.Parse((await RemoteApi.HandleAsync(Req("/api/pads"), actions)).Body);
        Assert.False(pads.RootElement[0].GetProperty("playing").GetBoolean());
        Assert.True(pads.RootElement[1].GetProperty("playing").GetBoolean());
        using var status = JsonDocument.Parse((await RemoteApi.HandleAsync(Req("/api/status"), actions)).Body);
        Assert.Equal("Mona Lisa", status.RootElement.GetProperty("music").GetString());
    }

    [Fact]
    public async Task Deck_KeepsOnlyValidButtons()
    {
        var pad = "pad:11111111-1111-1111-1111-111111111111";
        Assert.True(RemoteApi.IsDeckItem(pad));
        Assert.True(RemoteApi.IsDeckItem("preset:Robot"));
        Assert.True(RemoteApi.IsDeckItem("say:GG !"));
        Assert.True(RemoteApi.IsDeckItem("act:ptt"));
        Assert.False(RemoteApi.IsDeckItem("act:shutdown"));
        Assert.False(RemoteApi.IsDeckItem("pad:pas-un-guid"));
        Assert.False(RemoteApi.IsDeckItem("say:"));
        Assert.False(RemoteApi.IsDeckItem("bidule"));

        var actions = new FakeActions();
        var items = string.Join('|', [pad, "act:stop", "act:shutdown", pad, "say:" + new string('x', 400), "preset:Robot"]);
        var response = await RemoteApi.HandleAsync(Req("/api/deck", new() { ["items"] = items }), actions);
        Assert.Equal(200, response.Status);
        Assert.Equal([pad, "act:stop", "preset:Robot"], actions.Deck);

        var tooMany = string.Join('|', Enumerable.Range(0, 100).Select(i => $"say:phrase {i}"));
        Assert.Equal(RemoteApi.MaxDeckItems, RemoteApi.ParseDeck(tooMany).Count);
    }

    [Fact]
    public void Codes_AreLongAndUnambiguous()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => RemoteServer.NewCode()).ToList();
        Assert.All(codes, c => Assert.Matches("^[a-hjkmnp-z2-9]{10}$", c));
        Assert.Equal(50, codes.Distinct().Count());
    }

    [Fact]
    public void Settings_RemoteIsOffByDefaultAndSanitized()
    {
        var s = new AppSettings();
        Assert.False(s.Remote.Enabled);
        Assert.False(s.Remote.AllowNetwork);
        Assert.Empty(s.Remote.Deck);
        var back = SettingsStore.Deserialize("""{ "Remote": { "Enabled": true, "Port": 80, "Code": "", "Deck": ["act:stop", "act:rm", null, "act:stop"] } }""");
        Assert.True(back.Remote.Enabled);
        Assert.Equal(RemoteServer.DefaultPort, back.Remote.Port);
        Assert.Equal(10, back.Remote.Code.Length);
        Assert.Equal(["act:stop"], back.Remote.Deck);
    }
}
