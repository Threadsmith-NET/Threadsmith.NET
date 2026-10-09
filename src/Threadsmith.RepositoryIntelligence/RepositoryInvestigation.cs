namespace Threadsmith.RepositoryIntelligence;

using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Explicit question, literal scope and bounded invocation-only work.</summary>
internal sealed record RepositoryInvestigationSelection
{
    /// <summary>Concrete repository question, treated as untrusted data.</summary>
    public required string Question { get; init; }

    /// <summary>Explicit literal scope; a single dot selects the checkout root.</summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>Ref resolved once before collection.</summary>
    public string Revision { get; init; } = "HEAD";

    /// <summary>History requires explicit selection.</summary>
    public bool IncludeHistory { get; init; }

    /// <summary>Mutable observations remain separate from committed evidence.</summary>
    public bool IncludeOverlay { get; init; }

    /// <summary>Focused interpretation operation.</summary>
    public RepositoryInterpretationKind Kind { get; init; } = RepositoryInterpretationKind.Intent;

    /// <summary>Cumulative file acquisition ceiling, including profiling.</summary>
    public int MaximumFiles { get; init; } = 32;

    /// <summary>Cumulative historical commit frontier.</summary>
    public int MaximumCommits { get; init; } = 8;

    /// <summary>Cumulative internal inference calls.</summary>
    public int MaximumModelCalls { get; init; } = 6;
}

/// <summary>Completed host-retained result; evidence IDs never retain a live packet.</summary>
internal sealed record RepositoryInvestigationResult(
    string OperationId,
    RepositoryInvestigationSelection Selection,
    RepositoryIntelligenceResourceLimits ResourceLimits,
    GitSnapshotMetadata Target,
    GitSnapshotMetadata? AfterInvestigation,
    bool PendingChanges,
    string HistoryCoverage,
    RepositoryInterpretationResponse Interpretation,
    BoundedModelSelection? Model,
    int ModelCalls,
    long ModelInputBytes,
    long ModelOutputBytes,
    RepositoryEvidenceConsumption Consumption,
    IReadOnlyList<RepositoryEvidenceExcerpt> Evidence,
    IReadOnlyList<RepositoryEvidenceOmission> Omissions,
    string RetentionMode = "InvocationOnlyWithOrdinaryHostSessionRetention",
    string EvidenceLifetime = "ExpiredAtReturn; retained locators require a new authorized source read; retry starts a new operation");

/// <summary>Coordinates the shared profile, evidence and interpretation owners under one admission.</summary>
internal sealed class RepositoryInvestigation
{
    private readonly IToolInvocationPipeline _pipeline;
    private readonly IBoundedModelInference? _inference;
    private readonly IPromptLoader _prompts;

    /// <summary>Initializes a new instance of the <see cref="RepositoryInvestigation"/> class.</summary>
    internal RepositoryInvestigation(IToolInvocationPipeline pipeline, IBoundedModelInference? inference, IPromptLoader prompts)
    {
        _pipeline = pipeline;
        _inference = inference;
        _prompts = prompts;
    }

