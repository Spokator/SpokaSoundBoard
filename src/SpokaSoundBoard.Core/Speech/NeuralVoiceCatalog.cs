namespace SpokaSoundBoard.Core.Speech;

/// <summary>Neural model families the engine knows how to run.</summary>
public enum NeuralModelKind
{
    /// <summary>Supertonic (flow matching, character input: no phonemizer, so no GPL espeak-ng).</summary>
    Supertonic,
}

/// <summary>One file of a voice model pack, checked by size and SHA-256 after download.</summary>
public sealed record VoiceModelFile(string Name, long Size, string Sha256);

/// <summary>
/// A downloadable voice model (data only, never executable code). Several voices can share one pack: a
/// multi-speaker model is downloaded once.
/// </summary>
/// <param name="BaseUrl">Folder URL, pinned to an immutable revision; each file is <c>BaseUrl/Name</c>.</param>
/// <param name="License">License of the model weights (shown to the user before downloading).</param>
public sealed record VoiceModelPack(
    string Id,
    string Name,
    NeuralModelKind Kind,
    string BaseUrl,
    IReadOnlyList<VoiceModelFile> Files,
    string License,
    string LicenseUrl,
    string SourceUrl)
{
    public long TotalSize => Files.Sum(f => f.Size);

    public Uri UrlOf(VoiceModelFile file) => new($"{BaseUrl.TrimEnd('/')}/{file.Name}");
}

/// <summary>A natural (neural) voice: one speaker of a <see cref="VoiceModelPack"/>.</summary>
/// <param name="Key">Stable short key, part of the persisted voice id (never change it).</param>
public sealed record NeuralVoice(string Key, string PackId, int SpeakerId, string Name, bool IsFemale)
{
    public const string IdPrefix = "neural:";

    /// <summary>Id stored in settings.json (TextToSpeechSettings.VoiceId), distinct from WinRT voice ids.</summary>
    public string Id => $"{IdPrefix}{PackId}:{Key}";
}

