namespace Threadsmith.Core;

/// <summary>Read-only, ownership-fenced access to collected repository intelligence.</summary>
public interface IRepositoryIntelligenceQuery
{
    /// <summary>Maximum accepted explicit question length.</summary>
    public const int MaximumQueryCharacters = 8000;

    /// <summary>Checks current feature readiness without collecting or waiting.</summary>
    RepositoryIntelligenceAvailability Capture(string repositoryIdentity);

    /// <summary>Searches existing intelligence without rebuilding embeddings.</summary>
    Task<RepositoryIntelligenceQueryResult> SearchAsync(string repositoryIdentity, string query, CancellationToken cancellationToken = default);
}

/// <summary>Shared feature readiness, independent of ordinary memory and automatic recall settings.</summary>
public sealed record RepositoryIntelligenceAvailability(bool Ready, string ReasonCode, string Message);

/// <summary>Existing search evidence paired with explicit feature readiness.</summary>
public sealed record RepositoryIntelligenceQueryResult(
    RepositoryIntelligenceAvailability Availability,
    string Query,
    RepositoryMemoryRetrievalResult? Result);
