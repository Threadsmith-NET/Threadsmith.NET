namespace Threadsmith.Core;

/// <summary>Host-owned raw filesystem notification before consumer-specific relevance and echo filtering.</summary>
public sealed record RepositoryFileChange(string Path, SemanticFileChangeKind Kind, string? PreviousPath = null)
{
    /// <summary>Observation has already been restarted; consumers must reconcile without restarting it again.</summary>
    public bool ObservationRestarted { get; init; }
}

/// <summary>A directory subscription whose disposal releases only its own observation interest.</summary>
public interface IRepositoryDirectoryObservation : IDisposable
{
    /// <summary>Absolute nonrecursive directory covered by this subscription.</summary>
    string Path { get; }

    /// <summary>Replaces native observation after loss while notifying other subscribers of the coverage gap.</summary>
    void Restart();
}

/// <summary>Shared filesystem observation independently consumed by semantics and repository intelligence.</summary>
public interface IRepositoryFileObserver : IDisposable
{
    /// <summary>Observes one directory using shared native resources; callbacks enqueue work only.</summary>
    IRepositoryDirectoryObservation Subscribe(string directory, Action<RepositoryFileChange> changed, Action failed);
}
