namespace Threadsmith.Tools;

using System.Text.Json;
using Threadsmith.Core;

/// <summary>A focused search of stored architectural guidance.</summary>
public sealed record RepositoryIntelligenceInput(string Query);

/// <summary>Readiness checked by advertisement independently of persisted tool switches.</summary>
public interface IRepositoryFeatureTool
{
    /// <summary>Returns current readiness for the bound repository.</summary>
    RepositoryIntelligenceAvailability CaptureAvailability(string repositoryPath);
}

/// <summary>Read-only access to already collected repository intelligence.</summary>
public sealed class RepositoryIntelligenceTool : Tool<RepositoryIntelligenceInput, MemoriesOutput>, IRepositoryFeatureTool
{
    private readonly IRepositoryIntelligenceQuery _query;
    private readonly IRepositoryMemoryOptionsProvider _options;
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceTool"/> class.</summary>
    public RepositoryIntelligenceTool(IRepositoryIntelligenceQuery query, IRepositoryMemoryOptionsProvider options, IPromptLoader prompts)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(prompts);
        var definition = ToolDefinitionFactory.Create<RepositoryIntelligenceInput, MemoriesOutput>(
            "repository_intelligence",
            prompts.Get(PromptFileNames.ToolRepositoryIntelligenceDescription),
            ToolCategory.RepositoryInspection,
            RepositoryTrustLevel.TrustedRead,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            TimeSpan.FromMinutes(2),
            64 * 1024);
        _definition = definition with
        {
            DisplayName = "Repository intelligence",
            InputSchema = definition.InputSchema with
            {
                JsonSchema = $$$"""{"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":{{{IRepositoryIntelligenceQuery.MaximumQueryCharacters}}}}},"required":["query"],"additionalProperties":false}""",
            },
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public RepositoryIntelligenceAvailability CaptureAvailability(string repositoryPath) => _query.Capture(RepositoryIdentity.Create(repositoryPath));

    /// <inheritdoc />
    public override async Task<ToolExecution<MemoriesOutput>> ExecuteAsync(RepositoryIntelligenceInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        var repositoryIdentity = RepositoryIdentity.Create(context.Invocation.RepositoryPath);
        var options = _options.Capture(repositoryIdentity);
        var searched = await _query.SearchAsync(repositoryIdentity, input.Query, cancellationToken);
        if (searched.Result is not { } result)
        {
            var status = searched.Availability.ReasonCode switch
            {
                "master_disabled" => "disabled",
                "onboarding_required" => "not_collected",
                _ => "error",
            };
            var unavailable = new MemoriesOutput("repository_intelligence", status, null, null, [], 0)
            {
                ReasonCode = searched.Availability.ReasonCode,
                Resolution = searched.Availability.Message,
                Query = searched.Query,
                IsComplete = false,
            };
            return BoundOutput(unavailable, context);
        }

        var entries = new List<MemoryInfo>();
        var bytes = 0;
        foreach (var candidate in result.Selected.Take(20))
        {
            var item = candidate.Entry;
            if (item.RepositoryIdentity != repositoryIdentity || item.Ownership != RepositoryMemoryOwnership.Intelligence)
            {
                throw new InvalidOperationException("Intelligence search returned an entry outside the requested ownership or repository.");
            }

            var info = new MemoryInfo(item.Id.Value.ToString("D"), item.Text, item.Origin.ToString().ToLowerInvariant(), item.MemoryType == RepositoryMemoryType.StandingPreference ? "standingPreference" : "situational", item.CreatedAt, item.UpdatedAt, item.InclusionCount, item.LastIncludedAt, item.EmbeddingSpaceId, item.Revision, item.Kind.ToString().ToLowerInvariant(), item.Concepts)
            {
                Ownership = "intelligence", Supports = item.Supports,
                Trust = "untrusted",
                Freshness = item.Supports.Count == 0 || item.Supports.Any(support => support.IsUnknown) ? "unknown"
                    : item.Supports.Any(support => support.IsDirty) ? "dirty" : "observed",
            };
            bytes += JsonSerializer.SerializeToUtf8Bytes(info).Length;
            if (bytes > Math.Min(48 * 1024, options.MaximumListBytes))
            {
                break;
            }

            entries.Add(info);
        }

        var outcome = result.SearchDetails?.Lexical == RepositoryMemorySearchBranchStatus.Unavailable ? "error"
            : !result.IsComplete ? "incomplete" : result.Selected.Count == 0 ? "no_matches" : "matches";
        var output = new MemoriesOutput("repository_intelligence", outcome, null, null, entries, result.Selected.Count - entries.Count)
        {
            IsComplete = result.IsComplete,
            Query = searched.Query,
            ReasonCode = outcome,
            QueryTruncated = result.QueryTruncated,
            SearchDetails = result.SearchDetails is { } details ? details with
            {
                Candidates = [], CandidateEvidenceOmissions = details.CandidateEvidenceOmissions + details.Candidates.Count,
            } : null,
        };
        return BoundOutput(output, context);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(RepositoryIntelligenceInput input, ToolInvocationContext context) => [context.RepositoryPath];

    /// <inheritdoc />
    protected override void ValidateInput(RepositoryIntelligenceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Query) || input.Query.Length > IRepositoryIntelligenceQuery.MaximumQueryCharacters)
        {
            throw new ToolArgumentValidationException("query requires 1–8000 characters and must not be blank.");
        }
    }

    private static ToolExecution<MemoriesOutput> BoundOutput(MemoriesOutput output, ToolExecutionContext context)
    {
        var maximumBytes = Math.Min(64 * 1024, context.MaximumOutputBytes ?? 64 * 1024);
        var truncated = output.OmittedEntries > 0 || output.QueryTruncated;
        while (JsonSerializer.SerializeToUtf8Bytes(output).Length > maximumBytes)
        {
            truncated = true;
            if (!string.IsNullOrEmpty(output.Query))
            {
                output = output with { Query = output.Query[..(output.Query.Length / 2)], QueryTruncated = true };
            }
            else if (output.SearchDetails is not null)
            {
                output = output with { SearchDetails = null };
            }
            else if (!string.IsNullOrEmpty(output.Resolution))
            {
                output = output with { Resolution = output.Resolution[..(output.Resolution.Length / 2)] };
            }
            else if (output.Entries.Count > 0)
            {
                output = output with { Entries = output.Entries.Take(output.Entries.Count - 1).ToArray(), OmittedEntries = output.OmittedEntries + 1 };
            }
            else
            {
                // A limit smaller than the status envelope is left to the normal pipeline error.
                break;
            }
        }

        return new(output, [], truncated);
    }
}
