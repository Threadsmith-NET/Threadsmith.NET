namespace Threadsmith.Execution;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Ordinary conversation tool using the execution-owned direct edit command.</summary>
public sealed class SourceEditTool : Tool<SourceEditInput, SourceEditReceipt>, IExactMutationAuthorizationTool
{
    private readonly ICommandHandler<ApplySourceEditCommand, SourceEditReceipt> _edits;
    private readonly ToolDefinition _definition;
    private readonly int _maximumMutations;

    /// <summary>Initializes a new instance of the <see cref="SourceEditTool"/> class.</summary>
    public SourceEditTool(ICommandHandler<ApplySourceEditCommand, SourceEditReceipt> edits, IPromptLoader prompts, WorkspaceResourceLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(prompts);
        _edits = edits;
        var effectiveLimits = limits ?? new WorkspaceResourceLimits();
        effectiveLimits.Validate();
        _maximumMutations = effectiveLimits.MaximumMutations;
        var schema = JsonNode.Parse(MutationInstructionSchema.Schema)
            ?? throw new InvalidOperationException("Mutation schema is unavailable.");
        var properties = schema["properties"]?.AsObject()
            ?? throw new InvalidOperationException("Mutation properties are unavailable.");
        var mutations = properties["mutations"] ?? throw new InvalidOperationException("Mutation schema has no operations.");
        mutations["minItems"] = 1;
        mutations["maxItems"] = effectiveLimits.MaximumMutations;
        _definition = new()
        {
            Id = "edit_source",
            DisplayName = "Edit source",
            Source = "Built-in",
            Version = "1.0.0",
            Description = prompts.Get(PromptFileNames.ToolSourceEditDescription),
            Category = ToolCategory.FileWrite,
            InputSchema = new(nameof(SourceEditInput), 1, schema.ToJsonString()),
            OutputSchema = new(nameof(SourceEditReceipt), 1, "{\"type\":\"object\"}"),
            RequiredTrust = RepositoryTrustLevel.TrustedMutation,
            RequiredApproval = ApprovalLevel.None,
            SideEffect = ToolSideEffect.WritesFiles,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = Timeout.InfiniteTimeSpan,
            MaximumOutputBytes = 32 * 1024,
            SubagentAvailable = false,
            RequiresWorkspace = true,
            Scheduling = new()
            {
                ConcurrencyMode = ToolConcurrencyMode.ExclusiveSession,
                ClaimResolverId = "source-edit-session-v1",
            },
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<SourceEditReceipt>> ExecuteAsync(SourceEditInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        var workspaceId = context.Invocation.WorkspaceId ?? throw new InvalidOperationException("Source editing requires an active workspace.");
        var key = context.InvocationKey ?? context.ToolInvocationId.Value.ToString("D");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{context.SessionId.Value:D}|{context.RunId.Value:D}|edit_source|{key}"));
        var instructions = new MutationProposalSet { Rationale = input.Rationale, Mutations = input.Mutations };
        var command = new ApplySourceEditCommand(context.SessionId, context.RunId, workspaceId, new Guid(hash.AsSpan(0, 16)), instructions, context.RequireExactDiffReview);
        try
        {
            var receipt = await _edits.HandleAsync(command, cancellationToken);
            var modelResult = JsonSerializer.SerializeToNode(receipt)?.AsObject()
                ?? throw new InvalidOperationException("Source edit receipt could not be serialized.");
            if (receipt.Analysis is { } analysis)
            {
                modelResult[nameof(receipt.Analysis)] = SourceEditAnalysisProjection.Create(analysis);
            }

            var reuseDetail = receipt.Status == SourceEditStatus.Applied && receipt.Analysis is { CandidateReused: true, Obsolete: false, CommittedGeneration: not null }
                ? "Candidate analysis reused for committed source" + (receipt.Analysis.Pending ? "; broader analysis pending" : string.Empty)
                : null;
            return new(receipt, receipt.ChangedFiles.Select(path => new ToolProvenanceSource("file", path)).ToArray(), ModelResultContent: modelResult.ToJsonString(), TransientActivityDetail: reuseDetail);
        }
        catch (MutationInstructionException exception)
        {
            throw new ToolArgumentValidationException(exception.Diagnostic.SafeMessage, exception);
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(SourceEditInput input, ToolInvocationContext context)
    {
        return input.Mutations.SelectMany(change => change is MoveFileMutationProposal move
            ? new[] { change.RelativePath, move.DestinationRelativePath } : [change.RelativePath]).ToArray();
    }

    /// <inheritdoc />
    protected override void ValidateInput(SourceEditInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Rationale);
        if (input.Mutations is null || input.Mutations.Count == 0 || input.Mutations.Count > _maximumMutations
            || input.Mutations.Any(change => change is null || string.IsNullOrWhiteSpace(change.RelativePath)
                || (change is MoveFileMutationProposal move && string.IsNullOrWhiteSpace(move.DestinationRelativePath))))
        {
            throw new ToolArgumentValidationException("Source edits require a bounded nonempty list of operations with valid endpoints.");
        }
    }
}

/// <summary>Model-owned ordered instructions without execution plans or host scheduling identities.</summary>
public sealed record SourceEditInput
{
    /// <summary>Why the requested changes are needed.</summary>
    public required string Rationale { get; init; }

    /// <summary>Ordered source operations.</summary>
    public required IReadOnlyList<MutationProposalChange> Mutations { get; init; }
}
