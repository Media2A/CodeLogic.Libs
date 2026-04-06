namespace CL.SocialConnect.Models;

/// <summary>
/// Categorizes errors returned by SocialConnect services.
/// Use these values as error codes within <c>Error</c> objects returned in <c>Result</c> failures.
/// </summary>
public enum SocialError
{
    /// <summary>An HTTP or socket-level network error occurred.</summary>
    NetworkError,

    /// <summary>The API returned an unexpected or malformed response.</summary>
    InvalidResponse,

    /// <summary>The webhook delivery failed (non-2xx HTTP status).</summary>
    WebhookDeliveryFailed,

    /// <summary>The requested Steam user was not found.</summary>
    UserNotFound,

    /// <summary>Steam authentication failed or the ticket was invalid.</summary>
    AuthenticationFailed,

    /// <summary>The Steam Web API returned an error status.</summary>
    ApiError,

    /// <summary>A required configuration value is missing or invalid.</summary>
    ConfigurationError,

    /// <summary>The operation exceeded the configured timeout.</summary>
    Timeout,

    /// <summary>JSON serialization or deserialization failed.</summary>
    SerializationError
}
