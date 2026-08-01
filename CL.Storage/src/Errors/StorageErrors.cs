using CodeLogic.Core.Results;

namespace CL.Storage.Errors;

/// <summary>Creates storage errors with stable, provider-neutral codes.</summary>
public static class StorageErrors
{
    public const string InvalidPathCode = "storage.invalid_path";
    public const string NotFoundCode = "storage.not_found";
    public const string UnauthorizedCode = "storage.unauthorized";
    public const string TimeoutCode = "storage.timeout";
    public const string ConflictCode = "storage.conflict";
    public const string UnavailableCode = "storage.unavailable";
    public const string UnsupportedCode = "storage.unsupported";
    public const string TooLargeCode = "storage.too_large";
    public const string ProviderErrorCode = "storage.provider_error";

    public static Error InvalidPath(string message, string details = "") => Error.Validation(InvalidPathCode, message, details);
    public static Error NotFound(string message, string details = "") => Error.NotFound(NotFoundCode, message, details);
    public static Error Unauthorized(string message, string details = "") => Error.Unauthorized(UnauthorizedCode, message, details);
    public static Error Timeout(string message, string details = "") => Error.Timeout(TimeoutCode, message, details);
    public static Error Conflict(string message, string details = "") => Error.Conflict(ConflictCode, message, details);
    public static Error Unavailable(string message, string details = "") => Error.Unavailable(UnavailableCode, message, details);
    public static Error Unsupported(string message, string details = "") => Error.Validation(UnsupportedCode, message, details);
    public static Error TooLarge(string message, string details = "") => Error.Validation(TooLargeCode, message, details);
    public static Error ProviderError(string message, string details = "") => Error.Internal(ProviderErrorCode, message, details);

    public static Error FromException(Exception exception, string operation)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => NotFound($"{operation}: item was not found."),
            UnauthorizedAccessException => Unauthorized($"{operation}: access was denied."),
            TimeoutException => Timeout($"{operation}: operation timed out."),
            PathTooLongException or ArgumentException or NotSupportedException => InvalidPath($"{operation}: the path is invalid."),
            IOException => ProviderError($"{operation}: filesystem operation failed.", exception.Message),
            _ => ProviderError($"{operation}: provider operation failed.", exception.Message)
        };
    }
}
