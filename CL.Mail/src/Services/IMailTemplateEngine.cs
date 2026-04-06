using CL.Mail.Models;

namespace CL.Mail.Services;

/// <summary>
/// Renders a <see cref="MailTemplate"/> by substituting variables, processing
/// conditionals and loops, and applying optional layout templates.
/// </summary>
public interface IMailTemplateEngine
{
    /// <summary>Display name of this engine (e.g., "Simple", "Razor").</summary>
    string Name { get; }

    /// <summary>
    /// Renders the template and returns a fully substituted <see cref="RenderedTemplate"/>.
    /// </summary>
    /// <param name="template">The template to render.</param>
    /// <param name="variables">Key/value pairs injected into the template.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MailResult<RenderedTemplate>> RenderAsync(
        MailTemplate template,
        Dictionary<string, object?> variables,
        CancellationToken cancellationToken = default);
}
