namespace Threadsmith.Core;

/// <summary>Host receipt for final provider-submitted memory content revisions.</summary>
public sealed record RepositoryMemorySubmission(
    SessionId SessionId,
    string RepositoryIdentity,
    IReadOnlyList<RepositoryMemoryInclusion> Inclusions)
{
    /// <summary>Sanitized content captured with the exact submitted selection for transient inspection.</summary>
    public IReadOnlyList<RepositoryMemoryContextItemProjection> Items { get; init; } = [];
}
