using System.Net;
using System.Text.Json;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Remote;

namespace SpokaSoundBoard.Tests;

/// <summary>2.6: remote control (HTTP server, access code, routes).</summary>
public sealed class RemoteTests
{
    private sealed class FakeActions : IRemoteActions
    {
        public readonly List<string> Calls = [];
        public readonly List<RemotePad> Pads =
        [
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Applaudissements", "#7C5CFF", "Réactions", false, "F1"),
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Sirène", "#EF4444", "", true, null),
        ];

        public Task<IReadOnlyList<RemotePad>> PadsAsync() => Task.FromResult<IReadOnlyList<RemotePad>>(Pads);

        public Task<bool> PlayPadAsync(string idOrName)
        {
            var pad = RemoteApi.FindPad(Pads, idOrName, p => p.Id, p => p.Name);
            if (pad != null) Calls.Add("play " + pad.Name);
            return Task.FromResult(pad != null);
        }

        public Task StopAllAsync() { Calls.Add("stop"); return Task.CompletedTask; }
        public Task ToggleMicAsync() { Calls.Add("mic"); return Task.CompletedTask; }
        public Task ToggleEffectsAsync() { Calls.Add("fx"); return Task.CompletedTask; }
        public Task CyclePresetAsync(int direction) { Calls.Add("preset " + direction); return Task.CompletedTask; }
        public Task MusicPlayPauseAsync() { Calls.Add("music"); return Task.CompletedTask; }
        public Task MusicNextAsync() { Calls.Add("next"); return Task.CompletedTask; }
        public Task<RemoteStatus> StatusAsync() => Task.FromResult(new RemoteStatus(true, false, "Robot", "Mona Lisa", true));
    }

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
        Assert.Contains("code", await refused.Content.ReadAsStringAsync());
        Assert.Equal(["play Applaudissements", "stop"], actions.Calls);

        var page = await http.GetAsync("/?code=abcdefghjk");
        Assert.Contains("Télécommande SpokaSoundBoard", await page.Content.ReadAsStringAsync());
        Assert.StartsWith("text/html", page.Content.Headers.ContentType!.ToString());
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
        async Task<RemoteResponse> Call(string path) =>
            await RemoteApi.HandleAsync(new RemoteRequest("POST", path, new Dictionary<string, string>(), IPAddress.Loopback), actions);

        foreach (var path in new[] { "/api/stop", "/api/mic/toggle", "/api/effects/toggle", "/api/preset/next", "/api/preset/previous",
                     "/api/music/playpause", "/api/music/next", "/api/pads/sirene/play", "/api/pads/22222222-2222-2222-2222-222222222222/play" })
            Assert.Equal(200, (await Call(path)).Status);
        Assert.Equal(["stop", "mic", "fx", "preset 1", "preset -1", "music", "next", "play Sirène", "play Sirène"], actions.Calls);

        Assert.Equal(404, (await Call("/api/pads/inconnu/play")).Status);
        Assert.Equal(404, (await Call("/api/nope")).Status);

        using var pads = JsonDocument.Parse((await Call("/api/pads")).Body);
        Assert.Equal("Applaudissements", pads.RootElement[0].GetProperty("name").GetString());
        Assert.True(pads.RootElement[1].GetProperty("playing").GetBoolean());
        using var status = JsonDocument.Parse((await Call("/api/status")).Body);
        Assert.Equal("Robot", status.RootElement.GetProperty("preset").GetString());
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
        var back = SettingsStore.Deserialize("""{ "Remote": { "Enabled": true, "Port": 80, "Code": "" } }""");
        Assert.True(back.Remote.Enabled);
        Assert.Equal(RemoteServer.DefaultPort, back.Remote.Port);
        Assert.Equal(10, back.Remote.Code.Length);
    }
}
