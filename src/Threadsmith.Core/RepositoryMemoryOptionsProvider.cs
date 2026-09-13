namespace Threadsmith.Core;

/// <summary>Supplies an immutable options snapshot fenced to the currently bound repository.</summary>
public interface IRepositoryMemoryOptionsProvider
{
    /// <summary>Captures the current bounds for advertised metadata; execution must still use the repository-fenced capture.</summary>
    RepositoryMemoryOptions CaptureCurrent();

    /// <summary>Captures effective memory bounds for one operation or turn.</summary>
    RepositoryMemoryOptions Capture(string repositoryIdentity);
}
