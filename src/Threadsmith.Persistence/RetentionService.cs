namespace Threadsmith.Persistence;

using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Configuration for the retention policy (strategy §19.6).</summary>
public sealed record RetentionOptions
{
    /// <summary>Whether age-based retention cleanup is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Minimum session age before retention cleanup may remove it.</summary>
    public TimeSpan SessionAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Whether full prompt artifacts are retained.</summary>
    public bool RetainFullPrompts { get; init; } = true;

    /// <summary>Whether full model output artifacts are retained.</summary>
    public bool RetainFullModelOutput { get; init; } = true;

    /// <summary>Whether process logs are retained.</summary>
    public bool RetainProcessLogs { get; init; } = true;

    /// <summary>Whether source excerpts are retained.</summary>
    public bool RetainSourceExcerpts { get; init; } = true;

    /// <summary>Whether diffs are retained.</summary>
    public bool RetainDiffs { get; init; } = true;

    /// <summary>Whether telemetry artifacts are retained.</summary>
    public bool RetainTelemetry { get; init; } = true;

    /// <summary>Whether session summaries are retained (metadata-only mode).</summary>
    public bool RetainSessionSummaries { get; init; } = true;

    /// <summary>Whether archived conversation bodies survive body-specific retention.</summary>
    public bool RetainConversationBodies { get; init; }

    /// <summary>Independent retention age for full archived message bodies.</summary>
    public TimeSpan ConversationMessageBodyAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Whether metadata-only retention is required (sensitive repositories, §19.6).</summary>
    public bool MetadataOnly { get; init; }
}

/// <summary>Applies the configured retention policy to the durable store (strategy §19.6).</summary>
public sealed class RetentionService
{
    private readonly SqliteEventStore _eventStore;
    private readonly IArtifactStore _artifactStore;
    private readonly RetentionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IConversationStore? _conversationStore;
    private readonly ILogger<RetentionService> _logger;
    private readonly SqliteSkillStateStore? _skillStateStore;

    /// <summary>Initializes a new instance of the <see cref="RetentionService"/> class.</summary>
    public RetentionService(
        SqliteEventStore eventStore,
        IArtifactStore artifactStore,
        RetentionOptions options,
        ILogger<RetentionService> logger,
        TimeProvider? timeProvider = null,
        IConversationStore? conversationStore = null,
        SqliteSkillStateStore? skillStateStore = null)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        if (options.SessionAge <= TimeSpan.Zero || options.ConversationMessageBodyAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retention ages must be positive.");
        }

        _eventStore = eventStore;
        _artifactStore = artifactStore;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _conversationStore = conversationStore;
        _skillStateStore = skillStateStore;
    }

    /// <summary>Gets the configured retention options.</summary>
    public RetentionOptions Options => _options;

    /// <summary>Runs one retention cleanup pass and returns the outcome.</summary>
    /// <param name="cancellationToken">A token that cancels the cleanup.</param>
    /// <returns>The retention outcome.</returns>
    public async Task<RetentionOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new RetentionOutcome(0, 0, "Retention is disabled.");
        }

        var now = _timeProvider.GetUtcNow();
        var cutoff = now - _options.SessionAge;
        var messageBodyCutoff = now - _options.ConversationMessageBodyAge;
        var removedSessions = await _eventStore.DeleteSessionsOlderThanAsync(cutoff, cancellationToken);
        var removedMessageBodies = _conversationStore is not null
            && (!_options.RetainConversationBodies || _options.MetadataOnly)
            ? await _conversationStore.RemoveMessageBodiesOlderThanAsync(messageBodyCutoff, cancellationToken)
            : 0;
        var removedSkillWorkflows = _skillStateStore is null
            ? 0
            : await _skillStateStore.DeleteCheckpointsOlderThanAsync(cutoff, cancellationToken);
        var removedArtifacts = 0;

        // Artifact cleanup is delegated to the artifact store's session-scoped listing; in
        // metadata-only mode artifacts are already sanitized so they remain unless aged.
        var artifacts = await _artifactStore.ListAsync(
            sessionId: null,
            cancellationToken);
        foreach (var artifact in artifacts)
        {
            if (artifact.RecordedAt < cutoff && ShouldRemoveArtifact(artifact.Kind))
            {
                if (await _artifactStore.DeleteAsync(artifact.ContentHash, cancellationToken))
                {
                    removedArtifacts++;
                }
            }
        }

        if (removedSessions > 0
            || removedArtifacts > 0
            || removedMessageBodies > 0
            || removedSkillWorkflows > 0)
        {
            _logger.LogInformation(
                "Retention removed {Sessions} sessions, {Artifacts} aged artifacts, {MessageBodies} conversation bodies, and {SkillWorkflows} skill workflows.",
                removedSessions,
                removedArtifacts,
                removedMessageBodies,
                removedSkillWorkflows);
        }

        return new RetentionOutcome(
            removedSessions,
            removedArtifacts,
            $"Removed {removedSessions} sessions, {removedArtifacts} aged artifacts, {removedMessageBodies} conversation bodies, and {removedSkillWorkflows} skill workflows older than {_options.SessionAge}.",
            removedMessageBodies,
            removedSkillWorkflows);
    }

    private bool ShouldRemoveArtifact(string kind)
    {
        // Conversation retention first detaches archived message references and only then
        // deletes an artifact after confirming that no retained message still uses it.
        if (string.Equals(kind, "conversation-message", StringComparison.Ordinal))
        {
            return false;
        }

        // Metadata-only mode retains no large artifacts regardless of kind.
        if (_options.MetadataOnly)
        {
            return true;
        }

        return kind switch
        {
            "prompt" => !_options.RetainFullPrompts,
            "modelOutput" => !_options.RetainFullModelOutput,
            "processOutput" or "buildLog" or "testLog" => !_options.RetainProcessLogs,
            "sourceExcerpt" => !_options.RetainSourceExcerpts,
            "diff" => !_options.RetainDiffs,
            "telemetry" => !_options.RetainTelemetry,
            "sessionSummary" => !_options.RetainSessionSummaries,
            _ => true,
        };
    }
}

/// <summary>Outcome of one retention cleanup pass.</summary>
public sealed record RetentionOutcome(
    int RemovedSessions,
    int RemovedArtifacts,
    string Summary,
    int RemovedConversationMessageBodies = 0,
    int RemovedSkillWorkflows = 0);
