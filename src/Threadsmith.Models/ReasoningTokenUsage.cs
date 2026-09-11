namespace Threadsmith.Models;

using System.Text.Json;

/// <summary>Reads optional provider usage breakdowns without guessing missing or invalid counters.</summary>
public static class ReasoningTokenUsage
{
    /// <summary>Returns a valid output-token subset, or null if the optional metadata is missing or malformed.</summary>
    public static long? Read(JsonElement details, string field, long outputTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        return details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var count)
            && count >= 0 && count <= outputTokens
                ? count : null;
    }
}