/// <summary>A language a multilingual neural model can read (the text is not translated).</summary>
public sealed record SpeechLanguage(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>The natural voices offered for download. Licenses are recorded per pack and shown in the UI.</summary>
public static class NeuralVoiceCatalog
{
    public const string DefaultLanguage = "fr";

    /// <summary>
    /// Supertonic 3 (Supertone Inc., 2026), int8 build by the sherpa-onnx team. 10 speakers, 31 languages, 44.1 kHz.
    /// Weights under the BigScience OpenRAIL-M license: free use, redistribution and commercial use allowed, with
    /// use-based restrictions (no impersonation without consent, no harassment or defamation, machine-generated
    /// content must not be passed off as human…) that the user must accept. Mirror pinned to a commit so the
    /// hashes below never change under us.
    /// </summary>
    public static readonly VoiceModelPack Supertonic3 = new(
        Id: "supertonic3",
        Name: "Supertonic 3",
        Kind: NeuralModelKind.Supertonic,
        BaseUrl: "https://huggingface.co/csukuangfj2/sherpa-onnx-supertonic-3-tts-int8-2026-05-11/resolve/cca5a0e6c96e1d2c720986bf7e75fcc81dee3ae4",
        Files:
        [
            new("tts.json", 8253, "42078d3aef1cd43ab43021f3c54f47d2d75ceb4e75f627f118890128b06a0d09"),
            new("unicode_indexer.bin", 262144, "8402ca48e5189a8950138580b0fff64db6f072f24ac07cd54ba8b2fbb9883b30"),
            new("voice.bin", 517168, "67d5209b0ee8ce6c74105ffbe12fe6a7628aea3b4ba2fcb308a4a67938a93ce8"),
            new("duration_predictor.int8.onnx", 3700147, "c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db"),
            new("text_encoder.int8.onnx", 36416150, "c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff"),
            new("vocoder.int8.onnx", 25991073, "e923d60f53f95eb1ce235f1dc33ec56d9c057823c96fa6f8acf98f32b0da6152"),
            new("vector_estimator.int8.onnx", 78400833, "20cd86fa5c6effedfda0e7cffe5b0569ca401c440a0c3a1d72bf39286c0db3fd"),
        ],
        License: "OpenRAIL-M",
        LicenseUrl: "https://huggingface.co/Supertone/supertonic-3/blob/main/LICENSE",
        SourceUrl: "https://huggingface.co/Supertone/supertonic-3");

    public static IReadOnlyList<VoiceModelPack> Packs { get; } = [Supertonic3];

    /// <summary>
    /// Speaker ids follow sherpa-onnx's voice.bin, built from the sorted style files: F1…F5 then M1…M5.
    /// </summary>
    public static IReadOnlyList<NeuralVoice> Voices { get; } =
    [
        new("F1", Supertonic3.Id, 0, "Femme 1", true),
        new("F2", Supertonic3.Id, 1, "Femme 2", true),
        new("F3", Supertonic3.Id, 2, "Femme 3", true),
        new("F4", Supertonic3.Id, 3, "Femme 4", true),
        new("F5", Supertonic3.Id, 4, "Femme 5", true),
        new("M1", Supertonic3.Id, 5, "Homme 1", false),
        new("M2", Supertonic3.Id, 6, "Homme 2", false),
        new("M3", Supertonic3.Id, 7, "Homme 3", false),
        new("M4", Supertonic3.Id, 8, "Homme 4", false),
        new("M5", Supertonic3.Id, 9, "Homme 5", false),
    ];

    /// <summary>Default natural voice once the pack is downloaded.</summary>
    public static NeuralVoice DefaultVoice => Voices[0];

    /// <summary>Languages of Supertonic 3: French and English first, then by French name.</summary>
    public static IReadOnlyList<SpeechLanguage> Languages { get; } =
    [
        new("fr", "Français"),
        new("en", "Anglais"),
        new("de", "Allemand"),
        new("ar", "Arabe"),
        new("bg", "Bulgare"),
        new("ko", "Coréen"),
        new("hr", "Croate"),
        new("da", "Danois"),
        new("es", "Espagnol"),
        new("et", "Estonien"),
        new("fi", "Finnois"),
        new("el", "Grec"),
        new("hi", "Hindi"),
        new("hu", "Hongrois"),
        new("id", "Indonésien"),
        new("it", "Italien"),
        new("ja", "Japonais"),
        new("lv", "Letton"),
        new("lt", "Lituanien"),
        new("nl", "Néerlandais"),
        new("pl", "Polonais"),
        new("pt", "Portugais"),
        new("ro", "Roumain"),
        new("ru", "Russe"),
        new("sk", "Slovaque"),
        new("sl", "Slovène"),
        new("sv", "Suédois"),
        new("cs", "Tchèque"),
        new("tr", "Turc"),
        new("uk", "Ukrainien"),
        new("vi", "Vietnamien"),
    ];

    public static bool IsNeuralVoiceId(string? id) => id != null && id.StartsWith(NeuralVoice.IdPrefix, StringComparison.Ordinal);

    /// <summary>The natural voice of a persisted id, or null (Windows voice, unknown or retired voice).</summary>
    public static NeuralVoice? FindVoice(string? id) =>
        IsNeuralVoiceId(id) ? Voices.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.Ordinal)) : null;

    public static VoiceModelPack? FindPack(string? id) => Packs.FirstOrDefault(p => p.Id == id);

    public static VoiceModelPack PackOf(NeuralVoice voice) =>
        FindPack(voice.PackId) ?? throw new InvalidOperationException($"Pack inconnu : {voice.PackId}");

    /// <summary>A supported language code (case-insensitive, "fr-FR" → "fr"), or <see cref="DefaultLanguage"/>.</summary>
    public static string NormalizeLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return DefaultLanguage;
        var primary = code.Trim().Split('-', '_')[0].ToLowerInvariant();
        return Languages.Any(l => l.Code == primary) ? primary : DefaultLanguage;
    }

    /// <summary>Phrase used by the voice preview button.</summary>
    public static string SamplePhrase(string language) => NormalizeLanguage(language) switch
    {
        "fr" => "Salut tout le monde ! Voici ma voix, prête à parler dans votre micro.",
        "es" => "¡Hola a todos! Esta es mi voz, lista para hablar por tu micrófono.",
        "de" => "Hallo zusammen! Das ist meine Stimme, bereit für dein Mikrofon.",
        "it" => "Ciao a tutti! Questa è la mia voce, pronta a parlare nel tuo microfono.",
        "pt" => "Olá a todos! Esta é a minha voz, pronta para falar no seu microfone.",
        _ => "Hi everyone! This is my voice, ready to speak into your microphone.",
    };
}
