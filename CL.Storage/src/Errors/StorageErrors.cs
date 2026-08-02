using CodeLogic.Core.Results;

namespace CL.Storage.Errors;

/// <summary>Creates storage errors with stable, provider-neutral codes.</summary>
public static class StorageErrors
{
    /// <summary>Stable code for invalid or escaping provider-neutral paths.</summary>
    public const string InvalidPathCode = "storage.invalid_path";
    /// <summary>Stable code for malformed content or checksum input.</summary>
    public const string InvalidContentCode = "storage.invalid_content";
    /// <summary>Stable code for missing files, directories, or mounted resources.</summary>
    public const string NotFoundCode = "storage.not_found";
    /// <summary>Stable code for failed provider authentication or authorization.</summary>
    public const string UnauthorizedCode = "storage.unauthorized";
    /// <summary>Stable code for an operation exceeding its provider timeout.</summary>
    public const string TimeoutCode = "storage.timeout";
    /// <summary>Stable code for destination, identity, or non-empty-directory conflicts.</summary>
    public const string ConflictCode = "storage.conflict";
    /// <summary>Stable code for temporarily unavailable storage.</summary>
    public const string UnavailableCode = "storage.unavailable";
    /// <summary>Stable code for behavior not implemented by the active connection.</summary>
    public const string UnsupportedCode = "storage.unsupported";
    /// <summary>Stable code for caller or provider size-limit violations.</summary>
    public const string TooLargeCode = "storage.too_large";
    /// <summary>Stable code for a mutation that completed only part of its commit or rollback.</summary>
    public const string PartialFailureCode = "storage.partial_failure";
    /// <summary>Stable code for sanitized provider failures that have no narrower mapping.</summary>
    public const string ProviderErrorCode = "storage.provider_error";

    /// <summary>Creates an invalid-path error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional sanitized diagnostics.</param>
    /// <returns>A validation error with <see cref="InvalidPathCode"/>.</returns>
    public static Error InvalidPath(string message, string details = "") => Error.Validation(InvalidPathCode, message, details);
    /// <summary>Creates an invalid-content error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional sanitized diagnostics.</param>
    /// <returns>A validation error with <see cref="InvalidContentCode"/>.</returns>
    public static Error InvalidContent(string message, string details = "") => Error.Validation(InvalidContentCode, message, details);
    /// <summary>Creates a not-found error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional sanitized diagnostics.</param>
    /// <returns>A not-found error with <see cref="NotFoundCode"/>.</returns>
    public static Error NotFound(string message, string details = "") => Error.NotFound(NotFoundCode, message, details);
    /// <summary>Creates an unauthorized error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional sanitized diagnostics.</param>
    /// <returns>An authorization error with <see cref="UnauthorizedCode"/>.</returns>
    public static Error Unauthorized(string message, string details = "") => Error.Unauthorized(UnauthorizedCode, message, details);
    /// <summary>Creates a timeout error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional sanitized diagnostics.</param>
    /// <returns>A timeout error with <see cref="TimeoutCode"/>.</returns>
    public static Error Timeout(string message, string details = "") => Error.Timeout(TimeoutCode, message, details);
    /// <summary>Creates a conflict error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional sanitized diagnostics.</param>
    /// <returns>A conflict error with <see cref="ConflictCode"/>.</returns>
    public static Error Conflict(string message, string details = "") => Error.Conflict(ConflictCode, message, details);
    /// <summary>Creates an unavailable-provider error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional sanitized diagnostics.</param>
    /// <returns>An unavailable error with <see cref="UnavailableCode"/>.</returns>
    public static Error Unavailable(string message, string details = "") => Error.Unavailable(UnavailableCode, message, details);
    /// <summary>Creates an unsupported-operation error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional sanitized diagnostics.</param>
    /// <returns>A validation error with <see cref="UnsupportedCode"/>.</returns>
    public static Error Unsupported(string message, string details = "") => Error.Validation(UnsupportedCode, message, details);
    /// <summary>Creates a size-limit error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional sanitized diagnostics.</param>
    /// <returns>A validation error with <see cref="TooLargeCode"/>.</returns>
    public static Error TooLarge(string message, string details = "") => Error.Validation(TooLargeCode, message, details);
    /// <summary>Creates an error for an incomplete mutation, cleanup, or rollback.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Sanitized state and provider-neutral error codes.</param>
    /// <returns>An internal error with <see cref="PartialFailureCode"/>.</returns>
    public static Error PartialFailure(string message, string details = "") => Error.Internal(PartialFailureCode, message, details);
    /// <summary>Creates a sanitized fallback provider error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional sanitized diagnostics.</param>
    /// <returns>An internal error with <see cref="ProviderErrorCode"/>.</returns>
    public static Error ProviderError(string message, string details = "") => Error.Internal(ProviderErrorCode, message, details);

    /// <summary>Maps common filesystem exceptions without exposing exception messages or secrets.</summary>
    /// <param name="exception">Exception to classify.</param>
    /// <param name="operation">Safe operation label included in the public message.</param>
    /// <returns>A provider-neutral sanitized error.</returns>
    public static Error FromException(Exception exception, string operation)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => NotFound($"{operation}: item was not found."),
            UnauthorizedAccessException => Unauthorized($"{operation}: access was denied."),
            TimeoutException => Timeout($"{operation}: operation timed out."),
            PathTooLongException or ArgumentException or NotSupportedException => InvalidPath($"{operation}: the path is invalid."),
            IOException => ProviderError($"{operation}: filesystem operation failed."),
            _ => ProviderError($"{operation}: provider operation failed.")
        };
    }
}
