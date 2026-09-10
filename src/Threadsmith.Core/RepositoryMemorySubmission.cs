namespace Threadsmith.Core;

/// <summary>Host receipt for final provider-submitted memory content revisions.</summary>
public sealed record RepositoryMemorySubmission(
    SessionId SessionId,
    string RepositoryIdentity,
    IReadOnlyList<RepositoryMemoryInclusion> Inclusions);
