namespace SpokaSoundBoard.Core.Models;

public static class BuiltInPresets
{
    /// <summary>Called « Dark Vador » before 2.5.1 (a trademark): settings saved with the old name are moved over.</summary>
    public const string DarkLordName = "Seigneur noir";
    public const string OldDarkLordName = "Dark Vador";

    public static IReadOnlyList<EffectPreset> All { get; } = Create();

    private static List<EffectPreset> Create()
    {
        EffectPreset P(string name, string icon, string description, Action<VoiceEffectSettings> configure)
        {
            var s = new VoiceEffectSettings
            {
                RadioEnabled = false,
                CompressorEnabled = true,
                CompressorAmount = 0.4f,
                SquelchEnabled = false,
            };
            configure(s);
            return new EffectPreset { Name = name, Icon = icon, Description = description, IsBuiltIn = true, Settings = s };
        }

        return
        [
            P("Voix normale", "\uE720", "Votre voix naturelle, légèrement compressée.", _ => { }),
            P("Pilote d'hélico", "\uE709", "Radio de cockpit : bande étroite, saturation, souffle et squelch.", s =>
            {
                s.RadioEnabled = true;
                s.RadioLowCutHz = 450f;
                s.RadioHighCutHz = 2800f;
                s.RadioDrive = 0.55f;
                s.RadioNoise = 0.18f;
                s.SquelchEnabled = true;
                s.RogerBeepEnabled = true;
                s.CompressorAmount = 0.75f;
            }),
            P("Tour de contrôle", "\uE80F", "Radio ATC claire avec un léger souffle.", s =>
            {
                s.RadioEnabled = true;
                s.RadioLowCutHz = 300f;
                s.RadioHighCutHz = 3400f;
                s.RadioDrive = 0.25f;
                s.RadioNoise = 0.25f;
                s.SquelchEnabled = true;
                s.CompressorAmount = 0.6f;
            }),
            P("Talkie-walkie", "\uE717", "Petit haut-parleur saturé, très radio.", s =>
            {
                s.RadioEnabled = true;
                s.RadioLowCutHz = 650f;
                s.RadioHighCutHz = 2300f;
                s.RadioDrive = 0.85f;
                s.RadioNoise = 0.3f;
                s.SquelchEnabled = true;
                s.RogerBeepEnabled = true;
                s.CompressorAmount = 0.8f;
            }),
            P("Mégaphone", "\uE767", "Voix projetée, agressive avec un léger écho.", s =>
            {
                s.RadioEnabled = true;
                s.RadioLowCutHz = 700f;
                s.RadioHighCutHz = 4200f;
                s.RadioDrive = 0.7f;
                s.RadioNoise = 0f;
                s.EchoEnabled = true;
                s.EchoDelayMs = 120f;
                s.EchoFeedback = 0.2f;
                s.EchoMix = 0.18f;
            }),
            P("Robot", "\uE99A", "Voix métallique de machine.", s =>
            {
                s.RobotEnabled = true;
                s.RobotFrequency = 55f;
                s.RobotMix = 0.85f;
                s.EchoEnabled = true;
                s.EchoDelayMs = 60f;
                s.EchoFeedback = 0.25f;
                s.EchoMix = 0.15f;
            }),
            P("Voix grave", "\uE74B", "Voix profonde de géant / démon.", s =>
            {
                s.PitchSemitones = -5f;
                s.ReverbEnabled = true;
                s.ReverbSize = 0.5f;
                s.ReverbMix = 0.15f;
            }),
            P("Voix aiguë", "\uE74A", "Voix de petit personnage.", s => s.PitchSemitones = 6f),
            P("Écho canyon", "\uE909", "Écho répété dans les montagnes.", s =>
            {
                s.EchoEnabled = true;
                s.EchoDelayMs = 420f;
                s.EchoFeedback = 0.45f;
                s.EchoMix = 0.4f;
            }),
            P("Cathédrale", "\uE80F", "Grande réverbération.", s =>
            {
                s.ReverbEnabled = true;
                s.ReverbSize = 0.92f;
                s.ReverbMix = 0.45f;
            }),
            P("Astronaute (NASA)", "\uE9F3", "Radio spatiale avec les bips « Quindar » au début et à la fin.", s =>
            {
                s.RadioEnabled = true;
                s.RadioLowCutHz = 300f;
                s.RadioHighCutHz = 3000f;
                s.RadioDrive = 0.35f;
                s.RadioNoise = 0.22f;
                s.SquelchEnabled = false;
                s.RogerBeepEnabled = true;
                s.BeepStyle = RadioBeepStyle.Nasa;
                s.CompressorAmount = 0.7f;
            }),
            P("Pilote de chasse", "\uE709", "Masque à oxygène : voix étouffée, compressée et radio.", s =>
            {
                s.RadioEnabled = true;
                s.RadioLowCutHz = 500f;
                s.RadioHighCutHz = 2600f;
                s.RadioDrive = 0.6f;
                s.RadioNoise = 0.12f;
                s.SquelchEnabled = true;
                s.RogerBeepEnabled = true;
                s.BeepStyle = RadioBeepStyle.Simple;
                s.MuffleEnabled = true;
                s.MuffleHz = 2200f;
                s.CompressorAmount = 0.85f;
            }),
            P("Téléphone", "\uE717", "Appel téléphonique classique.", s =>
            {
                s.RadioEnabled = true;
                s.RadioLowCutHz = 320f;
                s.RadioHighCutHz = 3400f;
                s.RadioDrive = 0.2f;
                s.RadioNoise = 0f;
                s.CompressorAmount = 0.6f;
            }),
            P("Vieille radio", "\uE7F5", "Poste des années 40 : grésillant et lo-fi.", s =>
            {
                s.RadioEnabled = true;
                s.RadioLowCutHz = 450f;
                s.RadioHighCutHz = 2500f;
                s.RadioDrive = 0.4f;
                s.RadioNoise = 0.35f;
                s.LofiEnabled = true;
                s.LofiBits = 8f;
                s.LofiDownsample = 2f;
                s.LofiDrive = 0.2f;
            }),
            P("Annonce stade / gare", "\uE789", "Haut-parleur public avec grand écho.", s =>
            {
                s.RadioEnabled = true;
                s.RadioLowCutHz = 250f;
                s.RadioHighCutHz = 5000f;
                s.RadioDrive = 0.25f;
                s.RadioNoise = 0f;
                s.ReverbEnabled = true;
                s.ReverbSize = 0.88f;
                s.ReverbMix = 0.4f;
                s.EchoEnabled = true;
                s.EchoDelayMs = 320f;
                s.EchoFeedback = 0.3f;
                s.EchoMix = 0.22f;
            }),
            P("Voix pro (podcast)", "\uE720", "Voix chaude et présente, comme à la radio FM.", s =>
            {
                s.EqEnabled = true;
                s.EqLowDb = 4f;
                s.EqMidDb = -1.5f;
                s.EqHighDb = 3f;
                s.CompressorAmount = 0.8f;
            }),
            P(DarkLordName, "\uE7E8", "Voix grave, métallique et caverneuse, comme un seigneur noir de l'espace.", s =>
            {
                s.PitchSemitones = -4f;
                s.RobotEnabled = true;
                s.RobotFrequency = 30f;
                s.RobotMix = 0.3f;
                s.EqEnabled = true;
                s.EqLowDb = 6f;
                s.ReverbEnabled = true;
                s.ReverbSize = 0.35f;
                s.ReverbMix = 0.18f;
            }),
            P("Démon", "\uE945", "Voix monstrueuse, très grave et saturée.", s =>
            {
                s.PitchSemitones = -8f;
                s.LofiEnabled = true;
                s.LofiDrive = 0.5f;
                s.LofiBits = 12f;
                s.LofiDownsample = 1f;
                s.ReverbEnabled = true;
                s.ReverbSize = 0.6f;
                s.ReverbMix = 0.3f;
            }),
            P("Chipmunk", "\uE76E", "Voix d'écureuil suraiguë.", s => s.PitchSemitones = 12f),
            P("Fantôme", "\uE9CA", "Voix spectrale, dédoublée et lointaine.", s =>
            {
                s.PitchSemitones = 3f;
                s.ChorusEnabled = true;
                s.ChorusRate = 0.4f;
                s.ChorusDepth = 0.9f;
                s.ChorusMix = 0.7f;
                s.ReverbEnabled = true;
                s.ReverbSize = 0.9f;
                s.ReverbMix = 0.5f;
            }),
            P("Alien", "\uE9F3", "Voix extraterrestre ondulante.", s =>
            {
                s.PitchSemitones = 5f;
                s.ChorusEnabled = true;
                s.ChorusRate = 4f;
                s.ChorusDepth = 0.8f;
                s.ChorusMix = 0.6f;
                s.RobotEnabled = true;
                s.RobotFrequency = 120f;
                s.RobotMix = 0.35f;
            }),
            P("Sous l'eau", "\uE9C8", "Voix étouffée et ondulante.", s =>
            {
                s.MuffleEnabled = true;
                s.MuffleHz = 450f;
                s.ChorusEnabled = true;
                s.ChorusRate = 0.6f;
                s.ChorusDepth = 1f;
                s.ChorusMix = 0.7f;
                s.EqEnabled = true;
                s.EqLowDb = 5f;
            }),
            P("Lo-fi 8 bits", "\uE7FC", "Voix de vieux jeu vidéo.", s =>
            {
                s.LofiEnabled = true;
                s.LofiBits = 5f;
                s.LofiDownsample = 4f;
                s.LofiDrive = 0.1f;
            }),
        ];
    }
}
