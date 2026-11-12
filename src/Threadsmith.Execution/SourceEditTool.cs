namespace Threadsmith.Execution;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Ordinary conversation tool using the execution-owned direct edit command.</summary>
public sealed class SourceEditTool : Tool<SourceEditInput, SourceEditReceipt>, IExactMutationAuthorizationTool, IPostSanitizationToolOutputBoundary
{
    private static readonly SourceEditReceiptSerialization _evidenceSerialization = new(ConversationReceiptChecks.MaximumEvidenceBytes);
    private readonly ICommandHandler<ApplySourceEditCommand, SourceEditReceipt> _edits;
    private readonly ToolDefinition _definition;
    private readonly IPromptLoader _prompts;
    private readonly int _maximumMutations;

    /// <summary>Initializes a new instance of the <see cref="SourceEditTool"/> class.</summary>
    public SourceEditTool(ICommandHandler<ApplySourceEditCommand, SourceEditReceipt> edits, IPromptLoader prompts, WorkspaceResourceLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(prompts);
        _edits = edits;
        _prompts = prompts;
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
            DescriptionResolver = () => prompts.Get(PromptFileNames.ToolSourceEditDescription),
            Category = ToolCategory.FileWrite,
            InputSchema = new(nameof(SourceEditInput), 1, schema.ToJsonString()),
            OutputSchema = new(nameof(SourceEditReceipt), 1, "{\"type\":\"object\"}"),
            RequiredTrust = RepositoryTrustLevel.TrustedMutation,
            RequiredApproval = ApprovalLevel.None,
            SideEffect = ToolSideEffect.WritesFiles,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = Timeout.InfiniteTimeSpan,
            MaximumOutputBytes = 65536,
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
        var maximumOutputBytes = context.MaximumOutputBytes ?? Definition.MaximumOutputBytes;
        if (maximumOutputBytes < 1024)
        {
            throw new ToolArgumentValidationException("edit_source requires maximumOutputBytes of at least 1024 to preserve its disk outcome. No edit was attempted.");
        }

        var workspaceId = context.Invocation.WorkspaceId ?? throw new InvalidOperationException("Source editing requires an active workspace.");
        var key = context.InvocationKey ?? context.ToolInvocationId.Value.ToString("D");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{context.SessionId.Value:D}|{context.RunId.Value:D}|edit_source|{key}"));
        var instructions = new MutationProposalSet { Rationale = input.Rationale, Mutations = input.Mutations };
        var command = new ApplySourceEditCommand(context.SessionId, context.RunId, workspaceId, new Guid(hash.AsSpan(0, 16)), instructions, context.RequireExactDiffReview);
        try
        {
            var receipt = await _edits.HandleAsync(command, cancellationToken);
            var bounded = BoundReceipt(receipt, maximumOutputBytes, _prompts.Get(PromptFileNames.ToolSourceEditReceiptTruncated));
            var modelResult = CreateModelResult(bounded);

            var reuseDetail = receipt.Status == SourceEditStatus.Applied && receipt.Analysis is { CandidateReused: true, Obsolete: false, CommittedGeneration: not null }
                ? "Candidate analysis reused for committed source" + (receipt.Analysis.Pending ? "; broader analysis pending" : string.Empty)
                : null;
            return new(bounded, receipt.ChangedFiles.Select(path => new ToolProvenanceSource("file", path)).ToArray(), bounded.OutputTruncated, ModelResultContent: modelResult, TransientActivityDetail: reuseDetail)
            {
                RetainedEvidenceContent = _evidenceSerialization.TrySerialize(receipt),
            };
        }
        catch (MutationInstructionException exception)
        {
            throw new ToolArgumentValidationException(exception.Diagnostic.SafeMessage, exception);
        }
    }

    /// <inheritdoc />
    PostSanitizationToolOutput IPostSanitizationToolOutputBoundary.BoundSanitizedOutput(
        string resultJson,
        string? modelResultContent,
        ToolInvocationContext context,
        int maximumOutputBytes)
    {
        var receipt = JsonSerializer.Deserialize<SourceEditReceipt>(resultJson)
            ?? throw new InvalidOperationException("The sanitized edit receipt could not be deserialized.");

        // This boundary may only reduce sanitized content, never reload raw prompt overrides.
        var bounded = BoundReceipt(receipt, maximumOutputBytes, receipt.Detail);
        return new(JsonSerializer.Serialize(bounded), CreateModelResult(bounded), bounded.OutputTruncated);
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

    private static string CreateModelResult(SourceEditReceipt receipt)
    {
        var result = JsonSerializer.SerializeToNode(receipt)?.AsObject()
            ?? throw new InvalidOperationException("Source edit receipt could not be serialized.");
        result[nameof(receipt.Status)] = receipt.Status.ToString();
        if (receipt.Analysis is { } analysis)
        {
            result[nameof(receipt.Analysis)] = SourceEditAnalysisProjection.Create(analysis);
        }

        return result.ToJsonString();
    }

    private static SourceEditReceipt BoundReceipt(SourceEditReceipt receipt, int maximumOutputBytes, string truncationDetail)
    {
        var serialization = new SourceEditReceiptSerialization(maximumOutputBytes);
        while (serialization.TrySerialize(receipt) is null
            || Encoding.UTF8.GetByteCount(CreateModelResult(receipt)) > maximumOutputBytes)
        {
            receipt = receipt with
            {
                OutputTruncated = true,
                OriginalChangedFileCount = receipt.OriginalChangedFileCount ?? receipt.ChangedFiles.Count,
                Detail = truncationDetail,
            };
            if (receipt.Commit is not null)
            {
                receipt = receipt with { Commit = null };
            }
            else if (receipt.Analysis is { Diagnostics.Count: > 0 } analysis)
            {
                receipt = receipt with { Analysis = analysis with { Diagnostics = analysis.Diagnostics.Take(Math.Min(analysis.Diagnostics.Count / 2, maximumOutputBytes / 2)).ToArray(), FindingProjectionTruncated = true } };
            }
            else if (receipt.ChangedFiles.Count > 0)
            {
                receipt = receipt with { ChangedFiles = receipt.ChangedFiles.Take(Math.Min(receipt.ChangedFiles.Count / 2, maximumOutputBytes / 2)).ToArray() };
            }
            else if (receipt.Analysis is not null)
            {
                receipt = receipt with { Analysis = null };
            }
            else
            {
                // Prompt overrides cannot displace identities and outcome flags from the minimum envelope.
                return receipt with { Detail = string.Empty };
            }
        }

        return receipt;
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
