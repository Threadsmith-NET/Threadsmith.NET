namespace Threadsmith.Tools;

using System.Text.Json;
using Threadsmith.Core;

/// <summary>Explicit repository memory action with action-specific optional arguments.</summary>
public sealed record MemoriesInput(string Action, string? Id = null, string? Text = null);

/// <summary>Inspectable memory metadata without embedding components or internal provenance.</summary>
public sealed record MemoryInfo(
    string Id,
    string Text,
    string Origin,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long InclusionCount,
    DateTimeOffset? LastIncludedAt,
    string? EmbeddingSpaceId);

/// <summary>A bounded operation outcome with explicit list omissions.</summary>
public sealed record MemoriesOutput(string Action, string Outcome, string? Id, bool? Removed, IReadOnlyList<MemoryInfo> Entries, int OmittedEntries);

/// <summary>Admits explicit memory changes through the shared repository memory service.</summary>
public sealed class MemoriesTool : Tool<MemoriesInput, MemoriesOutput>, ITransientToolActivityDetail
{
    private const int MaximumListBytes = 48 * 1024;
    private readonly IManagedRepositoryMemoryService _memories;
    private readonly IRepositoryMemoryOptionsProvider _options;
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="MemoriesTool"/> class.</summary>
    public MemoriesTool(IManagedRepositoryMemoryService memories, IRepositoryMemoryOptionsProvider options, IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompts);
        _memories = memories;
        _options = options;
        var definition = ToolDefinitionFactory.Create<MemoriesInput, MemoriesOutput>(
            "memories",
            prompts.Get(PromptFileNames.ToolMemoriesDescription),
            ToolCategory.Workflow,
            RepositoryTrustLevel.TrustedRead,
            ApprovalLevel.None,
            ToolSideEffect.WritesRepositoryMemory,
            TimeSpan.FromMinutes(2),
            64 * 1024);
        _definition = definition with
        {
            DisplayName = "Memories",
            ConversationAvailable = true,
            InputSchema = definition.InputSchema with
            {
                JsonSchema = """
                    {"type":"object","properties":{"action":{"type":"string","enum":["add","update","remove","list"]},"id":{"type":["string","null"]},"text":{"type":["string","null"]}},"required":["action"],"additionalProperties":false}
                    """,
            },
            Scheduling = new ToolSchedulingDescriptor
            {
                ConcurrencyMode = ToolConcurrencyMode.SerializedPerResource,
                ClaimResolverId = "repository-memories-v1",
            },
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<MemoriesOutput>> ExecuteAsync(MemoriesInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        var repositoryIdentity = RepositoryIdentity.Create(context.Invocation.RepositoryPath);
        var result = await _memories.ExecuteAsync(
            new RepositoryMemoryOperationRequest
            {
                RepositoryIdentity = repositoryIdentity,
                Action = input.Action,
                Id = input.Id is null ? null : new RepositoryMemoryId(Guid.Parse(input.Id)),
                Text = input.Text,
                Origin = RepositoryMemoryOrigin.Model,
                SourceSessionId = context.SessionId.Value.ToString("D"),
                SourceRunId = context.RunId.Value.ToString("D"),
                SourceInvocationId = context.ToolInvocationId.Value.ToString("D"),
                Options = _options.Capture(repositoryIdentity),
            },
            cancellationToken);
        var entries = new List<MemoryInfo>();
        var bytes = 0;
        var source = result.Entry is { } entry ? (IReadOnlyList<RepositoryMemoryEntry>)[entry] : result.Entries;
        foreach (var item in source)
        {
            var info = new MemoryInfo(item.Id.Value.ToString("D"), item.Text, item.Origin.ToString().ToLowerInvariant(), item.CreatedAt, item.UpdatedAt, item.InclusionCount, item.LastIncludedAt, item.EmbeddingSpaceId);
            bytes += JsonSerializer.SerializeToUtf8Bytes(info).Length;
            if (bytes > MaximumListBytes)
            {
                break;
            }

            entries.Add(info);
        }

        var output = new MemoriesOutput(input.Action, result.Outcome, result.Id?.Value.ToString("D"), input.Action == "remove" ? result.Outcome == "removed" : null, entries, source.Count - entries.Count);
        return new ToolExecution<MemoriesOutput>(output, [], output.OmittedEntries > 0);
    }

    /// <inheritdoc />
    string? ITransientToolActivityDetail.GetTransientActivityDetail(object input, ToolExecutionContext context)
    {
        return ((MemoriesInput)input).Text;
    }

    /// <inheritdoc />
    protected override void ValidateInput(MemoriesInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var valid = input.Action switch
        {
            "add" => input.Id is null && !string.IsNullOrWhiteSpace(input.Text),
            "update" => IsId(input.Id) && !string.IsNullOrWhiteSpace(input.Text),
            "remove" => IsId(input.Id) && input.Text is null,
            "list" => input.Id is null && input.Text is null,
            _ => false,
        };
        if (!valid)
        {
            throw new ToolArgumentValidationException("memories requires add(text), update(id,text), remove(id), or list() with no other arguments. IDs must be nonempty UUIDs.");
        }
    }

    /// <inheritdoc />
    protected override string? DescribeActivity(MemoriesInput input) => input.Id is null ? input.Action : $"{input.Action} {input.Id}";

    private static bool IsId(string? id) => Guid.TryParse(id, out var value) && value != Guid.Empty;
}
