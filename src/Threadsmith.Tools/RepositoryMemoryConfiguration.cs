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

    private static RepositoryMemoryOptions Read(IConfiguration configuration, RepositoryMemoryOptions fallback)
    {
        var options = new RepositoryMemoryOptions
        {
            MaxNumberOfRepoMemories = configuration.GetValue($"{SectionName}:MaxNumberOfRepoMemories", fallback.MaxNumberOfRepoMemories),
            MaxRepoMemoriesInContext = configuration.GetValue($"{SectionName}:MaxRepoMemoriesInContext", fallback.MaxRepoMemoriesInContext),
        };
        options.Validate();
        return options;
    }

    private sealed record Snapshot(string Identity, RepositoryMemoryOptions Options);
}
