namespace Threadsmith.Validation;

using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.RegularExpressions;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Normalizes framework-specific <c>dotnet test</c> summaries.</summary>
public sealed partial class TestResultNormalizer
{
    /// <summary>Normalizes one selected test-project process result.</summary>
    /// <param name="project">Executed test project.</param>
    /// <param name="process">Bounded process result.</param>
    /// <param name="relatedMutationIds">Mutations that drove selection.</param>
    /// <returns>Framework-neutral aggregate test evidence.</returns>
    public static TestResult Normalize(
        TestProject project,
        ProcessExecutionResult process,
        IReadOnlyList<MutationId> relatedMutationIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(relatedMutationIds);
        var output = string.Concat(
            process.StandardOutput,
            Environment.NewLine,
            process.StandardError).Trim();
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var configuredLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = configuredLine.Trim();
            if (line.StartsWith("succeeded:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), CultureInfo.InvariantCulture, out var succeeded))
            {
                passed = succeeded;
            }
            else if (line.StartsWith("failed:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), CultureInfo.InvariantCulture, out var failures))
            {
                failed = failures;
            }
            else if (line.StartsWith("skipped:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), CultureInfo.InvariantCulture, out var skips))
            {
                skipped = skips;
            }
        }

        var vstest = VstestSummaryRegex().Match(output);
        if (vstest.Success)
        {
            failed = int.Parse(vstest.Groups["failed"].Value, CultureInfo.InvariantCulture);
            passed = int.Parse(vstest.Groups["passed"].Value, CultureInfo.InvariantCulture);
            skipped = int.Parse(vstest.Groups["skipped"].Value, CultureInfo.InvariantCulture);
        }

        var processSucceeded = !process.TimedOut && process.ExitCode == 0;
        var outcome = !processSucceeded || failed > 0
            ? TestOutcome.Failed
            : skipped > 0
                ? TestOutcome.Skipped
                : TestOutcome.Passed;
        return new TestResult
        {
            Project = project,
            Outcome = outcome,
            ProcessCompleted = !process.TimedOut && process.ExitCode.HasValue,
            Passed = passed,
            Failed = failed,
            Skipped = skipped,
            Output = output,
            Duration = process.Duration,
            RelatedMutationIds = relatedMutationIds.ToArray(),
        };
    }

    [GeneratedRegex(
        @"Failed:\s*(?<failed>\d+)\s*,\s*Passed:\s*(?<passed>\d+)\s*,\s*Skipped:\s*(?<skipped>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex VstestSummaryRegex();
}

/// <summary>Runs selected test projects through the tracked process manager.</summary>
public sealed class TestRunner
{
    private static readonly Histogram<double> _testLatency = ValidationMetrics.Meter.CreateHistogram<double>(
        "threadsmith.validation.tests.duration",
        "ms");

    private static readonly Counter<long> _testsPassed = ValidationMetrics.Meter.CreateCounter<long>(
        "threadsmith.validation.tests.passed");

    private static readonly Counter<long> _testsFailed = ValidationMetrics.Meter.CreateCounter<long>(
        "threadsmith.validation.tests.failed");

    private static readonly Counter<long> _testsSkipped = ValidationMetrics.Meter.CreateCounter<long>(
        "threadsmith.validation.tests.skipped");

    private readonly IDomainEventStream _events;
    private readonly IProcessManager _processManager;

    /// <summary>Initializes a new instance of the <see cref="TestRunner"/> class.</summary>
    public TestRunner(IProcessManager processManager, IDomainEventStream events)
    {
        ArgumentNullException.ThrowIfNull(processManager);
        ArgumentNullException.ThrowIfNull(events);
        _processManager = processManager;
        _events = events;
    }

    /// <summary>Runs a selected project-level test scope without restoring or rebuilding.</summary>
    /// <param name="sessionId">Owning session.</param>
    /// <param name="runId">Owning run.</param>
    /// <param name="baseline">Trusted workspace baseline.</param>
    /// <param name="selection">Explained selected scope.</param>
    /// <param name="timeout">Per-project execution timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalized test evidence.</returns>
    public async Task<TestValidationResult> RunAsync(
        SessionId sessionId,
        RunId runId,
        WorkspaceBaseline baseline,
        TestSelection selection,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(selection);
        if (sessionId == default || runId == default)
        {
            throw new ArgumentException("Test execution ownership ids cannot be default.", nameof(sessionId));
        }

        if (baseline.TrustLevel < RepositoryTrustLevel.TrustedBuild)
        {
            throw new UnauthorizedAccessException(
                "Test execution requires TrustedBuild or stronger repository trust because repository code will run.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseline.RepositoryPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var results = new List<TestResult>();
        foreach (var project in selection.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(project.FilePath);
            var relativeTarget = Path.GetRelativePath(root, target).Replace('\\', '/');
            if (relativeTarget.Equals("..", comparison)
                || relativeTarget.StartsWith("../", comparison)
                || Path.IsPathRooted(relativeTarget)
                || !File.Exists(target))
            {
                throw new InvalidOperationException(
                    $"Test target '{project.FilePath}' must exist under the repository root.");
            }

            if (RepositoryPathPolicy.IsProhibited(relativeTarget, baseline.ProhibitedPaths ?? []))
            {
                throw new UnauthorizedAccessException(
                    $"Test target '{relativeTarget}' is prohibited by repository path policy.");
            }

            ValidationPathGuard.EnsureNoReparsePointTraversal(
                root,
                target,
                relativeTarget,
                "Test target");

            var process = await _processManager.RunAsync(
                new ProcessExecutionRequest
                {
                    ToolInvocationId = new ToolInvocationId(Guid.NewGuid()),
                    RunId = runId,
                    FileName = "dotnet",
                    Arguments = project.Framework == TestFramework.MicrosoftTestingPlatform
                        ?
                        [
                            "test",
                            "--project",
                            target,
                            "--no-restore",
                            "--no-build",
                            "--configuration",
                            baseline.Configuration,
                            "--verbosity",
                            "minimal",
                        ]
                        :
                        [
                            "test",
                            target,
                            "--no-restore",
                            "--no-build",
                            "--nologo",
                            "--verbosity:minimal",
                            $"-property:Configuration={baseline.Configuration}",
                            $"-property:Platform={baseline.Platform}",
                        ],
                    WorkingDirectory = root,
                    Timeout = timeout,
                    MaximumOutputCharacters = 1024 * 1024,
                    Origin = ProcessRequestOrigin.Host,
                },
                cancellationToken);
            results.Add(TestResultNormalizer.Normalize(project, process, selection.RelatedMutationIds));
        }

        var validation = new TestValidationResult
        {
            Selection = selection,
            Results = results,
            Completed = results.Count == selection.Projects.Count
                && results.All(result => result.ProcessCompleted),
        };
        _testLatency.Record(results.Sum(result => result.Duration.TotalMilliseconds));
        _testsPassed.Add(validation.Passed);
        _testsFailed.Add(validation.Failed);
        _testsSkipped.Add(validation.Skipped);
        await _events.PublishAsync(
            new TestRunCompleted(
                sessionId,
                DateTimeOffset.UtcNow,
                validation.Passed,
                validation.Failed,
                validation.Skipped,
                validation)
            {
                SchemaVersion = 2,
            },
            cancellationToken);
        return validation;
    }
}

/// <summary>Coordinates supported-project discovery, explained selection, case enumeration, and execution.</summary>
public sealed class TestValidationPipeline
{
    private readonly TestDiscoverer _discoverer;
    private readonly IDomainEventStream _events;
    private readonly TestRunner _runner;

    /// <summary>Initializes a new instance of the <see cref="TestValidationPipeline"/> class.</summary>
    public TestValidationPipeline(
        TestDiscoverer discoverer,
        TestRunner runner,
        IDomainEventStream events)
    {
        ArgumentNullException.ThrowIfNull(discoverer);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(events);
        _discoverer = discoverer;
        _runner = runner;
        _events = events;
    }

    /// <summary>Validates the conservatively selected project-level test scope.</summary>
    /// <param name="request">Build request carrying trust, ownership, affected projects, and inventory.</param>
    /// <param name="mutationSet">Mutation set that drives selection.</param>
    /// <param name="timeout">Per-project discovery and execution timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Explained normalized test evidence.</returns>
    public async Task<TestValidationResult> ValidateAsync(
        BuildValidationRequest request,
        MutationSet mutationSet,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mutationSet);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        if (request.Baseline.TrustLevel < RepositoryTrustLevel.TrustedBuild)
        {
            throw new UnauthorizedAccessException(
                "Test discovery and execution require TrustedBuild because repository test adapters can run code.");
        }

        var projects = TestDiscoverer.DiscoverProjects(
            request.Baseline.RepositoryPath,
            request.ProjectInventory,
            request.Baseline.ProhibitedPaths);
        var selection = TestSelector.Select(projects, request.Projects, mutationSet);
        if (selection.Projects.Count == 0)
        {
            var empty = new TestValidationResult
            {
                Selection = selection,
                Completed = true,
            };
            await _events.PublishAsync(
                new TestRunCompleted(
                    request.SessionId,
                    DateTimeOffset.UtcNow,
                    Passed: 0,
                    Failed: 0,
                    Skipped: 0,
                    StructuredResult: empty)
                {
                    SchemaVersion = 2,
                },
                cancellationToken);
            return empty;
        }

        var cases = await _discoverer.DiscoverCasesAsync(
            request.RunId,
            request.Baseline.RepositoryPath,
            selection.Projects,
            timeout,
            cancellationToken);
        selection = selection with
        {
            TestCases = cases,
        };
        return await _runner.RunAsync(
            request.SessionId,
            request.RunId,
            request.Baseline,
            selection,
            timeout,
            cancellationToken);
    }
}
