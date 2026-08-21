namespace Threadsmith.Workspaces;

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;

/// <summary>Creates and explicitly removes Git worktrees without invoking a shell.</summary>
public sealed class GitWorktreeManager
{
    private const int _maximumProcessOutputCharacters = 64 * 1024;
    private readonly ILogger<GitWorktreeManager> _logger;
    private readonly HashSet<string> _ownedWorktrees = new(PathComparer);
    private readonly Lock _gate = new();

    /// <summary>Initializes a new instance of the <see cref="GitWorktreeManager"/> class.</summary>
    public GitWorktreeManager(ILogger<GitWorktreeManager>? logger = null)
    {
        _logger = logger ?? NullLogger<GitWorktreeManager>.Instance;
    }

    /// <summary>Creates a detached worktree at an explicit or host-owned temporary path.</summary>
    public async Task<WorkspaceIsolation> CreateAsync(
        string repositoryPath,
        string? worktreePath = null,
        string revision = "HEAD",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        if (revision.StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("Git revisions cannot begin with '-'.", nameof(revision));
        }

        var normalizedRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (!Directory.Exists(normalizedRepository))
        {
            throw new DirectoryNotFoundException(
                $"Repository '{normalizedRepository}' does not exist.");
        }

        var ownedRoot = Path.Combine(Path.GetTempPath(), "Threadsmith", "worktrees");
        Directory.CreateDirectory(ownedRoot);
        var normalizedWorktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            worktreePath ?? Path.Combine(ownedRoot, Guid.NewGuid().ToString("N"))));
        if (Directory.Exists(normalizedWorktree) || File.Exists(normalizedWorktree))
        {
            throw new IOException($"Worktree target '{normalizedWorktree}' already exists.");
        }

        _ = await RunGitAsync(
            normalizedRepository,
            ["worktree", "add", "--detach", normalizedWorktree, revision],
            cancellationToken);
        lock (_gate)
        {
            _ownedWorktrees.Add(normalizedWorktree);
        }

        return new WorkspaceIsolation(
            WorkspaceIsolationMode.GitWorktree,
            normalizedWorktree,
            revision);
    }

    /// <summary>Explicitly removes a worktree previously created by this manager.</summary>
    public async Task RemoveAsync(
        string repositoryPath,
        WorkspaceIsolation isolation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(isolation);
        if (isolation.Mode != WorkspaceIsolationMode.GitWorktree)
        {
            throw new ArgumentException("Only Git-worktree isolation can be removed here.", nameof(isolation));
        }

        var normalizedWorktree = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(isolation.RepositoryPath));
        lock (_gate)
        {
            if (!_ownedWorktrees.Contains(normalizedWorktree))
            {
                throw new UnauthorizedAccessException(
                    "The worktree was not created by this manager instance.");
            }
        }

        var normalizedRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        _ = await RunGitAsync(
            normalizedRepository,
            ["worktree", "remove", "--force", normalizedWorktree],
            cancellationToken);
        lock (_gate)
        {
            _ownedWorktrees.Remove(normalizedWorktree);
        }
    }

    private async Task<string> RunGitAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Git did not start.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the state check and cancellation callback.
                }
            });
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Git worktree command failed: {error[..Math.Min(error.Length, _maximumProcessOutputCharacters)]}");
            }

            return output[..Math.Min(output.Length, _maximumProcessOutputCharacters)];
        }
        catch (Win32Exception exception)
        {
            _logger.LogError(
                exception,
                "Git executable was not found while operating in {RepositoryPath}",
                repositoryPath);
            throw new InvalidOperationException(
                "Git executable was not found on PATH. Install Git or add its executable directory to PATH.",
                exception);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Git worktree operation failed in {RepositoryPath}",
                repositoryPath);
            throw;
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
