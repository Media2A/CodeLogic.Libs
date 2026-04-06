using CL.Mail.Models;

namespace CL.Mail.Services;

/// <summary>
/// Loads <see cref="MailTemplate"/> objects from a backing store (file system, database, etc.).
/// </summary>
public interface IMailTemplateProvider
{
    /// <summary>
    /// Loads a single template by its <see cref="MailTemplate.Id"/>.
    /// </summary>
    /// <param name="templateId">The template identifier to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful <see cref="MailResult{T}"/> containing the template, or a failure
    /// with <see cref="MailError.TemplateNotFound"/> if no matching template exists.
    /// </returns>
    Task<MailResult<MailTemplate>> LoadTemplateAsync(string templateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all available template IDs from the backing store.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MailResult<IReadOnlyList<string>>> ListTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a template to the backing store, creating it if it does not exist.
    /// </summary>
    /// <param name="template">The template to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MailResult> SaveTemplateAsync(MailTemplate template, CancellationToken cancellationToken = default);
}
