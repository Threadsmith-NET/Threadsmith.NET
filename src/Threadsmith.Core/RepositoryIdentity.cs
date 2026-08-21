namespace Threadsmith.Core;

using System.Security.Cryptography;
using System.Text;

/// <summary>Creates stable non-disclosing identities for local repository paths.</summary>
public static class RepositoryIdentity
{
    /// <summary>Creates the canonical identity shared by repository-scoped durable subsystems.</summary>
    /// <param name="repositoryPath">Local repository root path.</param>
    /// <returns>Lowercase SHA-256 identity of the platform-canonical repository path.</returns>
    public static string Create(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var canonicalRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var identitySource = OperatingSystem.IsWindows()
            ? canonicalRepository.ToUpperInvariant()
            : canonicalRepository;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identitySource)));
    }
}
