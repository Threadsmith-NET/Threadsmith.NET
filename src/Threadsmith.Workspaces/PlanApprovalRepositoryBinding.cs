namespace Threadsmith.Workspaces;

using System.Security.Cryptography;
using System.Text;

/// <summary>Immutable repository identity and configuration path used by plan approval policy storage.</summary>
/// <param name="RepositoryRoot">Normalized repository root.</param>
/// <param name="ConfigurationPath">Normalized repository configuration path.</param>
/// <param name="RepositoryIdentity">Exact deterministic repository identity.</param>
internal sealed record PlanApprovalRepositoryBinding(
    string RepositoryRoot,
    string ConfigurationPath,
    string RepositoryIdentity)
{
    /// <summary>Gets the directory containing the repository configuration file.</summary>
    public string ConfigurationDirectory => Path.GetDirectoryName(ConfigurationPath)
        ?? throw new InvalidOperationException("The repository configuration path has no parent directory.");

    /// <summary>Creates a binding for the repository root that is currently active.</summary>
    /// <param name="repositoryRoot">Repository root to bind.</param>
    /// <returns>Immutable repository approval-policy binding.</returns>
    public static PlanApprovalRepositoryBinding CreateFromRepositoryRoot(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Repository root '{normalizedRoot}' does not exist.");
        }

        var configurationPath = Path.Combine(normalizedRoot, ".threadsmith", "config.json");
        PlanApprovalPathSafety.EnsureRepositoryConfinedWithoutReparsePoints(normalizedRoot, configurationPath);
        return new PlanApprovalRepositoryBinding(
            normalizedRoot,
            configurationPath,
            CreateRepositoryIdentity(normalizedRoot));
    }

    /// <summary>Creates a binding from a known repository configuration path, when one is configured.</summary>
    /// <param name="configurationPath">Repository configuration path, or <see langword="null" />.</param>
    /// <returns>Immutable repository approval-policy binding, or <see langword="null" />.</returns>
    public static PlanApprovalRepositoryBinding? CreateFromConfigurationPath(string? configurationPath)
    {
        if (configurationPath is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var normalizedConfigurationPath = Path.GetFullPath(configurationPath);
        var directory = Path.GetDirectoryName(normalizedConfigurationPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The repository configuration path has no parent directory.");
        }

        var repositoryRoot = Directory.GetParent(directory)?.FullName
            ?? throw new InvalidOperationException("The repository configuration path has no repository root.");
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Repository root '{normalizedRoot}' does not exist.");
        }

        PlanApprovalPathSafety.EnsureRepositoryConfinedWithoutReparsePoints(normalizedRoot, normalizedConfigurationPath);
        return new PlanApprovalRepositoryBinding(
            normalizedRoot,
            normalizedConfigurationPath,
            CreateRepositoryIdentity(normalizedRoot));
    }

    private static string CreateRepositoryIdentity(string repositoryRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var identityInput = OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identityInput)));
    }
}
