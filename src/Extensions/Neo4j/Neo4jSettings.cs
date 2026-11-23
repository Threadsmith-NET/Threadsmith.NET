namespace Threadsmith.Extensions.Neo4j;

using System.Globalization;
using Threadsmith.Extensions.Abstractions;

/// <summary>Immutable, generation-owned connection settings and resource limits.</summary>
/// <remarks>
/// Settings are validated during activation without opening a connection or resolving a credential.
/// A model argument can never change the destination, credential, access mode, or limits recorded here.
/// </remarks>
internal sealed record Neo4jSettings
{
    /// <summary>Lowest accepted query timeout in seconds.</summary>
    internal const int MinimumQueryTimeoutSeconds = 1;

    /// <summary>Highest accepted query timeout in seconds.</summary>
    internal const int MaximumQueryTimeoutSeconds = 300;

    /// <summary>Lowest accepted retained-row limit.</summary>
    internal const int MinimumRows = 1;

    /// <summary>Highest accepted retained-row limit.</summary>
    internal const int MaximumRowLimit = 10_000;

    /// <summary>Lowest accepted retained-byte limit.</summary>
    internal const int MinimumResultBytes = 4_096;

    /// <summary>Highest accepted retained-byte limit.</summary>
    internal const int MaximumResultByteLimit = 262_144;

    /// <summary>Lowest accepted schema sample limit.</summary>
    internal const int MinimumSchemaSampleLimit = 1;

    /// <summary>Highest accepted schema sample limit.</summary>
    internal const int MaximumSchemaSampleLimit = 10_000;

    /// <summary>Port used when the configured address omits one.</summary>
    internal const int DefaultPort = 7687;

    private static readonly string[] _recognizedKeys =
    [
        "uri",
        "database",
        "username",
        "passwordReference",
        "readOnly",
        "queryTimeoutSeconds",
        "maximumRows",
        "maximumResultBytes",
        "schemaSampleLimit",
        "allowInsecureLocalConnections",
    ];

    private static readonly string[] _supportedSchemes =
    [
        "bolt",
        "bolt+s",
        "bolt+ssc",
        "neo4j",
        "neo4j+s",
        "neo4j+ssc",
    ];

    private static readonly string[] _validatedTlsSchemes = ["bolt+s", "neo4j+s"];

    private static readonly string[] _selfSignedTlsSchemes = ["bolt+ssc", "neo4j+ssc"];

    /// <summary>The configured address passed to the driver.</summary>
    public required string Endpoint { get; init; }

    /// <summary>The scheme, host, and port shown in operator review and provenance.</summary>
    public required string Destination { get; init; }

    /// <summary>The target database name.</summary>
    public required string Database { get; init; }

    /// <summary>The configured least-privilege account name.</summary>
    public required string Username { get; init; }

    /// <summary>The logical reference the host resolves during admitted execution.</summary>
    public required string PasswordReference { get; init; }

    /// <summary>Whether execution uses driver read mode without approval. The model cannot override it.</summary>
    /// <remarks>Database account permissions remain the operator's responsibility.</remarks>
    public required bool ReadOnly { get; init; }

    /// <summary>Whether the configured scheme uses cluster routing.</summary>
    public required bool Routed { get; init; }

    /// <summary>Whether the transport is encrypted.</summary>
    public required bool Encrypted { get; init; }

    /// <summary>The server-side transaction timeout.</summary>
    public required TimeSpan QueryTimeout { get; init; }

    /// <summary>Maximum number of records retained in a result.</summary>
    public required int MaximumRows { get; init; }

    /// <summary>Maximum number of serialized record bytes retained in a result.</summary>
    public required int MaximumResultBytes { get; init; }

    /// <summary>Upper bound a schema request cannot raise.</summary>
    public required int SchemaSampleLimit { get; init; }

    /// <summary>Validates the generation's settings section without performing any I/O.</summary>
    /// <param name="configuration">The extension's own settings section.</param>
    /// <returns>The validated settings.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a required value is missing or unsafe.</exception>
    internal static Neo4jSettings Create(IExtensionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var key in configuration.Keys)
        {
            if (!_recognizedKeys.Contains(key, StringComparer.Ordinal))
            {
                throw Invalid("settings", "contain an unrecognized key; remove keys the extension does not define");
            }
        }

