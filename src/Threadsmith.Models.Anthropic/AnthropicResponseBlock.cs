namespace Threadsmith.Models.Anthropic;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Models;

/// <summary>One ordered response block with exact private content independent of display projection.</summary>
internal sealed class AnthropicResponseBlock
{
    private readonly JsonObject _original;
    private readonly StringBuilder _text;
    private readonly StringBuilder _thinking;
    private readonly StringBuilder _signature;
    private readonly StringBuilder _input = new();

    /// <summary>Initializes a new instance of the <see cref="AnthropicResponseBlock"/> class.</summary>
    internal AnthropicResponseBlock(JsonElement element)
    {
        _original = JsonNode.Parse(element.GetRawText()) as JsonObject
            ?? throw new MalformedModelOutputException("Anthropic content block is invalid.");
        Type = AnthropicStreamAdapter.String(element, "type");
        if (Type is not ("text" or "thinking" or "redacted_thinking" or "tool_use"))
        {
            throw new MalformedModelOutputException("Anthropic emitted an unsupported content block.");
        }

        _text = new StringBuilder(Type == "text" ? AnthropicStreamAdapter.String(element, "text") : string.Empty);
        _thinking = new StringBuilder(Type == "thinking" ? AnthropicStreamAdapter.String(element, "thinking") : string.Empty);
        _signature = new StringBuilder(Type == "thinking" ? AnthropicStreamAdapter.String(element, "signature") : string.Empty);
        if (Type == "redacted_thinking" && string.IsNullOrEmpty(AnthropicStreamAdapter.String(element, "data")))
        {
            throw new MalformedModelOutputException("Anthropic redacted thinking block is invalid.");
        }
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal string Type { get; }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal bool Closed { get; set; }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal string VisibleText => _text.ToString();

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal string ThinkingText => _thinking.ToString();

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal string WireId => _original["id"]?.GetValue<string>() ?? string.Empty;

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal string Name => _original["name"]?.GetValue<string>() ?? string.Empty;

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal long EstimatedOutputTokens => ((long)_text.Length + _thinking.Length + _input.Length + 3) / 4;

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal void Append(string deltaType, string content)
    {
        var builder = (Type, deltaType) switch
        {
            ("text", "text_delta") => _text,
            ("thinking", "thinking_delta") => _thinking,
            ("thinking", "signature_delta") => _signature,
            ("tool_use", "input_json_delta") => _input,
            _ => throw new MalformedModelOutputException("Anthropic content delta does not match its open block."),
        };
        builder.Append(content);
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal JsonObject Complete()
    {
        var block = (JsonObject)_original.DeepClone();
        switch (Type)
        {
            case "text":
                block["text"] = _text.ToString();
                break;
            case "thinking":
                if (_signature.Length == 0)
                {
                    throw new MalformedModelOutputException("Anthropic thinking response is missing its required signature.");
                }

                block["thinking"] = _thinking.ToString();
                block["signature"] = _signature.ToString();
                break;
            case "tool_use":
                var arguments = _input.Length == 0 ? block["input"]?.ToJsonString() ?? string.Empty : _input.ToString();
                if (_input.Length > 0 && block["input"] is JsonObject { Count: > 0 })
                {
                    throw new MalformedInvocationException("Anthropic tool arguments supplied conflicting initial and streamed content.");
                }

                try
                {
                    using var document = JsonDocument.Parse(arguments);
                    if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement))
                    {
                        throw new MalformedInvocationException("Anthropic tool arguments must be unambiguous JSON objects.");
                    }

                    block["input"] = JsonNode.Parse(arguments);
                }
                catch (JsonException)
                {
                    throw new MalformedInvocationException("Anthropic tool arguments contain malformed JSON.");
                }

                break;
        }

        return block;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(HasDuplicateProperties);
        }

        return false;
    }
}
