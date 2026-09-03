namespace Threadsmith.Tools;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Inspects NuGet dependency and advisory health without restoring or mutating packages.</summary>
public sealed class NuGetHealthTool : Tool<NuGetDependencyHealthRequest, NuGetDependencyHealthResult>
{
    private readonly ToolDefinition _definition;
    private readonly INativeValidationToolService _service;

    /// <summary>Initializes a new instance of the <see cref="NuGetHealthTool"/> class.</summary>
    public NuGetHealthTool(INativeValidationToolService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        _definition = NativeValidationToolDefinitions.Create<NuGetDependencyHealthRequest, NuGetDependencyHealthResult>(
            "nuget_health",
            promptLoader,
            PromptFileNames.ToolNugetHealthDescription,
            RepositoryTrustLevel.TrustedBuild,
            executable: true);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<NuGetDependencyHealthResult>> ExecuteAsync(
        NuGetDependencyHealthRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.InspectPackagesAsync(
            context.Invocation.RepositoryPath,
            context.RunId,
            input,
            cancellationToken);
        return new(
            result,
            result.Sources.Select(source => new ToolProvenanceSource("nuget", source, input.ProjectPath)).ToArray(),
            result.IsTruncated,
            ModelResultContent: NativeValidationModelProjection.Create(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(NuGetDependencyHealthRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ProjectPath);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        NuGetDependencyHealthRequest input,
        ToolInvocationContext context)
    {
        var directory = Path.GetDirectoryName(input.ProjectPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        return [input.ProjectPath, Path.Combine(directory, "obj", "project.assets.json")];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(NuGetDependencyHealthRequest input)
    {
        return input.SourceMode == PackageHealthSourceMode.ConfiguredSources ? "dotnet" : null;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetSecretReferences(NuGetDependencyHealthRequest input)
    {
        return input.SourceMode == PackageHealthSourceMode.ConfiguredSources
            ? _service.ConfiguredSecretReferences
            : [];
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetNetworkHosts(NuGetDependencyHealthRequest input)
    {
        return input.SourceMode == PackageHealthSourceMode.ConfiguredSources
            ? _service.ConfiguredNetworkHosts
            : [];
    }
}

/// <summary>Runs a typed exploratory .NET build.</summary>
public sealed class DotNetBuildTool : NativeValidationTargetTool<BuildToolRequest>
{
    /// <summary>Initializes a new instance of the <see cref="DotNetBuildTool"/> class.</summary>
    public DotNetBuildTool(INativeValidationToolService service, IPromptLoader promptLoader)
        : base(
            service,
            NativeValidationToolDefinitions.Create<BuildToolRequest, ValidationToolResult>(
                "dotnet_build",
                promptLoader,
                PromptFileNames.ToolDotnetBuildDescription,
                RepositoryTrustLevel.TrustedBuild,
                executable: true))
    {
    }

    /// <inheritdoc />
    protected override Task<ValidationToolResult> RunAsync(
        string repositoryPath,
        RunId runId,
        BuildToolRequest input,
        CancellationToken cancellationToken)
    {
        return Service.BuildAsync(repositoryPath, runId, input, cancellationToken);
    }
}

/// <summary>Runs analyzers through a typed exploratory build.</summary>
public sealed class DotNetAnalyzerTool : NativeValidationTargetTool<AnalyzerToolRequest>
{
    /// <summary>Initializes a new instance of the <see cref="DotNetAnalyzerTool"/> class.</summary>
    public DotNetAnalyzerTool(INativeValidationToolService service, IPromptLoader promptLoader)
        : base(
            service,
            NativeValidationToolDefinitions.Create<AnalyzerToolRequest, ValidationToolResult>(
                "dotnet_analyzers",
                promptLoader,
                PromptFileNames.ToolDotnetAnalyzersDescription,
                RepositoryTrustLevel.TrustedBuild,
                executable: true))
    {
    }

    /// <inheritdoc />
    protected override Task<ValidationToolResult> RunAsync(
        string repositoryPath,
        RunId runId,
        AnalyzerToolRequest input,
        CancellationToken cancellationToken)
    {
        return Service.AnalyzeAsync(repositoryPath, runId, input, cancellationToken);
    }
}

/// <summary>Checks formatter drift without writing repository files.</summary>
public sealed class DotNetFormatCheckTool : Tool<FormatCheckRequest, ValidationToolResult>
{
    private readonly ToolDefinition _definition;
    private readonly INativeValidationToolService _service;

    /// <summary>Initializes a new instance of the <see cref="DotNetFormatCheckTool"/> class.</summary>
    public DotNetFormatCheckTool(INativeValidationToolService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        _definition = NativeValidationToolDefinitions.Create<FormatCheckRequest, ValidationToolResult>(
            "dotnet_format_check",
            promptLoader,
            PromptFileNames.ToolDotnetFormatCheckDescription,
            RepositoryTrustLevel.TrustedBuild,
            executable: true);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<ValidationToolResult>> ExecuteAsync(
        FormatCheckRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.CheckFormatAsync(
            context.Invocation.RepositoryPath,
            context.RunId,
            input,
            cancellationToken);
        return new(
            result,
            [new ToolProvenanceSource("validation", result.InvocationId, result.EffectiveScope)],
            result.IsTruncated,
            ModelResultContent: NativeValidationModelProjection.Create(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(FormatCheckRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.TargetPath);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        FormatCheckRequest input,
        ToolInvocationContext context)
    {
        return [input.TargetPath];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(FormatCheckRequest input)
    {
        return "dotnet";
    }
}

/// <summary>Queries normalized exploratory diagnostics.</summary>
public sealed class DiagnosticQueryTool : Tool<DiagnosticQuery, DiagnosticQueryResult>
{
    private readonly ToolDefinition _definition;
    private readonly INativeValidationToolService _service;

    /// <summary>Initializes a new instance of the <see cref="DiagnosticQueryTool"/> class.</summary>
    public DiagnosticQueryTool(INativeValidationToolService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        _definition = NativeValidationToolDefinitions.Create<DiagnosticQuery, DiagnosticQueryResult>(
            "diagnostic_query",
            promptLoader,
            PromptFileNames.ToolDiagnosticQueryDescription,
            RepositoryTrustLevel.TrustedRead,
            executable: false);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<DiagnosticQueryResult>> ExecuteAsync(
        DiagnosticQuery input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.QueryDiagnosticsAsync(
            context.Invocation.RepositoryPath,
            input,
            cancellationToken);
        DiagnosticQueryItem[] allowed = [.. result.Items.Where(item => IsAllowed(item, context.Invocation))];
        var omitted = allowed.Length != result.Items.Count;
        result = result with
        {
            Items = allowed,
            Total = omitted ? allowed.Length : result.Total,
        };
        var isTruncated = result.ContinuationToken is not null || omitted;
        return new(
            result,
            [new ToolProvenanceSource("diagnostic-index", input.InvocationId ?? "current")],
            isTruncated,
            ModelResultContent: NativeValidationModelProjection.Create(result, isTruncated));
    }

    /// <inheritdoc />
    protected override void ValidateInput(DiagnosticQuery input)
    {
        if (input.ContinuationToken is { Length: > 128 })
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Diagnostic continuation token exceeds the host limit.");
        }

        if (new[] { input.InvocationId, input.Project, input.File, input.Code }
            .Any(value => value is { Length: > 1024 }))
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Diagnostic query text exceeds host limits.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        DiagnosticQuery input,
        ToolInvocationContext context)
    {
        return input.File is null ? [] : [input.File];
    }

    private static bool IsAllowed(DiagnosticQueryItem item, ToolInvocationContext context)
    {
        try
        {
            _ = ToolPathRules.NormalizeAndValidate(item.ScopePath, context);
            if (item.Diagnostic.File is not null)
            {
                _ = ToolPathRules.NormalizeAndValidate(item.Diagnostic.File, context);
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Discovers stable test identities in one supported project.</summary>
public sealed class TestDiscoveryTool : Tool<TestDiscoveryRequest, TestDiscoveryResult>
{
    private readonly ToolDefinition _definition;
    private readonly INativeValidationToolService _service;

    /// <summary>Initializes a new instance of the <see cref="TestDiscoveryTool"/> class.</summary>
    public TestDiscoveryTool(INativeValidationToolService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        _definition = NativeValidationToolDefinitions.Create<TestDiscoveryRequest, TestDiscoveryResult>(
            "test_discover",
            promptLoader,
            PromptFileNames.ToolTestDiscoverDescription,
            RepositoryTrustLevel.TrustedBuild,
            executable: true);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<TestDiscoveryResult>> ExecuteAsync(
        TestDiscoveryRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.DiscoverTestsAsync(
            context.Invocation.RepositoryPath,
            context.RunId,
            input,
            cancellationToken);
        return new(
            result,
            [new ToolProvenanceSource("test-discovery", input.ProjectPath, result.DiscoveryId)],
            result.IsTruncated,
            ModelResultContent: NativeValidationModelProjection.Create(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(TestDiscoveryRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ProjectPath);
        if (input.Trait is { } trait)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(trait.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(trait.Value);
            if (trait.Name.Length > 128
                || trait.Name.Any(character => !char.IsAsciiLetterOrDigit(character)
                    && character is not '_' and not '.'))
            {
                throw new ArgumentException("Trait names contain only ASCII letters, digits, underscores, and periods.", nameof(input));
            }
        }

        var oversized = new[] { input.Namespace, input.ClassName, input.MethodName, input.Trait?.Name, input.Trait?.Value }
            .FirstOrDefault(value => value is { Length: > 512 });
        if (oversized is not null)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Test discovery filters exceed host limits.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        TestDiscoveryRequest input,
        ToolInvocationContext context)
    {
        return [input.ProjectPath];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(TestDiscoveryRequest input)
    {
        return "dotnet";
    }
}

/// <summary>Runs exactly one previously discovered stable test identity.</summary>
public sealed class TargetedTestTool : Tool<TargetedTestRequest, TargetedTestResult>
{
    private readonly ToolDefinition _definition;
    private readonly INativeValidationToolService _service;

    /// <summary>Initializes a new instance of the <see cref="TargetedTestTool"/> class.</summary>
    public TargetedTestTool(INativeValidationToolService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        _definition = NativeValidationToolDefinitions.Create<TargetedTestRequest, TargetedTestResult>(
            "test_run_targeted",
            promptLoader,
            PromptFileNames.ToolTestRunTargetedDescription,
            RepositoryTrustLevel.TrustedBuild,
            executable: true);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<TargetedTestResult>> ExecuteAsync(
        TargetedTestRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.RunTargetedTestAsync(
            context.Invocation.RepositoryPath,
            context.RunId,
            input,
            cancellationToken);
        return new(
            result,
            [new ToolProvenanceSource("test", result.Test.Id.Value, result.EffectiveFilter)],
            result.IsTruncated,
            ModelResultContent: NativeValidationModelProjection.Create(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(TargetedTestRequest input)
    {
        ArgumentNullException.ThrowIfNull(input.TestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.TestId.Value);
        if (input.TestId.Value.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Test identity exceeds the host limit.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        TargetedTestRequest input,
        ToolInvocationContext context)
    {
        return [_service.ResolveTestProjectPath(context.RepositoryPath, input.TestId)];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(TargetedTestRequest input)
    {
        return "dotnet";
    }
}

/// <summary>Common adapter for closed target-based validation tools.</summary>
public abstract class NativeValidationTargetTool<TRequest> : Tool<TRequest, ValidationToolResult>
    where TRequest : DotNetValidationTargetRequest
{
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="NativeValidationTargetTool{TRequest}"/> class.</summary>
    protected NativeValidationTargetTool(INativeValidationToolService service, ToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(definition);
        Service = service;
        _definition = definition;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <summary>Gets the reusable validation facade.</summary>
    protected INativeValidationToolService Service { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<ValidationToolResult>> ExecuteAsync(
        TRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            context.Invocation.RepositoryPath,
            context.RunId,
            input,
            cancellationToken);
        return new(
            result,
            [new ToolProvenanceSource("validation", result.InvocationId, result.EffectiveScope)],
            result.IsTruncated,
            ModelResultContent: NativeValidationModelProjection.Create(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(TRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.TargetPath);
        if (input.TargetFramework is { Length: > 64 })
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Target framework exceeds the host limit.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        TRequest input,
        ToolInvocationContext context)
    {
        return [input.TargetPath];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(TRequest input)
    {
        return "dotnet";
    }

    /// <summary>Dispatches the specific closed operation.</summary>
    protected abstract Task<ValidationToolResult> RunAsync(
        string repositoryPath,
        RunId runId,
        TRequest input,
        CancellationToken cancellationToken);
}

/// <summary>Creates common Plan-42 tool definitions.</summary>
internal static class NativeValidationToolDefinitions
{
    /// <summary>Creates a bounded native validation tool definition.</summary>
    internal static ToolDefinition Create<TInput, TOutput>(
        string id,
        IPromptLoader promptLoader,
        string promptFileName,
        RepositoryTrustLevel trust,
        bool executable)
    {
        ArgumentNullException.ThrowIfNull(promptLoader);
        return ToolDefinitionFactory.Create<TInput, TOutput>(
            id,
            promptLoader.Get(promptFileName),
            executable ? ToolCategory.ProcessExecution : ToolCategory.RepositoryInspection,
            trust,
            ApprovalLevel.None,
            executable ? ToolSideEffect.ExecutesCode : ToolSideEffect.ReadOnly,
            executable ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(10),
            1024 * 1024);
    }
}

/// <summary>Creates bounded model-facing projections while retaining audit-rich native results host-side.</summary>
internal static class NativeValidationModelProjection
{
    private const int MaximumModelAdvisories = 200;
    private const int MaximumModelDependencies = 100;
    private const int MaximumModelDiagnostics = 100;
    private const int MaximumModelTests = 200;
    private static readonly JsonSerializerOptions ModelJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Projects package health for model consumption.</summary>
    internal static string Create(NuGetDependencyHealthResult result)
    {
        var dependencies = result.Dependencies
            .Take(MaximumModelDependencies)
            .Select(dependency => new
            {
                id = Bound(dependency.Id, 256),
                version = Bound(dependency.ResolvedVersion, 128),
                direct = dependency.IsDirect,
                framework = Bound(dependency.TargetFramework, 128),
            })
            .ToArray();
        var advisories = result.Advisories
            .Take(MaximumModelAdvisories)
            .Select(advisory => new
            {
                package = Bound(advisory.PackageId, 256),
                version = Bound(advisory.ResolvedVersion, 128),
                kind = advisory.Kind.ToString(),
                severity = Bound(advisory.Severity, 64),
                url = BoundNullable(advisory.AdvisoryUrl, 2048),
            })
            .ToArray();
        return JsonSerializer.Serialize(
            new
            {
                authority = result.Authority.ToString(),
                complete = result.IsComplete,
                offline = result.IsOffline,
                stale = result.IsStale,
                truncated = result.IsTruncated || dependencies.Length != result.Dependencies.Count,
                dependencies,
                omittedDependencies = result.Dependencies.Count - dependencies.Length,
                advisories,
                omissions = result.Omissions.Take(20).Select(omission => Bound(omission, 512)).ToArray(),
            },
            ModelJsonOptions);
    }

    /// <summary>Projects build, analyzer, or formatting evidence for model consumption.</summary>
    internal static string Create(ValidationToolResult result)
    {
        var diagnostics = result.Diagnostics
            .Take(MaximumModelDiagnostics)
            .Select(CreateDiagnostic)
            .ToArray();
        return JsonSerializer.Serialize(
            new
            {
                success = result.Succeeded,
                timedOut = result.TimedOut,
                truncated = result.IsTruncated || diagnostics.Length != result.Diagnostics.Count,
                diagnostics,
                omittedDiagnostics = result.Diagnostics.Count - diagnostics.Length,
                output = result.Succeeded ? null : Bound(result.Output, 16 * 1024),
            },
            ModelJsonOptions);
    }

    /// <summary>Projects one diagnostic page without repeated run provenance.</summary>
    internal static string Create(DiagnosticQueryResult result, bool isTruncated)
    {
        return JsonSerializer.Serialize(
            new
            {
                total = result.Total,
                diagnostics = result.Items.Select(item => CreateDiagnostic(item.Diagnostic)).ToArray(),
                continuationToken = result.ContinuationToken,
                truncated = isTruncated,
            },
            ModelJsonOptions);
    }

    /// <summary>Projects stable discovered test identities for model consumption.</summary>
    internal static string Create(TestDiscoveryResult result)
    {
        var tests = result.Tests
            .Take(MaximumModelTests)
            .Select(test => new
            {
                id = Bound(test.Id.Value, 128),
                name = Bound(test.FullyQualifiedName, 1024),
                project = Bound(test.ProjectPath, 1024),
            })
            .ToArray();
        return JsonSerializer.Serialize(
            new
            {
                tests,
                truncated = result.IsTruncated || tests.Length != result.Tests.Count,
                omittedTests = result.Tests.Count - tests.Length,
            },
            ModelJsonOptions);
    }

    /// <summary>Projects targeted test outcome while omitting successful raw output.</summary>
    internal static string Create(TargetedTestResult result)
    {
        return JsonSerializer.Serialize(
            new
            {
                test = new
                {
                    id = Bound(result.Test.Id.Value, 128),
                    name = Bound(result.Test.FullyQualifiedName, 1024),
                    project = Bound(result.Test.ProjectPath, 1024),
                },
                outcome = result.Outcome.ToString(),
                passed = result.Passed,
                failed = result.Failed,
                skipped = result.Skipped,
                timedOut = result.TimedOut,
                truncated = result.IsTruncated,
                output = result.Outcome == TestOutcome.Passed ? null : Bound(result.Output, 16 * 1024),
                attachments = result.Attachments.Take(10).Select(attachment => Bound(attachment, 1024)).ToArray(),
            },
            ModelJsonOptions);
    }

    private static object CreateDiagnostic(Diagnostic diagnostic)
    {
        return new
        {
            code = Bound(diagnostic.Code, 128),
            severity = diagnostic.Severity.ToString(),
            project = Bound(diagnostic.Project, 512),
            framework = Bound(diagnostic.TargetFramework, 128),
            file = BoundNullable(diagnostic.File, 1024),
            range = diagnostic.Range,
            message = Bound(diagnostic.Message, 2048),
            classification = diagnostic.Classification.ToString(),
        };
    }

    private static string Bound(string value, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters));
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maximumCharacters)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static string? BoundNullable(string? value, int maximumCharacters)
    {
        return value is null ? null : Bound(value, maximumCharacters);
    }
}
