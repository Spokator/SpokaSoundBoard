using SpokaSoundBoard.Core.Audio;

namespace SpokaSoundBoard.Tests;

/// <summary>2.16.1: telling the user which microphone Discord and games must use to hear the changed voice.</summary>
public sealed class VoiceRouteTests
{
    [Fact]
    public void NoCable_NobodyElseHearsIt()
    {
        var hint = VoiceRoute.Describe(cableInstalled: false, gameMic: null, discordMic: null, gameVoice: true, discordVoice: true);
        Assert.True(hint.Warning);
        Assert.False(hint.CanOpenDiscord);
        Assert.Contains("vrai micro", hint.Text);
        Assert.Contains("VB-CABLE", hint.Text);
    }

    [Fact]
    public void CableInstalled_ButGameOutputIsASpeaker_SaysSo()
    {
        // The reported setup: VB-CABLE missing or unused, « Realtek Digital Output » chosen as the virtual microphone.
        var hint = VoiceRoute.Describe(cableInstalled: true, gameMic: null, discordMic: null, gameVoice: true, discordVoice: true);
        Assert.True(hint.Warning);
        Assert.Contains("n'est pas un micro virtuel", hint.Text);
    }

    [Fact]
    public void OneCable_SameMicrophoneForDiscordAndGames()
    {
        var hint = VoiceRoute.Describe(true, "CABLE Output (VB-Audio Virtual Cable)", null, true, true);
        Assert.False(hint.Warning);
        Assert.True(hint.CanOpenDiscord);
        Assert.Contains("« CABLE Output (VB-Audio Virtual Cable) » comme micro dans Discord et dans vos jeux", hint.Text);
    }

    [Fact]
    public void TwoCables_NamesEach_AndFlagsAVoiceMutedForDiscord()
    {
        var hint = VoiceRoute.Describe(true, "CABLE Output", "CABLE-A Output", gameVoice: true, discordVoice: false);
        Assert.Contains("« CABLE-A Output » dans Discord", hint.Text);
        Assert.Contains("« CABLE Output » dans vos jeux", hint.Text);
        Assert.Contains("coupée pour Discord", hint.Text);
        Assert.True(hint.Warning);
        Assert.True(hint.CanOpenDiscord);
    }

    [Fact]
    public void DiscordOnTheRealMic_IsTheWarning_EvenWhenEverythingElseIsRight()
    {
        // Reported by players: the game hears the changed voice, Discord the natural one. Discord kept the headset mic.
        var listeners = new[]
        {
            new MicListener("Microphone (HyperX Cloud Alpha Wireless)", false, "SpokaSoundBoard"),
            new MicListener("Microphone (HyperX Cloud Alpha Wireless)", false, "Discord"),
            new MicListener("CABLE Output (VB-Audio Virtual Cable)", true, "FortniteClient-Win64-Shipping"),
        };
        var discord = DiscordMicCheck.Evaluate(listeners);
        Assert.Equal(DiscordMicState.RealMic, discord.State);
        Assert.Equal("Microphone (HyperX Cloud Alpha Wireless)", discord.DeviceName);

        var hint = VoiceRoute.Describe(true, "CABLE Output (VB-Audio Virtual Cable)", null, true, true, discord);
        Assert.True(hint.Warning);
        Assert.True(hint.CanOpenDiscord);
        Assert.Contains("Discord écoute votre vrai micro (« Microphone (HyperX Cloud Alpha Wireless) »)", hint.Text);
        Assert.Contains("choisissez « CABLE Output (VB-Audio Virtual Cable) »", hint.Text);
    }

    [Fact]
    public void DiscordOnTheCable_IsConfirmed_AndClosedDiscordSaysNothing()
    {
        var ok = DiscordMicCheck.Evaluate([new MicListener("CABLE-A Output (VB-Audio Cable A)", true, "discordptb")]);
        Assert.Equal(DiscordMicState.VirtualMic, ok.State);
        var hint = VoiceRoute.Describe(true, "CABLE Output", "CABLE-A Output (VB-Audio Cable A)", true, true, ok);
        Assert.False(hint.Warning);
        Assert.Contains("Discord écoute bien le micro virtuel", hint.Text);

        Assert.Equal(DiscordMicState.Unknown, DiscordMicCheck.Evaluate([new MicListener("Micro", false, "obs64")]).State);
        Assert.DoesNotContain("Discord écoute", VoiceRoute.Describe(true, "CABLE Output", null, true, true, DiscordMicCheck.Unknown).Text);
    }

    [Fact]
    public void Snapshot_NeverThrows()
    {
        var listeners = MicListeners.Snapshot(); // real Windows audio sessions, read only
        Assert.NotNull(listeners);
    }
}
