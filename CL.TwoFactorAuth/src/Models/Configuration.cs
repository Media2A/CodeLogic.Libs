using CodeLogic.Core.Configuration;

namespace CL.TwoFactorAuth.Models;

/// <summary>
/// Root configuration model for the CL.TwoFactorAuth library.
/// Serialized as <c>config.twofactorauth.json</c> in the library's config directory.
/// </summary>
[ConfigSection("twofactorauth")]
public class TwoFactorAuthConfig : ConfigModelBase
{
    /// <summary>Whether the TwoFactorAuth library is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Time step in seconds for TOTP generation. Default: 30.</summary>
    public int TimeStepSeconds { get; set; } = 30;

    /// <summary>
    /// Validation window size (number of time steps to check before and after current).
    /// Default: 1 (allows ±30 seconds of clock drift).
    /// </summary>
    public int WindowSize { get; set; } = 1;

    /// <summary>Pixel size of each QR code module (square). Default: 20.</summary>
    public int QrCodeModuleSize { get; set; } = 20;

    /// <summary>Error correction level for generated QR codes. Default: Q.</summary>
    public QrErrorCorrectionLevel ErrorCorrectionLevel { get; set; } = QrErrorCorrectionLevel.Q;

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (TimeStepSeconds <= 0)
            errors.Add("TimeStepSeconds must be greater than 0");

        if (WindowSize < 0)
            errors.Add("WindowSize must be 0 or greater");

        if (QrCodeModuleSize <= 0)
            errors.Add("QrCodeModuleSize must be greater than 0");

        return errors.Any()
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// QR code error correction level. Higher levels allow more damage before the code becomes unreadable,
/// but also increase the QR code size.
/// </summary>
public enum QrErrorCorrectionLevel
{
    /// <summary>Low — ~7% of codewords can be restored.</summary>
    L,
    /// <summary>Medium — ~15% of codewords can be restored.</summary>
    M,
    /// <summary>Quartile — ~25% of codewords can be restored.</summary>
    Q,
    /// <summary>High — ~30% of codewords can be restored.</summary>
    H
}

/// <summary>
/// Represents the result of a TOTP validation attempt.
/// </summary>
public record TotpValidationResult
{
    /// <summary>Whether the provided TOTP code was valid.</summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// The time window offset that matched, relative to the current window.
    /// <c>0</c> means the current window matched; negative/positive values indicate past/future windows.
    /// <c>null</c> when validation failed.
    /// </summary>
    public int? MatchedWindow { get; init; }

    /// <summary>Human-readable error message when <see cref="IsValid"/> is <c>false</c>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a successful validation result.</summary>
    /// <param name="window">Optional matched time window offset.</param>
    public static TotpValidationResult Valid(int? window = null) => new()
    {
        IsValid = true,
        MatchedWindow = window
    };

    /// <summary>Creates a failed validation result with an optional error message.</summary>
    /// <param name="msg">Human-readable reason for failure.</param>
    public static TotpValidationResult Invalid(string msg = "Invalid code") => new()
    {
        IsValid = false,
        ErrorMessage = msg
    };
}
