namespace Threadsmith.Tools;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>User-action origins accepted for outbound consent.</summary>
public enum OutboundConsentOrigin
{
    /// <summary>An interactive disclosure was affirmed by the user.</summary>
    ExplicitInteractive,

    /// <summary>An explicit headless consent command was issued by the user.</summary>
    ExplicitHeadless,
}

/// <summary>Versioned, repository-bound authorization for one outbound tool.</summary>
public sealed record OutboundConsentRecord
{
    /// <summary>Current record schema covering search, retrieval, and current-message URL authorization.</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>Supported record schema; omitted values remain invalid and require re-consent.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Stable tool id.</summary>
    public required string ToolId { get; init; }

    /// <summary>Host-derived opaque repository identity.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Proof that a host-owned user action created the record.</summary>
    public OutboundConsentOrigin Origin { get; init; }

    /// <summary>UTC time of the affirmative user action.</summary>
    public DateTimeOffset GrantedAt { get; init; }
}

/// <summary>Persists outbound consent in user-owned state outside repository control.</summary>
public sealed class OutboundConsentStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly Lock _gate = new();
    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="OutboundConsentStore"/> class.</summary>
    public OutboundConsentStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Threadsmith",
            "outbound-consent.json");
    }

    /// <summary>Returns whether a current, correctly scoped consent record exists.</summary>
    public bool HasValidConsent(string toolId, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        lock (_gate)
        {
            var consentToolId = string.Equals(toolId, "web_fetch", StringComparison.OrdinalIgnoreCase)
                ? "web_search"
                : toolId;
            return ReadCore().Any(record => record.SchemaVersion is 2 or OutboundConsentRecord.CurrentSchemaVersion
                && string.Equals(record.ToolId, consentToolId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.RepositoryIdentity, DeriveRepositoryIdentity(repositoryRoot), StringComparison.Ordinal)
                && Enum.IsDefined(record.Origin));
        }
    }

    /// <summary>Returns whether consent includes current-message exact-URL authorization.</summary>
    public bool HasCurrentMessageUrlConsent(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        lock (_gate)
        {
            return ReadCore().Any(record => record.SchemaVersion == OutboundConsentRecord.CurrentSchemaVersion
                && string.Equals(record.ToolId, "web_search", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.RepositoryIdentity, DeriveRepositoryIdentity(repositoryRoot), StringComparison.Ordinal)
                && Enum.IsDefined(record.Origin));
        }
    }

    /// <summary>Records an explicit host-owned user consent action.</summary>
    public Task GrantAsync(
        string toolId,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        return GrantAsync(
                toolId,
                repositoryRoot,
                OutboundConsentOrigin.ExplicitInteractive,
                OutboundConsentRecord.CurrentSchemaVersion,
                cancellationToken);
    }

    /// <summary>Records an explicit user consent action with its host surface provenance.</summary>
    public async Task GrantAsync(
        string toolId,
        string repositoryRoot,
        OutboundConsentOrigin origin,
        int schemaVersion = OutboundConsentRecord.CurrentSchemaVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        cancellationToken.ThrowIfCancellationRequested();
        if (schemaVersion is not 2 and not OutboundConsentRecord.CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        OutboundConsentRecord[] records;
        lock (_gate)
        {
            var identity = DeriveRepositoryIdentity(repositoryRoot);
            records =
            [
                .. ReadCore().Where(record => !string.Equals(record.ToolId, toolId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(record.RepositoryIdentity, identity, StringComparison.Ordinal)),
                new OutboundConsentRecord
                {
                    SchemaVersion = schemaVersion,
                    ToolId = toolId,
                    RepositoryIdentity = identity,
                    Origin = origin,
                    GrantedAt = DateTimeOffset.UtcNow,
                },
            ];
        }

        await WriteAsync(records, cancellationToken);
    }

    /// <summary>Revokes consent for a tool in one repository.</summary>
    public async Task RevokeAsync(
        string toolId,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var identity = DeriveRepositoryIdentity(repositoryRoot);
        OutboundConsentRecord[] records;
        lock (_gate)
        {
            records = [.. ReadCore().Where(record => !string.Equals(record.ToolId, toolId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(record.RepositoryIdentity, identity, StringComparison.Ordinal))];
        }

        await WriteAsync(records, cancellationToken);
    }

    /// <summary>Derives a non-reversible identity from the canonical repository path.</summary>
    public static string DeriveRepositoryIdentity(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (OperatingSystem.IsWindows())
        {
            canonical = canonical.ToUpperInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private IReadOnlyList<OutboundConsentRecord> ReadCore()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<OutboundConsentRecord[]>(File.ReadAllText(_path)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task WriteAsync(OutboundConsentRecord[] records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Consent store path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(records, _jsonOptions), cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }
}
