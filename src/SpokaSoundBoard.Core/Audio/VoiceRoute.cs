namespace SpokaSoundBoard.Core.Audio;

/// <summary>What to tell the user about where their (changed) voice goes.</summary>
/// <param name="Warning">Nobody else hears the voice as things are set up.</param>
/// <param name="CanOpenDiscord">The setup is right on our side: what is left is picking the microphone in Discord.</param>
public sealed record VoiceRouteHint(string Text, bool Warning, bool CanOpenDiscord);

/// <summary>
/// Other programs only hear what SpokaSoundBoard sends through a virtual microphone (VB-CABLE, Spoka Cable…): a voice
/// effect or the AI voice changer never reaches Discord or a game that still listens to the real microphone.
/// </summary>
public static class VoiceRoute
{
    /// <param name="cableInstalled">A virtual cable exists on this PC.</param>
    /// <param name="gameMic">Microphone name of the game output when it is a virtual cable, otherwise null.</param>
    /// <param name="discordMic">Microphone name of the Discord output when it is a virtual cable, otherwise null.</param>
    /// <param name="gameVoice">« Ma voix » is sent to the game output (Mixage).</param>
    /// <param name="discordVoice">« Ma voix » is sent to the Discord output (Mixage).</param>
    /// <param name="discord">What Discord is recording right now, when it is in a voice channel.</param>
    public static VoiceRouteHint Describe(bool cableInstalled, string? gameMic, string? discordMic, bool gameVoice, bool discordVoice,
        DiscordMicCheck? discord = null)
    {
        if (!cableInstalled)
            return new("Pour l'instant, personne d'autre ne l'entend : Discord et vos jeux reçoivent votre vrai micro. " +
                       "Installez le micro virtuel (VB-CABLE, gratuit) dans Configuration, puis choisissez-le comme micro dans Discord et dans vos jeux.",
                true, false);
        if (gameMic == null)
            return new("La sortie « Micro virtuel » choisie dans Configuration n'est pas un micro virtuel : personne d'autre n'entend cette voix. " +
                       "Choisissez « CABLE Input » (ou votre micro virtuel) à la place.",
                true, false);

        // The usual reason why the game hears the changed voice and Discord does not: Discord kept the real microphone
        // (often « Par défaut », which follows the Windows default microphone).
        if (discord is { State: DiscordMicState.RealMic } real)
            return new($"Discord écoute votre vrai micro (« {real.DeviceName} ») : vos amis sur Discord entendent votre voix naturelle. " +
                       $"Dans Discord, Voix et vidéo → Périphérique d'entrée, choisissez « {discordMic ?? gameMic} » (pas « Par défaut »).",
                true, true);

        string text;
        bool muted;
        if (discordMic != null)
        {
            text = $"Pour qu'on l'entende, choisissez comme micro « {discordMic} » dans Discord et « {gameMic} » dans vos jeux, pas votre vrai micro.";
            muted = !gameVoice || !discordVoice;
            if (!discordVoice) text += " Attention : « Ma voix » est coupée pour Discord (Mixage).";
            if (!gameVoice) text += " Attention : « Ma voix » est coupée pour le jeu (Mixage).";
        }
        else
        {
            text = $"Pour qu'on l'entende, choisissez « {gameMic} » comme micro dans Discord et dans vos jeux, pas votre vrai micro.";
            muted = !gameVoice;
            if (muted) text += " Attention : « Ma voix » est coupée (Mixage).";
        }
        if (discord is { State: DiscordMicState.VirtualMic } ok) text += $" Discord écoute bien le micro virtuel (« {ok.DeviceName} »).";
        return new(text, muted, true);
    }
}
