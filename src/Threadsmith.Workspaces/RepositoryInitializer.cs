namespace Threadsmith.Workspaces;

using System.Text;
using Threadsmith.Core;

/// <summary>Inspects and scaffolds repository-local Threadsmith configuration without overwriting user data.</summary>
public static class RepositoryInitializer
{
    private const string InitialConfiguration = """
        {
          "solution": {
            "path": null
          },
          "tools": {
            "disabled": [],
            "config": {}
          }
        }
        """;

    private static readonly HashSet<string> _solutionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx", ".csproj", ".fsproj", ".vbproj",
    };

    /// <summary>Determines whether an existing repository has configuration or .NET solution candidates.</summary>
    public static Task<RepositoryInitializationStatus> GetStatusAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Repository root '{normalizedRoot}' does not exist.");
        }

        if ((File.GetAttributes(normalizedRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Repository roots cannot be symbolic links or junctions.");
        }

        var configurationDirectory = Path.Combine(normalizedRoot, ".threadsmith");
        var hasConfigurationDirectory = Directory.Exists(configurationDirectory);
        if (hasConfigurationDirectory
            && (File.GetAttributes(configurationDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "The repository configuration directory cannot be a symbolic link or junction.");
        }

        var hasSolutionCandidates = RepositoryLifecycle
            .EnumerateSafeFiles(normalizedRoot, cancellationToken)
            .Any(path => _solutionExtensions.Contains(Path.GetExtension(path)));
        return Task.FromResult(new RepositoryInitializationStatus(
            normalizedRoot,
            hasConfigurationDirectory,
            hasSolutionCandidates));
    }

    /// <summary>Atomically creates a minimal repository configuration scaffold when none exists.</summary>
    public static async Task<RepositoryInitializationResult> InitializeAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(repositoryPath, cancellationToken);
        var configurationDirectory = Path.Combine(status.RepositoryPath, ".threadsmith");
        var configurationPath = Path.Combine(configurationDirectory, "config.json");
        if (File.Exists(configurationPath))
        {
            return new RepositoryInitializationResult(
                status.RepositoryPath,
                configurationPath,
                false);
        }

        Directory.CreateDirectory(configurationDirectory);
        if ((File.GetAttributes(configurationDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "The repository configuration directory cannot be a symbolic link or junction.");
        }

        var temporaryPath = Path.Combine(
            configurationDirectory,
            $".config-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                InitialConfiguration + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            try
            {
                File.Move(temporaryPath, configurationPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(configurationPath))
            {
                File.Delete(temporaryPath);
                return new RepositoryInitializationResult(
                    status.RepositoryPath,
                    configurationPath,
                    false);
            }
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }

        return new RepositoryInitializationResult(
            status.RepositoryPath,
            configurationPath,
            true);
    }
}
