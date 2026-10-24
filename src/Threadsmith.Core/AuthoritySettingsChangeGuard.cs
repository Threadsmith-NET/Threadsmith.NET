namespace Threadsmith.Core;

using System.Collections.Immutable;
using System.Text.Json;

/// <summary>Checks semantic authority changes on the existing coordinated settings path.</summary>
public static class AuthoritySettingsChangeGuard
{
    /// <summary>Gets the authority fields; mixed ordinary trees are intentionally not included.</summary>
    public static ImmutableArray<string> Keys => ConfigurationPermissionCatalog.Paths;

    /// <summary>Allows only exact fields owned by the caller's existing reviewed settings operation.</summary>
    public static void Validate(ReadOnlyMemory<byte> before, ReadOnlyMemory<byte> after, ImmutableArray<string> authorizedKeys)
    {
        _ = ConfigurationDocument.Parse(before, validatePermissionParents: false);
        _ = ConfigurationDocument.Parse(after, validatePermissionParents: false);
        using var original = JsonDocument.Parse(WithoutBom(before), RepositorySettingsCoordinator.DocumentOptions);
        using var replacement = JsonDocument.Parse(WithoutBom(after), RepositorySettingsCoordinator.DocumentOptions);
        foreach (var key in Keys)
        {
            if (authorizedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var left = Find(original.RootElement, key);
            var right = Find(replacement.RootElement, key);
            if (!Equivalent(left, right))
            {
                throw new UnauthorizedAccessException($"AuthoritySettingReviewRequired: the settings operation does not authorize changing '{key}'. Make that policy change manually.");
            }
        }
    }

    private static ReadOnlyMemory<byte> WithoutBom(ReadOnlyMemory<byte> content)
        => content.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? content[3..] : content;

    private static JsonElement? Find(JsonElement root, string key)
    {
        foreach (var segment in key.Split(':'))
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                // Preserve an unrelated legacy scalar exactly; replacing it with an authority
                // object (or changing it) still differs and is rejected by the semantic check.
                return root;
            }

            var properties = root.EnumerateObject().Where(property => property.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (properties.Length == 0)
            {
                return null;
            }

            if (properties.Length != 1)
            {
                throw new InvalidDataException("AuthoritySettingReviewRequired: duplicate authority setting keys are ambiguous.");
            }

            root = properties[0].Value;
        }

        return root;
    }

    private static bool Equivalent(JsonElement? left, JsonElement? right)
    {
        if (left is not { } first || right is not { } second)
        {
            return left is null && right is null;
        }

        if (first.ValueKind != second.ValueKind)
        {
            return false;
        }

        if (first.ValueKind == JsonValueKind.Object)
        {
            var properties = first.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
            var replacements = second.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
            return properties.Count == replacements.Count && properties.All(property =>
                replacements.TryGetValue(property.Key, out var value) && Equivalent(property.Value, value));
        }

        if (first.ValueKind == JsonValueKind.Array)
        {
            return first.GetArrayLength() == second.GetArrayLength()
                && first.EnumerateArray().Zip(second.EnumerateArray()).All(pair => Equivalent(pair.First, pair.Second));
        }

        return JsonElement.DeepEquals(first, second);
    }
}
