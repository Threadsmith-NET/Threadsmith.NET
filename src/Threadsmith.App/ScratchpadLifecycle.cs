namespace Threadsmith.App;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Owns safe activation, session-boundary cleanup, and scratchpad authority.</summary>
internal sealed class ScratchpadLifecycle : IScratchpadSessionCapabilityProvider, IAsyncDisposable
{
    /// <summary>Canonical configuration key.</summary>
    internal const string ConfigurationKey = "scratchpad:enabled";
    private readonly string[] _arguments;
    private readonly ILogger<ScratchpadLifecycle> _logger;
    private readonly IProcessManager _processes;
    private readonly ToolRegistry _tools;
    private readonly ToolStateManager _toolState;
    private readonly SessionPolicyBinding? _policyBinding;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EffectiveSessionPolicy? _fallbackPolicy;
    private ScratchpadSessionCapability _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.NotConfigured);
    private IConfigurationRoot _configuration;
    private ConfigurationPaths _paths;
    private string? _lifecycleRoot;
    private long _generation;
    private IReadOnlyList<string> _lastActivationWarnings = [];
    private bool _revokedUntilBoundary;

    /// <summary>Initializes a new instance of the <see cref="ScratchpadLifecycle"/> class.</summary>
    internal ScratchpadLifecycle(
        string[] arguments,
        IConfigurationRoot configuration,
        ConfigurationPaths paths,
        IProcessManager processes,
        ToolStateManager toolState,
        ToolRegistry tools,
        ILogger<ScratchpadLifecycle> logger,
        SessionPolicyBinding? policyBinding = null)
    {
        _arguments = [.. arguments];
        _configuration = configuration;
        _paths = paths;
        _processes = processes;
        _toolState = toolState;
        _tools = tools;
        _logger = logger;
        _policyBinding = policyBinding;
    }

    /// <inheritdoc />
    public ScratchpadSessionCapability Current
    {
        get
        {
            lock (_gate)
            {
                if (_current.IsActive && !_revokedUntilBoundary && !IsWriteFileAvailable())
                {
                    _revokedUntilBoundary = true;
                    _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.WriteFileUnavailable);
                }

                return _current;
            }
        }
    }

    /// <summary>Gets the configuration snapshot bound to the current repository.</summary>
    internal IConfiguration CurrentConfiguration
    {
        get
        {
            lock (_gate)
            {
                return _configuration;
            }
        }
    }

    /// <summary>Gets warnings produced by the latest activation boundary.</summary>
    internal IReadOnlyList<string> LastActivationWarnings
    {
        get
        {
            lock (_gate)
            {
                return _lastActivationWarnings;
            }
        }
    }

    /// <inheritdoc />
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.NotConfigured);
            if (_lifecycleRoot is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await ClearAsync(_lifecycleRoot, timeout.Token);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Scratchpad shutdown cleanup failed for {ScratchpadPath}.", _lifecycleRoot);
                }
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>Activates and cleans the configured startup directory.</summary>
    internal async Task<IReadOnlyList<string>> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _lastActivationWarnings = await ActivateCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clears transient contents before activating a new session.</summary>
    internal async Task<IReadOnlyList<string>> BeginNewSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.NotConfigured);
            var clearedRoot = _lifecycleRoot;
            if (clearedRoot is not null)
            {
                await ClearAsync(clearedRoot, cancellationToken);
            }

            _revokedUntilBoundary = false;
            return _lastActivationWarnings = await ActivateCoreAsync(cancellationToken, clearedRoot, failOnCleanup: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clears the old directory and binds configuration for a new repository.</summary>
    internal async Task BindRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (PathEquals(repositoryRoot, _paths.RepositoryRoot))
            {
                return;
            }

            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.NotConfigured);
            if (_lifecycleRoot is not null)
            {
                await ClearAsync(_lifecycleRoot, cancellationToken);
            }

            _paths = ConfigurationBootstrap.ResolvePaths(repositoryRoot);
            _configuration = ConfigurationBootstrap.Build(_arguments, _paths);
            _lifecycleRoot = null;
            _revokedUntilBoundary = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restores and reactivates a repository binding after a failed repository transition.</summary>
    internal async Task RestoreRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        await BindRepositoryAsync(repositoryRoot, cancellationToken);
        _ = await StartAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ActivateCoreAsync(
        CancellationToken cancellationToken,
        string? alreadyClearedRoot = null,
        bool failOnCleanup = false)
    {
        var warnings = new List<string>();
        bool enabled;
        try
        {
            _fallbackPolicy = _policyBinding is null ? SecurityPolicyConfigurationLoader.Load(_paths, Guid.Empty, 0) : null;
            _configuration = ConfigurationBootstrap.Build(_arguments, _paths);
            var value = _configuration[ConfigurationKey];
            enabled = false;
            if (value is not null && !bool.TryParse(value, out enabled))
            {
                throw new ArgumentException("scratchpad:enabled must be true or false.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.InvalidConfiguration);
            warnings.Add($"Scratchpad is disabled: {exception.Message}");
            return warnings;
        }

        if (!enabled)
        {
            _lifecycleRoot = null;
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.NotConfigured);
            return warnings;
        }

        var root = Path.GetFullPath(Path.Combine(_paths.RepositoryRoot, ".threadsmith", ".scratchpad"));
        try
        {
            ValidateRoot(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or UnauthorizedAccessException or IOException)
        {
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.InvalidConfiguration);
            warnings.Add($"Scratchpad is disabled: {exception.Message}");
            return warnings;
        }

        if (File.Exists(root))
        {
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.InvalidConfiguration);
            warnings.Add("Scratchpad is disabled: the configured path is a file.");
            return warnings;
        }

        if (!Directory.Exists(root))
        {
            try
            {
                ValidateExistingAncestors(root, _paths.RepositoryRoot);
                Directory.CreateDirectory(root);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.InvalidConfiguration);
                warnings.Add($"Scratchpad is disabled: {exception.Message}");
                return warnings;
            }
        }

        try
        {
            ValidateNoReparsePoints(root, _paths.RepositoryRoot);
            if (!await IsIgnoredByRepositoryGitIgnoreAsync(root, cancellationToken))
            {
                _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.InvalidConfiguration);
                warnings.Add("Scratchpad is disabled: add .threadsmith/.scratchpad/ to a repository .gitignore file.");
                return warnings;
            }

            _lifecycleRoot = root;
            if (alreadyClearedRoot is null || !PathEquals(root, alreadyClearedRoot))
            {
                await ClearAsync(root, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.CleanupFailed);
            if (failOnCleanup)
            {
                throw new IOException("Scratchpad cleanup failed; the new session was not started.", exception);
            }

            warnings.Add($"Scratchpad is disabled because startup cleanup failed: {exception.Message}");
            return warnings;
        }

        if (!IsWriteFileAvailable())
        {
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.WriteFileUnavailable);
            warnings.Add("Scratchpad is disabled for this session because the built-in write_file tool is unavailable.");
            return warnings;
        }

        _current = new ScratchpadSessionCapability
        {
            IsActive = true,
            DisabledReason = ScratchpadDisabledReason.None,
            RootPath = root,
            ModelPath = ".threadsmith/.scratchpad",
            IsInsideRepository = true,
            RepositoryIdentity = RepositoryIdentity.Create(_paths.RepositoryRoot),
            Generation = Interlocked.Increment(ref _generation),
        };
        return warnings;
    }

    private bool IsWriteFileAvailable()
    {
        try
        {
            var registration = _tools.GetRegistration("write_file");
            if (registration.Source.Kind != ToolActivitySourceKind.BuiltIn
                || registration.Implementation is not WriteFileTool
                || !_toolState.IsEnabled("write_file"))
            {
                return false;
            }
        }
        catch (KeyNotFoundException)
        {
            return false;
        }

        try
        {
            var policy = _policyBinding?.Current ?? _fallbackPolicy;
            return policy is not null && policy.AllowsTool("write_file")
                && !policy.ReviewTools.Contains("write_file", StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<bool> IsIgnoredByRepositoryGitIgnoreAsync(string root, CancellationToken cancellationToken)
    {
        var relative = Path.GetRelativePath(_paths.RepositoryRoot, root).Replace('\\', '/') + "/";
        try
        {
            var result = await _processes.RunAsync(
                new ProcessExecutionRequest
                {
                    ToolInvocationId = ToolInvocationId.New(),
                    RunId = RunId.New(),
                    FileName = "git",
                    Arguments = ["check-ignore", "-v", "-z", "--no-index", "--stdin"],
                    StandardInput = relative + '\0',
                    StandardOutputFormat = ProcessStandardOutputFormat.NullDelimitedJsonArray,
                    WorkingDirectory = _paths.RepositoryRoot,
                    Timeout = TimeSpan.FromSeconds(5),
                    MaximumOutputCharacters = 4096,
                    Origin = ProcessRequestOrigin.Host,
                },
                cancellationToken);
            if (result.ExitCode != 0 || result.StandardOutputTruncated || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return false;
            }

            var fields = JsonSerializer.Deserialize<string[]>(result.StandardOutput);
            if (fields is not { Length: 4 } || fields[2].StartsWith('!'))
            {
                return false;
            }

            var source = fields[0];
            var sourcePath = Path.GetFullPath(source, _paths.RepositoryRoot);
            return Path.GetFileName(sourcePath).Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
                && IsSameOrChild(sourcePath, _paths.RepositoryRoot)
                && !sourcePath.Contains(Path.Combine(".git", "info"), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Scratchpad Git ignore inspection was indeterminate.");
            return false;
        }
    }

    private async Task ClearAsync(string root, CancellationToken cancellationToken)
    {
        ValidateRoot(root);
        ValidateNoReparsePoints(root, _paths.RepositoryRoot);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteEntry(entry, cancellationToken);
            await Task.Yield();
        }

        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException("Scratchpad cleanup left entries behind.");
        }
    }

    private static void DeleteEntry(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path);
            }
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }

            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                DeleteEntry(child, cancellationToken);
            }

            File.SetAttributes(path, FileAttributes.Directory);
            Directory.Delete(path);
            return;
        }

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private void ValidateRoot(string root)
    {
        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.RepositoryRoot));
        if (!PathEquals(root, Path.Combine(repository, ".threadsmith", ".scratchpad")))
        {
            throw new UnauthorizedAccessException("Scratchpad cleanup is restricted to .threadsmith/.scratchpad in the active repository.");
        }

        var filesystemRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetPathRoot(root) ?? throw new IOException("Scratchpad has no filesystem root.")));
        if (PathEquals(root, filesystemRoot)
            || IsSameOrChild(repository, root))
        {
            throw new UnauthorizedAccessException("The configured scratchpad is a protected filesystem or repository root.");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && IsSameOrChild(Path.GetFullPath(home), root))
        {
            throw new UnauthorizedAccessException("The configured scratchpad cannot be the user profile or its ancestor.");
        }

        foreach (var protectedPath in new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(_paths.MachineConfiguration) ?? throw new IOException("Machine configuration has no parent directory."),
            Path.GetDirectoryName(_paths.UserConfiguration) ?? throw new IOException("User configuration has no parent directory."),
            Path.Combine(repository, ".git"),
            Path.Combine(repository, ".threadsmith"),
        })
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedPath));
            if (PathEquals(normalized, Path.Combine(repository, ".threadsmith"))
                && IsSameOrChild(root, Path.Combine(normalized, ".scratchpad")))
            {
                continue;
            }

            if (IsSameOrChild(root, normalized) || IsSameOrChild(normalized, root))
            {
                throw new UnauthorizedAccessException("The configured scratchpad overlaps a protected Threadsmith or Git path.");
            }
        }

        var relative = Path.GetRelativePath(repository, root).Replace('\\', '/');
        var prohibited = (_policyBinding?.Current ?? _fallbackPolicy)?.ProhibitedPaths ?? [];
        if (IsStrictChild(root, repository) && RepositoryPathPolicy.IsProhibited(relative, prohibited, repository))
        {
            throw new UnauthorizedAccessException("The configured scratchpad matches a prohibited path.");
        }
    }

    private static void ValidateNoReparsePoints(string root, string anchor)
    {
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
             IsSameOrChild(current, Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchor)));
             current = Path.GetDirectoryName(current) ?? throw new IOException("Scratchpad traversal escaped its anchor."))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("The configured scratchpad cannot use symbolic links, junctions, or reparse points.");
            }

            if (PathEquals(current, anchor))
            {
                break;
            }
        }
    }

    private static void ValidateExistingAncestors(string root, string anchor)
    {
        var normalizedAnchor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchor));
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
             IsSameOrChild(current, normalizedAnchor);
             current = Path.GetDirectoryName(current) ?? throw new IOException("Scratchpad traversal escaped its anchor."))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("The configured scratchpad cannot use symbolic links, junctions, or reparse points.");
            }

            if (PathEquals(current, normalizedAnchor))
            {
                break;
            }
        }
    }

    private static bool IsStrictChild(string candidate, string root)
    {
        return !PathEquals(candidate, root) && IsSameOrChild(Path.GetFullPath(candidate), Path.GetFullPath(root));
    }

    private static bool IsSameOrChild(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedCandidate.Equals(normalizedRoot, comparison)
            || normalizedCandidate.StartsWith(
                Path.EndsInDirectorySeparator(normalizedRoot)
                    ? normalizedRoot
                    : normalizedRoot + Path.DirectorySeparatorChar,
                comparison);
    }

    private static bool PathEquals(string left, string right)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
