using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core;

/// <summary>Readers for a tool call's JSON arguments. Lenient on purpose: models send "true" and
/// "10" as strings often enough that refusing them only costs a retry.</summary>
public static class JsonArgs
{
    public static JsonObject Object(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    public static string? String(string json, string key) => String(Object(json), key);

    public static string? String(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int Int(string json, string key, int defaultValue) => Int(Object(json), key, defaultValue);

    public static int Int(JsonObject obj, string key, int defaultValue)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue v) return defaultValue;
        if (JsonNumbers.TryGetInt(v, out var n)) return n;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return defaultValue;
    }

    public static bool Bool(string json, string key, bool defaultValue) => Bool(Object(json), key, defaultValue);

    public static bool Bool(JsonObject obj, string key, bool defaultValue)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue v) return defaultValue;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s))
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "true": case "yes": case "1": return true;
                case "false": case "no": case "0": return false;
            }
        }
        return defaultValue;
    }

    /// <summary>A JSON string literal for <paramref name="value"/> (quotes included).</summary>
    public static string Quote(string value) => JsonSerializer.Serialize(value);
}

/// <summary>Number readers matching Swift's <c>as? Int</c>: integral JSON numbers only.</summary>
public static class JsonNumbers
{
    public static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.GetValueKind() != JsonValueKind.Number) return false;
        if (v.TryGetValue<int>(out value)) return true;
        if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) { value = (int)l; return true; }
        if (v.TryGetValue<double>(out var d) && Math.Abs(d % 1) < double.Epsilon && d is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)d;
            return true;
        }
        return false;
    }

    /// <summary>A positive integer at <paramref name="key"/>, or null.</summary>
    public static int? Positive(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && TryGetInt(node, out var n) && n > 0 ? n : null;
}
