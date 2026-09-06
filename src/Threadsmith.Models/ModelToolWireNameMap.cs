namespace Threadsmith.Models;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>Creates reversible bounded wire aliases for model-visible tool names.</summary>
/// <remarks>
/// Some provider function-call transports accept a narrower name grammar than Threadsmith's canonical
/// tool identifiers. This map lets a provider send a safe alias while returning the host-owned
/// canonical identifier before the invocation crosses back into execution.
/// </remarks>
public sealed class ModelToolWireNameMap
{
    /// <summary>Maximum OpenAI-family function name length.</summary>
    public const int OpenAiMaximumWireNameCharacters = 64;

    private readonly IReadOnlyDictionary<string, string> _canonicalToWire;
    private readonly int _maximumWireNameCharacters;
    private readonly IReadOnlyDictionary<string, string> _wireToCanonical;

    private ModelToolWireNameMap(
        IReadOnlyDictionary<string, string> canonicalToWire,
        IReadOnlyDictionary<string, string> wireToCanonical,
        int maximumWireNameCharacters)
    {
        _canonicalToWire = canonicalToWire;
        _wireToCanonical = wireToCanonical;
        _maximumWireNameCharacters = maximumWireNameCharacters;
    }

    /// <summary>Creates a reversible map for the supplied canonical tool inventory.</summary>
    public static ModelToolWireNameMap Create(
        IReadOnlyList<ModelToolDefinition> tools,
        int maximumWireNameCharacters = OpenAiMaximumWireNameCharacters)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumWireNameCharacters, 16);
        var canonicalToWire = new Dictionary<string, string>(StringComparer.Ordinal);
        var wireToCanonical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var wireName = CreatePreferredWireName(tool.Name, maximumWireNameCharacters);
            if (wireToCanonical.TryGetValue(wireName, out var existing)
                && !string.Equals(existing, tool.Name, StringComparison.Ordinal))
            {
                wireName = CreateDigestWireName(tool.Name, wireName, maximumWireNameCharacters, attempt: 0);
            }

            for (var attempt = 1; wireToCanonical.ContainsKey(wireName); attempt++)
            {
                wireName = CreateDigestWireName(tool.Name, wireName, maximumWireNameCharacters, attempt);
            }

            canonicalToWire.Add(tool.Name, wireName);
            wireToCanonical.Add(wireName, tool.Name);
        }

        return new ModelToolWireNameMap(canonicalToWire, wireToCanonical, maximumWireNameCharacters);
    }

    /// <summary>Returns the provider-safe wire alias for a canonical tool name.</summary>
    public string ToWireName(string canonicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);
        return _canonicalToWire.TryGetValue(canonicalName, out var wireName)
            ? wireName
            : CreatePreferredWireName(canonicalName, _maximumWireNameCharacters);
    }

    /// <summary>Returns the host canonical tool name for a provider-returned wire alias.</summary>
    public string ToCanonicalName(string wireName)
    {
        return string.IsNullOrWhiteSpace(wireName)
            ? wireName
            : _wireToCanonical.TryGetValue(wireName, out var canonicalName)
                ? canonicalName
                : wireName;
    }

    private static string CreatePreferredWireName(string canonicalName, int maximumWireNameCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);
        if (canonicalName.Length <= maximumWireNameCharacters && canonicalName.All(IsWireNameCharacter))
        {
            return canonicalName;
        }

        var builder = new StringBuilder(canonicalName.Length);
        foreach (var character in canonicalName)
        {
            builder.Append(IsWireNameCharacter(character) ? character : '_');
        }

        var sanitized = builder.ToString().Trim('_');
        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "tool";
        }

        return sanitized.Length <= maximumWireNameCharacters
            ? sanitized
            : CreateDigestWireName(canonicalName, sanitized, maximumWireNameCharacters, attempt: 0);
    }

    private static string CreateDigestWireName(
        string canonicalName,
        string preferredName,
        int maximumWireNameCharacters,
        int attempt)
    {
        var digestInput = attempt == 0
            ? canonicalName
            : string.Concat(canonicalName, "#", attempt.ToString(CultureInfo.InvariantCulture));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(digestInput)))[..12];
        var suffix = string.Concat("__", digest);
        var prefixLength = Math.Max(1, maximumWireNameCharacters - suffix.Length);
        var prefix = preferredName.Length <= prefixLength
            ? preferredName
            : preferredName[..prefixLength];
        return string.Concat(prefix.Trim('_'), suffix);
    }

    private static bool IsWireNameCharacter(char character)
    {
        return character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_' or '-';
    }
}
