using System.Globalization;
using System.Text.Json.Nodes;

namespace Forge.Core.Model;

/// <summary>
/// Tolerant readers over JSON:API payloads. Forge sends ids as numbers or
/// strings depending on the endpoint, and any field can be null, so every read
/// goes through here rather than through a typed deserializer.
/// </summary>
internal static class Json
{
    public static JsonNode? Get(JsonNode? node, string property) =>
        node is JsonObject o && o.TryGetPropertyValue(property, out var value) ? value : null;

    public static JsonNode? Get(JsonNode? node, params string[] path)
    {
        var current = node;
        foreach (var p in path) current = Get(current, p);
        return current;
    }

    public static IEnumerable<JsonNode> Items(JsonNode? node, string property) =>
        Get(node, property) is JsonArray a ? a.Where(n => n is not null).Cast<JsonNode>() : [];

    public static string Str(JsonNode? node)
    {
        if (node is not JsonValue v) return "";
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        if (v.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        return v.ToJsonString();
    }

    /// <summary>JS's <c>String(x || fallback)</c>.</summary>
    public static string Str(JsonNode? node, string fallback)
    {
        var s = Str(node);
        return s == "" || s == "0" && node is JsonValue v && v.TryGetValue<long>(out _) ? fallback : s;
    }

    public static bool IsTrue(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static double Num(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return 0;
    }

    public static bool IsNumber(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<double>(out _) || v.TryGetValue<long>(out _)) &&
        !v.TryGetValue<string>(out _);

    public static long? ParseIsoMs(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t.ToUnixTimeMilliseconds()
            : null;
}
