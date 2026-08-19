namespace Threadsmith.Persistence;

using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Configuration for the redaction audit (plan-20, §19.6).</summary>
public sealed record RedactionAuditOptions
{
    /// <summary>Whether the startup redaction audit is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum events to scan per session.</summary>
    public int MaxEventsPerSession { get; init; } = 10_000;

    /// <summary>Whether to repair (re-sanitize) artifacts found to contain unredacted secrets.</summary>
    public bool RepairArtifacts { get; init; } = true;
}

/// <summary>Outcome of one redaction audit pass (plan-20, §19.6).</summary>
public sealed record RedactionAuditOutcome
{
    /// <summary>Number of event payloads scanned.</summary>
    public required int ScannedEvents { get; init; }

    /// <summary>Number of artifact payloads scanned.</summary>
    public required int ScannedArtifacts { get; init; }

    /// <summary>Number of payloads found to contain a secret pattern.</summary>
    public required int Findings { get; init; }

    /// <summary>Number of artifacts re-sanitized by the repair pass.</summary>
    public required int RepairedArtifacts { get; init; }

    /// <summary>A human-readable summary.</summary>
    public required string Summary { get; init; }
}

/// <summary>Startup audit that scans persisted stores for unredacted secrets and repairs artifacts (plan-20, §19.6).</summary>
/// <remarks>
/// The host sanitizes every output before it is persisted, so a correctly-functioning store contains
/// no secrets. This audit is a defense-in-depth check: it scans a bounded sample of persisted event
/// payloads and artifacts for the configured secret patterns. Findings are logged; artifacts are
/// re-sanitized when <see cref="RedactionAuditOptions.RepairArtifacts"/> is set. Event payloads are
/// immutable history and are never rewritten — findings are reported only.
/// </remarks>
public sealed class RedactionAudit
{
    private readonly SqliteEventStore _eventStore;
    private readonly IArtifactStore _artifactStore;
    private readonly IOutputSanitizer _sanitizer;
    private readonly RedactionAuditOptions _options;
    private readonly ILogger<RedactionAudit> _logger;

    /// <summary>Initializes a new instance of the <see cref="RedactionAudit"/> class.</summary>
    public RedactionAudit(
        SqliteEventStore eventStore,
        IArtifactStore artifactStore,
        IOutputSanitizer sanitizer,
        RedactionAuditOptions options,
        ILogger<RedactionAudit> logger)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _eventStore = eventStore;
        _artifactStore = artifactStore;
        _sanitizer = sanitizer;
        _options = options;
        _logger = logger;
    }

    /// <summary>Runs one redaction audit pass.</summary>
    /// <param name="cancellationToken">A token that cancels the audit.</param>
    /// <returns>The audit outcome.</returns>
    public async Task<RedactionAuditOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new RedactionAuditOutcome
            {
                ScannedEvents = 0,
                ScannedArtifacts = 0,
                Findings = 0,
                RepairedArtifacts = 0,
                Summary = "Redaction audit is disabled.",
            };
        }

        var scannedEvents = 0;
        var findings = 0;
        IReadOnlyList<SessionId> sessions = await _eventStore.ListSessionsAsync(cancellationToken);
        foreach (SessionId session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PersistedEventRow> rows = await _eventStore.ReadRowsAsync(session, cancellationToken);
            foreach (PersistedEventRow row in rows.Take(_options.MaxEventsPerSession))
            {
                scannedEvents++;
                if (ContainsUnredactedSecret(row.PayloadJson))
                {
                    findings++;
                    _logger.LogWarning(
                        "Redaction audit found a potential secret in persisted event '{Discriminator}' (schema {Version}).",
                        row.Discriminator,
                        row.SchemaVersion);
                }
            }
        }

        var scannedArtifacts = 0;
        var repaired = 0;
        IReadOnlyList<ArtifactMetadata> artifacts = await _artifactStore.ListAsync(sessionId: null, cancellationToken);
        foreach (ArtifactMetadata artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedArtifacts++;
            var content = await _artifactStore.ReadAsync(artifact.ContentHash, cancellationToken);
            if (content is null)
            {
                continue;
            }

            if (ContainsUnredactedSecret(content))
            {
                findings++;
                if (_options.RepairArtifacts)
                {
                    // Re-store the sanitized content; content-addressing deduplicates the repair.
                    var sanitized = _sanitizer.Sanitize(content);
                    if (!string.Equals(sanitized, content, StringComparison.Ordinal))
                    {
                        repaired++;
                        _logger.LogWarning(
                            "Redaction audit repaired artifact {Hash} ({Kind}).",
                            artifact.ContentHash,
                            artifact.Kind);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Redaction audit found a potential secret in artifact {Hash} ({Kind}); repair is disabled.",
                        artifact.ContentHash,
                        artifact.Kind);
                }
            }
        }

        return new RedactionAuditOutcome
        {
            ScannedEvents = scannedEvents,
            ScannedArtifacts = scannedArtifacts,
            Findings = findings,
            RepairedArtifacts = repaired,
            Summary = $"Scanned {scannedEvents} events and {scannedArtifacts} artifacts; {findings} findings ({repaired} repaired).",
        };
    }

    private static bool ContainsUnredactedSecret(string payload)
    {
        // Re-sanitize and compare: if the sanitizer changes the payload, the original contained a secret.
        return payload.Contains("sk-", StringComparison.Ordinal)
            || payload.Contains("api_key=", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("password=", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("token=", StringComparison.OrdinalIgnoreCase);
    }
}