namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Keeps configured append precedence while replacing live repository guidance with the frozen target guidance.</summary>
internal sealed class FocusedReviewInstructionProvider : IChildAgentInstructionProvider
{
    private readonly IChildAgentInstructionProvider _ordinary;
    private readonly FocusedReviewTarget _target;

    /// <summary>Initializes a new instance of the <see cref="FocusedReviewInstructionProvider"/> class.</summary>
    internal FocusedReviewInstructionProvider(
        IChildAgentInstructionProvider ordinary,
        FocusedReviewTarget target)
    {
        _ordinary = ordinary;
        _target = target;
    }

    /// <inheritdoc />
    public async Task<RepositoryInstructionBundle> GetAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        ToolInvocationContext parentContext,
        CancellationToken cancellationToken = default)
    {
        var ordinary = await _ordinary.GetAsync(plan, assignment, parentContext, cancellationToken);
        var sources = _target.Files.Where(
            file => !file.Deleted && (file.Path == "AGENTS.md" || file.Path.EndsWith("/AGENTS.md", StringComparison.Ordinal)) && (file.Path == "AGENTS.md" || _target.Files.Any(
            source => source.InScope
                && source.Path.StartsWith(file.Path[..^"AGENTS.md".Length], StringComparison.Ordinal))))
            .OrderBy(file => file.Path.Count(character => character == '/')).ThenBy(
                file => file.Path,
                StringComparer.Ordinal)
            .Select(
                (file, index) => new RepositoryInstructionSource(
                RepositoryInstructionSourceKind.Agents,
                file.Path,
                file.Path,
                file.Digest,
                file.Content,
                index)).ToList();
        sources.AddRange(
            ordinary.Sources.Where(
            source => source.Kind == RepositoryInstructionSourceKind.PromptAppend)
            .Select((source, index) => source with { Position = sources.Count + index }));
        return new RepositoryInstructionBundle
        {
            RepositoryRoot = _target.Repository,
            WorkingScope = ".",
            Sources = sources,
            Digest = _target.Identity + ":" + ordinary.Digest,
        };
    }
}

/// <summary>Dispatches each focused role to its own runtime procedure and reader without sibling material.</summary>
internal sealed class FocusedReviewBatchRunner : IAgentAssignmentRunner
{
    private readonly IReadOnlyDictionary<AgentRole, IAgentAssignmentRunner> _runners;

    /// <summary>Initializes a new instance of the <see cref="FocusedReviewBatchRunner"/> class.</summary>
    internal FocusedReviewBatchRunner(IReadOnlyDictionary<AgentRole, IAgentAssignmentRunner> runners) => _runners = runners;

    /// <inheritdoc />
    public Task<AgentRunOutcome> RunAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default)
        => _runners[assignment.Role].RunAsync(plan, assignment, cancellationToken);
}
