namespace Threadsmith.Tools;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Binds ordinary layered memory settings without carrying overrides across repositories.</summary>
public sealed class RepositoryMemoryConfiguration : IRepositoryMemoryOptionsProvider
{
    /// <summary>The sole configuration namespace for repository memories.</summary>
    public const string SectionName = "tools:config:memories";

    private readonly RepositoryMemoryOptions _fallback;
    private Snapshot _current;

    /// <summary>Initializes a new instance of the <see cref="RepositoryMemoryConfiguration"/> class.</summary>
    public RepositoryMemoryConfiguration(IConfiguration configuration, IConfiguration fallbackConfiguration, string repositoryPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(fallbackConfiguration);
        var identity = RepositoryIdentity.Create(repositoryPath);
        _fallback = Read(fallbackConfiguration, new RepositoryMemoryOptions());
        _current = new Snapshot(identity, Read(configuration, _fallback));
    }

    /// <inheritdoc />
    public RepositoryMemoryOptions Capture(string repositoryIdentity)
    {
        var snapshot = Volatile.Read(ref _current);
        if (!string.Equals(snapshot.Identity, repositoryIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Memory settings belong to another repository. Reopen the repository before retrying.");
        }

        return snapshot.Options;
    }

    /// <summary>Rebinds repository overrides after validating the complete effective snapshot.</summary>
    public void BindRepository(string repositoryPath, IConfiguration repositoryConfiguration)
    {
        ArgumentNullException.ThrowIfNull(repositoryConfiguration);
        var identity = RepositoryIdentity.Create(repositoryPath);
        var options = Read(repositoryConfiguration, _fallback);
        Volatile.Write(ref _current, new Snapshot(identity, options));
    }

    /// <summary>Validates effective options without changing the active repository snapshot.</summary>
    public RepositoryMemoryOptions ReadRepositoryOptions(IConfiguration repositoryConfiguration)
    {
        ArgumentNullException.ThrowIfNull(repositoryConfiguration);
        return Read(repositoryConfiguration, _fallback);
    }

    /// <summary>Publishes a previously prepared immutable snapshot after repository persistence commits.</summary>
    public void BindRepository(string repositoryPath, RepositoryMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var identity = RepositoryIdentity.Create(repositoryPath);
        Volatile.Write(ref _current, new Snapshot(identity, options));
    }

    private static RepositoryMemoryOptions Read(IConfiguration configuration, RepositoryMemoryOptions fallback)
    {
        var options = new RepositoryMemoryOptions
        {
            MaxNumberOfRepoMemories = configuration.GetValue($"{SectionName}:MaxNumberOfRepoMemories", fallback.MaxNumberOfRepoMemories),
            MaxRepoMemoriesInContext = configuration.GetValue($"{SectionName}:MaxRepoMemoriesInContext", fallback.MaxRepoMemoriesInContext),
            SemanticMinimum = configuration.GetValue($"{SectionName}:SemanticMinimum", fallback.SemanticMinimum),
            RerankerEnabled = configuration.GetValue($"{SectionName}:RerankerEnabled", fallback.RerankerEnabled),
            RerankerCandidateLimit = configuration.GetValue($"{SectionName}:RerankerCandidateLimit", fallback.RerankerCandidateLimit),
            RerankerMinimumScore = ReadNullableDouble(configuration, $"{SectionName}:RerankerMinimumScore", fallback.RerankerMinimumScore),
            StandingPreferenceWarningThreshold = configuration.GetValue(
                $"{SectionName}:standingPreferenceWarningThreshold",
                fallback.StandingPreferenceWarningThreshold),
        };
        options.Validate();
        return options;
    }

    private static double? ReadNullableDouble(IConfiguration configuration, string key, double? fallback)
    {
        var supplied = configuration.AsEnumerable()
            .Any(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        return supplied ? configuration.GetValue<double?>(key) : fallback;
    }

    private sealed record Snapshot(string Identity, RepositoryMemoryOptions Options);
}
