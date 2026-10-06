namespace Threadsmith.Models;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Validates versioned host-owned model outputs before execution.</summary>
public static class ModelOutputValidator
{
    /// <summary>Validates the supported schema version and type-specific invariants.</summary>
    public static void Validate(ModelOutput output, int supportedSchemaVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.SchemaVersion != supportedSchemaVersion)
        {
            throw new MalformedModelOutputException(
                $"Unsupported model-output schema version {output.SchemaVersion}; "
                + $"expected {supportedSchemaVersion}.");
        }

        switch (output)
        {
            case TextModelOutput text when string.IsNullOrWhiteSpace(text.Text):
                throw new MalformedModelOutputException("Text model output is empty.");
            case ToolRequestModelOutput tool:
                ValidateInvocation(tool);
                break;
            case TextModelOutput:
                break;
            default:
                throw new MalformedModelOutputException(
                    $"Unsupported model-output type '{output.GetType().Name}'.");
        }
    }

    /// <summary>Validates a model-authored tool invocation before it reaches execution.</summary>
    public static void ValidateInvocation(
        ToolRequestModelOutput tool,
        string? providerFamily = null,
        int? toolOrdinal = null,
        int? toolCallCount = null)
    {
        using var arguments = ParseInvocationArguments(tool, providerFamily, toolOrdinal, toolCallCount);
    }

    private static JsonDocument ParseInvocationArguments(
        ToolRequestModelOutput tool,
        string? providerFamily,
        int? toolOrdinal,
        int? toolCallCount)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.ToolName))
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.MissingToolName,
                "Tool name is missing.",
                tool.ToolName,
                tool.ArgumentsJson,
                providerFamily,
                toolOrdinal,
                toolCallCount,
                jsonException: null,
                innerException: null);
        }

        try
        {
            var arguments = JsonDocument.Parse(ModelJsonCleanup.Clean(tool.ArgumentsJson));
            if (arguments.RootElement.ValueKind == JsonValueKind.Object)
            {
                return arguments;
            }

            arguments.Dispose();
            throw CreateInvocationException(
                MalformedInvocationFailureKind.NonObjectArguments,
                "Tool arguments must be a JSON object.",
                tool.ToolName,
                tool.ArgumentsJson,
                providerFamily,
                toolOrdinal,
                toolCallCount,
                jsonException: null,
                innerException: null);
        }
        catch (MalformedInvocationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.InvalidJsonArguments,
                "Tool arguments are not valid JSON.",
                tool.ToolName,
                tool.ArgumentsJson,
                providerFamily,
                toolOrdinal,
                toolCallCount,
                exception,
                exception);
        }
    }

    private static MalformedInvocationException CreateInvocationException(
        MalformedInvocationFailureKind kind,
        string safeMessage,
        string? toolName,
        string? argumentsJson,
        string? providerFamily,
        int? toolOrdinal,
        int? toolCallCount,
        JsonException? jsonException,
        Exception? innerException)
    {
        var diagnostic = new MalformedInvocationDiagnostic
        {
            Kind = kind,
            SafeMessage = BoundSingleLine(safeMessage, 512),
            ToolName = BoundNullable(toolName, 128),
            ToolOrdinal = toolOrdinal,
            ToolCallCount = toolCallCount,
            ProviderFamily = BoundNullable(providerFamily, 64),
            ArgumentCharacterCount = argumentsJson?.Length,
            ArgumentSha256 = argumentsJson is null
                ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson))).ToLowerInvariant(),
            JsonPath = BoundNullable(jsonException?.Path, 256),
            JsonLineNumber = jsonException?.LineNumber,
            JsonBytePositionInLine = jsonException?.BytePositionInLine,
        };
        return innerException is null
            ? new MalformedInvocationException(diagnostic)
            : new MalformedInvocationException(diagnostic, innerException);
    }

    private static string? BoundNullable(string? value, int maximumCharacters)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : BoundSingleLine(value, maximumCharacters);
    }

    private static string BoundSingleLine(string value, int maximumCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters));
        foreach (var character in value)
        {
            if (builder.Length == maximumCharacters)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }
}
