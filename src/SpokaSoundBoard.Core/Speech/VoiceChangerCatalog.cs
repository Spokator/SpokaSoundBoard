namespace SpokaSoundBoard.Core.Speech;

/// <summary>A voice of the voice changer: the same characters as the natural text-to-speech voices.</summary>
public sealed record VoiceChangerVoice(string Key, string Name, bool IsFemale, string Description)
{
    public string FileName => $"voice_{Key}.bin";
}

/// <summary>
/// Models of the real-time AI voice changer (Seed-VC, run by the separate GPL-3.0 program SpokaVoiceEngine). Data
/// files only, hosted on the project's GitHub (« voice-models-1 » pre-release) and pinned by SHA-256.
/// </summary>
public static class VoiceChangerCatalog
{
    private const string Release = "https://github.com/Spokator/SpokaSoundBoard/releases/download/voice-models-1";

    public static readonly VoiceModelPack SeedVc = new(
        Id: "voicechanger1",
        Name: "Seed-VC temps réel",
        Kind: NeuralModelKind.SeedVc,
        BaseUrl: Release,
        Files:
        [
            new("source.json", 648, "70173976b0708ce90194b885e010aa2502f9e322b883405c6edb789d1a91b5a8"),
            new("regulator.onnx", 4650411, "72a69e68060f769aeb3ca34fe3f716c02334a514fe0e8937ce054806e4abc38b"),
            new("f0.onnx", 6553276, "e4e4cba5631046f9e7eea4d2e70913ad9648ca621568ff9137440bb579c9f473"),
            new("hift.onnx", 34528991, "36dafc3851ee7b81c3f90cdb3fca43f2eb3f54c1bbaee736dec55bbe1c2ebab3"),
            new("dit.onnx", 41326007, "294cf0296e094fbbca1b0b029069b8a6c1b3a8a48689128dc017d2d9abdd1a40"),
            new("content.onnx", 329019241, "df30bdbfd896164551cd67f69b0782fbadb3c1ad04b7ebc656fc58cbce07ca81"),
            new("voice_F1.bin", 479628, "43da3e5b685b7f58146468f7890ffed7b93ee43d78f2b388064d891152d6a35c"),
            new("voice_F2.bin", 479628, "cc2871a3c2e2e931a8ba2be1ebac0189d1c1fd36d87e09634da36dbc901a6847"),
            new("voice_F3.bin", 479628, "4543add75b9421d953b81bbced8f4dd233ccc0caae5eba60d05aa7c4f2c5463d"),
            new("voice_F4.bin", 479628, "335c8f618917c8d51d5bd5ec9e2366f3beddf9b359fde3103a5f63e08dc3729c"),
            new("voice_F5.bin", 479628, "e5e010eec1bcfda0204793876eba328f5053df6e3df5fb932cc07329983fc425"),
            new("voice_M1.bin", 479628, "0863d4315f515e484ff0891caf3bbb700fa5f2fdffbe0b08a275f48dab1bbc59"),
            new("voice_M2.bin", 479628, "9b9df8ebe40e91e4c5eedd7c0bad399d5f87b7aa5903a8eed9ff486017fbd709"),
            new("voice_M3.bin", 479628, "78473111157bd9d73a44b07e7f783eec0c6d07dbe33d7a0f9ebbef3034bd5582"),
            new("voice_M4.bin", 479628, "818eb809de6a1c157e7959807b2e16757fdeaa5d249c57aa6c7472d9e163e234"),
            new("voice_M5.bin", 479628, "8813caa4f05d33f03364952cd58e47502bf69cff8feb2356ba9cfe342fb968b2"),
        ],
        License: "GPL-3.0 (Seed-VC), Apache-2.0 (XLS-R, HiFT), OpenRAIL-M (voix)",
        LicenseUrl: "https://github.com/Spokator/SpokaSoundBoard/blob/main/engine/README.md",
        SourceUrl: "https://github.com/Plachtaa/seed-vc");

    /// <summary>The ten voices, named and described like the natural voices they come from.</summary>
    public static IReadOnlyList<VoiceChangerVoice> Voices { get; } =
        NeuralVoiceCatalog.Voices.Select(v => new VoiceChangerVoice(v.Key, v.Name, v.IsFemale, v.Description)).ToList();

    public const string DefaultVoiceKey = "M5";

    public static VoiceChangerVoice FindVoice(string? key) =>
        Voices.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? Voices.First(v => v.Key == DefaultVoiceKey);
}
