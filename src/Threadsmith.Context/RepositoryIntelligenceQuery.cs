namespace Threadsmith.Context;

using Threadsmith.Core;

/// <summary>Uses the existing search engine with fixed ownership and no maintenance authority.</summary>
public sealed class RepositoryIntelligenceQuery : IRepositoryIntelligenceQuery
{
    private readonly IRepositoryMemorySearch _search;
    private readonly IRepositoryMemoryOptionsProvider _options;
    private readonly IRepositoryIntelligenceStatusProvider _status;

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceQuery"/> class.</summary>
    public RepositoryIntelligenceQuery(IRepositoryMemorySearch search, IRepositoryMemoryOptionsProvider options, IRepositoryIntelligenceStatusProvider status)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _status = status ?? throw new ArgumentNullException(nameof(status));
    }

    /// <inheritdoc />
    public RepositoryIntelligenceAvailability Capture(string repositoryIdentity)
    {
        if (!_options.Capture(repositoryIdentity).Intelligence.Enabled)
        {
            return new(false, "master_disabled", "Repository intelligence is disabled.");
        }

        var status = _status.Capture(repositoryIdentity);
        return status.RepositoryIdentity != repositoryIdentity ? new(false, "unavailable", "Repository binding changed.")
            : status.Fault is not null ? new(false, "monitor_unhealthy", "Repository intelligence monitoring is unhealthy.")
            : !status.Onboarded ? new(false, "onboarding_required", "Repository intelligence has not been collected.")
            : !status.IsEffective ? new(false, "monitor_starting", "Repository intelligence monitoring is starting.")
            : new(true, "ready", "Stored repository intelligence is available.");
    }

    /// <inheritdoc />
    public async Task<RepositoryIntelligenceQueryResult> SearchAsync(string repositoryIdentity, string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Length, IRepositoryIntelligenceQuery.MaximumQueryCharacters);
        cancellationToken.ThrowIfCancellationRequested();
        var availability = Capture(repositoryIdentity);
        if (!availability.Ready)
        {
            return new(availability, query, null);
        }

        var options = _options.Capture(repositoryIdentity).Intelligence.Recall;
        var request = new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = repositoryIdentity,
            Query = query,
            Ownership = RepositoryMemoryOwnership.Intelligence,
            IncludeStandingPreferences = true,
            AllowEmbeddingRebuild = false,
            Options = options with { MaximumResults = Math.Min(20, options.MaximumResults) },
        };
        var result = await _search.SearchAsync(request, cancellationToken);
        if (result.Selected.Any(candidate => candidate.Entry.RepositoryIdentity != repositoryIdentity
            || candidate.Entry.Ownership != RepositoryMemoryOwnership.Intelligence))
        {
            throw new InvalidOperationException("Intelligence search returned an entry outside the requested ownership or repository.");
        }

        return new(availability, query, result);
    }
}
