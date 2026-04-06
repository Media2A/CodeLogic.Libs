using CL.Mail.Models;
using CodeLogic.Core.Logging;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CL.Mail.Services;

/// <summary>
/// Lightweight template engine supporting variable substitution, conditionals, loops,
/// named sections, and optional layout wrapping.
/// <para>
/// Supported syntax:
/// <list type="bullet">
///   <item><c>{{variable}}</c> — variable substitution</item>
///   <item><c>${variable}</c> — variable substitution (alternate syntax)</item>
///   <item><c>{variable}</c> — variable substitution (legacy syntax)</item>
///   <item><c>{{#if var}}…{{/if}}</c> — conditional block</item>
///   <item><c>{{#if var}}…{{#else}}…{{/if}}</c> — conditional with else</item>
///   <item><c>{{#each items}}…{{/each}}</c> — loop over a collection</item>
///   <item><c>{{#section name}}…{{/section}}</c> — named section for layout composition</item>
/// </list>
/// </para>
/// </summary>
public sealed class SimpleTemplateEngine : IMailTemplateEngine
{
    private readonly ILogger? _logger;
    private readonly IMailTemplateProvider? _templateProvider;

    /// <inheritdoc/>
    public string Name => "Simple";

    /// <summary>Initialises without layout support.</summary>
    /// <param name="logger">Optional logger.</param>
    public SimpleTemplateEngine(ILogger? logger = null) => _logger = logger;

