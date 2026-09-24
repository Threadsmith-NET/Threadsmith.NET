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
    internal const string ConfigurationKey = "scratchpad:path";
    private readonly string[] _arguments;
    private readonly ILogger<ScratchpadLifecycle> _logger;
    private readonly IProcessManager _processes;
    private readonly ToolRegistry _tools;
    private readonly ToolStateManager _toolState;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
        ILogger<ScratchpadLifecycle> logger)
    {
        _arguments = [.. arguments];
        _configuration = configuration;
        _paths = paths;
        _processes = processes;
        _toolState = toolState;
        _tools = tools;
        _logger = logger;
    }

    /// <inheritdoc />
    public ScratchpadSessionCapability Current
    {
        get
        {
            lock (_gate)
            {
                if (_current.IsActive && !_revokedUntilBoundary && !IsWriteFileAvailable(_configuration))
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
        ResolvedValue configured;
        try
        {
            configured = ResolveConfiguredValue();
        }
        catch (ArgumentException exception)
        {
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.InvalidConfiguration);
            warnings.Add($"Scratchpad is disabled: {exception.Message}");
            return warnings;
        }

        if (!configured.IsPresent || configured.Value is null)
        {
            _lifecycleRoot = null;
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.NotConfigured);
            return warnings;
        }

        string root;
        try
        {
            ValidateRawValue(configured.Value);
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured.Value, _paths.RepositoryRoot));
            ValidateRoot(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or UnauthorizedAccessException or IOException)
        {
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.InvalidConfiguration);
            warnings.Add($"Scratchpad is disabled: {exception.Message}");
            return warnings;
        }

        var insideRepository = IsStrictChild(root, _paths.RepositoryRoot);
        if (configured.RepositoryOwned && !insideRepository)
        {
            _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.InvalidConfiguration);
            warnings.Add("Scratchpad is disabled: repository and session configuration must select a directory inside the repository.");
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
            if (!insideRepository)
            {
                _current = ScratchpadSessionCapability.Disabled(ScratchpadDisabledReason.ExternalDirectoryMissing);
                warnings.Add($"Scratchpad is disabled for this session because the external directory does not exist: {root}");
                return warnings;
            }

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
            ValidateNoReparsePoints(root, insideRepository ? _paths.RepositoryRoot : Path.GetPathRoot(root)!);
            _lifecycleRoot = root;
            if (insideRepository && !await IsIgnoredByRepositoryGitIgnoreAsync(root, cancellationToken))
            {
                warnings.Add($"Scratchpad directory is not covered by a repository .gitignore rule: {Path.GetRelativePath(_paths.RepositoryRoot, root).Replace('\\', '/')}");
            }

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

        if (!IsWriteFileAvailable(_configuration))
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
            ModelPath = insideRepository
                ? Path.GetRelativePath(_paths.RepositoryRoot, root).Replace('\\', '/')
                : root,
            IsInsideRepository = insideRepository,
            RepositoryIdentity = RepositoryIdentity.Create(_paths.RepositoryRoot),
            Generation = Interlocked.Increment(ref _generation),
        };
        return warnings;
    }

    private bool IsWriteFileAvailable(IConfiguration configuration)
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

        var allowed = configuration.GetSection("tools:allow").Get<string[]>() ?? [];
        var denied = configuration.GetSection("tools:deny").Get<string[]>() ?? [];
        var requireApproval = configuration.GetSection("tools:requireApproval").Get<string[]>() ?? [];
        return (allowed.Length == 0 || allowed.Contains("write_file", StringComparer.OrdinalIgnoreCase))
            && !denied.Contains("write_file", StringComparer.OrdinalIgnoreCase)
            && !requireApproval.Contains("write_file", StringComparer.OrdinalIgnoreCase);
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
                    Arguments = ["check-ignore", "-v", "--no-index", "--", relative],
                    WorkingDirectory = _paths.RepositoryRoot,
                    Timeout = TimeSpan.FromSeconds(5),
                    MaximumOutputCharacters = 4096,
                    Origin = ProcessRequestOrigin.Host,
                },
                cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return false;
            }

            var source = result.StandardOutput.Split(':', 2)[0].Trim();
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
        ValidateNoReparsePoints(root, IsStrictChild(root, _paths.RepositoryRoot) ? _paths.RepositoryRoot : Path.GetPathRoot(root)!);
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
        var filesystemRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetPathRoot(root)!));
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
            Path.GetDirectoryName(_paths.MachineConfiguration)!,
            Path.GetDirectoryName(_paths.UserConfiguration)!,
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
        var prohibited = _configuration.GetSection("prohibitedPaths").Get<string[]>() ?? [];
        if (IsStrictChild(root, repository) && RepositoryPathPolicy.IsProhibited(relative, prohibited))
        {
            throw new UnauthorizedAccessException("The configured scratchpad matches a prohibited path.");
        }
    }

    private static void ValidateNoReparsePoints(string root, string anchor)
    {
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
             IsSameOrChild(current, Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchor)));
             current = Path.GetDirectoryName(current)!)
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
             current = Path.GetDirectoryName(current)!)
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

    private static void ValidateRawValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)
            || value.IndexOfAny(['*', '?']) >= 0
            || value.Contains('%') || value.Contains("${", StringComparison.Ordinal)
            || value.StartsWith('~'))
        {
            throw new ArgumentException("scratchpad:path must be a nonblank literal directory path without wildcards, variables, or home shorthand.");
        }

        if (Path.IsPathRooted(value) && !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("scratchpad:path must be repository-relative or fully qualified.");
        }
    }

    private ResolvedValue ResolveConfiguredValue()
    {
        foreach (var environmentVariable in Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(key => key.ToString()!))
        {
            if (environmentVariable.Equals("THREADSMITH_scratchpad__path", StringComparison.OrdinalIgnoreCase))
            {
                return new(true, Environment.GetEnvironmentVariable(environmentVariable), false);
            }
        }

        var commandLine = _arguments.LastOrDefault(argument => argument.StartsWith("--set:scratchpad:path=", StringComparison.OrdinalIgnoreCase));
        if (commandLine is not null)
        {
            return new(true, commandLine[(commandLine.IndexOf('=') + 1)..], false);
        }

        foreach (var source in new[]
        {
            (Path: _paths.SessionConfiguration, RepositoryOwned: true),
            (Path: _paths.RepositoryConfiguration, RepositoryOwned: true),
            (Path: _paths.UserConfiguration, RepositoryOwned: false),
            (Path: _paths.MachineConfiguration, RepositoryOwned: false),
        })
        {
            if (TryReadJsonValue(source.Path, out var present, out var value) && present)
            {
                return new(true, value, source.RepositoryOwned);
            }
        }

        return new(false, null, false);
    }

    private static bool TryReadJsonValue(string path, out bool present, out string? value)
    {
        present = false;
        value = null;
        if (!File.Exists(path))
        {
            return false;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (!TryGetProperty(document.RootElement, "scratchpad", out var scratchpad)
            || scratchpad.ValueKind != JsonValueKind.Object
            || !TryGetProperty(scratchpad, "path", out var pathValue))
        {
            return true;
        }

        present = true;
        value = pathValue.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => pathValue.GetString(),
            _ => throw new ArgumentException("scratchpad:path must be a string or null."),
        };
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsStrictChild(string candidate, string root)
        => !PathEquals(candidate, root) && IsSameOrChild(Path.GetFullPath(candidate), Path.GetFullPath(root));

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
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record ResolvedValue(bool IsPresent, string? Value, bool RepositoryOwned);
}
