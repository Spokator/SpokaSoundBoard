namespace SpokaSoundBoard.Core.Models;

public static class BuiltInPresets
{
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
        ];
    }
}