        var endpoint = ReadEndpoint(configuration);
        var allowInsecure = ReadBoolean(configuration, "allowInsecureLocalConnections", defaultValue: false);
        var encrypted = ValidateTransport(endpoint, allowInsecure);
        var port = endpoint.Port > 0 ? endpoint.Port : DefaultPort;
        return new Neo4jSettings
        {
            Endpoint = endpoint.OriginalString,
            Destination = string.Create(
                CultureInfo.InvariantCulture,
                $"{endpoint.Scheme}://{endpoint.Host}:{port}"),
            Database = ReadName(configuration, "database"),
            Username = ReadRequired(configuration, "username"),
            PasswordReference = ReadRequired(configuration, "passwordReference"),
            ReadOnly = ReadBoolean(configuration, "readOnly", defaultValue: true),
            Routed = endpoint.Scheme.StartsWith("neo4j", StringComparison.Ordinal),
            Encrypted = encrypted,
            QueryTimeout = TimeSpan.FromSeconds(ReadInteger(
                configuration,
                "queryTimeoutSeconds",
                30,
                MinimumQueryTimeoutSeconds,
                MaximumQueryTimeoutSeconds)),
            MaximumRows = ReadInteger(configuration, "maximumRows", 500, MinimumRows, MaximumRowLimit),
            MaximumResultBytes = ReadInteger(
                configuration,
                "maximumResultBytes",
                65_536,
                MinimumResultBytes,
                MaximumResultByteLimit),
            SchemaSampleLimit = ReadInteger(
                configuration,
                "schemaSampleLimit",
                1_000,
                MinimumSchemaSampleLimit,
                MaximumSchemaSampleLimit),
        };
    }

    private static Uri ReadEndpoint(IExtensionConfiguration configuration)
    {
        var value = ReadRequired(configuration, "uri");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || !_supportedSchemes.Contains(endpoint.Scheme, StringComparer.Ordinal)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.AbsolutePath.Length > 1
            || string.IsNullOrEmpty(endpoint.Host))
        {
            throw Invalid(
                "uri",
                "must be a credential-free bolt, bolt+s, bolt+ssc, neo4j, neo4j+s, or neo4j+ssc address with a host and no path");
        }

        return endpoint;
    }

    private static bool ValidateTransport(Uri endpoint, bool allowInsecure)
    {
        if (_validatedTlsSchemes.Contains(endpoint.Scheme, StringComparer.Ordinal))
        {
            return true;
        }

        if (!allowInsecure)
        {
            throw Invalid(
                "uri",
                "must use bolt+s or neo4j+s so TLS certificates are validated, unless 'allowInsecureLocalConnections' is true");
        }

        // Routing can return cluster members on other hosts, so the exception covers only the direct
        // schemes, where the configured address is the single destination.
        if (endpoint.Scheme.StartsWith("neo4j", StringComparison.Ordinal))
        {
            throw Invalid(
                "uri",
                "must use neo4j+s for a routed connection; the local exception covers only bolt and bolt+ssc");
        }

        // The development exception never covers a remote endpoint. A self-signed scheme does not
        // authenticate the server, so the credential and query data must stay on the loopback device.
        if (!endpoint.IsLoopback)
        {
            throw Invalid(
                "uri",
                "may use a self-signed or unencrypted scheme only for a loopback development server");
        }

        return _selfSignedTlsSchemes.Contains(endpoint.Scheme, StringComparer.Ordinal);
    }

    private static string ReadRequired(IExtensionConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(key, "is required");
        }

        return value.Trim();
    }

    private static string ReadName(IExtensionConfiguration configuration, string key)
    {
        var value = ReadRequired(configuration, key);
        if (value.Length > 63 || !value.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            throw Invalid(key, "must be at most 63 ASCII letters, digits, '.', '-', or '_'");
        }

        return value;
    }

    private static bool ReadBoolean(IExtensionConfiguration configuration, string key, bool defaultValue)
    {
        var value = configuration[key];
        if (value is null)
        {
            return defaultValue;
        }

        if (!bool.TryParse(value.Trim(), out var parsed))
        {
            throw Invalid(key, "must be 'true' or 'false'");
        }

        return parsed;
    }

    private static int ReadInteger(
        IExtensionConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var value = configuration[key];
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw Invalid(
                key,
                string.Create(CultureInfo.InvariantCulture, $"must be a whole number from {minimum} to {maximum}"));
        }

        return parsed;
    }

    private static InvalidOperationException Invalid(string key, string requirement)
    {
        // The message names the key and its rule. It never echoes the configured value.
        return new InvalidOperationException($"Neo4j extension setting '{key}' {requirement}.");
    }
}
