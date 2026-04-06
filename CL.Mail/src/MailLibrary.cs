using CL.Mail.Models;
using CL.Mail.Services;
using CodeLogic.Framework.Libraries;

namespace CL.Mail;

/// <summary>
/// <b>CL.Mail</b> — CodeLogic library providing SMTP/IMAP e-mail functionality
/// with a built-in variable-substitution template engine.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — registers <see cref="MailConfig"/> (→ config.mail.json).</description></item>
///   <item><description><b>Initialize</b> — loads config, creates <see cref="SmtpService"/>,
///     optional <see cref="ImapService"/>, and the template system (provider + engine).</description></item>
///   <item><description><b>Start</b> — no-op (connections are opened on demand).</description></item>
///   <item><description><b>Stop</b> — disconnects IMAP if connected and disposes services.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class MailLibrary : ILibrary
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.Mail",
        Name = "Mail Library",
        Version = "1.0.0",
        Description = "SMTP/IMAP e-mail with variable-substitution template engine",
        Author = "Media2A",
        Tags = ["email", "smtp", "imap", "templates"]
    };

    private LibraryContext? _context;
    private SmtpService? _smtp;
    private ImapService? _imap;
    private IMailTemplateProvider? _templateProvider;
    private IMailTemplateEngine? _templateEngine;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");
        context.Configuration.Register<MailConfig>("mail");
        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        var config = context.Configuration.Get<MailConfig>();

        if (!config.Enabled)
        {
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
            return Task.CompletedTask;
        }

        // Validate before building services
        var validation = config.Validate();
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Errors);
            throw new InvalidOperationException($"{Manifest.Name} configuration is invalid: {errors}");
        }

        // SMTP (always required when enabled)
        _smtp = new SmtpService(config.Smtp, context.Logger);
        context.Logger.Info($"SMTP: {config.Smtp.Host}:{config.Smtp.Port} ({config.Smtp.SecurityMode})");

        // Template system
        var templateDir = string.IsNullOrWhiteSpace(config.TemplateDirectory)
            ? Path.Combine(context.DataDirectory, "templates")
            : Path.IsPathRooted(config.TemplateDirectory)
                ? config.TemplateDirectory
                : Path.Combine(context.DataDirectory, config.TemplateDirectory);

        _templateProvider = new FileMailTemplateProvider(templateDir, context.Logger);
        _templateEngine = new SimpleTemplateEngine(_templateProvider, context.Logger);
        context.Logger.Info($"Template directory: {templateDir}");

        // IMAP (optional)
        if (config.Imap is not null)
        {
            _imap = new ImapService(config.Imap, context.Logger);
            context.Logger.Info($"IMAP: {config.Imap.Host}:{config.Imap.Port} ({config.Imap.SecurityMode})");
        }

        context.Logger.Info($"{Manifest.Name} initialized");
        return Task.CompletedTask;
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"{Manifest.Name} started");
        return Task.CompletedTask;
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        if (_imap is not null)
        {
            await _imap.DisconnectAsync().ConfigureAwait(false);
            _imap.Dispose();
            _imap = null;
        }

        _smtp?.Dispose();
        _smtp = null;
        _templateProvider = null;
        _templateEngine = null;

        _context?.Logger.Info($"{Manifest.Name} stopped");
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<HealthStatus> HealthCheckAsync()
    {
        var config = _context?.Configuration.Get<MailConfig>();

        if (config is null || !config.Enabled)
            return Task.FromResult(HealthStatus.Healthy("Mail library is disabled"));

        if (_smtp is null || _templateProvider is null)
            return Task.FromResult(HealthStatus.Unhealthy("Mail library not initialized"));

        var imapInfo = config.Imap is not null ? $", IMAP: {config.Imap.Host}:{config.Imap.Port}" : "";
        var msg = $"SMTP: {config.Smtp.Host}:{config.Smtp.Port}{imapInfo}";

        return Task.FromResult(
            string.IsNullOrWhiteSpace(config.Smtp.Host)
                ? HealthStatus.Degraded("SMTP host not configured")
                : HealthStatus.Healthy(msg));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="SmtpService"/> for sending e-mail.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the library is not initialized or disabled.</exception>
    public SmtpService Smtp =>
        _smtp ?? throw new InvalidOperationException($"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>
    /// Returns the <see cref="ImapService"/> for reading e-mail.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when IMAP is not configured or the library is not initialized.
    /// </exception>
    public ImapService Imap =>
        _imap ?? throw new InvalidOperationException(
            "IMAP is not configured. Set the Imap section in config.mail.json.");

    /// <summary>Returns true when IMAP is configured and available.</summary>
    public bool HasImap => _imap is not null;

    /// <summary>Returns the template provider for loading / saving templates.</summary>
    public IMailTemplateProvider TemplateProvider =>
        _templateProvider ?? throw new InvalidOperationException($"{Manifest.Name} has not been initialized.");

    /// <summary>Returns the template engine for rendering templates.</summary>
    public IMailTemplateEngine TemplateEngine =>
        _templateEngine ?? throw new InvalidOperationException($"{Manifest.Name} has not been initialized.");

    /// <summary>Creates a new fluent <see cref="MailBuilder"/> for composing messages.</summary>
    public MailBuilder CreateMessage() => new();

    /// <summary>
    /// Convenience method: renders a template by ID and sends the result via SMTP.
    /// </summary>
    /// <param name="templateId">Template identifier.</param>
    /// <param name="variables">Template variable values.</param>
    /// <param name="configure">Callback to configure the mail (From, To, etc.) on the builder.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<MailResult> SendTemplatedAsync(
        string templateId,
        Dictionary<string, object?> variables,
        Action<MailBuilder> configure,
        CancellationToken cancellationToken = default)
    {
        var loadResult = await TemplateProvider.LoadTemplateAsync(templateId, cancellationToken).ConfigureAwait(false);
        if (!loadResult.IsSuccess)
            return MailResult.Failure(loadResult.Error, loadResult.ErrorMessage);

        var renderResult = await TemplateEngine.RenderAsync(loadResult.Value!, variables, cancellationToken).ConfigureAwait(false);
        if (!renderResult.IsSuccess)
            return MailResult.Failure(renderResult.Error, renderResult.ErrorMessage);

        var rendered = renderResult.Value!;
        var builder = new MailBuilder().Subject(rendered.Subject);
        if (rendered.TextBody is not null) builder.TextBody(rendered.TextBody);
        if (rendered.HtmlBody is not null) builder.HtmlBody(rendered.HtmlBody);

        configure(builder);

        var message = builder.Build();
        return await Smtp.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _imap?.Dispose();
        _imap = null;
        _smtp?.Dispose();
        _smtp = null;
        _templateProvider = null;
        _templateEngine = null;
    }
}
