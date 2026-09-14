namespace Threadsmith.Execution;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Confined frozen source read arguments; omission of path lists the target inventory.</summary>
internal sealed record FocusedReviewReadInput
{
    /// <summary>Reads the independently frozen requirements document and its criterion identifiers.</summary>
    public bool Requirements { get; init; }

    /// <summary>Exact captured repository-relative path, or null for an inventory page.</summary>
    public string? Path { get; init; }

    /// <summary>Inclusive first source line to deliver.</summary>
    public int StartLine { get; init; } = 1;

    /// <summary>Maximum lines in a single bounded frozen read.</summary>
    public int MaximumLines { get; init; } = 200;

    /// <summary>Selects the captured comparison source for understanding previous behavior.</summary>
    public bool Baseline { get; init; }

    /// <summary>First entry of the deterministic source inventory page.</summary>
    public int Offset { get; init; }
}

/// <summary>One immutable source range or inventory page.</summary>
internal sealed record FocusedReviewReadOutput(
    string Snapshot,
    string? Path,
    string? Digest,
    bool Baseline,
    int StartLine,
    int EndLine,
    string Content,
    IReadOnlyList<string> Files,
    int? NextOffset,
    bool IsTruncated,
    string Source = "reviewTarget")
{
    /// <summary>Captured mode transition, available alongside the unchanged source.</summary>
    public string? ModeChange { get; init; }
}

/// <summary>Reads only captured text, with no repository, process, network or mutation authority.</summary>
internal sealed class FocusedReviewReadTool : Tool<FocusedReviewReadInput, FocusedReviewReadOutput>
{
    /// <summary>Child-local reader identity; it never enters the public tool registry.</summary>
    internal const string ToolId = "read_review_file";
    private readonly IOutputSanitizer _sanitizer;
    private readonly FocusedReviewTarget _target;
    private readonly IFocusedReviewCompletionPolicy _completion;

    /// <summary>Initializes a new instance of the <see cref="FocusedReviewReadTool"/> class.</summary>
    internal FocusedReviewReadTool(
        FocusedReviewTarget target,
        IFocusedReviewCompletionPolicy completion,
        IPromptLoader prompts,
        IOutputSanitizer sanitizer)
    {
        _sanitizer = sanitizer;
        _target = target;
        _completion = completion;
        Definition = new ToolDefinition
        {
            Id = ToolId,
            Version = "1.0.0",
            DisplayName = "Read review source",
            Description = prompts.Get(PromptFileNames.ToolReadReviewFileDescription),
            Category = ToolCategory.FileRead,
            RequiredTrust = RepositoryTrustLevel.TrustedRead,
            RequiredApproval = ApprovalLevel.None,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = TimeSpan.FromSeconds(10),
            MaximumOutputBytes = 256 * 1024,
            InputSchema = new ToolSchema("review-read-input", 1, """{"type":"object","additionalProperties":false,"properties":{"requirements":{"type":"boolean"},"path":{"type":["string","null"]},"startLine":{"type":"integer","minimum":1},"maximumLines":{"type":"integer","minimum":1,"maximum":500},"baseline":{"type":"boolean"},"offset":{"type":"integer","minimum":0}}}"""),
            OutputSchema = new ToolSchema("review-read-output", 1, """{"type":"object"}"""),
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override Task<ToolExecution<FocusedReviewReadOutput>> ExecuteAsync(
        FocusedReviewReadInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Path is null && !input.Requirements)
        {
            var inventory = _target.Files.OrderByDescending(file => file.InScope).ThenBy(file => file.Path, StringComparer.Ordinal).ToArray();
            var entries = inventory.Skip(input.Offset).Take(100).Select(
                file =>
                $"{file.Path} | {(file.InScope ? "review scope" : "supporting context")} | {(file.Deleted ? "deleted; old lines" : "current")} | {file.Digest}{(file.ModeChange is null ? string.Empty : " | " + file.ModeChange)}").ToArray();
            int? next = input.Offset + entries.Length < inventory.Length ? input.Offset + entries.Length : null;
            return Task.FromResult(
                new ToolExecution<FocusedReviewReadOutput>(
                new FocusedReviewReadOutput(_target.Identity, null, null, false, 0, 0, string.Empty, entries, next, next is not null),
                []));
        }

        var requirements = input.Requirements
            ? _target.Requirements ?? throw new InvalidDataException("No requirements document was captured.")
            : null;
        var file = requirements is null
            ? _target.Files.SingleOrDefault(item => item.Path == input.Path)
                ?? throw new UnauthorizedAccessException("Path is not in the authorized frozen review inventory.")
            : new FocusedReviewFile(requirements.Path, requirements.Digest, requirements.Content, false, []);
        var content = input.Baseline ? file.BaselineContent ?? throw new InvalidDataException("No captured baseline content for this path.") : file.Content;
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var selected = BoundedTextLines.Select(lines, input.StartLine, input.MaximumLines, 24000, countTrailingNewline: true, cancellationToken: cancellationToken).Lines;

        if (selected.Count == 0)
        {
            throw new InvalidDataException("No source range fits the requested read; choose an existing bounded range.");
        }

        var deliveredContent = string.Join('\n', selected);
        if (_sanitizer.Sanitize(deliveredContent) != deliveredContent)
        {
            throw new InvalidDataException("Secret redaction would alter this source range. Select a different range or report the inspection limitation.");
        }

        var end = input.StartLine + selected.Count - 1;
        if (!input.Baseline && requirements is null)
        {
            _completion.RecordRead(file.Path, input.StartLine, end);
        }

        var criterionLabels = requirements?.Criteria.Where(criterion => criterion.Line >= input.StartLine && criterion.Line <= end)
            .Select(criterion => $"{criterion.Id} | definition line {criterion.Line} | requires runtime/manual evidence: {criterion.RequiresExecution}").ToArray() ?? [];
        var output = new FocusedReviewReadOutput(
            _target.Identity,
            file.Path,
            file.Digest,
            input.Baseline,
            input.StartLine,
            end,
            deliveredContent,
            criterionLabels,
            null,
            end < lines.Length,
            requirements is null ? "reviewTarget" : "requirements:" + requirements.Source) { ModeChange = file.ModeChange };
        return Task.FromResult(
            new ToolExecution<FocusedReviewReadOutput>(
            output,
            [new ToolProvenanceSource("review-snapshot", file.Path, $"L{input.StartLine}-L{end}")],
            output.IsTruncated));
    }

    /// <inheritdoc />
    protected override string DescribeActivity(FocusedReviewReadInput input)
    {
        if (input.Path is null && !input.Requirements)
        {
            return $"source inventory from {input.Offset}, up to 100 entries";
        }

        var source = input.Requirements ? "requirements" : input.Path;
        var endLine = (long)input.StartLine + input.MaximumLines - 1;
        return $"lines {input.StartLine}-{endLine}, {(input.Baseline ? "baseline " : string.Empty)}{source}";
    }

    /// <inheritdoc />
    protected override void ValidateInput(
        FocusedReviewReadInput input)
    {
        if (input.Requirements && (input.Path is not null || input.Baseline))
        {
            throw new ToolArgumentValidationException("Requirements reads select the frozen document without a source path or baseline flag.");
        }

        if (input.StartLine < 1 || input.MaximumLines is < 1 or > 500 || input.Offset < 0)
        {
            throw new ToolArgumentValidationException("Review read requires positive lines, at most 500 lines and a nonnegative inventory offset.");
        }
    }
}
