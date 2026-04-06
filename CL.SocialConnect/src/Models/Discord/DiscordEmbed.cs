using System.Text.Json.Serialization;

namespace CL.SocialConnect.Models.Discord;

/// <summary>
/// Represents a rich embed object attached to a Discord webhook message.
/// </summary>
public class DiscordEmbed
{
    /// <summary>Title of the embed.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Description text of the embed. Supports Discord markdown.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>URL that the title links to.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Color of the embed's left border as a decimal integer (e.g. 0xFF0000 for red).</summary>
    [JsonPropertyName("color")]
    public int? Color { get; set; }

    /// <summary>Timestamp displayed in the embed footer. ISO 8601 format.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }

    /// <summary>Footer displayed at the bottom of the embed.</summary>
    [JsonPropertyName("footer")]
    public DiscordEmbedFooter? Footer { get; set; }

    /// <summary>Image displayed inside the embed body.</summary>
    [JsonPropertyName("image")]
    public DiscordEmbedImage? Image { get; set; }

    /// <summary>Thumbnail image displayed in the top-right corner of the embed.</summary>
    [JsonPropertyName("thumbnail")]
    public DiscordEmbedImage? Thumbnail { get; set; }

    /// <summary>Author information displayed above the embed title.</summary>
    [JsonPropertyName("author")]
    public DiscordEmbedAuthor? Author { get; set; }

    /// <summary>List of field name/value pairs displayed in the embed body.</summary>
    [JsonPropertyName("fields")]
    public List<DiscordEmbedField>? Fields { get; set; }
}

/// <summary>
/// Represents a field within a Discord embed.
/// </summary>
public class DiscordEmbedField
{
    /// <summary>Name (label) of the field. Required.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>Value (content) of the field. Required.</summary>
    [JsonPropertyName("value")]
    public required string Value { get; set; }

    /// <summary>When <c>true</c>, this field is displayed inline with adjacent inline fields.</summary>
    [JsonPropertyName("inline")]
    public bool Inline { get; set; } = false;
}

/// <summary>
/// Represents an image or thumbnail in a Discord embed.
/// </summary>
public class DiscordEmbedImage
{
    /// <summary>URL of the image. HTTPS required for external images.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; set; }

    /// <summary>Width of the image in pixels (informational, set by Discord).</summary>
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    /// <summary>Height of the image in pixels (informational, set by Discord).</summary>
    [JsonPropertyName("height")]
    public int? Height { get; set; }
}

/// <summary>
/// Represents the author section displayed above the embed title.
/// </summary>
public class DiscordEmbedAuthor
{
    /// <summary>Name of the author. Required.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>URL that the author name links to.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>URL of a small icon displayed next to the author name.</summary>
    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }
}

/// <summary>
/// Represents the footer section at the bottom of a Discord embed.
/// </summary>
public class DiscordEmbedFooter
{
    /// <summary>Footer text. Required.</summary>
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    /// <summary>URL of a small icon displayed next to the footer text.</summary>
    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }
}
