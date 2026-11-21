namespace Threadsmith.Extensions.Runtime;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Tools;

/// <summary>Builds a complete review from the immutable generation target and the admitted JSON.</summary>
internal static class ExtensionStatementPreview
{
    /// <summary>Formats the exact statement and typed parameter names without retaining parameter values.</summary>
    internal static string Create(ExtensionStatementReview review, string extension, string tool, ExtensionGenerationId generation, string input, IOutputSanitizer sanitizer, ExtensionSecretProtection protection)
    {
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || review.StatementProperty == review.ParametersProperty
            || !root.TryGetProperty(review.StatementProperty, out var statement) || statement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Statement review unavailable.");
        }

        var result = new StringBuilder();
        Add("Extension", extension);
        Add("Tool", tool);
        Add("Generation", generation.Value.ToString("D"));
        Add("Destination", review.Destination);
        Add("Database", review.Database);
        Add("Statement", statement.GetString() ?? string.Empty);
        result.AppendLine("Parameters (names and JSON types only):");
        foreach (var field in root.EnumerateObject())
        {
            if (field.NameEquals(review.StatementProperty))
            {
                continue;
            }

            if (!field.NameEquals(review.ParametersProperty) || field.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Unexpected statement argument.");
            }

            foreach (var parameter in field.Value.EnumerateObject())
            {
                var kind = parameter.Value.ValueKind;
                Add(kind is JsonValueKind.True or JsonValueKind.False ? "Boolean" : kind.ToString(), parameter.Name);
            }
        }

        return result.ToString();

        void Add(string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > IToolStatementReview.MaximumCharacters || protection.Contains(text)
                || !string.Equals(text, sanitizer.Sanitize(text), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Statement review unavailable.");
            }

            // JSON quoting makes line breaks, direction controls and terminal escapes unambiguous.
            result.Append(label).Append(": ").AppendLine(JsonSerializer.Serialize(text));
            if (result.Length > IToolStatementReview.MaximumCharacters - 64)
            {
                throw new InvalidOperationException("Statement review exceeds its display limit.");
            }
        }
    }
}
