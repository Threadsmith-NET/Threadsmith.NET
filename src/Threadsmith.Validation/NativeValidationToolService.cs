namespace Threadsmith.Validation;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>One trusted NuGet advisory source and optional logical credential.</summary>
public sealed record NuGetAdvisorySourceOptions(
    string Name,
    Uri Source,
    string? Username = null,
    string? SecretReference = null);

/// <summary>Runs bounded exploratory .NET health and validation operations through the tracked process owner.</summary>
public sealed partial class NativeValidationToolService : INativeValidationToolService
{
    private const int MaximumOutputCharacters = 512 * 1024;
    private const long MaximumAssetsBytes = 16 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, DiagnosticRun> _diagnosticRuns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StoredDiscoveredTest> _discoveredTests = new(StringComparer.Ordinal);
    private readonly IProcessManager _processManager;
    private readonly ISecretResolver? _secretResolver;
    private readonly IReadOnlyList<NuGetAdvisorySourceOptions> _sources;
    private readonly TestDiscoverer _testDiscoverer;

    /// <summary>Initializes a new instance of the <see cref="NativeValidationToolService"/> class.</summary>
    public NativeValidationToolService(IProcessManager processManager, IEnumerable<Uri>? sources = null)
        : this(
            processManager,
            secretResolver: null,
            (sources ?? []).Select((source, index) =>
                new NuGetAdvisorySourceOptions($"source-{index + 1}", source)))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NativeValidationToolService"/> class with trusted sources and final-boundary secret resolution.</summary>
    public NativeValidationToolService(
        IProcessManager processManager,
        ISecretResolver? secretResolver,
        IEnumerable<NuGetAdvisorySourceOptions> sources)
    {
        ArgumentNullException.ThrowIfNull(processManager);
        ArgumentNullException.ThrowIfNull(sources);
        _processManager = processManager;
        _secretResolver = secretResolver;
        _testDiscoverer = new TestDiscoverer(processManager);
        NuGetAdvisorySourceOptions[] configuredSources = [.. sources];
        if (configuredSources.Length > 16
            || configuredSources.Any(source => !IsValidSource(source)
                || (source.SecretReference is not null && secretResolver is null)))
        {
            throw new ArgumentException(
                "Package advisory sources must be bounded named credential-free HTTPS URIs with complete logical credential configuration.",
                nameof(sources));
        }

        _sources = configuredSources
            .DistinctBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_sources.Count != configuredSources.Length)
        {
            throw new ArgumentException("Package advisory source names must be unique.", nameof(sources));
        }
    }

    /// <summary>Initializes a new instance of the <see cref="NativeValidationToolService"/> class for legacy hosts and tests.</summary>
    public NativeValidationToolService(
        IProcessManager processManager,
        ISecretStore secretStore,
        IEnumerable<NuGetAdvisorySourceOptions> sources)
        : this(processManager, new LegacySecretStoreResolver(secretStore), sources)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ConfiguredNetworkHosts => _sources
        .Select(source => source.Source.Host)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <inheritdoc />
    public IReadOnlyList<string> ConfiguredSecretReferences => _sources
        .Select(source => source.SecretReference)
        .Where(reference => reference is not null)
        .Select(reference => reference ?? string.Empty)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <inheritdoc />
    public async Task<NuGetDependencyHealthResult> InspectPackagesAsync(
        string repositoryPath,
        RunId runId,
        NuGetDependencyHealthRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumDependencies is < 1 or > 1000 || request.MaximumAdvisories is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Package result bounds are outside host limits.");
        }

        var root = NormalizeRoot(repositoryPath);
        var project = ResolveTarget(root, request.ProjectPath, requireProject: true);
        var assetsPath = Path.Combine(Path.GetDirectoryName(project) ?? root, "obj", "project.assets.json");
        var omissions = new List<string>();
        var dependencies = new List<NuGetDependencyNode>();
        DateTimeOffset? generatedAt = null;
        if (!File.Exists(assetsPath))
        {
            omissions.Add("Existing restore assets are absent; no restore was attempted.");
        }
        else
        {
            var relativeAssets = Path.GetRelativePath(root, assetsPath);
            ValidationPathGuard.EnsureNoReparsePointTraversal(
                root,
                assetsPath,
                relativeAssets,
                "NuGet assets");
            var info = new FileInfo(assetsPath);
            if (info.Length > MaximumAssetsBytes)
            {
                throw new InvalidDataException("NuGet restore assets exceed the inspection size limit.");
            }

            generatedAt = info.LastWriteTimeUtc;
            await using FileStream stream = File.OpenRead(assetsPath);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            dependencies.AddRange(ParseDependencies(document.RootElement, request.IncludeTransitive));
        }

        IReadOnlyList<PackageAdvisory> advisories = [];
        var sourceNames = new List<string> { "project.assets.json" };
        if (request.SourceMode == PackageHealthSourceMode.ConfiguredSources)
        {
            if (_sources.Count == 0)
            {
                omissions.Add("No trusted package advisory sources are configured.");
            }
            else
            {
                using NuGetQueryContext queryContext = await CreateNuGetQueryContextAsync(cancellationToken);
                var collected = new List<PackageAdvisory>();
                var advisoryOutputTruncated = false;
                foreach (var category in new[] { "--vulnerable", "--deprecated", "--outdated" })
                {
                    ProcessExecutionResult process = await RunWithEnvironmentAsync(
                        root,
                        runId,
                        CreatePackageArguments(project, queryContext.ConfigurationPath, category),
                        TimeSpan.FromSeconds(60),
                        queryContext.EnvironmentVariables,
                        cancellationToken);
                    if (!process.TimedOut && process.ExitCode == 0)
                    {
                        if (process.StandardOutputTruncated || process.StandardErrorTruncated)
                        {
                            advisoryOutputTruncated = true;
                            omissions.Add($"Configured-source {category[2..]} query output was truncated.");
                        }

                        try
                        {
                            collected.AddRange(ParseAdvisories(
                                process.StandardOutput,
                                request.MaximumAdvisories - collected.Count));
                        }
                        catch (JsonException)
                        {
                            omissions.Add($"Configured-source {category[2..]} query returned malformed JSON.");
                        }
                    }
                    else
                    {
                        omissions.Add($"Configured-source {category[2..]} query did not complete successfully.");
                    }
                }

                sourceNames.AddRange(_sources.Select(source => source.Source.GetLeftPart(UriPartial.Authority)));
                advisories = collected
                    .Distinct()
                    .Take(request.MaximumAdvisories)
                    .ToArray();

                if (advisoryOutputTruncated)
                {
                    omissions.Add("One or more configured-source advisory results were incomplete.");
                }
            }
        }

        NuGetDependencyNode[] boundedDependencies = [.. dependencies
            .OrderBy(item => item.TargetFramework, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Take(request.MaximumDependencies)];
        var truncated = boundedDependencies.Length != dependencies.Count
            || advisories.Count >= request.MaximumAdvisories
            || omissions.Any(omission => omission.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        var complete = File.Exists(assetsPath)
            && (request.SourceMode == PackageHealthSourceMode.Offline
                || (_sources.Count > 0 && omissions.Count == 0));
        return new NuGetDependencyHealthResult(
            boundedDependencies,
            advisories,
            generatedAt,
            DateTimeOffset.UtcNow,
            complete,
            request.SourceMode == PackageHealthSourceMode.Offline,
            generatedAt is null || DateTimeOffset.UtcNow - generatedAt > TimeSpan.FromDays(7),
            sourceNames,
            omissions,
            truncated,
            ValidationAuthority.Exploratory);
    }

    /// <inheritdoc />
    public Task<ValidationToolResult> BuildAsync(
        string repositoryPath,
        RunId runId,
        BuildToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunValidationAsync(
            repositoryPath,
            runId,
            request,
            ValidationInvocationKind.Build,
            ["build"],
            DiagnosticOrigin.Compiler,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ValidationToolResult> AnalyzeAsync(
        string repositoryPath,
        RunId runId,
        AnalyzerToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunValidationAsync(
            repositoryPath,
            runId,
            request,
            ValidationInvocationKind.Analyzer,
            ["build", "-property:RunAnalyzers=true", "-property:RunAnalyzersDuringBuild=true"],
            DiagnosticOrigin.Analyzer,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ValidationToolResult> CheckFormatAsync(
        string repositoryPath,
        RunId runId,
        FormatCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTimeout(request.TimeoutSeconds);
        var root = NormalizeRoot(repositoryPath);
        var target = ResolveTarget(root, request.TargetPath, requireProject: false);
        string[] arguments =
        [
            "format", target, "--no-restore", "--verify-no-changes", "--verbosity", "minimal",
        ];
        ProcessExecutionResult process = await RunAsync(
            root,
            runId,
            arguments,
            TimeSpan.FromSeconds(request.TimeoutSeconds),
            cancellationToken);
        var output = string.Concat(process.StandardOutput, Environment.NewLine, process.StandardError).Trim();
        var invocationId = CreateIdentity(string.Join('|', runId.Value, ValidationInvocationKind.FormatCheck, DateTimeOffset.UtcNow.Ticks, target));
        _diagnosticRuns[invocationId] = new DiagnosticRun(
            invocationId,
            runId,
            root,
            Path.GetRelativePath(root, target).Replace('\\', '/'),
            DiagnosticOrigin.Analyzer,
            [],
            DateTimeOffset.UtcNow);
        TrimIndexes();
        return new ValidationToolResult(
            invocationId,
            ValidationInvocationKind.FormatCheck,
            ValidationAuthority.Exploratory,
            !process.TimedOut && process.ExitCode == 0,
            Path.GetRelativePath(root, target).Replace('\\', '/'),
            arguments,
            [],
            output,
            process.ExitCode ?? -1,
            process.Duration,
            process.TimedOut,
            process.StandardOutputTruncated || process.StandardErrorTruncated);
    }

    /// <inheritdoc />
    public Task<DiagnosticQueryResult> QueryDiagnosticsAsync(
        string repositoryPath,
        DiagnosticQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = NormalizeRoot(repositoryPath);
        if (query.Page < 0 || query.PageSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Diagnostic page bounds are invalid.");
        }

        if (new[] { query.InvocationId, query.Project, query.File, query.Code }
            .Any(value => value is { Length: > 1024 }))
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Diagnostic query text exceeds host limits.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<DiagnosticQueryItem> items = _diagnosticRuns.Values
            .Where(run => RepositoryPathsEqual(run.RepositoryPath, root))
            .OrderBy(run => run.CreatedAt)
            .ThenBy(run => run.InvocationId, StringComparer.Ordinal)
            .SelectMany(run => run.Diagnostics.Select(diagnostic =>
                new DiagnosticQueryItem(run.InvocationId, run.RunId, run.ScopePath, run.Origin, ValidationAuthority.Exploratory, diagnostic)));
        if (!string.IsNullOrWhiteSpace(query.InvocationId))
        {
            items = items.Where(item => item.InvocationId.Equals(query.InvocationId, StringComparison.Ordinal));
        }

        if (query.RunId is { } runId)
        {
            items = items.Where(item => item.RunId == runId);
        }

        if (!string.IsNullOrWhiteSpace(query.Project))
        {
            items = items.Where(item => item.Diagnostic.Project.Equals(query.Project, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.File))
        {
            var file = query.File.Replace('\\', '/');
            items = items.Where(item => string.Equals(item.Diagnostic.File, file, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Code))
        {
            items = items.Where(item => item.Diagnostic.Code.Equals(query.Code, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Severity is { } severity)
        {
            items = items.Where(item => item.Diagnostic.Severity == severity);
        }

        if (query.Origin is { } origin)
        {
            items = items.Where(item => item.Origin == origin);
        }

        if (query.BaselineClass is { } classification)
        {
            items = items.Where(item => item.Diagnostic.Classification == classification);
        }

        DiagnosticQueryItem[] all = [.. items];
        var requestedOffset = (long)query.Page * query.PageSize;
        var offset = requestedOffset >= all.Length ? all.Length : (int)requestedOffset;
        DiagnosticQueryItem[] page = [.. all.Skip(offset).Take(query.PageSize)];
        return Task.FromResult(new DiagnosticQueryResult(
            page,
            all.Length,
            query.Page,
            query.PageSize,
            offset + page.Length < all.Length));
    }

    /// <inheritdoc />
    public async Task<TestDiscoveryResult> DiscoverTestsAsync(
        string repositoryPath,
        RunId runId,
        TestDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTimeout(request.TimeoutSeconds);
        ValidateTestFilters(request);
        if (request.MaximumTests is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Test discovery returns at most 500 identities.");
        }

        var root = NormalizeRoot(repositoryPath);
        var projectPath = ResolveTarget(root, request.ProjectPath, requireProject: true);
        TestProject project = ReadTestProject(root, projectPath);
        var traitFilter = request.TraitName is not null && request.TraitValue is not null
            ? $"{request.TraitName}={EscapeTestFilterValue(request.TraitValue)}"
            : null;
        IReadOnlyList<TestCase> cases = await _testDiscoverer.DiscoverCasesAsync(
            runId,
            root,
            [project],
            traitFilter,
            TimeSpan.FromSeconds(request.TimeoutSeconds),
            cancellationToken);
        DiscoveredTest[] discovered = [.. cases
            .Where(testCase => testCase.FullyQualifiedName.Length <= 1024)
            .Select(testCase => CreateDiscoveredTest(root, testCase))];
        var overlongNamesOmitted = discovered.Length != cases.Count;
        if (request.TraitName is not null && request.TraitValue is not null)
        {
            discovered = [.. discovered.Select(test => test with
            {
                Traits = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [request.TraitName] = request.TraitValue,
                },
            })];
        }

        DiscoveredTest[] all = [.. ApplyTestFilters(discovered, request)];
        DiscoveredTest[] bounded = [.. all.Take(request.MaximumTests)];
        foreach (DiscoveredTest test in bounded)
        {
            _discoveredTests[test.Id.Value] = new StoredDiscoveredTest(root, test);
        }

        var discoveryId = CreateIdentity(string.Join('|', projectPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return new TestDiscoveryResult(
            discoveryId,
            bounded,
            project.Framework.ToString(),
            FormatDiscoveryFilter(request),
            DateTimeOffset.UtcNow,
            overlongNamesOmitted || bounded.Length != all.Length,
            ValidationAuthority.Exploratory);
    }

    /// <inheritdoc />
    public string ResolveTestProjectPath(string repositoryPath, DiscoveredTestId testId)
    {
        ArgumentNullException.ThrowIfNull(testId);
        if (testId.Value.Length is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(testId), "Test identity length is invalid.");
        }

        var root = NormalizeRoot(repositoryPath);
        if (!_discoveredTests.TryGetValue(testId.Value, out StoredDiscoveredTest? stored)
            || !stored.RepositoryPath.Equals(
                root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The test identity is unknown, expired, or belongs to another repository; discover tests again.");
        }

        return stored.Test.ProjectPath;
    }

    /// <inheritdoc />
    public async Task<TargetedTestResult> RunTargetedTestAsync(
        string repositoryPath,
        RunId runId,
        TargetedTestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.TestId);
        if (request.TestId.Value.Length is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Test identity length is invalid.");
        }

        ValidateTimeout(request.TimeoutSeconds);
        var root = NormalizeRoot(repositoryPath);
        _ = ResolveTestProjectPath(root, request.TestId);
        if (!_discoveredTests.TryGetValue(request.TestId.Value, out StoredDiscoveredTest? stored))
        {
            throw new InvalidOperationException("The test identity expired before execution; discover tests again.");
        }

        DiscoveredTest test = stored.Test;
        var projectPath = ResolveTarget(root, test.ProjectPath, requireProject: true);
        TestProject project = ReadTestProject(root, projectPath);
        string effectiveFilter;
        var arguments = new List<string>();
        if (project.Framework == TestFramework.MicrosoftTestingPlatform)
        {
            effectiveFilter = test.FullyQualifiedName;
            arguments.AddRange(["test", "--project", projectPath, "--no-restore", "--no-build"]);
            AddConfigurationAndFramework(arguments, request.Configuration, request.TargetFramework);
            arguments.AddRange(["--", "--filter-method", effectiveFilter]);
        }
        else
        {
            effectiveFilter = $"FullyQualifiedName={EscapeTestFilterValue(test.FullyQualifiedName)}";
            arguments.AddRange(["test", projectPath, "--no-restore", "--no-build", "--nologo", "--verbosity:minimal"]);
            AddConfigurationAndFramework(arguments, request.Configuration, request.TargetFramework);
            arguments.AddRange(["--filter", effectiveFilter]);
        }

        ProcessExecutionResult process = await RunAsync(
            root,
            runId,
            arguments,
            TimeSpan.FromSeconds(request.TimeoutSeconds),
            cancellationToken);
        var selection = new TestSelection { Projects = [project] };
        TestResult normalized = TestResultNormalizer.Normalize(project, process, selection.RelatedMutationIds);
        return new TargetedTestResult(
            test,
            effectiveFilter,
            ValidationAuthority.Exploratory,
            normalized.Outcome,
            normalized.Passed,
            normalized.Failed,
            normalized.Skipped,
            normalized.Output,
            normalized.Duration,
            process.TimedOut,
            process.StandardOutputTruncated || process.StandardErrorTruncated,
            []);
    }

    private async Task<ValidationToolResult> RunValidationAsync(
        string repositoryPath,
        RunId runId,
        DotNetValidationTargetRequest request,
        ValidationInvocationKind kind,
        IReadOnlyList<string> command,
        DiagnosticOrigin origin,
        CancellationToken cancellationToken)
    {
        ValidateTimeout(request.TimeoutSeconds);
        var root = NormalizeRoot(repositoryPath);
        var target = ResolveTarget(root, request.TargetPath, requireProject: false);
        ValidateFramework(request.TargetFramework);
        var arguments = new List<string>(command);
        arguments.Insert(1, target);
        arguments.Add("--no-restore");
        arguments.Add("--nologo");
        arguments.Add("--verbosity:minimal");
        AddConfigurationAndFramework(arguments, request.Configuration, request.TargetFramework);
        arguments.Add("-property:GenerateFullPaths=true");

        ProcessExecutionResult process = await RunAsync(
            root,
            runId,
            arguments,
            TimeSpan.FromSeconds(request.TimeoutSeconds),
            cancellationToken);
        var output = string.Concat(process.StandardOutput, Environment.NewLine, process.StandardError).Trim();
        Diagnostic[] diagnostics = [.. DiagnosticNormalizer.Normalize(
            output,
            root,
            Path.GetFileNameWithoutExtension(target),
            request.TargetFramework ?? string.Empty,
            SemanticConfidenceLevel.None)];
        var invocationId = CreateIdentity(string.Join('|', runId.Value, kind, DateTimeOffset.UtcNow.Ticks, target));
        _diagnosticRuns[invocationId] = new DiagnosticRun(
            invocationId,
            runId,
            root,
            Path.GetRelativePath(root, target).Replace('\\', '/'),
            origin,
            diagnostics,
            DateTimeOffset.UtcNow);
        TrimIndexes();
        return new ValidationToolResult(
            invocationId,
            kind,
            ValidationAuthority.Exploratory,
            !process.TimedOut && process.ExitCode == 0,
            Path.GetRelativePath(root, target).Replace('\\', '/'),
            arguments,
            diagnostics,
            output,
            process.ExitCode ?? -1,
            process.Duration,
            process.TimedOut,
            process.StandardOutputTruncated || process.StandardErrorTruncated);
    }

    private async Task<NuGetQueryContext> CreateNuGetQueryContextAsync(
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Threadsmith", "nuget-health");
        Directory.CreateDirectory(directory);
        var configurationPath = Path.Combine(directory, $"{Guid.NewGuid():N}.config");
        var document = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    _sources.Select(source => new XElement(
                        "add",
                        new XAttribute("key", source.Name),
                        new XAttribute("value", source.Source.AbsoluteUri))))));
        try
        {
            await File.WriteAllTextAsync(
                configurationPath,
                document.ToString(SaveOptions.DisableFormatting),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (NuGetAdvisorySourceOptions source in _sources.Where(source => source.SecretReference is not null))
            {
                var secretReference = source.SecretReference ?? string.Empty;
                var resolutionRequest = new SecretResolutionRequest
                {
                    Reference = SecretReference.Parse(secretReference),
                    ComponentId = SecretResolutionRequest.CreateConfiguredComponentId(
                        "validation:nuget",
                        source.Name),
                    Purpose = "authenticate a trusted private NuGet advisory source",
                    MinimumTrust = SecretProviderTrust.UserOwned,
                };
                SecretResolutionResult resolution = await (_secretResolver
                    ?? throw new InvalidOperationException("NuGet credential resolver is unavailable."))
                    .ResolveAsync(resolutionRequest, cancellationToken);
                var secret = resolution.RequireValue(resolutionRequest);
                if (secret.Length == 0
                    || secret.Length > 16 * 1024
                    || secret.IndexOfAny(['\0', '\r', '\n', ';']) >= 0)
                {
                    throw new InvalidOperationException($"Credential for NuGet source '{source.Name}' is unavailable or invalid.");
                }

                environment[$"NuGetPackageSourceCredentials_{source.Name}"] =
                    $"Username={source.Username};Password={secret};ValidAuthenticationTypes=Basic";
            }

            return new NuGetQueryContext(configurationPath, environment);
        }
        catch
        {
            File.Delete(configurationPath);
            throw;
        }
    }

    private Task<ProcessExecutionResult> RunAsync(
        string root,
        RunId runId,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return RunWithEnvironmentAsync(
            root,
            runId,
            arguments,
            timeout,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
    }

    private async Task<ProcessExecutionResult> RunWithEnvironmentAsync(
        string root,
        RunId runId,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        return await _processManager.RunAsync(
            new ProcessExecutionRequest
            {
                ToolInvocationId = new ToolInvocationId(Guid.NewGuid()),
                RunId = runId,
                FileName = "dotnet",
                Arguments = arguments,
                EnvironmentVariables = environmentVariables,
                WorkingDirectory = root,
                Timeout = timeout,
                MaximumOutputCharacters = MaximumOutputCharacters,
                Origin = ProcessRequestOrigin.Host,
            },
            cancellationToken);
    }

    private static IReadOnlyList<string> CreatePackageArguments(
        string project,
        string configurationPath,
        string category)
    {
        return
        [
            "list", project, "package", "--no-restore", "--format", "json",
            "--include-transitive", category, "--configfile", configurationPath,
        ];
    }

    private static IReadOnlyList<NuGetDependencyNode> ParseDependencies(JsonElement root, bool includeTransitive)
    {
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("project", out JsonElement project)
            && project.TryGetProperty("frameworks", out JsonElement frameworks))
        {
            foreach (JsonProperty framework in frameworks.EnumerateObject())
            {
                if (framework.Value.TryGetProperty("dependencies", out JsonElement values))
                {
                    foreach (JsonProperty dependency in values.EnumerateObject())
                    {
                        direct.Add($"{framework.Name}|{dependency.Name}");
                    }
                }
            }
        }

        var result = new List<NuGetDependencyNode>();
        if (!root.TryGetProperty("targets", out JsonElement targets))
        {
            return result;
        }

        foreach (JsonProperty target in targets.EnumerateObject())
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                var separator = library.Name.LastIndexOf('/');
                if (separator <= 0)
                {
                    continue;
                }

                var id = library.Name[..separator];
                var version = library.Name[(separator + 1)..];
                if (id.Length > 256 || version.Length > 256 || target.Name.Length > 256)
                {
                    continue;
                }

                var isDirect = direct.Contains($"{target.Name}|{id}")
                    || direct.Any(item => item.EndsWith($"|{id}", StringComparison.OrdinalIgnoreCase));
                if (!includeTransitive && !isDirect)
                {
                    continue;
                }

                string[] children = library.Value.TryGetProperty("dependencies", out JsonElement childDependencies)
                    ? [.. childDependencies.EnumerateObject()
                        .Select(item => item.Name)
                        .Where(item => item.Length <= 256)
                        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                        .Take(50)]
                    : [];
                result.Add(new NuGetDependencyNode(id, version, "project.assets.json", isDirect, target.Name, children));
            }
        }

        return result.Distinct().ToArray();
    }

    private static IReadOnlyList<PackageAdvisory> ParseAdvisories(string json, int maximum)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var advisories = new List<PackageAdvisory>();
        CollectAdvisories(document.RootElement, advisories, maximum);
        return advisories;
    }

    private static void CollectAdvisories(JsonElement element, List<PackageAdvisory> results, int maximum)
    {
        if (results.Count >= maximum)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var id = ReadString(element, "id") ?? ReadString(element, "name") ?? string.Empty;
            var version = ReadString(element, "resolvedVersion") ?? ReadString(element, "resolved") ?? string.Empty;
            if (id.Length > 0 && element.TryGetProperty("vulnerabilities", out JsonElement vulnerabilities))
            {
                foreach (JsonElement vulnerability in vulnerabilities.EnumerateArray())
                {
                    results.Add(new PackageAdvisory(
                        id,
                        version,
                        PackageAdvisoryKind.Vulnerability,
                        ReadString(vulnerability, "severity") ?? "unknown",
                        ReadString(vulnerability, "advisoryUrl"),
                        "configured-source"));
                    if (results.Count >= maximum)
                    {
                        return;
                    }
                }
            }

            if (id.Length > 0 && element.TryGetProperty("deprecationReasons", out JsonElement deprecationReasons))
            {
                var severity = deprecationReasons.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", deprecationReasons.EnumerateArray().Select(reason => reason.GetString()))
                    : "deprecated";
                results.Add(new PackageAdvisory(
                    id,
                    version,
                    PackageAdvisoryKind.Deprecation,
                    severity,
                    ReadString(element, "alternativePackage"),
                    "configured-source"));
            }

            var latestVersion = ReadString(element, "latestVersion");
            if (id.Length > 0
                && latestVersion is not null
                && !latestVersion.Equals(version, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new PackageAdvisory(
                    id,
                    version,
                    PackageAdvisoryKind.Outdated,
                    $"latest:{latestVersion}",
                    null,
                    "configured-source"));
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                CollectAdvisories(property.Value, results, maximum);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                CollectAdvisories(child, results, maximum);
            }
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        var result = element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        return result is { Length: <= 2048 } ? result : null;
    }

    private static bool IsValidSource(NuGetAdvisorySourceOptions source)
    {
        var hasUsername = !string.IsNullOrWhiteSpace(source.Username);
        var hasSecret = !string.IsNullOrWhiteSpace(source.SecretReference);
        return source.Name.Length is > 0 and <= 64
            && source.Name.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_')
            && source.Source.IsAbsoluteUri
            && source.Source.AbsoluteUri.Length <= 2048
            && source.Source.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(source.Source.UserInfo)
            && string.IsNullOrEmpty(source.Source.Query)
            && string.IsNullOrEmpty(source.Source.Fragment)
            && hasUsername == hasSecret
            && (!hasUsername
                || (source.Username?.Length <= 256
                    && source.Username.IndexOfAny(['\0', '\r', '\n', ';']) < 0
                    && source.SecretReference?.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase) == true
                    && source.SecretReference.Length <= 256));
    }

    private static string NormalizeRoot(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Repository root does not exist.");
        }

        return root;
    }

    private static string ResolveTarget(string root, string requestedPath, bool requireProject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        var target = Path.GetFullPath(requestedPath.Replace('/', Path.DirectorySeparatorChar), root);
        var relative = Path.GetRelativePath(root, target);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (relative.Equals("..", comparison)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison)
            || Path.IsPathRooted(relative)
            || !File.Exists(target))
        {
            throw new InvalidOperationException("The validation target must be an existing repository file.");
        }

        var extension = Path.GetExtension(target);
        var valid = extension.Equals(".sln", comparison)
            || extension.Equals(".slnx", comparison)
            || extension.EndsWith("proj", comparison);
        if (!valid || (requireProject && !extension.EndsWith("proj", comparison)))
        {
            throw new InvalidOperationException("The validation target must be a supported solution or project.");
        }

        ValidationPathGuard.EnsureNoReparsePointTraversal(root, target, relative, "Validation target");
        return target;
    }

    private static void ValidateTestFilters(TestDiscoveryRequest request)
    {
        if ((request.TraitValue is null) != (request.TraitName is null))
        {
            throw new ArgumentException("Trait discovery requires both an exact trait name and value.", nameof(request));
        }

        if (request.TraitName is not null
            && (request.TraitName.Length > 128
                || request.TraitName.Any(character => !char.IsAsciiLetterOrDigit(character)
                    && character is not '_' and not '.')))
        {
            throw new ArgumentException("Trait names contain only ASCII letters, digits, underscores, and periods.", nameof(request));
        }

        if (new[] { request.Namespace, request.ClassName, request.MethodName, request.TraitName, request.TraitValue }
            .Any(value => value is { Length: > 512 }))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Test discovery filters exceed host limits.");
        }
    }

    private static void ValidateTimeout(int timeoutSeconds)
    {
        if (timeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        }
    }

    private static void ValidateFramework(string? framework)
    {
        if (framework is not null && !TargetFrameworkRegex().IsMatch(framework))
        {
            throw new ArgumentException("Target framework is not a closed framework moniker.", nameof(framework));
        }
    }

    private static void AddConfigurationAndFramework(
        List<string> arguments,
        DotNetBuildConfiguration configuration,
        string? framework)
    {
        arguments.Add("--configuration");
        arguments.Add(configuration.ToString());
        if (!string.IsNullOrWhiteSpace(framework))
        {
            ValidateFramework(framework);
            arguments.Add("--framework");
            arguments.Add(framework);
        }
    }

    private static TestProject ReadTestProject(string root, string projectPath)
    {
        if (new FileInfo(projectPath).Length > 1024 * 1024)
        {
            throw new InvalidDataException("Test project metadata exceeds the inspection size limit.");
        }

        using var reader = XmlReader.Create(projectPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        string[] packages = [.. document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)];
        var mtp = packages.Any(package => package.StartsWith("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase))
            || document.Descendants().Any(element =>
                element.Name.LocalName is "UseMicrosoftTestingPlatformRunner" or "TestingPlatformDotnetTestSupport"
                && bool.TryParse(element.Value, out var enabled)
                && enabled);
        var xunit = packages.Any(package => package.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
        if (!mtp && !xunit)
        {
            throw new InvalidOperationException("The selected project is not a supported test project.");
        }

        return new TestProject
        {
            Name = Path.GetFileNameWithoutExtension(projectPath),
            FilePath = projectPath,
            Framework = mtp ? TestFramework.MicrosoftTestingPlatform : TestFramework.XUnit,
        };
    }

    private static DiscoveredTest CreateDiscoveredTest(string root, TestCase testCase)
    {
        var name = testCase.FullyQualifiedName;
        var parameterStart = name.IndexOf('(');
        var withoutParameters = parameterStart < 0 ? name : name[..parameterStart];
        var segments = withoutParameters.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var method = segments.Length > 0 ? segments[^1] : null;
        var className = segments.Length > 1 ? segments[^2] : null;
        var testNamespace = segments.Length > 2 ? string.Join('.', segments[..^2]) : null;
        var relativeProject = Path.GetRelativePath(root, testCase.ProjectPath).Replace('\\', '/');
        var repositoryIdentity = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var id = CreateIdentity($"{repositoryIdentity}|{relativeProject}|{name}");
        return new DiscoveredTest(
            new DiscoveredTestId(id),
            name,
            relativeProject,
            testNamespace,
            className,
            method,
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static IEnumerable<DiscoveredTest> ApplyTestFilters(
        IEnumerable<DiscoveredTest> tests,
        TestDiscoveryRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Namespace))
        {
            tests = tests.Where(test => string.Equals(test.Namespace, request.Namespace, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(request.ClassName))
        {
            tests = tests.Where(test => string.Equals(test.ClassName, request.ClassName, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(request.MethodName))
        {
            tests = tests.Where(test => string.Equals(test.MethodName, request.MethodName, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(request.TraitName))
        {
            tests = tests.Where(test => test.Traits.TryGetValue(request.TraitName, out var value)
                && (request.TraitValue is null || value.Equals(request.TraitValue, StringComparison.Ordinal)));
        }

        return tests;
    }

    private static string FormatDiscoveryFilter(TestDiscoveryRequest request)
    {
        string[] parts =
        [
            .. new[]
            {
                (Name: "namespace", Value: request.Namespace),
                (Name: "class", Value: request.ClassName),
                (Name: "method", Value: request.MethodName),
                (Name: "trait", Value: request.TraitName),
                (Name: "traitValue", Value: request.TraitValue),
            }
                .Where(part => !string.IsNullOrWhiteSpace(part.Value))
                .Select(part => $"{part.Name}={part.Value}"),
        ];
        return parts.Length == 0 ? "all discovered tests in project" : string.Join(" AND ", parts);
    }

    private void TrimIndexes()
    {
        foreach (var key in _diagnosticRuns.Values
            .OrderByDescending(run => run.CreatedAt)
            .Skip(100)
            .Select(run => run.InvocationId))
        {
            _diagnosticRuns.TryRemove(key, out _);
        }

        foreach (var key in _discoveredTests.Keys.OrderBy(key => key, StringComparer.Ordinal).Skip(10000))
        {
            _discoveredTests.TryRemove(key, out _);
        }
    }

    private static string EscapeTestFilterValue(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal)
            .Replace("&", "%26", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal)
            .Replace("=", "%3D", StringComparison.Ordinal)
            .Replace("!", "%21", StringComparison.Ordinal)
            .Replace("~", "%7E", StringComparison.Ordinal);
    }

    private static string CreateIdentity(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    }

    private static bool RepositoryPathsEqual(string left, string right)
    {
        return left.Equals(
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetFrameworkRegex();

    private sealed record DiagnosticRun(
        string InvocationId,
        RunId RunId,
        string RepositoryPath,
        string ScopePath,
        DiagnosticOrigin Origin,
        IReadOnlyList<Diagnostic> Diagnostics,
        DateTimeOffset CreatedAt);

    private sealed record StoredDiscoveredTest(string RepositoryPath, DiscoveredTest Test);

    private sealed class NuGetQueryContext : IDisposable
    {
        internal NuGetQueryContext(
            string configurationPath,
            Dictionary<string, string> environmentVariables)
        {
            ConfigurationPath = configurationPath;
            EnvironmentVariables = environmentVariables;
        }

        internal string ConfigurationPath { get; }

        internal Dictionary<string, string> EnvironmentVariables { get; }

        public void Dispose()
        {
            EnvironmentVariables.Clear();
            File.Delete(ConfigurationPath);
        }
    }
}
