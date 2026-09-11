namespace Threadsmith.Core;

/// <summary>Cached repository-wide Git state. Unknown is represented by an absent snapshot, never zero counts.</summary>
public sealed record RepositoryGitStatus(string? Branch, bool Detached, int Staged, int Modified, int Untracked, int Conflicts, bool IsTruncated);
