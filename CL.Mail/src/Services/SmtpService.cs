using CL.Mail.Models;
using CodeLogic.Core.Logging;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CL.Mail.Services;

/// <summary>
/// Sends e-mail via SMTP using <see href="https://github.com/jstedfast/MailKit">MailKit</see>.
/// A new connection is opened per <see cref="SendAsync"/> call — no connection pooling.
/// </summary>
public sealed class SmtpService : IDisposable
{
    private readonly SmtpConfig _config;
    private readonly ILogger? _logger;
    private bool _disposed;

    /// <summary>Initialises the service with SMTP configuration.</summary>
    /// <param name="config">SMTP server settings.</param>
    /// <param name="logger">Optional logger.</param>
    public SmtpService(SmtpConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    /// <summary>
    /// Sends <paramref name="message"/> through the configured SMTP server.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="MailResult"/> carrying the server-assigned Message-ID on success.
    /// </returns>
    public async Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        var configCheck = ValidateConfiguration();
        if (!configCheck.IsSuccess) return configCheck;

        var msgCheck = ValidateMessage(message);
        if (!msgCheck.IsSuccess) return msgCheck;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mime = BuildMimeMessage(message);

            using var client = new SmtpClient { Timeout = _config.TimeoutSeconds * 1000 };
            var security = MapSecurity(_config.SecurityMode);

            await client.ConnectAsync(_config.Host, _config.Port, security, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(_config.Username))
                await client.AuthenticateAsync(_config.Username, _config.Password, cancellationToken).ConfigureAwait(false);

            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

            _logger?.Info($"Email sent to {string.Join(", ", message.To)} (id={mime.MessageId})");
            return MailResult.Success(mime.MessageId);
        }
        catch (SmtpCommandException ex)
        {
            _logger?.Error("SMTP command rejected", ex);
            return MailResult.Failure(MailError.SmtpRejected, ex.Message);
        }
        catch (SmtpProtocolException ex)
        {
            _logger?.Error("SMTP protocol error", ex);
            return MailResult.Failure(MailError.SmtpError, ex.Message);
        }
        catch (AuthenticationException ex)
        {
            _logger?.Error("SMTP authentication failed", ex);
            return MailResult.Failure(MailError.SmtpAuthenticationFailed, "Authentication failed");
        }
        catch (OperationCanceledException)
        {
            _logger?.Warning("Email send cancelled");
            return MailResult.Failure(MailError.Unknown, "Operation cancelled");
        }
        catch (TimeoutException)
        {
            _logger?.Error("SMTP connection timed out");
            return MailResult.Failure(MailError.SmtpTimeout, "SMTP timeout");
        }
        catch (Exception ex)
        {
            _logger?.Error("Unexpected error sending email", ex);
            return MailResult.Failure(MailError.Unknown, ex.Message);
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private MailResult ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_config.Host))
            return MailResult.Failure(MailError.SmtpConfigInvalid, "SMTP host not configured");

        if (_config.Port is < 1 or > 65535)
            return MailResult.Failure(MailError.SmtpConfigInvalid, "SMTP port is invalid");

        return MailResult.Success();
    }

    private static MailResult ValidateMessage(MailMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.From))
            return MailResult.Failure(MailError.InvalidSender, "Sender not specified");

        if (message.To is null || message.To.Count == 0)
            return MailResult.Failure(MailError.InvalidRecipient, "No recipients specified");

        if (string.IsNullOrWhiteSpace(message.Subject))
            return MailResult.Failure(MailError.InvalidSubject, "Subject not specified");

        if (string.IsNullOrWhiteSpace(message.TextBody) && string.IsNullOrWhiteSpace(message.HtmlBody))
            return MailResult.Failure(MailError.InvalidRecipient, "Message body is empty");

        return MailResult.Success();
    }

    // ── MimeMessage builder ───────────────────────────────────────────────────

    private static MimeMessage BuildMimeMessage(MailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.FromName ?? message.From, message.From));

        foreach (var to in message.To)  mime.To.Add(MailboxAddress.Parse(to));
        foreach (var cc in message.Cc)  mime.Cc.Add(MailboxAddress.Parse(cc));
        foreach (var bcc in message.Bcc) mime.Bcc.Add(MailboxAddress.Parse(bcc));

        mime.Subject = message.Subject;
        mime.Importance = message.Priority switch
        {
            MailPriority.Low  => MessageImportance.Low,
            MailPriority.High => MessageImportance.High,
            _                 => MessageImportance.Normal
        };

        foreach (var header in message.Headers)
            mime.Headers.Add(header.Key, header.Value);

        var builder = new BodyBuilder();
        if (!string.IsNullOrWhiteSpace(message.TextBody)) builder.TextBody = message.TextBody;
        if (!string.IsNullOrWhiteSpace(message.HtmlBody)) builder.HtmlBody = message.HtmlBody;

        foreach (var path in message.Attachments)
            if (File.Exists(path)) builder.Attachments.Add(path);

        mime.Body = builder.ToMessageBody();
        return mime;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SecureSocketOptions MapSecurity(MailSecurityMode mode) => mode switch
    {
        MailSecurityMode.None     => SecureSocketOptions.None,
        MailSecurityMode.StartTls => SecureSocketOptions.StartTls,
        MailSecurityMode.SslTls   => SecureSocketOptions.SslOnConnect,
        _                         => SecureSocketOptions.Auto
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
