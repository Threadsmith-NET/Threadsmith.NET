namespace Threadsmith.Execution;

using System.Collections.Concurrent;
using System.Text;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Tools;
using Threadsmith.Tools.PullRequests;

/// <summary>The previously delivered evidence to retrieve.</summary>
internal sealed record ChildAgentEvidenceInput(Guid EvidenceId, int? StartLine = null, int? EndLine = null);

/// <summary>A request-local read capability for evidence already delivered to one child.</summary>
internal sealed class ChildAgentEvidenceTool : Tool<ChildAgentEvidenceInput, string>
{
    /// <summary>Child-local capability identifier.</summary>
    internal const string ToolId = "read_agent_evidence";

    private const string InputSchemaJson = """
        {"type":"object","additionalProperties":false,"required":["evidenceId"],"properties":{"evidenceId":{"type":"string","format":"uuid"},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1}}}
        """;

    private readonly IEvidenceStore _store;
    private readonly SessionId _sessionId;
    private readonly RunId _runId;
    private readonly IReadOnlySet<EvidenceId> _delivered;
    private readonly IReadOnlyDictionary<EvidenceId, PrFetchOutput> _capturedPullRequests;
    private readonly ConcurrentDictionary<EvidenceId, string> _capturedContent = new();
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="ChildAgentEvidenceTool"/> class.</summary>
    public ChildAgentEvidenceTool(
        IEvidenceStore store,
        SessionId sessionId,
        RunId runId,
        IReadOnlySet<EvidenceId> delivered,
        IPromptLoader prompts,
        IReadOnlyDictionary<EvidenceId, PrFetchOutput>? capturedPullRequests = null)
    {
        _store = store;
        _sessionId = sessionId;
        _runId = runId;
        _delivered = delivered;
        _capturedPullRequests = capturedPullRequests ?? new Dictionary<EvidenceId, PrFetchOutput>();
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

        string content;
        var evidenceId = new EvidenceId(input.EvidenceId);
        if (_capturedPullRequests.TryGetValue(evidenceId, out var snapshot))
        {
            content = _capturedContent.GetOrAdd(evidenceId, _ => RenderPullRequest(snapshot));
        }
        else
        {
            var evidence = _store.Snapshot(_sessionId).SingleOrDefault(item => item.EvidenceId.Value == input.EvidenceId);
            if (evidence is null || evidence.IsStale)
            {
                throw new InvalidOperationException("The requested evidence is missing or stale.");
            }

            content = evidence.Content;
        }

        if (input.StartLine is not null || input.EndLine is not null)
        {
            var first = input.StartLine ?? 1;
            content = SliceLines(content, first, input.EndLine);
        }

        return Task.FromResult(new ToolExecution<string>(
            content,
            [new ToolProvenanceSource("evidence", input.EvidenceId.ToString("D"))],
            ModelResultContent: content));
    }

    /// <inheritdoc />
    protected override void ValidateInput(ChildAgentEvidenceInput input)
    {
        if (input.EvidenceId == Guid.Empty)
        {
            throw new ToolArgumentValidationException("An evidence ID is required.");
        }

        if (input.StartLine is < 1 || input.EndLine is < 1
            || (input.StartLine is { } start && input.EndLine is { } end && end < start))
        {
            throw new ToolArgumentValidationException("Evidence lines must be positive and ordered.");
        }
    }

    private static string RenderPullRequest(PrFetchOutput snapshot)
    {
        var builder = new StringBuilder();
        builder.Append("PR snapshot ").Append(snapshot.SnapshotId.ToString("D"))
            .Append(" (captured ").Append(snapshot.CapturedAt.ToString("O")).AppendLine(")");
        builder.Append("Provider: ").AppendLine(snapshot.Provider);
        builder.Append("Kind: ").AppendLine(snapshot.Kind.ToString());
        builder.Append("URL: ").AppendLine(snapshot.Metadata.Url);
        builder.Append("Repository: ").AppendLine(snapshot.Metadata.Repository);
        builder.Append("Number: ").AppendLine(snapshot.Metadata.Number);
        builder.Append("Title: ").AppendLine(snapshot.Metadata.Title);
        builder.Append("Description: ").AppendLine(snapshot.Metadata.Description);
        builder.Append("State: ").AppendLine(snapshot.Metadata.State);
        builder.Append("Source repository: ").AppendLine(snapshot.Metadata.SourceRepository);
        builder.Append("Source commit: ").AppendLine(snapshot.Metadata.SourceCommit);
        builder.Append("Destination repository: ").AppendLine(snapshot.Metadata.DestinationRepository);
        builder.Append("Destination commit: ").AppendLine(snapshot.Metadata.DestinationCommit);
        builder.Append("Revision: ").AppendLine(snapshot.Metadata.Revision);
        builder.Append("Expected files: ").AppendLine(snapshot.Metadata.ExpectedFiles?.ToString() ?? "unknown");
        builder.AppendLine("Changed files:");
        foreach (var file in snapshot.Page.Files)
        {
            builder.Append(file.Status).Append(' ').Append(file.Path);
            if (file.PreviousPath is not null)
            {
                builder.Append(" (previous: ").Append(file.PreviousPath).Append(')');
            }

            builder.AppendLine();
            if (file.Limitation is not null)
            {
                builder.Append("File limitation: ").AppendLine(file.Limitation);
            }
        }

        foreach (var limitation in snapshot.Page.Limitations)
        {
            builder.Append("Limitation: ").AppendLine(limitation);
        }

        builder.AppendLine("Diff:");
        builder.Append(snapshot.Page.Diff);
        return builder.ToString();
    }

    private static string SliceLines(string content, int first, int? last)
    {
        var selected = new StringBuilder();
        var hasLine = false;
        var line = 1;
        var position = 0;
        while (position <= content.Length && (last is null || line <= last))
        {
            var next = content.AsSpan(position).IndexOf('\n');
            var end = next < 0 ? content.Length : position + next;
            if (line >= first)
            {
                if (hasLine)
                {
                    selected.Append('\n');
                }

                selected.Append(content.AsSpan(position, end - position));
                hasLine = true;
            }

            if (next < 0)
            {
                break;
            }

            position = end + 1;
            line++;
        }

        if (line < first)
        {
            throw new ToolArgumentValidationException("The requested evidence line range is outside the captured content.");
        }

        return selected.ToString();
    }
}