    /// <summary>Initialises with layout support via a template provider.</summary>
    /// <param name="templateProvider">Provider used to load layout templates.</param>
    /// <param name="logger">Optional logger.</param>
    public SimpleTemplateEngine(IMailTemplateProvider templateProvider, ILogger? logger = null)
    {
        _templateProvider = templateProvider ?? throw new ArgumentNullException(nameof(templateProvider));
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<MailResult<RenderedTemplate>> RenderAsync(
        MailTemplate template,
        Dictionary<string, object?> variables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        variables ??= [];

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var subject = RenderText(template.Subject, variables);
            var textBody = template.TextBody is not null ? RenderText(template.TextBody, variables) : null;
            var htmlBody = template.HtmlBody is not null ? RenderText(template.HtmlBody, variables) : null;

            // Apply layout if specified and a provider is available
            if (!string.IsNullOrWhiteSpace(template.Layout) && _templateProvider is not null)
            {
                var layoutResult = await _templateProvider
                    .LoadTemplateAsync(template.Layout, cancellationToken)
                    .ConfigureAwait(false);

                if (layoutResult.IsSuccess && layoutResult.Value is not null)
                {
                    var layout = layoutResult.Value;

                    // Extract named sections from rendered child bodies
                    var textSections = textBody is not null ? ExtractSections(textBody) : [];
                    var htmlSections = htmlBody is not null ? ExtractSections(htmlBody) : [];

                    textSections.TryAdd("body", StripSections(textBody ?? ""));
                    htmlSections.TryAdd("body", StripSections(htmlBody ?? ""));

                    // Merge section values into a copy of the variable dictionary for layout rendering
                    var layoutVars = new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);
                    foreach (var section in htmlSections)
                        layoutVars.TryAdd(section.Key, section.Value);

                    if (layout.TextBody is not null) textBody = RenderText(layout.TextBody, layoutVars);
                    if (layout.HtmlBody is not null) htmlBody = RenderText(layout.HtmlBody, layoutVars);
                }
                else
                {
                    _logger?.Warning($"Layout template '{template.Layout}' not found — rendering without layout");
                }
            }

            return MailResult<RenderedTemplate>.Success(new RenderedTemplate
            {
                Subject = subject,
                TextBody = textBody,
                HtmlBody = htmlBody
            });
        }
        catch (OperationCanceledException)
        {
            return MailResult<RenderedTemplate>.Failure(MailError.TemplateRenderingFailed, "Rendering cancelled");
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error rendering template '{template.Id}'", ex);
            return MailResult<RenderedTemplate>.Failure(MailError.TemplateRenderingFailed, ex.Message);
        }
    }

    // ── Rendering pipeline ────────────────────────────────────────────────────

    private string RenderText(string text, Dictionary<string, object?> variables)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = ProcessConditionals(text, variables);
        result = ProcessLoops(result, variables);
        result = SubstituteVariables(result, variables);
        return result;
    }

    /// <summary>
    /// Processes <c>{{#if var}}…{{/if}}</c> blocks iteratively from innermost out.
    /// </summary>
    private static string ProcessConditionals(string text, Dictionary<string, object?> variables)
    {
        const int MaxIterations = 100;
        for (var i = 0; i < MaxIterations; i++)
        {
            var match = Regex.Match(text,
                @"\{\{#if\s+(\w+)\}\}((?:(?!\{\{#if\b).)*?)\{\{/if\}\}",
                RegexOptions.Singleline);

            if (!match.Success) break;

            var varName = match.Groups[1].Value;
            var inner = match.Groups[2].Value;
            var truthy = IsTruthy(variables, varName);

            var elseIdx = inner.IndexOf("{{#else}}", StringComparison.Ordinal);
            var replacement = elseIdx >= 0
                ? (truthy ? inner[..elseIdx] : inner[(elseIdx + 9)..])
                : (truthy ? inner : "");

            text = string.Concat(text.AsSpan(0, match.Index), replacement, text.AsSpan(match.Index + match.Length));
        }
        return text;
    }

    /// <summary>
    /// Processes <c>{{#each items}}…{{/each}}</c> blocks.
    /// Items can be <c>Dictionary&lt;string, object?&gt;</c> or any type (via reflection).
    /// </summary>
    private string ProcessLoops(string text, Dictionary<string, object?> variables)
    {
        const int MaxIterations = 100;
        for (var i = 0; i < MaxIterations; i++)
        {
            var match = Regex.Match(text,
                @"\{\{#each\s+(\w+)\}\}(.*?)\{\{/each\}\}",
                RegexOptions.Singleline);

            if (!match.Success) break;

            var collectionName = match.Groups[1].Value;
            var itemTemplate = match.Groups[2].Value;
            var sb = new StringBuilder();

            if (TryGetVariable(variables, collectionName, out var raw) && raw is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    var iterVars = new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);

                    if (item is Dictionary<string, object?> d)
                        foreach (var kvp in d) iterVars[kvp.Key] = kvp.Value;
                    else if (item is IDictionary<string, object> d2)
                        foreach (var kvp in d2) iterVars[kvp.Key] = kvp.Value;
                    else if (item is not null)
                        foreach (var prop in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try { iterVars[prop.Name] = prop.GetValue(item); }
                            catch (Exception ex) { _logger?.Debug($"Skip inaccessible property '{prop.Name}': {ex.Message}"); }
                        }

                    var rendered = ProcessConditionals(itemTemplate, iterVars);
                    rendered = ProcessLoops(rendered, iterVars);
                    rendered = SubstituteVariables(rendered, iterVars);
                    sb.Append(rendered);
                }
            }

            text = string.Concat(text.AsSpan(0, match.Index), sb.ToString(), text.AsSpan(match.Index + match.Length));
        }
        return text;
    }

    /// <summary>
    /// Replaces <c>{{var}}</c>, <c>${var}</c>, and <c>{var}</c> placeholders with their values.
    /// </summary>
    private static string SubstituteVariables(string text, Dictionary<string, object?> variables)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = Regex.Replace(text, @"\{\{(\w+)\}\}", m =>
            TryGetVariable(variables, m.Groups[1].Value, out var v) ? v?.ToString() ?? "" : m.Value);

        text = Regex.Replace(text, @"\$\{(\w+)\}", m =>
            TryGetVariable(variables, m.Groups[1].Value, out var v) ? v?.ToString() ?? "" : m.Value);

        text = Regex.Replace(text, @"(?<!\{)\{(\w+)\}(?!\})", m =>
            TryGetVariable(variables, m.Groups[1].Value, out var v) ? v?.ToString() ?? "" : m.Value);

        return text;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static bool IsTruthy(Dictionary<string, object?> variables, string varName)
    {
        if (!TryGetVariable(variables, varName, out var value)) return false;
        return value switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            string s => !string.IsNullOrEmpty(s)
                        && !s.Equals("false", StringComparison.OrdinalIgnoreCase)
                        && s != "0",
            _ => true
        };
    }

    private static bool TryGetVariable(Dictionary<string, object?> variables, string key, out object? value)
    {
        if (variables.TryGetValue(key, out value)) return true;
        foreach (var kvp in variables)
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        value = null;
        return false;
    }

    private static Dictionary<string, string> ExtractSections(string text)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, @"\{\{#section\s+(\w+)\}\}(.*?)\{\{/section\}\}", RegexOptions.Singleline))
            sections[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return sections;
    }

    private static string StripSections(string text) =>
        Regex.Replace(text, @"\{\{#section\s+\w+\}\}.*?\{\{/section\}\}", "", RegexOptions.Singleline).Trim();
}