    /// <summary>Completes all evidence expansion before returning and releasing the live registry.</summary>
    internal async Task<RepositoryInvestigationResult> ExecuteAsync(
        RepositoryIntelligenceFeature feature,
        RepositoryInvestigationSelection request,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        await using var admission = await feature.AdmitAsync(
            RepositoryIdentity.Create(context.Invocation.RepositoryPath), RepositoryIntelligenceControl.Archeology, oneOff: true, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(80));
        return await feature.PrepareAsync(
            admission,
            async token =>
        {
            var selection = request with
            {
                MaximumFiles = Math.Min(request.MaximumFiles, feature.ResourceLimits.MaximumFiles),
                MaximumCommits = request.IncludeHistory ? Math.Min(request.MaximumCommits, feature.ResourceLimits.MaximumCommits) : 0,
                MaximumModelCalls = Math.Min(request.MaximumModelCalls, feature.ResourceLimits.MaximumModelCalls),
            };
            IBoundedModelOperation? operation = null;
            try
            {
                // Freeze provider and caller reasoning before repository acquisition. No inference occurs here.
                if (_inference is not null && selection.MaximumModelCalls > 0)
                {
                    try
                    {
                        operation = _inference.Open(context, selection.MaximumModelCalls, 32768);
                    }
                    catch (InvalidOperationException)
                    {
                        // Deterministic evidence remains available when configured inference is unavailable.
                    }
                }

                var limits = feature.ResourceLimits;
                var inputBytes = limits.Evidence.MaximumInputBytes;
                if (inputBytes < 2)
                {
                    throw new InvalidOperationException("The evidence input budget cannot hold prerequisite collection.");
                }

                limits = limits with
                {
                    Evidence = limits.Evidence with { MaximumProfileMetadataBytes = Math.Min(limits.Evidence.MaximumProfileMetadataBytes, inputBytes / 2) },
                };

                // The explicit root marker maps to the existing readers' empty literal-scope convention.
                var paths = selection.Paths.Contains(".", StringComparer.Ordinal) ? [] : selection.Paths;
                var reads = new RepositoryProfileCollector(_pipeline, limits.Evidence, limits.Git);
                var profile = await reads.CaptureAsync(
                    new RepositoryProfileSelection
                    {
                        Revision = selection.Revision,
                        Paths = paths,
                        MaximumFiles = Math.Min(8, selection.MaximumFiles),
                        MaximumBytes = Math.Min(65536, inputBytes / 2),
                        IncludeOverlay = selection.IncludeOverlay,
                    },
                    context,
                    token);
                var evidenceSelection = new RepositoryEvidenceSelection
                {
                    Question = selection.Question,
                    Paths = paths,
                    Mode = selection.IncludeHistory ? RepositoryEvidenceMode.History : RepositoryEvidenceMode.CurrentSnapshot,
                    MaximumFiles = selection.MaximumFiles,
                    MaximumCommits = selection.MaximumCommits,
                    MaximumInputBytes = inputBytes,
                    MaximumPacketBytes = limits.Evidence.MaximumPacketBytes,
                    MaximumOutputBytes = limits.Evidence.MaximumOutputBytes,
                };
                using var collector = new RepositoryEvidenceCollector(_pipeline, profile, evidenceSelection, limits, context, token);
                var initial = await collector.CollectAsync(evidenceSelection, cancellationToken: token);
                var interpreted = _inference is null
                    ? new RepositoryInterpretationResult(
                        Guid.NewGuid().ToString("N"),
                        selection.Kind,
                        new RepositoryInterpretationResponse(string.Empty, "Unavailable", null, null, [], [], ["Configured inference is unavailable; deterministic evidence remains usable."], [], [], null),
                        null,
                        0,
                        0,
                        0,
                        initial.Evidence,
                        initial.Omissions,
                        initial)
                    : await new RepositoryInterpreter(_inference, _prompts).InterpretAsync(
                        selection.Kind,
                        collector,
                        evidenceSelection,
                        initial,
                        context,
                        operation is null ? 0 : selection.MaximumModelCalls,
                        operation,
                        token);
                var response = interpreted.Response with { OperationId = interpreted.OperationId, Expansion = null };
                GitSnapshotMetadata? after = null;
                var omissions = interpreted.Omissions.ToList();
                try
                {
                    after = (await reads.ReadAsync<GitShowResult>("git_show", new GitShowInput { SnapshotMetadata = true, Revision = "HEAD" }, context, token)).Snapshot;
                }
                catch (Exception exception) when (exception is RepositoryReadUnavailableException or InvalidDataException)
                {
                    // A failed final observation cannot establish that the live checkout still matches the target.
                }

                token.ThrowIfCancellationRequested();
                var pending = interpreted.LastPacket.PendingChanges || after is null
                    || after.Head != profile.Snapshot.Head || after.Branch != profile.Snapshot.Branch
                    || after.RepositoryIdentity != profile.Snapshot.RepositoryIdentity || after.CheckoutIdentity != profile.Snapshot.CheckoutIdentity;
                if (after is null)
                {
                    omissions.Add(new RepositoryEvidenceOmission("FinalSnapshotUnavailable; live checkout compatibility is unverified"));
                }

                var referenced = response.SupportingEvidenceIds.Concat(response.ConflictingEvidenceIds)
                    .Concat(response.Candidates.SelectMany(item => item.EvidenceIds.Concat(item.ConflictingEvidenceIds)))
                    .Concat(response.Relationships.SelectMany(item => item.EvidenceIds)).ToHashSet(StringComparer.Ordinal);
                var evidence = referenced.Count > 0 ? interpreted.Evidence.Where(item => referenced.Contains(item.Id)).ToArray() : interpreted.Evidence;
                return new RepositoryInvestigationResult(
                    interpreted.OperationId,
                    selection,
                    limits,
                    interpreted.LastPacket.Target,
                    after,
                    pending,
                    interpreted.LastPacket.HistoryCoverage,
                    response,
                    interpreted.Model,
                    interpreted.ModelCalls,
                    interpreted.ModelInputBytes,
                    interpreted.ModelOutputBytes,
                    interpreted.LastPacket.Consumption with { ReadCalls = interpreted.LastPacket.Consumption.ReadCalls + reads.ReadCalls - profile.ReadCalls },
                    evidence,
                    omissions);
            }
            finally
            {
                operation?.Dispose();
            }
        },
            deadline.Token);
    }
}
