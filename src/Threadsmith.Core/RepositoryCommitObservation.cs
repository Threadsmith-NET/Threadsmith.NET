namespace Threadsmith.Core;

/// <summary>A standard reflog creation record validated against its commit object.</summary>
public sealed record RepositoryCommitCreation(string CommitId);

/// <summary>Workspace-owned metadata observation without native Git process execution.</summary>
public interface IRepositoryCommitObserver
{
    /// <summary>Observes new creation records for the opened repository, without replaying historical logs.</summary>
    IAsyncEnumerable<RepositoryCommitCreation> ObserveAsync(string repositoryPath, CancellationToken cancellationToken = default);
}
