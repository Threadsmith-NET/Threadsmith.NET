namespace Threadsmith.Skills;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Owns exact review orchestration, private durable launch records, deterministic join and idempotent report delivery.</summary>
public sealed class FocusedReviewWorkflow : ISkillReviewActionHandler
{
    private readonly FocusedReviewPrivateResolver _private;
    private readonly FocusedReviewTargetCapture _capture;
    private readonly IFocusedReviewExecutor _executor;
    private readonly Func<SkillInvocationRequest, CancellationToken, Task<ToolInvocationContext>> _authority;
    private readonly string _stateRoot;
    private readonly int _maximumCorrections;
    private readonly IDomainEventStream? _events;

    /// <summary>Initializes a new instance of the <see cref="FocusedReviewWorkflow"/> class.</summary>
    public FocusedReviewWorkflow(
        FocusedReviewPrivateResolver privateResolver,
        FocusedReviewTargetCapture capture,
        IFocusedReviewExecutor executor,
        Func<SkillInvocationRequest, CancellationToken, Task<ToolInvocationContext>> authority,
        string stateRoot,
        int maximumCorrections = 0,
        IDomainEventStream? events = null)
    {
        _private = privateResolver ?? throw new ArgumentNullException(nameof(privateResolver));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _stateRoot = Path.GetFullPath(stateRoot);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCorrections);
        _maximumCorrections = maximumCorrections;
        _events = events;
    }

    /// <inheritdoc />
    public SkillBudget DefaultBudget { get; } = new() { ModelTurns = 64, ToolCalls = 128 };

    /// <inheritdoc />
    public bool Handles(SkillCatalogCandidate candidate, SkillWorkflowStep step) => _private.Handles(candidate, step);

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(
        SkillCatalogCandidate candidate,
        SkillInvocationPlan plan,
        SkillWorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        Task ReportAsync(string message, CancellationToken token) => _events?.PublishAsync(
            new SkillInvocationProgressObserved(checkpoint.SessionId, DateTimeOffset.UtcNow, checkpoint.InvocationId, checkpoint.Generation, message) { RunId = checkpoint.RunId }, token) ?? Task.CompletedTask;

        await _private.VerifyEntryAsync(candidate, plan, cancellationToken);
        var authority = await _authority(plan.Request, cancellationToken);
        if (authority.TrustLevel < RepositoryTrustLevel.TrustedRead || authority.WorkspaceId != plan.Request.WorkspaceId)
        {
            throw new UnauthorizedAccessException("Review trust or workspace authority changed.");
        }

        var input = FocusedReviewInput.Parse(plan.Request.InputJson);
        Directory.CreateDirectory(_stateRoot);
        var recordPath = SkillPathPolicy.ResolveConfined(_stateRoot, plan.Request.InvocationId.Value.ToString("N") + ".json");

        // Exclusive ownership lasts through delivery. A second process cannot dispatch the same persisted invocation.
        await using var lease = new FileStream(recordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ReviewRecord? record = null;
        if (File.Exists(recordPath))
        {
            record = JsonSerializer.Deserialize<ReviewRecord>(
                await File.ReadAllTextAsync(recordPath, cancellationToken))
                ?? throw new InvalidDataException("Stored review state is unreadable.");
            if (record.Version != 2 || record.InvocationId != plan.Request.InvocationId || record.Package != plan.Package
                || record.InputJson != plan.Request.InputJson || record.SessionId != plan.Request.SessionId)
            {
                throw new InvalidDataException("Stored review provenance does not match this invocation.");
            }
        }

        var target = record?.Target ?? await _capture.CaptureAsync(input, plan.Request, authority, cancellationToken, ReportAsync);
        foreach (var file in target.Files)
        {
            _ = ReviewPathAccess.Resolve(file.Path, authority);
        }

        if (target.Requirements is { Source: "workspace" } requirements)
        {
            _ = ReviewPathAccess.Resolve(requirements.Path, authority);
        }

        var targetDigest = FocusedReviewTargetCapture.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(target)));
        if (record is not null && record.TargetDigest != targetDigest)
        {
            throw new InvalidDataException("Stored review target content changed.");
        }

        var generation = record?.Generation ?? checkpoint.Generation;
        var procedures = await _private.ResolveAsync(candidate, plan, target, generation, _maximumCorrections, cancellationToken);
        if (record is null)
        {
            await ReportAsync("Preparing reviewers", cancellationToken);
            var batches = await _executor.PrepareAsync(plan, target, procedures, cancellationToken);
            record = new ReviewRecord(
                2,
                plan.Request.InvocationId,
                plan.Request.SessionId,
                plan.Package,
                plan.Request.InputJson,
                generation,
                target,
                batches,
                [],
                [],
                null,
                null,
                null,
                null,
                false) { TargetDigest = targetDigest };
            await SaveAsync(recordPath, record, cancellationToken);
        }

        if (record.Markdown is null)
        {
            var outcomes = record.Outcomes.ToList();
            foreach (var batch in record.Batches)
            {
                if (record.CompletedBatches.Contains(batch.DelegationId))
                {
                    continue;
                }

                var restoreOnly = record.StartedBatches.Contains(batch.DelegationId);
                if (!restoreOnly)
                {
                    record = record with { StartedBatches = [.. record.StartedBatches, batch.DelegationId] };
                    await SaveAsync(recordPath, record, cancellationToken);
                }

                await ReportAsync("Running reviewers", cancellationToken);
                var result = await _executor.ExecuteAsync(plan, batch, target, procedures, restoreOnly, cancellationToken);
                outcomes.AddRange(result);
                record = record with { Outcomes = outcomes.ToArray(), CompletedBatches = [.. record.CompletedBatches, batch.DelegationId] };
                await SaveAsync(recordPath, record, CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
            }

            string? inbox = null;
            string? deliveryError = null;
            try
            {
                inbox = FocusedReviewReportWriter.ResolveInbox(target, authority);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                deliveryError = "The existing inbox destination is invalid or unauthorized. Review content is retained; repair the destination and resume delivery.";
            }

            await ReportAsync("Writing review report", cancellationToken);
            var markdown = FocusedReviewReportFormatter.Render(target, outcomes, inboxLinks: inbox is not null || deliveryError is not null);
            var filename = $"review-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{plan.Request.InvocationId.Value:N}.md";
            record = record with
            {
                Markdown = markdown,
                ContentDigest = FocusedReviewTargetCapture.Hash(Encoding.UTF8.GetBytes(markdown)),
                SavedPath = inbox is not null ? Path.Combine(inbox, filename) : null,
                DeliveryMode = deliveryError is not null ? "failed" : inbox is null ? "console" : "inbox",
            };
            await SaveAsync(recordPath, record, cancellationToken);
        }

        var status = record.Outcomes.Count == 4 && record.Outcomes.All(
            outcome => outcome.Status == AgentRunStatus.Completed && outcome.FocusedReviewValidated)
            && target.Exclusions.Count == 0 ? "complete" : "partial";
        var contentDigest = record.ContentDigest ?? throw new InvalidDataException("Missing report identity.");
        var report = record.Markdown ?? throw new InvalidDataException("Missing canonical report.");
        if (FocusedReviewTargetCapture.Hash(Encoding.UTF8.GetBytes(report)) != contentDigest)
        {
            throw new InvalidDataException("Stored report content changed.");
        }

        string? error = null;
        if (record.DeliveryMode is "inbox" or "failed")
        {
            try
            {
                if (record.SavedPath is null)
                {
                    var inbox = FocusedReviewReportWriter.ResolveInbox(
                        target,
                        authority)
                        ?? throw new IOException("The previously invalid inbox must be repaired before delivery.");
                    record = record with { SavedPath = Path.Combine(inbox, $"review-{plan.Request.InvocationId.Value:N}.md"), DeliveryMode = "inbox" };
                    await SaveAsync(recordPath, record, cancellationToken);
                }

                await FocusedReviewReportWriter.PublishAsync(record.SavedPath, report, contentDigest, target, authority, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = "Report delivery failed; the canonical report is retained. Repair the invoking repository inbox and resume; no new inference is needed.";
            }
        }

        if (error is not null)
        {
            throw new IOException(error);
        }

        record = record with { Delivered = true };
        await SaveAsync(recordPath, record, CancellationToken.None);
        var delivery = new FocusedReviewDelivery(
            status,
            record.DeliveryMode ?? "console",
            contentDigest,
            record.SavedPath,
            record.DeliveryMode == "console" ? report : null,
            null);
        return JsonSerializer.Serialize(delivery, FocusedReviewInput.JsonOptions);
    }

    private static async Task SaveAsync(
        string path,
        ReviewRecord record,
        CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record), new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record ReviewRecord(
        int Version,
        SkillInvocationId InvocationId,
        SessionId SessionId,
        SkillPackageIdentity Package,
        string InputJson,
        int Generation,
        FocusedReviewTarget Target,
        IReadOnlyList<DelegationPlan> Batches,
        IReadOnlyList<DelegationId> StartedBatches,
        IReadOnlyList<DelegationId> CompletedBatches,
        string? Markdown,
        string? ContentDigest,
        string? SavedPath,
        string? DeliveryMode,
        bool Delivered)
    {
        public string TargetDigest { get; init; } = string.Empty;

        public IReadOnlyList<AgentRunOutcome> Outcomes { get; init; } = [];
    }
}
