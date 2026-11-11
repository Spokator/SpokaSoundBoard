using System.Net;
using System.Text.Json;
using NAudio.Wave;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Tests;

/// <summary>2.2.1: corrupted settings, update integrity, soundboard voice cap, French error messages.</summary>
public sealed class HardeningTests
{
    private const int Rate = 48000;

    [Fact]
    public void Settings_CorruptedValuesAreBroughtBackInRange()
    {
        const string json = """
            {
              "Voice": { "EchoDelayMs": "NaN", "EchoFeedback": 5, "PitchSemitones": -99, "LofiBits": 0, "BeepStyle": 42, "InputGainDb": "Infinity" },
              "Mix": { "MusicVolume": "NaN", "DuckingAmountDb": -500, "DuckingReleaseMs": 0 },
              "Music": { "Effects": { "Speed": 0, "BassDb": "-Infinity", "Eq1k": 80, "StereoWidth": "NaN" } },
              "Hotkeys": { "PushToTalk": null, "PlayPause": null, "StopAll": null },
              "Pads": [ null, { "Name": "ok", "Hotkey": null } ],
              "CustomPresets": [ null, { "Name": "x", "Settings": { "ChorusRate": -3 } }, { "Name": "y", "Settings": null } ]
            }
            """;
        var s = SettingsStore.Deserialize(json);

        Assert.Equal(250f, s.Voice.EchoDelayMs);     // NaN → default
        Assert.Equal(0.9f, s.Voice.EchoFeedback);    // clamped to the slider maximum
        Assert.Equal(-12f, s.Voice.PitchSemitones);
        Assert.Equal(3f, s.Voice.LofiBits);
        Assert.Equal(RadioBeepStyle.Roger, s.Voice.BeepStyle);
        Assert.Equal(0f, s.Voice.InputGainDb);

        Assert.Equal(0.8f, s.Mix.MusicVolume);
        Assert.Equal(-40f, s.Mix.DuckingAmountDb);
        Assert.Equal(100f, s.Mix.DuckingReleaseMs);

        Assert.Equal(0.5f, s.Music.Effects.Speed);
        Assert.Equal(0f, s.Music.Effects.BassDb);
        Assert.Equal(12f, s.Music.Effects.Eq1k);
        Assert.Equal(1f, s.Music.Effects.StereoWidth);

        Assert.NotNull(s.Hotkeys.PushToTalk);
        Assert.NotNull(s.Hotkeys.PlayPause);
        Assert.NotNull(s.Hotkeys.StopAll);
        Assert.False(s.Hotkeys.PushToTalk.IsSet);

        Assert.Single(s.Pads);
        Assert.NotNull(s.Pads[0].Hotkey);
        Assert.Equal(2, s.CustomPresets.Count);
        Assert.Equal(0.1f, s.CustomPresets[0].Settings.ChorusRate);
        Assert.NotNull(s.CustomPresets[1].Settings);
    }

    [Fact]
    public void Settings_ValidValuesAreKept()
    {
        var s = new AppSettings();
        s.Voice.EchoDelayMs = 420;
        s.Voice.PitchSemitones = 5;
        s.Mix.MusicVolume = 1.2f;
        s.Music.Effects.Speed = 1.25f;
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal(420f, back.Voice.EchoDelayMs);
        Assert.Equal(5f, back.Voice.PitchSemitones);
        Assert.Equal(1.2f, back.Mix.MusicVolume);
        Assert.Equal(1.25f, back.Music.Effects.Speed);
    }

