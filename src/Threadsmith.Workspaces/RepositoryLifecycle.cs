namespace Threadsmith.Workspaces;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;

/// <summary>Coordinates safe repository opening, solution selection, and baseline capture.</summary>
public sealed class RepositoryLifecycle :
    ICommandHandler<GetRepositoryTrustCommand, RepositoryTrustState?>,
    ICommandHandler<GetRepositoryInitializationStatusCommand, RepositoryInitializationStatus>,
    ICommandHandler<InitializeRepositoryCommand, RepositoryInitializationResult>,
    ICommandHandler<OpenRepositoryCommand, RepositoryOpenResult>,
    ICommandHandler<SelectSolutionCommand, SolutionSelectionResult>,
    ICommandHandler<RecordBaselineCommand, WorkspaceBaseline>
{
    private static readonly Histogram<double> _baselineDuration =
        WorkspaceMetrics.Meter.CreateHistogram<double>("threadsmith.workspace.baseline.duration", "ms");

    private static readonly Counter<long> _baselineFiles =
        WorkspaceMetrics.Meter.CreateCounter<long>("threadsmith.workspace.baseline.files");

    private static readonly Counter<long> _skippedFileSystemEntries =
        WorkspaceMetrics.Meter.CreateCounter<long>("threadsmith.workspace.filesystem.skipped");

    private static readonly HashSet<string> _baselineExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".props", ".targets",
    };

    private static readonly HashSet<string> _baselineFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".editorconfig", "global.json", "NuGet.config",
    };

    private static readonly HashSet<string> _solutionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx", ".csproj", ".fsproj", ".vbproj",
    };

    private static readonly HashSet<string> _solutionContainerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx",
    };

    private static readonly HashSet<string> _projectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".fsproj", ".vbproj",
    };

    private readonly IDomainEventStream _events;

    private readonly IRepositoryFactsStore _factsStore;

    private readonly DotNetEnvironmentResolver _environmentResolver;

    private readonly ILogger<RepositoryLifecycle> _logger;

    private readonly TransactionalWorkspaceCoordinator? _mutationCoordinator;

    private readonly IMutationApprovalPolicy? _mutationApprovalPolicy;

    private readonly Func<string, CancellationToken, Task>? _repositoryOpened;

    private readonly Lock _sessionGate = new();

    private readonly Dictionary<WorkspaceId, RepositorySession> _sessions = [];

    private readonly Dictionary<SessionId, WorkspaceId> _workspacesBySession = [];

    /// <summary>Initializes a new instance of the <see cref="RepositoryLifecycle"/> class.</summary>
    public RepositoryLifecycle(
        IDomainEventStream events,
        IRepositoryFactsStore factsStore,
        DotNetEnvironmentResolver environmentResolver,
        TransactionalWorkspaceCoordinator? mutationCoordinator = null,
        ILogger<RepositoryLifecycle>? logger = null,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        Func<string, CancellationToken, Task>? repositoryOpened = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(factsStore);
        ArgumentNullException.ThrowIfNull(environmentResolver);
        _events = events;
        _factsStore = factsStore;
        _environmentResolver = environmentResolver;
        _mutationCoordinator = mutationCoordinator;
        _logger = logger ?? NullLogger<RepositoryLifecycle>.Instance;
        _mutationApprovalPolicy = mutationApprovalPolicy;
        _repositoryOpened = repositoryOpened;
    }

    /// <inheritdoc />
    public async Task<RepositoryTrustState?> HandleAsync(
        GetRepositoryTrustCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RepositoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        var repositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(command.RepositoryPath));
        RepositoryFacts? facts = await _factsStore.GetAsync(repositoryPath, cancellationToken);
        return facts?.Trust;
    }

    /// <inheritdoc />
    public Task<RepositoryInitializationStatus> HandleAsync(
        GetRepositoryInitializationStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RepositoryPath);
        return RepositoryInitializer.GetStatusAsync(command.RepositoryPath, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RepositoryInitializationResult> HandleAsync(
        InitializeRepositoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RepositoryPath);
        return RepositoryInitializer.InitializeAsync(command.RepositoryPath, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RepositoryOpenResult> HandleAsync(
        OpenRepositoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RepositoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        var repositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(command.RepositoryPath));
        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository root '{repositoryPath}' does not exist.");
        }

        FileAttributes attributes = File.GetAttributes(repositoryPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Repository roots cannot be symbolic links or junctions.");
        }

        RepositoryFacts? persisted = await _factsStore.GetAsync(repositoryPath, cancellationToken);
        RepositoryTrustLevel effectiveTrust = persisted is not null && persisted.Trust.Level > command.RequestedTrust
            ? persisted.Trust.Level
            : command.RequestedTrust;
        DateTimeOffset grantedAt = persisted is not null && persisted.Trust.Level == effectiveTrust
            ? persisted.Trust.GrantedAt
            : DateTimeOffset.UtcNow;
        var trust = new RepositoryTrustState(repositoryPath, effectiveTrust, grantedAt);

        var configPath = NormalizeUnderRoot(
            repositoryPath,
            Path.Combine(".threadsmith", "config.json"));
        if (File.Exists(configPath) && new FileInfo(configPath).Length > 1024 * 1024)
        {
            throw new InvalidDataException("Repository configuration exceeds the 1 MiB safety limit.");
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true)
            .Build();
        var configuredSolution = configuration["solution:path"];
        string[] approvedRoots = [.. configuration.GetSection("editableRoots")
            .GetChildren()
            .Select(item => item.Value)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item ?? string.Empty)];
        if (approvedRoots.Length == 0)
        {
            approvedRoots = ["."];
        }

        string[] prohibitedPaths = [.. configuration.GetSection("prohibitedPaths")
            .GetChildren()
            .Select(item => item.Value)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item ?? string.Empty)];
        foreach (var approvedRoot in approvedRoots)
        {
            var approvedPath = NormalizeUnderRoot(repositoryPath, approvedRoot);
            if (Directory.Exists(approvedPath)
                && (File.GetAttributes(approvedPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Approved roots cannot be symbolic links or junctions.");
            }
        }

        var config = new RepositoryConfigurationSnapshot(
            configuredSolution,
            approvedRoots,
            prohibitedPaths);
        string[] discovered = [.. EnumerateSafeFiles(repositoryPath, cancellationToken)
            .Where(path => _solutionExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !IsProhibited(repositoryPath, path, prohibitedPaths))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        var solutionFiles = discovered
            .Where(path => _solutionContainerExtensions.Contains(Path.GetExtension(path)))
            .ToList();
        if (solutionFiles.Count == 0)
        {
            solutionFiles.AddRange(discovered.Where(path =>
                _projectExtensions.Contains(Path.GetExtension(path))));
        }

        if (!string.IsNullOrWhiteSpace(configuredSolution))
        {
            var configuredPath = NormalizeUnderRoot(repositoryPath, configuredSolution);
            if (!File.Exists(configuredPath))
            {
                try
                {
                    await PersistSolutionPreferenceAsync(repositoryPath, null, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not clear stale remembered solution for {RepositoryPath}: {Message}",
                        repositoryPath,
                        exception.Message);
                }

                configuredSolution = null;
                config = config with { SolutionPath = null };
            }
            else
            {
                if (IsProhibited(repositoryPath, configuredPath, prohibitedPaths))
                {
                    throw new UnauthorizedAccessException("The configured solution path is prohibited.");
                }

                solutionFiles.RemoveAll(path => string.Equals(path, configuredPath, PathComparison));
                solutionFiles.Insert(0, configuredPath);
            }
        }

        MsBuildEnvironmentSnapshot? environment = effectiveTrust >= RepositoryTrustLevel.TrustedRead
            ? await DotNetEnvironmentResolver.ResolveAsync(
                repositoryPath,
                configuration["build:configuration"] ?? "Debug",
                configuration["build:platform"] ?? "Any CPU",
                effectiveTrust >= RepositoryTrustLevel.TrustedBuild,
                cancellationToken)
            : null;
        if (_mutationApprovalPolicy is not null)
        {
            await _mutationApprovalPolicy.BindRepositoryAsync(repositoryPath, cancellationToken);
        }

        var workspaceId = WorkspaceId.New();
        var facts = new RepositoryFacts(
            workspaceId,
            repositoryPath,
            trust,
            persisted?.SolutionPath,
            persisted?.TargetFrameworks.ToArray() ?? [],
            environment ?? persisted?.Environment);
        await _factsStore.UpsertAsync(facts, cancellationToken);
        var session = new RepositorySession(
            command.SessionId,
            workspaceId,
            repositoryPath,
            trust,
            config,
            environment,
            persisted?.SolutionPath,
            persisted?.TargetFrameworks.ToArray() ?? []);
        lock (_sessionGate)
        {
            if (_workspacesBySession.Remove(command.SessionId, out WorkspaceId previousWorkspaceId))
            {
                _sessions.Remove(previousWorkspaceId);
            }

            _sessions.Add(workspaceId, session);
            _workspacesBySession.Add(command.SessionId, workspaceId);
        }

        await _events.PublishAsync(
            new RepositoryOpened(
                command.SessionId,
                DateTimeOffset.UtcNow,
                repositoryPath,
                workspaceId,
                effectiveTrust,
                prohibitedPaths)
            {
                SchemaVersion = 2,
            },
            cancellationToken);
        if (_repositoryOpened is not null)
        {
            await _repositoryOpened(repositoryPath, cancellationToken);
        }

        return new RepositoryOpenResult(
            workspaceId,
            repositoryPath,
            trust,
            config,
            environment,
            solutionFiles);
    }

    /// <inheritdoc />
    public async Task<SolutionSelectionResult> HandleAsync(
        SelectSolutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SolutionPath);
        cancellationToken.ThrowIfCancellationRequested();
        RepositorySession? session;
        lock (_sessionGate)
        {
            _sessions.TryGetValue(command.WorkspaceId, out session);
        }

        if (session is null || session.SessionId != command.SessionId)
        {
            throw new InvalidOperationException("The workspace is not open for this session.");
        }

        if (session.Trust.Level < RepositoryTrustLevel.TrustedRead)
        {
            throw new UnauthorizedAccessException(
                "Solution selection requires TrustedRead. Reopen the repository after granting read trust.");
        }

        var solutionPath = NormalizeUnderRoot(session.RepositoryPath, command.SolutionPath);
        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException("The selected solution does not exist.", solutionPath);
        }

        if (IsProhibited(session.RepositoryPath, solutionPath, session.Configuration.ProhibitedPaths))
        {
            throw new UnauthorizedAccessException("The selected solution path is prohibited.");
        }

        var extension = Path.GetExtension(solutionPath);
        if (!_solutionExtensions.Contains(extension))
        {
            throw new InvalidDataException("Select a .sln, .slnx, or supported project file.");
        }

        var projectPaths = new List<string>();
        if (_projectExtensions.Contains(extension))
        {
            projectPaths.Add(solutionPath);
        }
        else if (string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var document = XDocument.Load(solutionPath, LoadOptions.None);
            projectPaths.AddRange(document.Descendants()
                .Where(element => element.Name.LocalName == "Project")
                .Select(element => element.Attribute("Path")?.Value)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => NormalizeUnderRoot(
                    session.RepositoryPath,
                    Path.Combine(Path.GetDirectoryName(solutionPath) ?? session.RepositoryPath, path ?? string.Empty))));
        }
        else
        {
            // Plan 05 intentionally reads the stable quoted project-path field without MSBuild
            // evaluation. Plan 06 owns replacing this compatibility parser with semantic loading.
            foreach (var line in await File.ReadAllLinesAsync(solutionPath, cancellationToken))
            {
                var parts = line.Split('"');
                if (parts.Length < 6 || parts[0].TrimStart().StartsWith("ProjectSection", StringComparison.Ordinal))
                {
                    continue;
                }

                var projectPath = parts[5];
                if (!_projectExtensions.Contains(Path.GetExtension(projectPath)))
                {
                    continue;
                }

                var portableProjectPath = projectPath
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                projectPaths.Add(NormalizeUnderRoot(
                    session.RepositoryPath,
                    Path.Combine(
                        Path.GetDirectoryName(solutionPath) ?? session.RepositoryPath,
                        portableProjectPath)));
            }
        }

        var targetFrameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectPath in projectPaths.Distinct(PathComparer))
        {
            if (!File.Exists(projectPath)
                || IsProhibited(session.RepositoryPath, projectPath, session.Configuration.ProhibitedPaths))
            {
                continue;
            }

            var project = XDocument.Load(projectPath, LoadOptions.None);
            foreach (XElement? property in project.Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
            {
                foreach (var framework in property.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    targetFrameworks.Add(framework.Trim());
                }
            }
        }

        string[] frameworks = [.. targetFrameworks.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)];
        MsBuildEnvironmentSnapshot? environment = session.Environment;
        if (session.Trust.Level >= RepositoryTrustLevel.TrustedBuild && environment is not null)
        {
            environment = await DotNetEnvironmentResolver.RestoreAsync(
                environment,
                session.RepositoryPath,
                solutionPath,
                cancellationToken);
        }

        RepositorySession updated = session with
        {
            SolutionPath = solutionPath,
            TargetFrameworks = frameworks,
            Environment = environment,
        };
        lock (_sessionGate)
        {
            if (!_sessions.ContainsKey(command.WorkspaceId))
            {
                throw new InvalidOperationException("The workspace was replaced while selecting a solution.");
            }

            _sessions[command.WorkspaceId] = updated;
        }

        var rememberedSolutionPath = Path.GetRelativePath(session.RepositoryPath, solutionPath)
            .Replace('\\', '/');
        try
        {
            await PersistSolutionPreferenceAsync(
                session.RepositoryPath,
                rememberedSolutionPath,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Could not remember selected solution {SolutionPath}: {Message}",
                solutionPath,
                exception.Message);
        }

        await _factsStore.UpsertAsync(
            new RepositoryFacts(
                updated.WorkspaceId,
                updated.RepositoryPath,
                updated.Trust,
                solutionPath,
                frameworks,
                updated.Environment),
            cancellationToken);
        await _events.PublishAsync(
            new SolutionLoaded(
                command.SessionId,
                DateTimeOffset.UtcNow,
                solutionPath,
                command.WorkspaceId,
                frameworks),
            cancellationToken);
        return new SolutionSelectionResult(command.WorkspaceId, solutionPath, frameworks);
    }

    /// <inheritdoc />
    public async Task<WorkspaceBaseline> HandleAsync(
        RecordBaselineCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RepositorySession? session;
        lock (_sessionGate)
        {
            _sessions.TryGetValue(command.WorkspaceId, out session);
        }

        if (session is null || session.SessionId != command.SessionId)
        {
            throw new InvalidOperationException("The workspace is not open for this session.");
        }

        if (session.Trust.Level < RepositoryTrustLevel.TrustedRead)
        {
            throw new UnauthorizedAccessException(
                "Baseline capture requires TrustedRead. Reopen the repository after granting read trust.");
        }

        var stopwatch = Stopwatch.StartNew();
        var candidates = new HashSet<string>(PathComparer);
        foreach (var approvedRoot in session.Configuration.ApprovedRoots)
        {
            var root = NormalizeUnderRoot(session.RepositoryPath, approvedRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in EnumerateSafeFiles(root, cancellationToken))
            {
                var extension = Path.GetExtension(path);
                if ((_baselineExtensions.Contains(extension) || _baselineFileNames.Contains(Path.GetFileName(path)))
                    && !IsProhibited(session.RepositoryPath, path, session.Configuration.ProhibitedPaths))
                {
                    candidates.Add(path);
                }
            }
        }

        var hashes = new ConcurrentBag<WorkspaceFileHash>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8)),
            },
            async (path, token) =>
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await SHA256.HashDataAsync(stream, token);
                var relativePath = Path.GetRelativePath(session.RepositoryPath, path).Replace('\\', '/');
                hashes.Add(new WorkspaceFileHash(relativePath, Convert.ToHexStringLower(hash), info.Length));
            });
        WorkspaceFileHash[] files = [.. hashes.OrderBy(item => item.RelativePath, StringComparer.Ordinal)];
        stopwatch.Stop();
        _baselineDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        _baselineFiles.Add(files.LongLength);
        var gitRevision = await RunGitQueryAsync(
            session.RepositoryPath,
            ["rev-parse", "HEAD"],
            cancellationToken);
        var gitStatus = await RunGitQueryAsync(
            session.RepositoryPath,
            ["status", "--short", "--untracked-files=normal"],
            cancellationToken);
        var gitStatusLines = gitStatus?.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(1000)
            .ToArray() ?? [];
        var baseline = new WorkspaceBaseline(
            command.WorkspaceId,
            session.RepositoryPath,
            DateTimeOffset.UtcNow,
            files,
            gitRevision?.Trim(),
            gitStatusLines,
            session.Configuration.ApprovedRoots.ToArray(),
            session.SolutionPath,
            session.Environment?.Configuration ?? "Debug",
            session.Environment?.Platform ?? "AnyCPU",
            session.Trust.Level,
            session.Configuration.ProhibitedPaths.ToArray());
        if (_mutationCoordinator is not null)
        {
            await _mutationCoordinator.RegisterBaselineAsync(
                baseline,
                cancellationToken: cancellationToken);
        }

        return baseline;
    }

    /// <summary>Enumerates regular repository files while skipping inaccessible and linked subtrees.</summary>
    internal static IEnumerable<string> EnumerateSafeFiles(
        string root,
        CancellationToken cancellationToken = default)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                _skippedFileSystemEntries.Add(
                    1,
                    new KeyValuePair<string, object?>("exception.type", exception.GetType().Name));
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
                {
                    _skippedFileSystemEntries.Add(
                        1,
                        new KeyValuePair<string, object?>("exception.type", exception.GetType().Name));
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var name = Path.GetFileName(entry);
                    if (name is not ".git" and not "bin" and not "obj")
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                yield return Path.GetFullPath(entry);
            }
        }
    }

    private async Task<string?> RunGitQueryAsync(
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
                return null;
            }

            const int maximumOutputCharacters = 64 * 1024;
            Task<string> outputTask = ReadBoundedOutputAsync(
                process.StandardOutput,
                maximumOutputCharacters,
                cancellationToken);
            Task<string> errorTask = ReadBoundedOutputAsync(
                process.StandardError,
                maximumOutputCharacters,
                cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                _logger.LogDebug(
                    "Git baseline query failed in {RepositoryPath}: {Error}",
                    repositoryPath,
                    error[..Math.Min(error.Length, 1024)]);
                return null;
            }

            return output;
        }
        catch (Exception exception) when (exception is Win32Exception
            or IOException
            or InvalidOperationException)
        {
            _logger.LogDebug(
                exception,
                "Git is unavailable while capturing the baseline for {RepositoryPath}",
                repositoryPath);
            return null;
        }
    }

    private static async Task<string> ReadBoundedOutputAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(maximumCharacters, buffer.Length));
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var retained = Math.Min(read, maximumCharacters - output.Length);
            if (retained > 0)
            {
                output.Append(buffer, 0, retained);
            }
        }

        return output.ToString();
    }

    private static string NormalizeUnderRoot(string repositoryPath, string candidatePath)
    {
        var fullPath = Path.GetFullPath(candidatePath, repositoryPath);
        var relative = Path.GetRelativePath(repositoryPath, fullPath);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new UnauthorizedAccessException($"Path '{candidatePath}' escapes the repository root.");
        }

        var currentPath = repositoryPath;
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                break;
            }

            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"Path '{candidatePath}' traverses a symbolic link or junction.");
            }
        }

        return fullPath;
    }

    private static bool IsProhibited(
        string repositoryPath,
        string path,
        IReadOnlyList<string> prohibitedPaths)
    {
        var relative = Path.GetRelativePath(repositoryPath, path).Replace('\\', '/');
        return RepositoryPathPolicy.IsProhibited(relative, prohibitedPaths);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static async Task PersistSolutionPreferenceAsync(
        string repositoryPath,
        string? solutionRelativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configurationDirectory = NormalizeUnderRoot(repositoryPath, ".threadsmith");
        var configurationPath = NormalizeUnderRoot(
            repositoryPath,
            Path.Combine(".threadsmith", "config.json"));
        if (Directory.Exists(configurationDirectory)
            && (File.GetAttributes(configurationDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "The repository configuration directory cannot be a symbolic link or junction.");
        }

        await RepositorySettingsCoordinator.ExecuteWriteAsync(
            configurationPath,
            async token =>
            {
                JsonObject root;
                if (File.Exists(configurationPath))
                {
                    if (new FileInfo(configurationPath).Length > 1024 * 1024)
                    {
                        throw new InvalidDataException(
                            "Repository configuration exceeds the 1 MiB safety limit.");
                    }

                    var content = await File.ReadAllTextAsync(configurationPath, token);
                    root = JsonNode.Parse(
                        content,
                        new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                        RepositorySettingsCoordinator.DocumentOptions) as JsonObject
                        ?? throw new InvalidDataException(
                            "Repository configuration must be a JSON object.");
                }
                else
                {
                    root = [];
                }

                JsonObject solution;
                if (root["solution"] is JsonObject existingSolution)
                {
                    solution = existingSolution;
                }
                else
                {
                    solution = [];
                    root["solution"] = solution;
                }

                solution["path"] = solutionRelativePath is null
                    ? null
                    : JsonValue.Create(solutionRelativePath);
                Directory.CreateDirectory(configurationDirectory);
                if ((File.GetAttributes(configurationDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException(
                        "The repository configuration directory cannot be a symbolic link or junction.");
                }

                var temporaryPath = Path.Combine(
                    configurationDirectory,
                    $".config-{Guid.NewGuid():N}.tmp");
                var serialized = root.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
                try
                {
                    await File.WriteAllTextAsync(
                        temporaryPath,
                        serialized,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        token);
                    File.Move(temporaryPath, configurationPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            },
            cancellationToken);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record RepositorySession(
        SessionId SessionId,
        WorkspaceId WorkspaceId,
        string RepositoryPath,
        RepositoryTrustState Trust,
        RepositoryConfigurationSnapshot Configuration,
        MsBuildEnvironmentSnapshot? Environment,
        string? SolutionPath,
        IReadOnlyList<string> TargetFrameworks);

    private static class WorkspaceMetrics
    {
        public static readonly Meter Meter = new("Threadsmith.Workspaces");
    }
}
