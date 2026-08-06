using System.Text.Json;

namespace App.Shared.Services;

/// <summary>
/// Helpers for OpenAI-compatible tool call argument JSON.
/// </summary>
internal static class OpenAiFunctionArgumentJson
{
    public static Dictionary<string, object?> ParseArgumentsJsonElement(JsonElement argsEl)
    {
        var argsJson = argsEl.ValueKind switch
        {
            JsonValueKind.String => argsEl.GetString() ?? "{}",
            JsonValueKind.Object => argsEl.GetRawText(),
            _ => "{}"
        };

        return ParseArgumentsJson(argsJson);
    }

    public static Dictionary<string, object?> ParseArgumentsJson(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return new Dictionary<string, object?>();

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, object?>();

            return CoerceObject(doc.RootElement);
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    public static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
            return "{}";

        var normalized = new Dictionary<string, object?>();
        foreach (var kv in arguments)
            normalized[kv.Key] = NormalizeValue(kv.Value);

        return JsonSerializer.Serialize(normalized);
    }

    private static Dictionary<string, object?> CoerceObject(JsonElement obj)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
            result[prop.Name] = CoerceValue(prop.Value);
        return result;
    }

    private static object? CoerceValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt32(out var i) ? i :
                                el.TryGetInt64(out var l) ? l :
                                el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(CoerceValue).ToList(),
        JsonValueKind.Object => CoerceObject(el),
        _ => el.GetRawText()
    };

    private static object? NormalizeValue(object? value) => value switch
    {
        null => null,
        JsonElement el => CoerceValue(el),
        _ => value
    };
}