    [Fact]
    public void BuiltInPresets_AreAlreadyInRange()
    {
        foreach (var preset in BuiltInPresets.All)
        {
            var copy = preset.Settings.Clone();
            copy.Sanitize();
            Assert.Equal(JsonSerializer.Serialize(preset.Settings), JsonSerializer.Serialize(copy));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VoiceChain_ExtremeSettingsStayFinite(bool maximum)
    {
        var v = new VoiceEffectSettings
        {
            EffectsEnabled = true, GateEnabled = true, RadioEnabled = true, RobotEnabled = true, EchoEnabled = true,
            ReverbEnabled = true, CompressorEnabled = true, EqEnabled = true, ChorusEnabled = true, LofiEnabled = true,
            MuffleEnabled = true, SquelchEnabled = true, RogerBeepEnabled = true,
        };
        float Pick(float min, float max) => maximum ? max : min;
        v.InputGainDb = Pick(-12, 24); v.VoiceVolumeDb = Pick(-24, 12); v.VoiceThresholdDb = Pick(-60, 0);
        v.PitchSemitones = Pick(-12, 12); v.RadioLowCutHz = Pick(100, 1200); v.RadioHighCutHz = Pick(1500, 6000);
        v.RadioDrive = Pick(0, 1); v.RadioNoise = Pick(0, 1); v.RobotFrequency = Pick(20, 300); v.RobotMix = Pick(0, 1);
        v.EchoDelayMs = Pick(40, 1000); v.EchoFeedback = Pick(0, 0.9f); v.EchoMix = Pick(0, 1);
        v.ReverbSize = Pick(0, 1); v.ReverbMix = Pick(0, 1); v.CompressorAmount = Pick(0, 1);
        v.EqLowDb = Pick(-12, 12); v.EqMidDb = Pick(-12, 12); v.EqHighDb = Pick(-12, 12);
        v.ChorusRate = Pick(0.1f, 8); v.ChorusDepth = Pick(0, 1); v.ChorusMix = Pick(0, 1);
        v.LofiDrive = Pick(0, 1); v.LofiBits = Pick(3, 16); v.LofiDownsample = Pick(1, 10); v.MuffleHz = Pick(200, 8000);

        var vp = new VoiceProcessor(Rate, v);
        var random = new Random(7);
        var input = new float[960];
        var output = new float[960];
        for (var block = 0; block < 200; block++) // 4 s of loud noise, in 20 ms blocks
        {
            for (var i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() * 2 - 1);
            vp.Process(input, output, true, false, false);
            foreach (var x in output) Assert.True(float.IsFinite(x) && Math.Abs(x) < 64f, $"sample {x} in block {block}");
        }
    }

    [Fact]
    public void UpdateChecker_ReadsSizeAndDigest()
    {
        const string json = """
            {"tag_name":"v9.0.0","draft":false,"prerelease":false,"html_url":"https://github.com/x/y","body":"",
             "assets":[{"name":"SpokaSoundBoard-Setup-9.0.0.exe","browser_download_url":"https://example.com/s.exe","size":1234,
                        "digest":"sha256:0476B6A57105196FE433091101617F2D320463C26D04DB4A50DD2BFB37F172CC"}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var update = UpdateChecker.Parse(doc.RootElement, new Version(2, 2, 0))!;
        Assert.Equal(1234, update.Size);
        Assert.Equal("0476b6a57105196fe433091101617f2d320463c26d04db4a50dd2bfb37f172cc", update.Sha256);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("sha512:abcd", null)]
    [InlineData("sha256:xyz", null)]
    [InlineData("sha256:0476b6a57105196fe433091101617f2d320463c26d04db4a50dd2bfb37f172cc", "0476b6a57105196fe433091101617f2d320463c26d04db4a50dd2bfb37f172cc")]
    public void UpdateChecker_ParsesDigest(string? digest, string? expected) =>
        Assert.Equal(expected, UpdateChecker.ParseDigest(digest));

    [Fact]
    public void UpdateChecker_RejectsWrongSizeOrHash()
    {
        const string hash = "0476b6a57105196fe433091101617f2d320463c26d04db4a50dd2bfb37f172cc";
        var update = new UpdateInfo(new Version(9, 0), "v9.0.0", "u", "p", "", 100, hash);
        Assert.Null(UpdateChecker.CheckIntegrity(update, 100, hash.ToUpperInvariant()));
        Assert.Contains("incomplet", UpdateChecker.CheckIntegrity(update, 99, hash));
        Assert.Contains("empreinte", UpdateChecker.CheckIntegrity(update, 100, new string('0', 64)));
        // Older releases without size or digest: nothing to compare, the download is accepted.
        Assert.Null(UpdateChecker.CheckIntegrity(update with { Size = 0, Sha256 = null }, 5, "ff"));
    }

    [Fact]
    public async Task SoundPads_AreCappedAndFinishedOnesRemoved()
    {
        var dir = Directory.CreateTempSubdirectory("spoka-pads-");
        try
        {
            var wav = Path.Combine(dir.FullName, "beep.wav");
            using (var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)))
                for (var i = 0; i < Rate / 10; i++) { w.WriteSample(0.1f); w.WriteSample(0.1f); } // 100 ms

            using var player = new SoundPadPlayer(Rate);
            for (var i = 0; i < SoundPadPlayer.MaxVoices + 6; i++)
                await player.PlayAsync(Guid.NewGuid(), wav, 1f, loop: false);
            Assert.Equal(SoundPadPlayer.MaxVoices, player.ActiveCount);

            var buffer = new float[Rate * 2 / 10];
            player.Read(buffer, 0, buffer.Length);   // the whole 100 ms
            player.Read(buffer, 0, buffer.Length);   // past the end: every sound finishes
            Assert.Equal(0, player.ActiveCount);
            Assert.All(buffer, x => Assert.Equal(0f, x));
        }
        finally
        {
            await Task.Delay(200); // let the thread pool dispose the readers before deleting the file
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    [Fact]
    public void FriendlyErrors_SpeakFrench()
    {
        Assert.Equal("introuvable sur le site (erreur 404)",
            FriendlyErrors.Describe(new HttpRequestException("Response status code does not indicate success: 404", null, HttpStatusCode.NotFound)));
        Assert.Equal("connexion impossible, vérifiez votre accès à Internet",
            FriendlyErrors.Describe(new HttpRequestException("No such host is known.")));
        Assert.Equal("le délai d'attente est dépassé", FriendlyErrors.Describe(new TaskCanceledException()));
        Assert.Equal("accès refusé au fichier ou au dossier", FriendlyErrors.Describe(new UnauthorizedAccessException("Access to the path is denied.")));
        Assert.Equal("fichier introuvable (a.mp3)", FriendlyErrors.Describe(new FileNotFoundException("Could not find file", @"C:\x\a.mp3")));
        Assert.Equal("le fichier est utilisé par un autre programme",
            FriendlyErrors.Describe(new IOException("The process cannot access the file", unchecked((int)0x80070020))));
        Assert.Equal("erreur inattendue (NotSupportedException)", FriendlyErrors.Describe(new NotSupportedException("Specified method is not supported.")));
        Assert.Equal("le délai d'attente est dépassé", FriendlyErrors.Describe(new AggregateException(new TimeoutException())));
        // The app's own exceptions already carry a French message.
        Assert.Equal("Installateur refusé : test.", FriendlyErrors.Describe(new UpdateIntegrityException("Installateur refusé : test.")));
    }
}
