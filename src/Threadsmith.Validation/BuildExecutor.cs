namespace Threadsmith.Validation;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Normalizes compiler output emitted by direct <c>dotnet build</c> processes.</summary>
public sealed partial class DiagnosticNormalizer
{
    /// <summary>Parses compiler diagnostic lines into stable host-owned records.</summary>
    /// <param name="output">Combined bounded standard output and error.</param>
    /// <param name="repositoryPath">Repository root used to normalize source paths.</param>
    /// <param name="projectName">Fallback project name.</param>
    /// <param name="targetFramework">Fallback target framework.</param>
    /// <param name="confidence">Semantic confidence carried by the build.</param>
    /// <returns>Deterministically de-duplicated diagnostics.</returns>
    public static IReadOnlyList<Diagnostic> Normalize(
        string output,
        string repositoryPath,
        string projectName,
        string targetFramework,
        SemanticConfidenceLevel confidence)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var diagnostics = new List<Diagnostic>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = CompilerDiagnosticRegex().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var fileValue = match.Groups["file"].Value.Trim();
            string? relativeFile = null;
            if (fileValue.Length > 0)
            {
                var fullFile = Path.IsPathRooted(fileValue)
                    ? Path.GetFullPath(fileValue)
                    : Path.GetFullPath(fileValue, root);
                var relative = Path.GetRelativePath(root, fullFile);
                if (!relative.Equals("..", StringComparison.Ordinal)
                    && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !Path.IsPathRooted(relative))
                {
                    relativeFile = relative.Replace('\\', '/');
                }
            }

