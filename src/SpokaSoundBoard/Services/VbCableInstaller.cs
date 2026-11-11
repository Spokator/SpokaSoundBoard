using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using SpokaSoundBoard.Core;

namespace SpokaSoundBoard.Services;

/// <summary>
/// Downloads the official free VB-CABLE driver and launches its installer (requires admin).
/// Store edition: never downloads or runs an installer (Store policy, see docs/STORE.md); the official download page
/// is opened instead and the user installs the driver by hand.
/// </summary>
public static class VbCableInstaller
{
    public const string HomePage = "https://vb-audio.com/Cable/";
    private const string PackageUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";

    /// <summary>False in the Store edition: the official page is opened instead of running the installer.</summary>
    public static bool CanInstallAutomatically => !AppEnvironment.IsPackaged;

    public const string ManualVbCableSteps =
        "Page VB-Audio ouverte : téléchargez « VBCABLE_Driver_Pack », extrayez le .zip, clic droit sur « VBCABLE_Setup_x64.exe » → « Exécuter en tant qu'administrateur » → « Install Driver », puis redémarrez le PC.";

    public const string ManualVacSteps =
        "Page Virtual Audio Cable ouverte : téléchargez « Virtual Audio Cable Lite », extrayez le .zip, lancez « setup64.exe » → « Install », puis redémarrez le PC si demandé.";

    public static async Task DownloadAndRunAsync(IProgress<string> progress)
    {
        if (!CanInstallAutomatically)
        {
            OpenUrl(HomePage);
            progress.Report(ManualVbCableSteps);
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "SpokaSoundBoard", "VBCABLE");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, "vbcable.zip");

        progress.Report("Téléchargement de VB-CABLE…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SpokaSoundBoard/" + AppEnvironment.UserAgentVersion);
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

    public const string VacHomePage = "https://vac.muzychenko.net/en/download.htm";
    private const string VacLiteUrl = "https://software.muzychenko.net/freeware/vac471lite.zip";

    /// <summary>
    /// Virtual Audio Cable Lite: free for home use, one cable named "Line 1". It coexists with VB-CABLE and
    /// gives a second, independent virtual microphone for Discord.
    /// </summary>
    public static async Task DownloadAndRunVacLiteAsync(IProgress<string> progress)
    {
        if (!CanInstallAutomatically)
        {
            OpenUrl(VacHomePage);
            progress.Report(ManualVacSteps);
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "SpokaSoundBoard", "VACLite");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, "vaclite.zip");

        progress.Report("Téléchargement de Virtual Audio Cable Lite…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SpokaSoundBoard/" + AppEnvironment.UserAgentVersion);
            await using var stream = await http.GetStreamAsync(VacLiteUrl);
            await using var file = File.Create(zip);
            await stream.CopyToAsync(file);
        }

        progress.Report("Extraction…");
        ZipFile.ExtractToDirectory(zip, folder, true);

        var setup = Path.Combine(folder, Environment.Is64BitOperatingSystem ? "setup64.exe" : "setup.exe");
        if (!File.Exists(setup)) setup = Path.Combine(folder, "setup.exe");
        if (!File.Exists(setup)) throw new FileNotFoundException("Installateur Virtual Audio Cable introuvable dans l'archive.");

        progress.Report("Suivez l'installation (« Install »), puis redémarrez le PC si demandé.");
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
