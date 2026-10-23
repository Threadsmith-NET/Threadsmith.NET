namespace Threadsmith.Persistence;

using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Telemetry;

/// <summary>Rebuilds projections from the durable event stream with schema-version tolerance (strategy §19.4, gap #3).</summary>
/// <remarks>
/// Restoration never throws for a single bad event. Events with an older schema version are migrated
/// when a migrator is registered, otherwise marked <see cref="LegacyState.Legacy"/> and skipped. A
/// restored session with any legacy events is reported as partial so the host can show a read-only
/// banner (plan-18 open decision: read-only + banner).
/// </remarks>
public sealed class SessionRestorer : ISessionRestorer
{
    private readonly SqliteEventStore _eventStore;
    private readonly DomainEventMigrationRegistry _migrationRegistry;
    private readonly IConversationStore? _conversationStore;
    private readonly ILogger<SessionRestorer> _logger;
    private readonly IOutputSanitizer _sanitizer;

    /// <summary>Initializes a new instance of the <see cref="SessionRestorer"/> class.</summary>
    public SessionRestorer(
        SqliteEventStore eventStore,
        DomainEventMigrationRegistry migrationRegistry,
        ILogger<SessionRestorer> logger,
        IConversationStore? conversationStore = null,
        IOutputSanitizer? sanitizer = null)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(migrationRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        _eventStore = eventStore;
        _migrationRegistry = migrationRegistry;
        _logger = logger;
        _conversationStore = conversationStore;
        _sanitizer = sanitizer ?? new SecretOutputSanitizer();
    }

    /// <inheritdoc />
    public async Task<SessionRestorationResult> RestoreAsync(
        SessionId sessionId,
        IProjectionStore projectionStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectionStore);
        var rows = await _eventStore.ReadRowsAsync(
            sessionId,
            cancellationToken);
        var replayed = 0;
        var migrated = 0;
        var legacy = 0;
        var warnings = new List<string>();
        var questions = new Dictionary<Guid, UserQuestionStateChanged>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = _migrationRegistry.Classify(
                row.Discriminator,
                row.SchemaVersion,
                row.PayloadJson);
            if (outcome.State == LegacyState.Legacy)
            {
                legacy++;
                warnings.Add(
                    $"Event '{row.Discriminator}' at schema {row.SchemaVersion} could not be migrated and was skipped.");
                continue;
            }

            if (outcome.State == LegacyState.Migrated)
            {
                migrated++;
            }

            IDomainEvent domainEvent;
            try
            {
                if (row.Discriminator == "userQuestionStateChanged"
                    && System.Text.Encoding.UTF8.GetByteCount(outcome.PayloadJson) > 64 * 1024)
                {
                    throw new InvalidDataException("The persisted question envelope exceeds its bounded payload.");
                }

                domainEvent = DomainEventJson.Deserialize(
                    row.Discriminator,
                    outcome.SchemaVersion,
                    outcome.PayloadJson);
            }
            catch (Exception exception)
            {
                // Never throw mid-restore (gap #3). Skip the unparseable event and record a warning.
                legacy++;
                warnings.Add(
                    $"Event '{row.Discriminator}' at schema {outcome.SchemaVersion} failed to deserialize: {(row.Discriminator == "userQuestionStateChanged" ? "Invalid clarification payload." : exception.Message)}");
                continue;
            }

            try
            {
                if (domainEvent is UserQuestionStateChanged question)
                {
                    var safeRequest = UserQuestionSanitizer.Sanitize(question.Request, _sanitizer);
                    question = question with { Request = safeRequest, Result = UserQuestionSanitizer.Sanitize(safeRequest, question.Result, _sanitizer) };
                    domainEvent = question;
                    if (question.Request.SessionId != sessionId || question.SessionId != sessionId
                        || question.Request.RequestId == Guid.Empty || !Enum.IsDefined(question.Result.Status)
                        || (question.Result.Status != UserQuestionStatus.Answered && question.Result.Answers.Count != 0))
                    {
                        throw new InvalidDataException("The persisted question lifecycle is invalid.");
                    }

                    UserQuestionValidation.ValidateQuestions(question.Request.Questions);
                    if (question.Result.Status == UserQuestionStatus.Answered)
                    {
                        UserQuestionValidation.ValidateAnswers(question.Request.Questions, question.Result.Answers);
                    }

                    if (!question.Consumed && question.Result.Status is UserQuestionStatus.Pending or UserQuestionStatus.Answered)
                    {
                        questions[question.Request.RequestId] = question;
                    }
                    else
                    {
                        questions.Remove(question.Request.RequestId);
                    }
                }

                await projectionStore.ApplyAsync(domainEvent, cancellationToken);
                replayed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A projection that throws on one event must not abort the whole restore.
                legacy++;
                warnings.Add(
                    $"Projection failed to apply event '{row.Discriminator}': {(row.Discriminator == "userQuestionStateChanged" ? "Invalid clarification payload." : exception.Message)}");
            }
        }

        foreach (var question in questions.Values)
        {
            var interrupted = question with { OccurredAt = DateTimeOffset.UtcNow, Result = new(UserQuestionStatus.Interrupted, []) };
            await _eventStore.AppendAsync(interrupted, cancellationToken);
            await projectionStore.ApplyAsync(interrupted, cancellationToken);
            warnings.Add($"Question {question.Request.RequestId:D} was interrupted. Its prior tool call will not resume; start a fresh turn explicitly.");
        }

        if (legacy > 0)
        {
            _logger.LogWarning(
                "Session {SessionId} restored with {Legacy} legacy events and {Replayed} replayed events.",
                sessionId.Value,
                legacy,
                replayed);
        }

        var conversation = _conversationStore is null
            ? null
            : await _conversationStore.GetSnapshotAsync(
                sessionId,
                includeBodies: false,
                cancellationToken: cancellationToken);
        return new SessionRestorationResult
        {
            SessionId = sessionId,
            ReplayedEvents = replayed,
            MigratedEvents = migrated,
            LegacyEvents = legacy,
            IsLegacy = legacy > 0,
            Succeeded = true,
            Warnings = warnings.Count > 0 ? string.Join(Environment.NewLine, warnings) : null,
            Conversation = conversation,
        };
    }
}
