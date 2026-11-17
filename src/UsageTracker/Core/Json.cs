using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageTracker.Core;

/// <summary>
/// Tolerant JSON accessors. Every endpoint we read is undocumented, so each lookup
/// returns null instead of throwing when a field is missing or has an unexpected type.
/// </summary>
internal static class Json
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static JsonNode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text, documentOptions: Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonNode? Get(this JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    public static JsonArray? GetArray(this JsonNode? node, string key) => node.Get(key) as JsonArray;

    public static JsonObject? GetObject(this JsonNode? node, string key) => node.Get(key) as JsonObject;

    public static string? GetString(this JsonNode? node, string key) => AsString(node.Get(key));

    public static double? GetDouble(this JsonNode? node, string key) => AsDouble(node.Get(key));

    public static long? GetLong(this JsonNode? node, string key)
    {
        var d = node.GetDouble(key);
        return d is null || double.IsNaN(d.Value) || double.IsInfinity(d.Value) ? null : (long)d.Value;
    }

    public static bool? GetBool(this JsonNode? node, string key)
    {
        if (node.Get(key) is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetValue<string>(), out var b) => b,
            _ => null,
        };
    }

    /// <summary>Reads an ISO-8601 string or an epoch number (seconds, or milliseconds when large).</summary>
    public static DateTimeOffset? GetTimestamp(this JsonNode? node, string key)
    {
        var raw = node.Get(key);
        if (raw is not JsonValue v) return null;
        if (v.GetValueKind() == JsonValueKind.Number) return FromEpoch(AsDouble(v));
        var s = AsString(v);
        if (s is null) return null;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? FromEpoch(n) : null;
    }

    public static DateOnly? GetDate(this JsonNode? node, string key)
    {
        var s = node.GetString(key);
        if (s is null) return null;
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? DateOnly.FromDateTime(dto.UtcDateTime)
            : null;
    }

    private static DateTimeOffset? FromEpoch(double? value)
    {
        if (value is not { } n || double.IsNaN(n) || n <= 0) return null;
        try
        {
            // Anything past year ~5000 in seconds is really milliseconds.
            return n > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)n)
                : DateTimeOffset.FromUnixTimeSeconds((long)n);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static double? AsDouble(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                return v.TryGetValue<double>(out var d) ? d : null;
            case JsonValueKind.String:
                return double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                    ? p
                    : null;
            default:
                return null;
        }
    }
}
