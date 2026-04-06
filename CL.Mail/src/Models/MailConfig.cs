using CodeLogic.Core.Configuration;
using System.ComponentModel.DataAnnotations;

namespace CL.Mail.Models;

/// <summary>
/// Root configuration for <c>CL.Mail</c>.
/// Serialized to / from <c>config.mail.json</c> in the library's config directory.
/// </summary>
[ConfigSection("mail")]
public sealed class MailConfig : ConfigModelBase
{
    /// <summary>Whether the library is active. When false, all operations are no-ops.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// SMTP server settings used for sending mail.
    /// </summary>
    public SmtpConfig Smtp { get; set; } = new();

    /// <summary>
    /// IMAP server settings used for receiving mail.
    /// Null disables IMAP support entirely.
    /// </summary>
    public ImapConfig? Imap { get; set; }

    /// <summary>
    /// Default sender e-mail address used when a message does not specify one explicitly.
    /// </summary>
    public string? DefaultFromEmail { get; set; }

    /// <summary>
    /// Default sender display name used when a message does not specify one explicitly.
    /// </summary>
    public string? DefaultFromName { get; set; }

    /// <summary>
    /// Directory (relative to the library's data directory) where template files are stored.
    /// </summary>
    public string TemplateDirectory { get; set; } = "templates";

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (!Enabled) return ConfigValidationResult.Valid();

        if (string.IsNullOrWhiteSpace(Smtp.Host))
            errors.Add("Smtp.Host is required.");

        if (Smtp.Port is < 1 or > 65535)
            errors.Add("Smtp.Port must be between 1 and 65535.");

        if (Imap is not null)
        {
            if (string.IsNullOrWhiteSpace(Imap.Host))
                errors.Add("Imap.Host is required.");

            if (Imap.Port is < 1 or > 65535)
                errors.Add("Imap.Port must be between 1 and 65535.");
        }

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// SMTP server connection settings.
/// </summary>
public sealed class SmtpConfig
{
    /// <summary>SMTP server hostname or IP (e.g., "smtp.gmail.com").</summary>
    public string Host { get; set; } = "smtp.example.com";

    /// <summary>SMTP server port. Common values: 25 (plain), 465 (SSL), 587 (STARTTLS).</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary>SMTP authentication username.</summary>
    public string Username { get; set; } = "";

    /// <summary>SMTP authentication password or App Password.</summary>
    public string Password { get; set; } = "";

    /// <summary>Transport security mode.</summary>
    public MailSecurityMode SecurityMode { get; set; } = MailSecurityMode.StartTls;

    /// <summary>Socket-level operation timeout in seconds.</summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// IMAP server connection settings.
/// </summary>
public sealed class ImapConfig
{
    /// <summary>IMAP server hostname or IP (e.g., "imap.gmail.com").</summary>
    public string Host { get; set; } = "imap.example.com";

    /// <summary>IMAP server port. Common values: 143 (plain/STARTTLS), 993 (SSL).</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 993;

    /// <summary>IMAP authentication username.</summary>
    public string Username { get; set; } = "";

    /// <summary>IMAP authentication password or App Password.</summary>
    public string Password { get; set; } = "";

    /// <summary>Transport security mode.</summary>
    public MailSecurityMode SecurityMode { get; set; } = MailSecurityMode.SslTls;

    /// <summary>Socket-level operation timeout in seconds.</summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to enable RFC 2177 IDLE push notifications.
    /// Requires the IMAP server to advertise the IDLE capability.
    /// </summary>
    public bool EnableIdle { get; set; } = false;

    /// <summary>
    /// How often (minutes) to re-send the IDLE command to keep the connection alive.
    /// RFC 2177 recommends less than 29 minutes.
    /// </summary>
    [Range(1, 28)]
    public int IdleRefreshMinutes { get; set; } = 25;
}

/// <summary>
/// Transport security mode for SMTP and IMAP connections.
/// </summary>
public enum MailSecurityMode
{
    /// <summary>Unencrypted connection (not recommended).</summary>
    None,

    /// <summary>Upgrade to TLS in-band using the STARTTLS command.</summary>
    StartTls,

    /// <summary>Connect over an implicit TLS/SSL socket from the start.</summary>
    SslTls
}
