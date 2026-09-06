namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>The previously delivered evidence to retrieve.</summary>
internal sealed record ChildAgentEvidenceInput(Guid EvidenceId);

/// <summary>A request-local read capability for evidence already delivered to one child.</summary>
internal sealed class ChildAgentEvidenceTool : Tool<ChildAgentEvidenceInput, string>
{
    /// <summary>Child-local capability identifier.</summary>
    internal const string ToolId = "read_agent_evidence";

    private const string InputSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["evidenceId"],"properties":{"evidenceId":{"type":"string","format":"uuid"}}}
        """;

    private readonly IEvidenceStore _store;
    private readonly SessionId _sessionId;
    private readonly RunId _runId;
    private readonly IReadOnlySet<EvidenceId> _delivered;
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="ChildAgentEvidenceTool"/> class.</summary>
    public ChildAgentEvidenceTool(
        IEvidenceStore store,
        SessionId sessionId,
        RunId runId,
        IReadOnlySet<EvidenceId> delivered,
        IPromptLoader prompts)
    {
        _store = store;
        _sessionId = sessionId;
        _runId = runId;
        _delivered = delivered;
        _definition = new ToolDefinition
        {
            Id = ToolId,
            Version = "1.0",
            DisplayName = "Read Agent Evidence",
            Description = prompts.Get(PromptFileNames.ToolReadAgentEvidenceDescription),
            Category = ToolCategory.SystemInformation,
            RequiredTrust = RepositoryTrustLevel.UntrustedInspection,
            RequiredApproval = ApprovalLevel.None,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = Timeout.InfiniteTimeSpan,
            MaximumOutputBytes = int.MaxValue,
            InputSchema = new ToolSchema("child-evidence-input", 1, InputSchemaJson),
            OutputSchema = new ToolSchema("child-evidence-output", 1, """{"type":"string"}"""),
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override Task<ToolExecution<string>> ExecuteAsync(
        ChildAgentEvidenceInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.SessionId != _sessionId || context.RunId != _runId
            || !_delivered.Contains(new EvidenceId(input.EvidenceId)))
        {
            throw new UnauthorizedAccessException("This evidence was not delivered to this child.");
        }

        var evidence = _store.Snapshot(_sessionId).SingleOrDefault(item => item.EvidenceId.Value == input.EvidenceId);
        if (evidence is null || evidence.IsStale)
        {
            throw new InvalidOperationException("The requested evidence is missing or stale.");
        }

        return Task.FromResult(new ToolExecution<string>(
            evidence.Content,
            [new ToolProvenanceSource("evidence", input.EvidenceId.ToString("D"))],
            ModelResultContent: evidence.Content));
    }

    /// <inheritdoc />
    protected override void ValidateInput(ChildAgentEvidenceInput input)
    {
        if (input.EvidenceId == Guid.Empty)
        {
            throw new ToolArgumentValidationException("An evidence ID is required.");
        }
    }
}
