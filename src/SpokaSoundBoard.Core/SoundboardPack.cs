using System.IO.Compression;
using System.Text.Json;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core;

/// <summary>
/// ".spokapack" files: a zip with the soundboard (sounds, colors, volumes, excerpts) and custom voice presets,
/// to share a soundboard with friends. Hotkeys are not exported (they are personal).
/// </summary>
public static class SoundboardPack
{
    public const string Extension = ".spokapack";
    public const string DialogFilter = "Pack SpokaSoundBoard (*.spokapack)|*.spokapack";
    private const string ManifestName = "pack.json";

    private sealed record PackPad(string Name, string File, float Volume, string Color, bool Loop, double StartSeconds,
        double EndSeconds, string? SourceUrl, double? NormalizationDb);

    private sealed record PackManifest(int Version, string Name, List<PackPad> Pads, List<EffectPreset> Presets);

    public static int Export(string target, string name, IEnumerable<SoundPad> pads, IEnumerable<EffectPreset> presets)
    {
        if (File.Exists(target)) File.Delete(target);
        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<PackPad>();
        foreach (var pad in pads)
        {
            if (!File.Exists(pad.FilePath)) continue;
            var fileName = UniqueName(used, Path.GetFileName(pad.FilePath));
            zip.CreateEntryFromFile(pad.FilePath, "sounds/" + fileName, CompressionLevel.Fastest);
            entries.Add(new PackPad(pad.Name, fileName, pad.Volume, pad.Color, pad.Loop, pad.StartSeconds, pad.EndSeconds,
                pad.SourceUrl, pad.NormalizationDb));
        }

        var manifest = new PackManifest(1, name, entries, presets.ToList());
        var entry = zip.CreateEntry(ManifestName);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions { WriteIndented = true });
        return entries.Count;
    }

    /// <summary>Extracts the pack into <paramref name="libraryDirectory"/> and returns new pads and presets.</summary>
    public static (List<SoundPad> Pads, List<EffectPreset> Presets) Import(string packPath, string libraryDirectory)
    {
        using var zip = ZipFile.OpenRead(packPath);
        var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("Ce fichier n'est pas un pack SpokaSoundBoard.");
        PackManifest manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<PackManifest>(stream) ?? throw new InvalidDataException("Pack illisible.");

        var folderName = SafeName(string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileNameWithoutExtension(packPath) : manifest.Name);
        var folder = Path.Combine(libraryDirectory, folderName);
        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;

        var pads = new List<SoundPad>();
        foreach (var p in manifest.Pads)
        {
            var entry = zip.GetEntry("sounds/" + p.File);
            if (entry == null) continue;
            var target = Path.GetFullPath(Path.Combine(folder, Path.GetFileName(p.File)));
            // Never write outside the pack folder (zip-slip protection).
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            entry.ExtractToFile(target, true);
            pads.Add(new SoundPad
            {
                Name = p.Name,
                FilePath = target,
                Volume = Math.Clamp(p.Volume, 0f, 2f),
                Color = p.Color,
                Loop = p.Loop,
                StartSeconds = Math.Max(0, p.StartSeconds),
                EndSeconds = Math.Max(0, p.EndSeconds),
                SourceUrl = p.SourceUrl,
                NormalizationDb = p.NormalizationDb,
            });
        }

        foreach (var preset in manifest.Presets) preset.IsBuiltIn = false;
        return (pads, manifest.Presets);
    }

    private static string UniqueName(HashSet<string> used, string name)
    {
        var candidate = name;
        var i = 2;
        while (!used.Add(candidate))
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({i++}){Path.GetExtension(name)}";
        return candidate;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Pack" : clean;
    }
}
