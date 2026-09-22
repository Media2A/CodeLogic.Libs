using System.Globalization;
using CodeLogic.Core.Results;

namespace CL.Storage.Errors;

/// <summary>Inspects storage errors for retry decisions and provider diagnostics.</summary>
public static class StorageErrorInfo
{
    /// <summary>Details key carrying a server-requested retry delay in milliseconds.</summary>
    public const string RetryAfterKey = "retryAfterMs";
    /// <summary>Details key carrying the FTP reply code.</summary>
    public const string FtpReplyKey = "ftpReply";
    /// <summary>Details key carrying the SFTP status code.</summary>
    public const string SftpStatusKey = "sftpStatus";
    /// <summary>Details key carrying the HTTP status code.</summary>
    public const string HttpStatusKey = "httpStatus";

    private static readonly HashSet<string> TransientCodes = new(StringComparer.Ordinal)
    {
        StorageErrors.TimeoutCode,
        StorageErrors.UnavailableCode,
        StorageErrors.ConnectionFailedCode,
        StorageErrors.ConnectionLostCode,
        StorageErrors.ServerBusyCode
    };

    /// <summary>Returns whether repeating the operation later may succeed without caller changes.</summary>
    /// <param name="error">Error to classify.</param>
    /// <returns><see langword="true"/> for timeouts, dropped or refused connections, and busy servers.</returns>
    public static bool IsTransient(Error? error) => error is not null && TransientCodes.Contains(error.Code);

    /// <summary>Returns whether the error means the connection itself is unusable and should be replaced.</summary>
    /// <param name="error">Error to classify.</param>
    /// <returns><see langword="true"/> for lost connections and TLS failures.</returns>
    public static bool IsConnectionFault(Error? error) =>
        error is not null && (error.Code == StorageErrors.ConnectionLostCode || error.Code == StorageErrors.TlsFailureCode);

    /// <summary>Reads a server-requested retry delay from the error details.</summary>
    /// <param name="error">Error to inspect.</param>
    /// <param name="delay">Receives the requested delay when present.</param>
    /// <returns><see langword="true"/> when the error carries a retry delay.</returns>
    public static bool TryGetRetryAfter(Error? error, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (!TryGetDetail(error, RetryAfterKey, out var raw) ||
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
            return false;
        delay = TimeSpan.FromMilliseconds(ms);
        return true;
    }

    /// <summary>Reads a <c>key=value</c> entry from the error details.</summary>
    /// <param name="error">Error to inspect.</param>
    /// <param name="key">Details key such as <see cref="FtpReplyKey"/>.</param>
    /// <param name="value">Receives the value when present.</param>
    /// <returns><see langword="true"/> when the key is present.</returns>
    public static bool TryGetDetail(Error? error, string key, out string value)
    {
        value = string.Empty;
        if (error?.Details is not { Length: > 0 } details)
            return false;
        foreach (var part in details.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && part.AsSpan(0, separator).Equals(key, StringComparison.Ordinal))
            {
                value = part[(separator + 1)..];
                return true;
            }
        }
        return false;
    }
}
