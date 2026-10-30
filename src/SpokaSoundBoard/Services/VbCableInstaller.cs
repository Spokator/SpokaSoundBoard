using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace SpokaSoundBoard.Services;

/// <summary>Downloads the official free VB-CABLE driver and launches its installer (requires admin).</summary>
public static class VbCableInstaller
{
    public const string HomePage = "https://vb-audio.com/Cable/";
    private const string PackageUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";

    public static async Task DownloadAndRunAsync(IProgress<string> progress)
    {
        var folder = Path.Combine(Path.GetTempPath(), "SpokaSoundBoard", "VBCABLE");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, "vbcable.zip");

        progress.Report("Téléchargement de VB-CABLE…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SpokaSoundBoard/1.0");
            await using var stream = await http.GetStreamAsync(PackageUrl);
            await using var file = File.Create(zip);
            await stream.CopyToAsync(file);
        }

        progress.Report("Extraction…");
        ZipFile.ExtractToDirectory(zip, folder, true);

        var setup = Path.Combine(folder, Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe");
        if (!File.Exists(setup)) throw new FileNotFoundException("Installateur VB-CABLE introuvable dans l'archive.");

        progress.Report("Cliquez sur « Install Driver » dans la fenêtre VB-CABLE, puis redémarrez le PC.");
        var process = Process.Start(new ProcessStartInfo(setup)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = folder,
        });
        if (process != null) await process.WaitForExitAsync();
    }

    public static void OpenHomePage() => OpenUrl(HomePage);

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
