namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Input for one bounded request-authorized historical evidence read.</summary>
public sealed record ActiveTurnEvidenceInput(
    Guid EvidenceId,
    int StartLine = 1,
    int? EndLine = null,
    int StartColumn = 1);

/// <summary>Closed recovery outcome for one referenced historical evidence body.</summary>
public enum ActiveTurnEvidenceStatus
{
    /// <summary>The requested bounded content is available.</summary>
    Available,

    /// <summary>The evidence was invalidated after its original delivery.</summary>
    Stale,

    /// <summary>The referenced evidence body is no longer retained.</summary>
    Missing,
}

/// <summary>Bounded historical evidence content and continuation state.</summary>
public sealed record ActiveTurnEvidenceOutput
{
    /// <summary>Recovery outcome.</summary>
    public required ActiveTurnEvidenceStatus Status { get; init; }

    /// <summary>Authorized evidence identity.</summary>
    public required Guid EvidenceId { get; init; }

    /// <summary>Historical sanitized content page, when available.</summary>
    public string? Content { get; init; }

    /// <summary>One-based next line, or null when complete.</summary>
    public int? NextLine { get; init; }

    /// <summary>One-based next column within <see cref="NextLine"/>.</summary>
    public int? NextColumn { get; init; }

    /// <summary>Total lines in the historical evidence body.</summary>
    public int? TotalLines { get; init; }

    /// <summary>Bounded stale or missing explanation.</summary>
    public string? Detail { get; init; }
}

/// <summary>Reads only historical evidence explicitly referenced by the current canonical request.</summary>
public sealed class ActiveTurnEvidenceTool : Tool<ActiveTurnEvidenceInput, ActiveTurnEvidenceOutput>
{
    /// <summary>Stable tool identifier advertised only while exact recovery references are visible.</summary>
    public const string ToolId = "read_active_turn_evidence";

    private const int MaximumOutputBytes = 128 * 1024;
    private const string InputSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["evidenceId"],"properties":{"evidenceId":{"type":"string","format":"uuid"},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1},"startColumn":{"type":"integer","minimum":1}}}
        """;

    private readonly ToolDefinition _definition;
    private readonly IEvidenceStore _store;

    /// <summary>Initializes a new instance of the <see cref="ActiveTurnEvidenceTool"/> class.</summary>
    public ActiveTurnEvidenceTool(IEvidenceStore store, IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(prompts);
        _store = store;
        _definition = new ToolDefinition
        {
            Id = ToolId,
            Version = "1.0",
            DisplayName = "Read Active-Turn Evidence",
            Description = prompts.Get(PromptFileNames.ToolReadActiveTurnEvidenceDescription),
            Category = ToolCategory.SystemInformation,
            RequiredTrust = RepositoryTrustLevel.UntrustedInspection,
            RequiredApproval = ApprovalLevel.None,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = Timeout.InfiniteTimeSpan,
            MaximumOutputBytes = MaximumOutputBytes,
            InputSchema = new ToolSchema("active-turn-evidence-input", 1, InputSchemaJson),
            OutputSchema = new ToolSchema(
                "active-turn-evidence-output",
                1,
                """{"type":"object","additionalProperties":false,"required":["status","evidenceId"],"properties":{"status":{"type":"string","enum":["Available","Stale","Missing"]},"evidenceId":{"type":"string","format":"uuid"},"content":{"type":["string","null"]},"nextLine":{"type":["integer","null"],"minimum":1},"nextColumn":{"type":["integer","null"],"minimum":1},"totalLines":{"type":["integer","null"],"minimum":0},"detail":{"type":["string","null"]}}}"""),
            ConversationAvailable = true,
            SubagentAvailable = false,
            Scheduling = new ToolSchedulingDescriptor
            {
                ConcurrencyMode = ToolConcurrencyMode.ParallelSafe,
                ClaimResolverId = "active-turn-evidence-v1",
            },
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override Task<ToolExecution<ActiveTurnEvidenceOutput>> ExecuteAsync(
        ActiveTurnEvidenceInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var evidenceId = new EvidenceId(input.EvidenceId);
        var reference = context.Invocation.ActiveTurnEvidenceReferences
            .FirstOrDefault(item => item.EvidenceId == evidenceId)
            ?? throw new UnauthorizedAccessException(
                "This evidence is not referenced by the current model request.");

        var evidence = _store.Find(context.SessionId, evidenceId);
        if (evidence?.RunId != context.RunId
            || evidence.Provenance.ToolInvocationId != reference.ToolInvocationId
            || !string.Equals(
                RepositoryIdentity.Create(context.Invocation.RepositoryPath),
                reference.RepositoryIdentity,
                StringComparison.Ordinal))
        {
            return Task.FromResult(CreateResult(
                ActiveTurnEvidenceStatus.Missing,
                input.EvidenceId,
                detail: "The referenced historical evidence body is no longer retained."));
        }

        if (evidence.IsStale)
        {
            return Task.FromResult(CreateResult(
                ActiveTurnEvidenceStatus.Stale,
                input.EvidenceId,
                detail: evidence.StaleReason ?? "The historical evidence was invalidated."));
        }

        var maximumCharacters = TextEvidenceDocument.GetMaximumReadCharacters(
            MaximumOutputBytes,
            envelopeBytes: 512);
        var read = new TextEvidenceDocument(evidence.Content).Read(
            input.StartLine,
            input.EndLine,
            input.StartColumn,
            maximumCharacters);
        var output = new ActiveTurnEvidenceOutput
        {
            Status = ActiveTurnEvidenceStatus.Available,
            EvidenceId = input.EvidenceId,
            Content = read.Content,
            NextLine = read.NextLine,
            NextColumn = read.NextColumn,
            TotalLines = read.TotalLines,
        };
        return Task.FromResult(new ToolExecution<ActiveTurnEvidenceOutput>(
            output,
            [new ToolProvenanceSource("evidence", input.EvidenceId.ToString("D"))],
            IsTruncated: read.NextLine is not null));
    }

    /// <inheritdoc />
    protected override void ValidateInput(ActiveTurnEvidenceInput input)
    {
        if (input.EvidenceId == Guid.Empty)
        {
            throw new ToolArgumentValidationException("An evidence ID is required.");
        }

        if (input.StartLine < 1 || input.EndLine is < 1 || input.StartColumn < 1
            || (input.EndLine is { } endLine && endLine < input.StartLine))
        {
            throw new ToolArgumentValidationException(
                "Evidence lines and columns must be positive and ordered.");
        }
    }

    private static ToolExecution<ActiveTurnEvidenceOutput> CreateResult(
        ActiveTurnEvidenceStatus status,
        Guid evidenceId,
        string detail)
    {
        return new ToolExecution<ActiveTurnEvidenceOutput>(
            new ActiveTurnEvidenceOutput
            {
                Status = status,
                EvidenceId = evidenceId,
                Detail = detail,
            },
            []);
    }
}
