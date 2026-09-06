namespace Threadsmith.Interaction.Sessions;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;

/// <summary>Immutable terminal-neutral state displayed beside the active composer.</summary>
/// <param name="Folder">Host-reported active working folder.</param>
/// <param name="Repository">Active repository display name.</param>
/// <param name="Model">Effective model display name and id.</param>
/// <param name="Reasoning">Effective reasoning level.</param>
/// <param name="ContextTokens">Latest governed request token estimate, or <see langword="null"/>.</param>
/// <param name="ContextLimit">Effective request context limit, or <see langword="null"/>.</param>
/// <param name="Usage">Cumulative provider usage for the active session.</param>
public sealed record SessionStatusSnapshot(
    string Folder,
    string Repository,
    string Model,
    ReasoningLevel Reasoning,
    long? ContextTokens,
    long? ContextLimit,
    SessionUsageSnapshot Usage)
{
    /// <summary>Gets the current local Git branch, or null when unavailable or detached.</summary>
    public string? Branch { get; init; }
}

/// <summary>Builds immutable status state from host-owned repository, model, and context projections.</summary>
internal static class SessionStatusAssembler
{
    /// <summary>Gets a repository display name, preserving filesystem roots that have no file name.</summary>
    /// <param name="repositoryPath">Absolute repository path.</param>
    /// <returns>The final path component, or the trimmed path when it is a filesystem root.</returns>
    internal static string GetRepositoryDisplayName(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var trimmedPath = Path.TrimEndingDirectorySeparator(repositoryPath);
        var repositoryName = Path.GetFileName(trimmedPath);
        return string.IsNullOrWhiteSpace(repositoryName) ? trimmedPath : repositoryName;
    }

    /// <summary>Creates an effective composer status snapshot.</summary>
    /// <param name="folder">Current host working folder.</param>
    /// <param name="repository">Current repository display name.</param>
    /// <param name="fallbackModel">Fallback model status when no configured profile is active.</param>
    /// <param name="profile">Effective configured model profile, or <see langword="null"/>.</param>
    /// <param name="reasoning">Effective reasoning level.</param>
    /// <param name="inspection">Latest governed context inspection, or <see langword="null"/>.</param>
    /// <param name="usage">Current session usage snapshot.</param>
    /// <param name="branch">Current local Git branch, or null when unavailable or detached.</param>
    /// <returns>An immutable terminal-neutral status snapshot.</returns>
    internal static SessionStatusSnapshot Create(
        string folder,
        string repository,
        string fallbackModel,
        ModelProfile? profile,
        ReasoningLevel reasoning,
        ContextInspectionProjection? inspection,
        SessionUsageSnapshot usage,
        string? branch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackModel);
        ArgumentNullException.ThrowIfNull(usage);

        long? contextLimit = profile?.ContextWindow ?? inspection?.TokenBudget;

        return new SessionStatusSnapshot(
            folder,
            repository,
            profile?.Name ?? fallbackModel,
            reasoning,
            inspection?.EstimatedTokens,
            contextLimit,
            usage)
        {
            Branch = branch,
        };
    }
}