            var lineNumber = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var column = int.Parse(match.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var endLine = match.Groups["endLine"].Success
                ? int.Parse(match.Groups["endLine"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : lineNumber;
            var endColumn = match.Groups["endColumn"].Success
                ? int.Parse(match.Groups["endColumn"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : column;
            var code = match.Groups["code"].Value.Trim();
            var message = match.Groups["message"].Value.Trim();
            var projectPath = match.Groups["project"].Value.Trim();
            var normalizedProject = projectPath.Length == 0
                ? projectName
                : Path.GetFileNameWithoutExtension(projectPath);
            var identitySource = string.Join(
                '|',
                code,
                normalizedProject,
                targetFramework,
                relativeFile,
                lineNumber,
                column,
                message);
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identitySource)))[..16];
            diagnostics.Add(new Diagnostic
            {
                Id = id,
                Code = code,
                Severity = Enum.Parse<DiagnosticSeverity>(
                    match.Groups["severity"].Value,
                    ignoreCase: true),
                Project = normalizedProject,
                TargetFramework = targetFramework,
                File = relativeFile,
                Range = new SourceRange(lineNumber, column, endLine, endColumn),
                Message = message,
                Confidence = confidence,
            });
        }

        return diagnostics
            .DistinctBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic.Project, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.TargetFramework, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.File, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Range?.StartLine ?? 0)
            .ThenBy(diagnostic => diagnostic.Range?.StartColumn ?? 0)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToArray();
    }

    [GeneratedRegex(
        @"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)(?:,(?<endLine>\d+),(?<endColumn>\d+))?\):\s*(?<severity>error|warning|info)\s+(?<code>[A-Za-z]+\d+):\s*(?<message>.*?)(?:\s+\[(?<project>[^\]]+\.[a-zA-Z]+proj)\])?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex CompilerDiagnosticRegex();
}

/// <summary>Executes trusted affected-project builds without invoking a shell.</summary>
public sealed class BuildExecutor
{
    private const int _maximumCapturedCharacters = 1024 * 1024;

    private static readonly Histogram<double> _buildLatency = ValidationMetrics.Meter.CreateHistogram<double>(
        "threadsmith.validation.build.duration",
        "ms");

    private static readonly Histogram<int> _diagnosticCount = ValidationMetrics.Meter.CreateHistogram<int>(
        "threadsmith.validation.diagnostics.count");

    private readonly TimeSpan _cancellationBackstop;
    private readonly DiagnosticNormalizer _normalizer;
    private readonly IDomainEventStream _events;
    private readonly ILogger<BuildExecutor> _logger;

    /// <summary>Initializes a new instance of the <see cref="BuildExecutor"/> class.</summary>
    public BuildExecutor(
        IDomainEventStream events,
        DiagnosticNormalizer normalizer,
        ILogger<BuildExecutor> logger,
        TimeSpan? cancellationBackstop = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(logger);
        if (cancellationBackstop <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cancellationBackstop));
        }

        _events = events;
        _normalizer = normalizer;
        _logger = logger;
        _cancellationBackstop = cancellationBackstop ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Builds affected projects or the selected baseline target and returns normalized diagnostics.</summary>
    /// <param name="request">Trusted build request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Structured build evidence.</returns>
    public async Task<BuildValidationResult> ExecuteAsync(
        BuildValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        if (request.SessionId == default || request.RunId == default)
        {
            throw new ArgumentException("Build ownership ids cannot be default.", nameof(request));
        }

        if (request.Baseline.TrustLevel < RepositoryTrustLevel.TrustedBuild)
        {
            throw new UnauthorizedAccessException("Build validation requires TrustedBuild or stronger repository trust.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Baseline.RepositoryPath));
        var invocations = new List<(string Target, string ProjectName, string TargetFramework)>();
        if (request.Projects.Count == 0)
        {
            var selected = request.Baseline.SelectedSolutionPath;
            ArgumentException.ThrowIfNullOrWhiteSpace(selected);
            var target = Path.IsPathRooted(selected)
                ? Path.GetFullPath(selected)
                : Path.GetFullPath(selected, root);
            invocations.Add((target, Path.GetFileNameWithoutExtension(target), string.Empty));
        }
        else
        {
            foreach (var project in request.Projects)
            {
                var target = Path.GetFullPath(project.FilePath);
                var frameworks = project.TargetFrameworks.Count == 0
                    ? [string.Empty]
                    : project.TargetFrameworks;
                invocations.AddRange(frameworks.Select(framework => (target, project.Name, framework)));
            }
        }

        foreach (var invocation in invocations)
        {
            if (!IsWithinRoot(invocation.Target, root) || !File.Exists(invocation.Target))
            {
                throw new InvalidOperationException($"Build target '{invocation.Target}' must exist under the repository root.");
            }

            var relativeTarget = Path.GetRelativePath(root, invocation.Target).Replace('\\', '/');
            if (RepositoryPathPolicy.IsProhibited(
                relativeTarget,
                request.Baseline.ProhibitedPaths ?? []))
            {
                throw new UnauthorizedAccessException(
                    $"Build target '{relativeTarget}' is prohibited by repository path policy.");
            }

            ValidationPathGuard.EnsureNoReparsePointTraversal(
                root,
                invocation.Target,
                relativeTarget,
                "Build target");
        }

        await _events.PublishAsync(
            new BuildStarted(
                request.SessionId,
                DateTimeOffset.UtcNow,
                request.RunId,
                invocations.Select(invocation => invocation.Target).Distinct().ToArray()),
            cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<Diagnostic>();
        var succeeded = true;
        foreach (var invocation in invocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            process.StartInfo.ArgumentList.Add("build");
            process.StartInfo.ArgumentList.Add(invocation.Target);
            process.StartInfo.ArgumentList.Add("--no-restore");
            process.StartInfo.ArgumentList.Add("--nologo");
            process.StartInfo.ArgumentList.Add("--verbosity:minimal");
            process.StartInfo.ArgumentList.Add("-property:GenerateFullPaths=true");
            process.StartInfo.ArgumentList.Add($"-property:Configuration={request.Baseline.Configuration}");
            process.StartInfo.ArgumentList.Add($"-property:Platform={request.Baseline.Platform}");
            if (invocation.TargetFramework.Length > 0)
            {
                process.StartInfo.ArgumentList.Add($"-property:TargetFramework={invocation.TargetFramework}");
            }

            process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start dotnet build for '{invocation.Target}'.");
            }

            var standardOutput = DrainAsync(process.StandardOutput);
            var standardError = DrainAsync(process.StandardError);
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(exitTask, cancellationTask);
            if (completed == cancellationTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between cancellation and tree termination.
                }

                var backstop = Task.Delay(_cancellationBackstop, CancellationToken.None);
                if (await Task.WhenAny(exitTask, backstop) != exitTask)
                {
                    _logger.LogWarning(
                        "Abandoning build process {ProcessId} after the cancellation backstop elapsed",
                        process.Id);
                    _ = exitTask.ContinueWith(
                        task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }

                _ = standardOutput.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                _ = standardError.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                throw new OperationCanceledException(cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _ = standardOutput.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                _ = standardError.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                throw new OperationCanceledException(cancellationToken);
            }

            var output = await standardOutput;
            var error = await standardError;
            succeeded &= process.ExitCode == 0;
            diagnostics.AddRange(DiagnosticNormalizer.Normalize(
                string.Concat(output, Environment.NewLine, error),
                root,
                invocation.ProjectName,
                invocation.TargetFramework,
                request.Confidence));
        }

        stopwatch.Stop();
        Diagnostic[] normalized = [.. diagnostics.DistinctBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)];
        _buildLatency.Record(stopwatch.Elapsed.TotalMilliseconds);
        _diagnosticCount.Record(normalized.Length);
        return new BuildValidationResult(
            succeeded,
            normalized,
            invocations.Select(invocation => invocation.Target).Distinct().ToArray(),
            stopwatch.Elapsed);
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var captured = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0)
            {
                return captured.ToString();
            }

            var remaining = _maximumCapturedCharacters - captured.Length;
            if (remaining > 0)
            {
                captured.Append(buffer, 0, Math.Min(remaining, count));
            }
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var relative = Path.GetRelativePath(root, path);
        return !relative.Equals("..", comparison)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison)
            && !Path.IsPathRooted(relative);
    }
}

/// <summary>Captures build diagnostics and semantic confidence against an immutable baseline.</summary>
public sealed class BaselineBuildCapture
{
    private readonly BuildExecutor _executor;

    /// <summary>Initializes a new instance of the <see cref="BaselineBuildCapture"/> class.</summary>
    /// <param name="executor">Trusted structured build executor.</param>
    public BaselineBuildCapture(BuildExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
    }

    /// <summary>Builds the committed baseline and records capture-time confidence.</summary>
    /// <param name="request">Baseline build request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Immutable baseline diagnostic capture.</returns>
    public async Task<BaselineCapture> CaptureAsync(
        BuildValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _executor.ExecuteAsync(request, cancellationToken);
        return new BaselineCapture(
            request.Baseline.WorkspaceId,
            request.Baseline.CapturedAt,
            DateTimeOffset.UtcNow,
            request.Confidence,
            result.Diagnostics.Select(diagnostic => diagnostic with
            {
                Confidence = request.Confidence,
                IsBaselineDiagnostic = true,
                Classification = request.Confidence == SemanticConfidenceLevel.FullSemantic
                    ? DiagnosticClassification.Baseline
                    : DiagnosticClassification.ConfidenceDegraded,
            }).ToArray());
    }
}
