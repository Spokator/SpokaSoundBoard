namespace SpokaSoundBoard.Core.Speech;

/// <summary>
/// The offline speech recognition model of « Parler avec cette voix »: what you say into the microphone is written
/// down, then read by the chosen synthetic voice.
/// </summary>
public static class SpeechRecognitionCatalog
{
    /// <summary>
    /// NVIDIA Canary 180M Flash (int8 build by the sherpa-onnx team): French, English, Spanish and German, with
    /// punctuation, about 0.3 s per phrase on a CPU. Weights under CC BY 4.0 (attribution shown in the UI). Plus the
    /// Silero voice activity detector (MIT), which cuts the microphone into phrases. Data files only, pinned by hash.
    /// </summary>
    public static readonly VoiceModelPack Canary180mFlash = new(
        Id: "canary180m",
        Name: "Canary 180M Flash",
        Kind: NeuralModelKind.Canary,
        BaseUrl: "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8/resolve/9077164e0d3dd1d5353743e89ceaa1d3a770838c",
        Files:
        [
            new("tokens.txt", 53555, "2dae6fc7815f9640645e0c765522b278ee0cef49b482d91f6913e334628d3e77"),
            new("decoder.int8.onnx", 74437848, "e41a2ab9c0c2fe81a1e8ade5a45fb02a74bc4db7d1f91b89a54a25e2cf79cba2"),
            new("encoder.int8.onnx", 132678643, "7a75b4e2a5857a6dcc0819503bbe3fad66943db4a3ccf21d3f27c633667d303f"),
            new("silero_vad.onnx", 643854, "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6",
                "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx"),
        ],
        License: "CC BY 4.0",
        LicenseUrl: "https://huggingface.co/nvidia/canary-180m-flash",
        SourceUrl: "https://huggingface.co/nvidia/canary-180m-flash");

    /// <summary>Languages the model understands (the voice then reads in the same language).</summary>
    public static IReadOnlyList<SpeechLanguage> Languages { get; } =
    [
        new("fr", "Français"),
        new("en", "Anglais"),
        new("es", "Espagnol"),
        new("de", "Allemand"),
    ];

    /// <summary>A supported language code, French otherwise.</summary>
    public static string NormalizeLanguage(string? code)
    {
        var c = NeuralVoiceCatalog.NormalizeLanguage(code);
        return Languages.Any(l => l.Code == c) ? c : "fr";
    }
}
