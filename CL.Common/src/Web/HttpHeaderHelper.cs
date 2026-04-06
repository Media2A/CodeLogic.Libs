using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace CL.Common.Web;

/// <summary>
/// Provides utilities for working with HTTP headers, client detection,
/// and IP address utilities. Framework-agnostic — pass header values directly.
/// </summary>
public static class HttpHeaderHelper
{
    // ── Accept-Language ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses an Accept-Language header value and returns the preferred culture.
    /// Example: "da-DK,da;q=0.9,en-US;q=0.8" → "da-DK"
    /// </summary>
    public static string GetPrimaryLocale(string? acceptLanguageHeader)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguageHeader)) return "en-US";

        return acceptLanguageHeader
            .Split(',')
            .Select(lang =>
            {
                var parts = lang.Trim().Split(';');
                var tag   = parts[0].Trim();
                var q     = 1.0;
                if (parts.Length > 1 && parts[1].Trim().StartsWith("q="))
                    double.TryParse(parts[1][2..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out q);
                return (tag, q);
            })
            .OrderByDescending(x => x.q)
            .Select(x => x.tag)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "en-US";
    }

    // ── Client IP ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines the most accurate public-facing client IP address.
    /// Checks X-Forwarded-For first, falls back to the remote address.
    /// </summary>
    public static string GetEffectiveClientIp(string? remoteIp, string? xForwardedFor)
    {
        var forwarded = xForwardedFor?
            .Split(',')
            .Select(ip => ip.Trim())
            .Where(ip => !string.IsNullOrWhiteSpace(ip) && !IsPrivateIp(ip))
            .FirstOrDefault();

        return forwarded ?? remoteIp ?? "0.0.0.0";
    }

    /// <summary>Returns true if the IP address is in a private/RFC1918 range or loopback.</summary>
    public static bool IsPrivateIp(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip)) return false;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false; // IPv6 — not private by this check

        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
    }

    // ── User-Agent ────────────────────────────────────────────────────────────

    private static readonly Regex BotRegex = new(
        @"bot|crawler|spider|scraper|baiduspider|ia_archiver|wget|curl|python|java|go-http",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns true if the User-Agent string matches known bot/crawler patterns.</summary>
    public static bool IsBot(string? userAgent) =>
        !string.IsNullOrWhiteSpace(userAgent) && BotRegex.IsMatch(userAgent);

    /// <summary>Returns true if the User-Agent suggests a mobile device.</summary>
    public static bool IsMobile(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return false;
        return Regex.IsMatch(userAgent, @"android|iphone|ipad|ipod|windows phone|mobile",
            RegexOptions.IgnoreCase);
    }

    // ── Content negotiation ───────────────────────────────────────────────────

    /// <summary>Returns true if the Accept header includes application/json.</summary>
    public static bool AcceptsJson(string? acceptHeader) =>
        acceptHeader?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true ||
        acceptHeader?.Contains("*/*", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Returns the value of a header by name (case-insensitive) from a dictionary.</summary>
    public static string? GetHeader(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var val) ? val
        : headers.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
