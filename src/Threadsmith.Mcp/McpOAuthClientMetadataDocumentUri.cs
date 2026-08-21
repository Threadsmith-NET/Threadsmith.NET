namespace Threadsmith.Mcp;

/// <summary>Validates OAuth Client ID Metadata Document URI requirements used by MCP OAuth profiles.</summary>
internal static class McpOAuthClientMetadataDocumentUri
{
    /// <summary>Human-readable requirements for configuration validation errors.</summary>
    public const string Requirements = "must be an HTTPS URL with a non-root absolute path, no fragment, no user info, and no dot path segments";

    /// <summary>Parses and validates a configured Client ID Metadata Document URI without hiding raw dot segments.</summary>
    /// <param name="configuredText">The configured URI text to validate.</param>
    /// <param name="uri">The parsed URI when validation succeeds.</param>
    /// <returns><see langword="true" /> when the configured text satisfies the required shape.</returns>
    public static bool TryCreate(string configuredText, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(configuredText) || ContainsUnsafePath(configuredText))
        {
            return false;
        }

        if (!Uri.TryCreate(configuredText, UriKind.Absolute, out var candidate) || !IsValid(candidate))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    /// <summary>Checks whether the URI is valid for use as a Client ID Metadata Document URI.</summary>
    /// <param name="uri">The URI to validate.</param>
    /// <returns><see langword="true" /> when the URI satisfies the required shape.</returns>
    public static bool IsValid(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Length > 1
            && string.IsNullOrEmpty(uri.Fragment)
            && string.IsNullOrEmpty(uri.UserInfo)
            && !ContainsUnsafePath(uri.OriginalString);
    }

    private static bool ContainsUnsafePath(string configuredText)
    {
        var schemeSeparator = configuredText.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return false;
        }

        var pathStart = configuredText.IndexOf('/', schemeSeparator + 3);
        if (pathStart < 0)
        {
            return false;
        }

        var pathEnd = configuredText.IndexOfAny(['?', '#'], pathStart);
        var path = pathEnd < 0
            ? configuredText[pathStart..]
            : configuredText[pathStart..pathEnd];
        if (path.Contains('\\'))
        {
            return true;
        }

        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return true;
        }

        // Encoded separators are rejected instead of being normalized because different origins
        // and proxies disagree about when they are decoded.
        if (decodedPath.Contains('\\')
            || decodedPath.Count(character => character == '/') != path.Count(character => character == '/'))
        {
            return true;
        }

        foreach (var segment in decodedPath.Split('/'))
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
