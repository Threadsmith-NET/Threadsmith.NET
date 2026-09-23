namespace Threadsmith.Core;

using System.Text;
using System.Text.Json;

/// <summary>Extracts a model-authored JSON value from optional prose or Markdown framing before validation.</summary>
public static class ModelJsonCleanup
{
    /// <summary>Returns the last complete JSON object or array when the text is not already a JSON value.</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (IsCompleteJson(trimmed))
        {
            return trimmed;
        }

        var bytes = Encoding.UTF8.GetBytes(trimmed);
        string? extracted = null;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] is not ((byte)'{' or (byte)'['))
            {
                continue;
            }

            var reader = new Utf8JsonReader(bytes.AsSpan(index));
            try
            {
                using var document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                {
                    continue;
                }

                var length = checked((int)reader.BytesConsumed);
                extracted = Encoding.UTF8.GetString(bytes.AsSpan(index, length));
                index += length - 1;
            }
            catch (JsonException)
            {
                // Non-JSON braces in surrounding prose are not candidates.
            }
        }

        return extracted ?? trimmed;
    }

    private static bool IsCompleteJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
