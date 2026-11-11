using System.Runtime.InteropServices;

namespace SpokaSoundBoard.Core;

/// <summary>How the application was installed: classic installer (Inno Setup) or Microsoft Store package (MSIX).</summary>
public static class AppEnvironment
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly Lazy<bool> Packaged = new(DetectPackage);

    /// <summary>
    /// True when running from an MSIX package (Microsoft Store). In that case the Store handles updates and
    /// autostart goes through a StartupTask; no executable code may be downloaded at runtime.
    /// </summary>
    public static bool IsPackaged => ForcePackaged ?? Packaged.Value;

    /// <summary>Test hook / debugging aid (environment variable SPOKA_FORCE_PACKAGED=1 sets it at startup).</summary>
    public static bool? ForcePackaged { get; set; } =
        Environment.GetEnvironmentVariable("SPOKA_FORCE_PACKAGED") == "1" ? true : null;

    /// <summary>
    /// Writable data folder: %AppData%\SpokaSoundBoard, or SPOKA_DATA_DIR when set (development / tests, so a
    /// debug build never touches the real soundboard and library).
    /// </summary>
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("SPOKA_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpokaSoundBoard");

    /// <summary>
    /// Suffix for machine-wide names (single-instance mutex, show event): empty for the real data folder, a stable
    /// hash of the folder when SPOKA_DATA_DIR is set, so a development build can run next to the installed app.
    /// </summary>
    public static string InstanceSuffix => InstanceSuffixFor(Environment.GetEnvironmentVariable("SPOKA_DATA_DIR"));

    public static string InstanceSuffixFor(string? customDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(customDataDirectory)) return "";
        var normalized = Path.GetFullPath(customDataDirectory).TrimEnd('\\', '/').ToUpperInvariant();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return "." + Convert.ToHexString(hash, 0, 6);
    }

    /// <summary>Folder of the running executable (read-only when packaged).</summary>
    public static string InstallDirectory => AppContext.BaseDirectory;

    /// <summary>"2.2.1": version of the running app, for HTTP User-Agent headers.</summary>
    public static string UserAgentVersion { get; } =
        (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(2, 0)).ToString(3);

    /// <summary>Tools shipped with the app (yt-dlp, ffmpeg): installer "tools" folder or package "tools" folder.</summary>
    public static string BundledToolsDirectory => Path.Combine(InstallDirectory, "tools");

    private static bool DetectPackage()
    {
        try
        {
            var length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return result != AppModelErrorNoPackage && (result == ErrorInsufficientBuffer || result == 0);
        }
        catch
        {
            // Windows < 8: never packaged.
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
