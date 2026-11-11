using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace SpokaSoundBoard.Core;

/// <summary>
/// Turns an exception into a short French explanation for toasts and status lines. .NET's own messages are in
/// English, so they are only shown for exceptions raised by this app (whose messages are written in French) and
/// by Windows itself (<see cref="Win32Exception"/>, already localized).
/// </summary>
public static class FriendlyErrors
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    public static string Describe(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } agg) ex = agg.InnerExceptions[0];
        if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;

        switch (ex)
        {
            case HttpRequestException http:
                return http.StatusCode switch
                {
                    HttpStatusCode.NotFound => "introuvable sur le site (erreur 404)",
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "accès refusé par le site",
                    HttpStatusCode.TooManyRequests => "trop de demandes, réessayez dans un moment",
                    { } code when (int)code >= 500 => "le site ne répond pas correctement, réessayez plus tard",
                    { } code => $"le site a répondu avec l'erreur {(int)code}",
                    null => "connexion impossible, vérifiez votre accès à Internet",
                };
            case TaskCanceledException or TimeoutException:
                return "le délai d'attente est dépassé";
            case OperationCanceledException:
                return "opération annulée";
            case UnauthorizedAccessException:
                return "accès refusé au fichier ou au dossier";
            case FileNotFoundException fnf:
                return string.IsNullOrEmpty(fnf.FileName) ? "fichier introuvable" : $"fichier introuvable ({Path.GetFileName(fnf.FileName)})";
            case DirectoryNotFoundException:
                return "dossier introuvable";
            case PathTooLongException:
                return "chemin de fichier trop long";
            case IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation:
                return "le fichier est utilisé par un autre programme";
            case IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull:
                return "le disque est plein";
            case Win32Exception win32:
                return win32.Message; // localized by Windows
        }

        if (IsOwnException(ex)) return ex.Message;

        return ex switch
        {
            IOException => "erreur de lecture ou d'écriture du fichier",
            InvalidDataException or FormatException => "fichier illisible ou format non pris en charge",
            COMException => "le périphérique audio ou le composant Windows ne répond pas",
            OutOfMemoryException => "mémoire insuffisante",
            _ => $"erreur inattendue ({ex.GetType().Name})",
        };
    }

    /// <summary>Exception types declared by SpokaSoundBoard, or thrown from its code: their message is French.</summary>
    private static bool IsOwnException(Exception ex)
    {
        if (ex.GetType().Namespace?.StartsWith("SpokaSoundBoard", StringComparison.Ordinal) == true) return true;
        try
        {
            return ex.TargetSite?.DeclaringType?.Namespace?.StartsWith("SpokaSoundBoard", StringComparison.Ordinal) == true;
        }
        catch
        {
            return false;
        }
    }
}
