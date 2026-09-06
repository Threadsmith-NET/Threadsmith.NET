namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Resolves the governed repository instruction bundle for one child boundary.</summary>
public interface IChildAgentInstructionProvider
{
    /// <summary>Loads applicable AGENTS.md and configured prompt-append content without transcript state.</summary>
    Task<RepositoryInstructionBundle> GetAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        ToolInvocationContext parentContext,
        CancellationToken cancellationToken = default);
}

/// <summary>Uses the ordinary repository instruction authorities at a bounded child turn boundary.</summary>
public sealed class ChildAgentInstructionProvider : IChildAgentInstructionProvider
{
    private readonly IRepositoryInstructionResolver _instructions;
    private readonly IPromptAppendLoader _promptAppends;
    private readonly IReadOnlyList<string> _promptAppendPaths;

    /// <summary>Initializes a new instance of the <see cref="ChildAgentInstructionProvider"/> class.</summary>
    public ChildAgentInstructionProvider(
        IRepositoryInstructionResolver instructions,
        IPromptAppendLoader promptAppends,
        IReadOnlyList<string> promptAppendPaths)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(promptAppends);
        ArgumentNullException.ThrowIfNull(promptAppendPaths);
        _instructions = instructions;
        _promptAppends = promptAppends;
        _promptAppendPaths = promptAppendPaths.ToArray();
    }

    /// <inheritdoc />
    public async Task<RepositoryInstructionBundle> GetAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        ToolInvocationContext parentContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(parentContext);
        var frozen = plan.Assignments.SingleOrDefault(
            candidate => candidate.AssignmentId == assignment.AssignmentId);
        if (frozen is null || frozen.ChildRunId != assignment.ChildRunId)
        {
            throw new UnauthorizedAccessException("The assignment does not belong to this delegation.");
        }

        var promptAppends = await _promptAppends.LoadAsync(
            new PromptAppendLoadRequest(
                parentContext.RepositoryPath,
                _promptAppendPaths,
                frozen.Policy.ProhibitedPaths),
            cancellationToken);
        EnsureEveryPromptAppendWasLoaded(promptAppends);
        return await _instructions.ResolveAsync(
            parentContext.RepositoryPath,
            ResolveWorkingScope(parentContext.RepositoryPath, frozen.Scope),
            promptAppends,
            frozen.Policy.ProhibitedPaths,
            trustGeneration: 0,
            cancellationToken);
    }

    private void EnsureEveryPromptAppendWasLoaded(
        IReadOnlyList<PromptAppendSegment> promptAppends)
    {
        var loadedPositions = promptAppends
            .Select(segment => segment.Position)
            .ToHashSet();
        for (var position = 0; position < _promptAppendPaths.Count; position++)
        {
            var configuredPath = _promptAppendPaths[position];
            if (!string.IsNullOrWhiteSpace(configuredPath)
                && !loadedPositions.Contains(position))
            {
                throw new InvalidDataException(
                    $"Configured prompt append '{configuredPath}' was not loaded; child execution requires the complete prompt-append set.");
            }
        }
    }

    private static string? ResolveWorkingScope(
        string repositoryPath,
        AgentAssignmentScope scope)
    {
        string[] candidates =
        [
            .. scope.Directories
                .Concat(scope.Files.Select(path => Path.GetDirectoryName(path) ?? string.Empty))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal),
        ];
        return candidates.Length == 1
            ? Path.GetFullPath(candidates[0], repositoryPath)
            : repositoryPath;
    }
}
