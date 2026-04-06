using System.Text.Json;
using System.Text.Json.Nodes;
using CodeLogic.Core.Results;

namespace CL.Common.Data;

/// <summary>
/// Provides JSON serialization, deserialization, file I/O, validation, and merging utilities.
/// Uses <c>System.Text.Json</c> as the primary engine.
/// All operations that can fail return <see cref="Result{T}"/> or <see cref="Result"/>.
/// </summary>
public static class JsonHelper
{
    private static readonly JsonSerializerOptions _compact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions _indented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Serializes the given object to a JSON string.</summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="obj">The object to serialize. May be null.</param>
    /// <param name="indented">When <c>true</c>, output is formatted with indentation. Default: <c>false</c>.</param>
    /// <returns>A <see cref="Result{T}"/> containing the JSON string on success, or a failure result.</returns>
    public static Result<string> Serialize<T>(T obj, bool indented = false)
    {
        try
        {
            return Result<string>.Success(JsonSerializer.Serialize(obj, indented ? _indented : _compact));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(Error.FromException(ex, ErrorCode.Internal));
        }
    }

    /// <summary>Deserializes a JSON string into an instance of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The target type.</typeparam>
    /// <param name="json">The JSON string to deserialize.</param>
    /// <returns>A <see cref="Result{T}"/> containing the deserialized value on success, or a failure result.</returns>
    public static Result<T> Deserialize<T>(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, _compact);
            return value is null
                ? Result<T>.Failure(Error.Internal(ErrorCode.Internal, "Deserialization returned null."))
                : Result<T>.Success(value);
        }
        catch (Exception ex)
        {
            return Result<T>.Failure(Error.FromException(ex, ErrorCode.Internal));
        }
    }

    /// <summary>Serializes an object to JSON and writes it to the specified file path.</summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="obj">The object to serialize and write.</param>
    /// <param name="filePath">The file path to write to. The parent directory is created if it does not exist.</param>
    /// <param name="indented">When <c>true</c>, the written JSON is formatted with indentation. Default: <c>true</c>.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public static async Task<Result> SerializeToFile<T>(T obj, string filePath, bool indented = true)
    {
        try
        {
            var json = JsonSerializer.Serialize(obj, indented ? _indented : _compact);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(filePath, json);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(Error.FromException(ex, ErrorCode.FileWriteFailed));
        }
    }

    /// <summary>Reads a JSON file from disk and deserializes its contents into an instance of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The target type.</typeparam>
    /// <param name="filePath">The path to the JSON file to read.</param>
    /// <returns>A <see cref="Result{T}"/> containing the deserialized value on success, or a failure result.</returns>
    public static async Task<Result<T>> DeserializeFromFile<T>(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return Result<T>.Failure(Error.NotFound(ErrorCode.FileNotFound, $"JSON file not found: {filePath}"));

            var json = await File.ReadAllTextAsync(filePath);
            return Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            return Result<T>.Failure(Error.FromException(ex, ErrorCode.FileReadFailed));
        }
    }

    /// <summary>Validates whether the given string is well-formed JSON.</summary>
    /// <param name="json">The string to validate. Returns <c>false</c> for null or whitespace.</param>
    /// <returns><c>true</c> if the string is valid JSON; otherwise <c>false</c>.</returns>
    public static bool IsValidJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { JsonDocument.Parse(json).Dispose(); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Merges two JSON objects. Properties from <paramref name="overlayJson"/> overwrite matching
    /// properties in <paramref name="baseJson"/>; non-conflicting properties from both are preserved.
    /// </summary>
    /// <param name="baseJson">The base JSON object string.</param>
    /// <param name="overlayJson">The overlay JSON object string whose values take precedence.</param>
    /// <returns>A <see cref="Result{T}"/> containing the merged JSON string on success, or a failure result.</returns>
    public static Result<string> Merge(string baseJson, string overlayJson)
    {
        try
        {
            var baseObj = JsonNode.Parse(baseJson)?.AsObject()
                ?? throw new InvalidOperationException("Base JSON must be a JSON object.");
            var overlayObj = JsonNode.Parse(overlayJson)?.AsObject();
            if (overlayObj is not null)
            {
                foreach (var property in overlayObj)
                    baseObj[property.Key] = property.Value?.DeepClone();
            }
            return Result<string>.Success(baseObj.ToJsonString(_indented));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(Error.FromException(ex, ErrorCode.Internal));
        }
    }

    /// <summary>Attempts to extract the raw JSON text of a top-level property from a JSON string.</summary>
    /// <param name="json">The JSON object string to read from.</param>
    /// <param name="propertyName">The property name to look up (case-sensitive).</param>
    /// <returns>A <see cref="Result{T}"/> containing the raw JSON value string on success, or a failure result.</returns>
    public static Result<string> GetProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(propertyName, out var element))
                return Result<string>.Success(element.GetRawText());
            return Result<string>.Failure(Error.NotFound(ErrorCode.NotFound, $"Property '{propertyName}' not found."));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(Error.FromException(ex, ErrorCode.Internal));
        }
    }
}
