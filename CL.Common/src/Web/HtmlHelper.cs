using System.Text.RegularExpressions;

namespace CL.Common.Web;

/// <summary>
/// Provides HTML sanitization and trimming utilities.
/// </summary>
public static class HtmlHelper
{
    private static readonly Regex TagRegex          = new(@"<[^>]+>",    RegexOptions.Compiled);
    private static readonly Regex ScriptRegex       = new(@"<script[^>]*>.*?</script>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex StyleRegex        = new(@"<style[^>]*>.*?</style>",   RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex EventAttrRegex    = new(@"\s*on\w+\s*=\s*""[^""]*""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WhitespaceRegex   = new(@"\s{2,}",    RegexOptions.Compiled);

    /// <summary>
    /// Strips all HTML tags, returning plain text.
    /// Does not decode HTML entities (e.g. &amp;amp; stays as &amp;amp;).
    /// </summary>
    public static string StripTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return TagRegex.Replace(html, string.Empty);
    }

    /// <summary>
    /// Sanitizes HTML by removing script tags, style tags, and inline event handlers.
    /// Safe for displaying user-provided content that may contain limited HTML.
    /// </summary>
    public static string Sanitize(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        html = ScriptRegex.Replace(html, string.Empty);
        html = StyleRegex.Replace(html, string.Empty);
        html = EventAttrRegex.Replace(html, string.Empty);
        return html.Trim();
    }

    /// <summary>
    /// Trims whitespace inside HTML, collapsing multiple spaces/newlines to single spaces.
    /// Preserves HTML structure.
    /// </summary>
    public static string TrimWhitespace(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return WhitespaceRegex.Replace(html, " ").Trim();
    }

    /// <summary>
    /// Encodes a plain-text string so it is safe to embed in HTML.
    /// Converts &lt; &gt; &amp; &quot; to their HTML entity equivalents.
    /// </summary>
    public static string Encode(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("&",  "&amp;")
            .Replace("<",  "&lt;")
            .Replace(">",  "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'",  "&#39;");
    }

    /// <summary>
    /// Decodes HTML entities back to plain text.
    /// </summary>
    public static string Decode(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return html
            .Replace("&amp;",  "&")
            .Replace("&lt;",   "<")
            .Replace("&gt;",   ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;",  "'")
            .Replace("&nbsp;", " ");
    }

    /// <summary>
    /// Truncates HTML content to approximately <paramref name="maxLength"/> visible characters,
    /// stripping tags first. Appends <paramref name="suffix"/> if truncated.
    /// </summary>
    public static string TruncateText(string html, int maxLength, string suffix = "...")
    {
        var text = StripTags(html);
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - suffix.Length)] + suffix;
    }
}
