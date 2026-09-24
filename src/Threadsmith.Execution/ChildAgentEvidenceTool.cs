namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Tools;
using Threadsmith.Tools.PullRequests;

/// <summary>The previously delivered evidence to retrieve.</summary>
internal sealed record ChildAgentEvidenceInput(Guid EvidenceId, int? StartLine = null, int? EndLine = null, int? StartColumn = null);

/// <summary>A request-local read capability for evidence already delivered to one child.</summary>
internal sealed class ChildAgentEvidenceTool : Tool<ChildAgentEvidenceInput, string>
{
    /// <summary>Child-local capability identifier.</summary>
    internal const string ToolId = "read_agent_evidence";

    private const string InputSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["evidenceId"],"properties":{"evidenceId":{"type":"string","format":"uuid"},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1},"startColumn":{"type":"integer","minimum":1}}}
        """;

    private readonly IEvidenceStore _store;
    private readonly SessionId _sessionId;
    private readonly RunId _runId;
    private readonly IReadOnlySet<EvidenceId> _delivered;
    private readonly IReadOnlyDictionary<EvidenceId, PrFetchOutput> _capturedPullRequests;
    private readonly PrEvidenceRegistry? _capturedRegistry;
    private readonly RunId _parentRunId;
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="ChildAgentEvidenceTool"/> class.</summary>
    public ChildAgentEvidenceTool(
        IEvidenceStore store,
        SessionId sessionId,
        RunId runId,
        IReadOnlySet<EvidenceId> delivered,
        IPromptLoader prompts,
        IReadOnlyDictionary<EvidenceId, PrFetchOutput>? capturedPullRequests = null,
        PrEvidenceRegistry? capturedRegistry = null,
        RunId parentRunId = default)
    {
        _store = store;
        _sessionId = sessionId;
        _runId = runId;
        _delivered = delivered;
        _capturedPullRequests = capturedPullRequests ?? new Dictionary<EvidenceId, PrFetchOutput>();
        if (_capturedPullRequests.Count > 0 && capturedRegistry is null)
        {
            throw new ArgumentException("Captured PR evidence requires its owning operation registry.", nameof(capturedRegistry));
        }

        _capturedRegistry = capturedRegistry;
        _parentRunId = parentRunId;
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
            MaximumOutputBytes = 128 * 1024,
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

        TextEvidenceReadResult read;
        var evidenceId = new EvidenceId(input.EvidenceId);
        if (_capturedPullRequests.ContainsKey(evidenceId))
        {
            var effective = context.Invocation.ModelRemainingInputBudgetTokens
                ?? context.Invocation.ModelEffectiveInputBudgetTokens;
            var deliveryBudgetBytes = effective is > 0
                ? (int)Math.Min(_definition.MaximumOutputBytes, (long)effective.Value * 3)
                : _definition.MaximumOutputBytes;
            var maximumCharacters = TextEvidenceDocument.GetMaximumReadCharacters(deliveryBudgetBytes, 256);
            if (maximumCharacters < 1)
            {
                throw new ToolArgumentValidationException("The selected model has too little remaining context for a captured PR evidence segment.");
            }

            read = (_capturedRegistry ?? throw new InvalidOperationException("Captured PR registry is unavailable."))
                .Read(_sessionId, _parentRunId, input.EvidenceId, context.Invocation, input.StartLine ?? 1, input.EndLine, input.StartColumn ?? 1, maximumCharacters);
        }
        else
        {
            var evidence = _store.Snapshot(_sessionId).SingleOrDefault(item => item.EvidenceId.Value == input.EvidenceId);
            if (evidence is null || evidence.IsStale)
            {
                throw new InvalidOperationException("The requested evidence is missing or stale.");
            }

            read = new TextEvidenceDocument(evidence.Content).Read(input.StartLine ?? 1, input.EndLine, input.StartColumn ?? 1);
        }

        var content = read.NextLine is { } nextLine
            ? read.Content + $"\n[More captured evidence: call read_agent_evidence with startLine={nextLine}, startColumn={read.NextColumn}; totalLines={read.TotalLines}.]"
            : read.Content;

        return Task.FromResult(new ToolExecution<string>(
            content,
            [new ToolProvenanceSource("evidence", input.EvidenceId.ToString("D"))],
            IsTruncated: read.NextLine is not null,
            ModelResultContent: content));
    }

    /// <inheritdoc />
    protected override void ValidateInput(ChildAgentEvidenceInput input)
    {
        if (input.EvidenceId == Guid.Empty)
        {
            throw new ToolArgumentValidationException("An evidence ID is required.");
        }

        if (input.StartLine is < 1 || input.EndLine is < 1 || input.StartColumn is < 1
            || (input.StartLine is { } start && input.EndLine is { } end && end < start))
        {
            throw new ToolArgumentValidationException("Evidence lines must be positive and ordered.");
        }
    }
}
