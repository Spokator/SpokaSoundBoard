using System.Net;
using System.Security.Cryptography;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Core.Speech;

/// <summary>Bytes downloaded so far for a voice pack.</summary>
public sealed record VoiceDownloadProgress(long Done, long Total)
{
    public double Percent => Total > 0 ? Math.Clamp(Done * 100.0 / Total, 0, 100) : 0;
}

/// <summary>
/// Downloaded neural voice models, one folder per pack under <c>DataDirectory\voices</c>. Only model data is
/// downloaded (allowed in the Store edition); every file is checked against the size and SHA-256 recorded in
/// <see cref="NeuralVoiceCatalog"/>. A pack is downloaded into a staging folder and renamed when complete, so a
/// half-downloaded pack is never loaded; verified files of a cancelled download are kept and not fetched again.
/// </summary>
public sealed class VoiceModelStore
{
    public const string FolderName = "voices";
    private const string StagingSuffix = ".partial";
    private const int Attempts = 3;

    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // Per-read inactivity is handled below; the whole pack can take minutes on a slow line.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(OnlineHttp.AppUserAgent);
        return client;
    });

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public VoiceModelStore(string rootDirectory, HttpClient? http = null)
    {
        RootDirectory = rootDirectory;
        _http = http ?? SharedClient.Value;
    }

    /// <summary>The "voices" folder (created on first download).</summary>
    public string RootDirectory { get; }

    /// <summary>Stall timeout of one read (no byte received for that long = network error).</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised (on any thread) after a pack was installed or deleted.</summary>
    public event Action<VoiceModelPack>? Changed;

    public string PackDirectory(VoiceModelPack pack) => Path.Combine(RootDirectory, SafeId(pack.Id));

    /// <summary>Full path of one model file of an installed pack.</summary>
    public string FilePath(VoiceModelPack pack, string fileName)
    {
        if (!pack.Files.Any(f => f.Name == fileName))
            throw new ArgumentException($"« {fileName} » ne fait pas partie de {pack.Name}.", nameof(fileName));
        return Path.Combine(PackDirectory(pack), fileName);
    }

    /// <summary>True when every file of the pack is present with its expected size (hashes were checked at download).</summary>
    public bool IsInstalled(VoiceModelPack pack)
    {
        try
        {
            var dir = PackDirectory(pack);
            return Directory.Exists(dir) && pack.Files.All(f =>
            {
                var info = new FileInfo(Path.Combine(dir, f.Name));
                return info.Exists && info.Length == f.Size;
            });
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Downloads (or completes) a pack. Throws <see cref="DownloadException"/> with a French message on failure.</summary>
    public async Task InstallAsync(VoiceModelPack pack, IProgress<VoiceDownloadProgress>? progress, CancellationToken ct)
    {
        await _installGate.WaitAsync(ct);
        try
        {
            if (IsInstalled(pack))
            {
                progress?.Report(new VoiceDownloadProgress(pack.TotalSize, pack.TotalSize));
                return;
            }

            var target = PackDirectory(pack);
            var staging = target + StagingSuffix;
            Directory.CreateDirectory(staging);
            // A pack folder that is incomplete (older or damaged install) is resumed from too.
            if (Directory.Exists(target)) MoveVerifiedFiles(pack, target, staging);
            EnsureFreeSpace(pack, staging);

            long done = 0;
            foreach (var file in pack.Files)
            {
                var path = Path.Combine(staging, file.Name);
                if (IsVerified(path, file))
                {
                    done += file.Size;
                    progress?.Report(new VoiceDownloadProgress(done, pack.TotalSize));
                    continue;
                }
                var before = done;
                await DownloadFileAsync(pack, file, path, d => progress?.Report(new VoiceDownloadProgress(before + d, pack.TotalSize)), ct);
                done += file.Size;
            }

            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            progress?.Report(new VoiceDownloadProgress(pack.TotalSize, pack.TotalSize));
        }
        catch (IOException ex) when (!ct.IsCancellationRequested)
        {
            throw new DownloadException($"Écriture impossible des fichiers de la voix ({ex.Message}).");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DownloadException($"Accès refusé au dossier des voix ({ex.Message}).");
        }
        finally
        {
            _installGate.Release();
        }
        Changed?.Invoke(pack);
    }

    /// <summary>Removes an installed pack and any unfinished download of it. The engine must have unloaded it first.</summary>
    public void Delete(VoiceModelPack pack)
    {
        var target = PackDirectory(pack);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        var staging = target + StagingSuffix;
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Changed?.Invoke(pack);
    }

    private async Task DownloadFileAsync(VoiceModelPack pack, VoiceModelFile file, string path, Action<long> progress, CancellationToken ct)
    {
        var temp = path + ".part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(pack.UrlOf(file), file, temp, progress, ct);
                File.Move(temp, path, true);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < Attempts && ex is HttpRequestException or TimeoutException)
            {
                TryDelete(temp);
                progress(0);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
            catch (HttpRequestException ex)
            {
                TryDelete(temp);
                throw new DownloadException($"Téléchargement de la voix impossible ({ex.Message}). Vérifiez votre connexion Internet.");
            }
            catch (TimeoutException)
            {
                TryDelete(temp);
                throw new DownloadException("Le serveur des voix ne répond plus (délai dépassé). Réessayez dans un instant.");
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    private async Task DownloadOnceAsync(Uri url, VoiceModelFile file, string temp, Action<long> progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            // 5xx and 429 are worth a retry; anything else (404, 403…) means the pinned file is gone.
            if (code >= 500 || code == 429) throw new HttpRequestException($"erreur {code}");
            throw new DownloadException($"Le fichier « {file.Name} » de la voix est introuvable sur le serveur (erreur {code}).");
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        var lastReport = Environment.TickCount64;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(ReadTimeout);
                int n;
                try
                {
                    n = await source.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException();
                }
                catch (IOException ex)
                {
                    // Connection reset in the middle of the body: a network error (retried), not a disk error.
                    throw new HttpRequestException(ex.Message, ex);
                }
                if (n == 0) break;
                done += n;
                if (done > file.Size) throw new DownloadException($"Le fichier « {file.Name} » de la voix ne correspond pas à la version attendue.");
                sha.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                var now = Environment.TickCount64;
                if (now - lastReport >= 100)
                {
                    lastReport = now;
                    progress(done);
                }
            }
        }
        progress(done);

        if (done != file.Size)
            throw new HttpRequestException($"téléchargement interrompu à {done} / {file.Size} octets");
        var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new DownloadException($"Le fichier « {file.Name} » de la voix est corrompu (empreinte SHA-256 inattendue).");
    }

    /// <summary>Full SHA-256 check of an already present file (staging files from a cancelled download).</summary>
    internal static bool IsVerified(string path, VoiceModelFile file)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size) return false;
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void MoveVerifiedFiles(VoiceModelPack pack, string from, string to)
    {
        foreach (var file in pack.Files)
        {
            var source = Path.Combine(from, file.Name);
            var destination = Path.Combine(to, file.Name);
            if (!File.Exists(destination) && IsVerified(source, file)) File.Move(source, destination);
        }
        Directory.Delete(from, true);
    }

    private static void EnsureFreeSpace(VoiceModelPack pack, string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            var needed = pack.TotalSize + 50L * 1024 * 1024;
            if (free < needed)
                throw new DownloadException($"Espace disque insuffisant : il faut {FormatSize(needed)} libres pour {pack.Name}.");
        }
        catch (DownloadException)
        {
            throw;
        }
        catch
        {
            // Network drives, unusual roots: let the download try.
        }
    }

    /// <summary>"145 Mo" (French units).</summary>
    public static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} Go"
        : bytes >= 1024 * 1024 ? $"{Math.Round(bytes / (1024.0 * 1024))} Mo"
        : $"{Math.Max(1, (long)Math.Round(bytes / 1024.0))} Ko";

    private static string SafeId(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        if (id.Length == 0 || id.Any(c => invalid.Contains(c)) || id is "." or "..")
            throw new ArgumentException($"Identifiant de voix invalide : {id}", nameof(id));
        return id;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
