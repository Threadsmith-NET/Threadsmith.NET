namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Inspects NuGet dependency and advisory health without restoring or mutating packages.</summary>
public sealed class NuGetHealthTool : Tool<NuGetDependencyHealthRequest, NuGetDependencyHealthResult>
{
    private static readonly ToolDefinition _definition = NativeValidationToolDefinitions.Create<NuGetDependencyHealthRequest, NuGetDependencyHealthResult>(
        "nuget_health",
        "Inspects existing NuGet restore assets and optional trusted-source advisories.",
        RepositoryTrustLevel.TrustedBuild,
        executable: true);

    private readonly INativeValidationToolService _service;

    /// <summary>Initializes a new instance of the <see cref="NuGetHealthTool"/> class.</summary>
    public NuGetHealthTool(INativeValidationToolService service)
    {
        ArgumentNullException.ThrowIfNull(service);
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
            result.IsTruncated);
    }

    /// <inheritdoc />
    protected override void ValidateInput(NuGetDependencyHealthRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ProjectPath);
        if (input.MaximumDependencies is < 1 or > 1000 || input.MaximumAdvisories is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Package result bounds are outside host limits.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        NuGetDependencyHealthRequest input,
        ToolInvocationContext context)
    {
        string directory = Path.GetDirectoryName(input.ProjectPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
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
    public DotNetBuildTool(INativeValidationToolService service)
        : base(
            service,
            NativeValidationToolDefinitions.Create<BuildToolRequest, ValidationToolResult>(
                "dotnet_build", "Runs a bounded exploratory build without restore.", RepositoryTrustLevel.TrustedBuild, executable: true))
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
    public DotNetAnalyzerTool(INativeValidationToolService service)
        : base(
            service,
            NativeValidationToolDefinitions.Create<AnalyzerToolRequest, ValidationToolResult>(
                "dotnet_analyzers", "Runs analyzers through a bounded no-restore build.", RepositoryTrustLevel.TrustedBuild, executable: true))
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
    private static readonly ToolDefinition _definition = NativeValidationToolDefinitions.Create<FormatCheckRequest, ValidationToolResult>(
        "dotnet_format_check",
        "Checks dotnet formatting without applying changes.",
        RepositoryTrustLevel.TrustedBuild,
        executable: true);

    private readonly INativeValidationToolService _service;

    /// <summary>Initializes a new instance of the <see cref="DotNetFormatCheckTool"/> class.</summary>
    public DotNetFormatCheckTool(INativeValidationToolService service)
    {
        ArgumentNullException.ThrowIfNull(service);
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
            result.IsTruncated);
    }

    /// <inheritdoc />
    protected override void ValidateInput(FormatCheckRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.TargetPath);
        NativeValidationToolDefinitions.ValidateBounds(input.TimeoutSeconds, 1);
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
    private static readonly ToolDefinition _definition = NativeValidationToolDefinitions.Create<DiagnosticQuery, DiagnosticQueryResult>(
        "diagnostic_query",
        "Queries bounded normalized diagnostics from exploratory native runs.",
        RepositoryTrustLevel.TrustedRead,
        executable: false);

    private readonly INativeValidationToolService _service;

    /// <summary>Initializes a new instance of the <see cref="DiagnosticQueryTool"/> class.</summary>
    public DiagnosticQueryTool(INativeValidationToolService service)
    {
        ArgumentNullException.ThrowIfNull(service);
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
        bool omitted = allowed.Length != result.Items.Count;
        result = result with
        {
            Items = allowed,
            Total = omitted ? allowed.Length : result.Total,
            HasMore = result.HasMore || omitted,
        };
        return new(result, [new ToolProvenanceSource("diagnostic-index", input.InvocationId ?? "current")], result.HasMore);
    }

    /// <inheritdoc />
    protected override void ValidateInput(DiagnosticQuery input)
    {
        if (input.Page < 0 || input.PageSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Diagnostic page bounds are invalid.");
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
    private static readonly ToolDefinition _definition = NativeValidationToolDefinitions.Create<TestDiscoveryRequest, TestDiscoveryResult>(
        "test_discover",
        "Discovers bounded stable test identities without restore or build.",
        RepositoryTrustLevel.TrustedBuild,
        executable: true);

    private readonly INativeValidationToolService _service;

    /// <summary>Initializes a new instance of the <see cref="TestDiscoveryTool"/> class.</summary>
    public TestDiscoveryTool(INativeValidationToolService service)
    {
        ArgumentNullException.ThrowIfNull(service);
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
        return new(result, [new ToolProvenanceSource("test-discovery", input.ProjectPath, result.DiscoveryId)], result.IsTruncated);
    }

    /// <inheritdoc />
    protected override void ValidateInput(TestDiscoveryRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ProjectPath);
        NativeValidationToolDefinitions.ValidateBounds(input.TimeoutSeconds, input.MaximumTests);
        if (input.MaximumTests > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Test discovery returns at most 500 identities.");
        }

        if ((input.TraitValue is null) != (input.TraitName is null))
        {
            throw new ArgumentException("Trait discovery requires both an exact trait name and value.", nameof(input));
        }

        if (input.TraitName is not null
            && (input.TraitName.Length > 128
                || input.TraitName.Any(character => !char.IsAsciiLetterOrDigit(character)
                    && character is not '_' and not '.')))
        {
            throw new ArgumentException("Trait names contain only ASCII letters, digits, underscores, and periods.", nameof(input));
        }

        string? oversized = new[] { input.Namespace, input.ClassName, input.MethodName, input.TraitName, input.TraitValue }
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
    private static readonly ToolDefinition _definition = NativeValidationToolDefinitions.Create<TargetedTestRequest, TargetedTestResult>(
        "test_run_targeted",
        "Runs one host-issued stable test identity with an explained generated filter.",
        RepositoryTrustLevel.TrustedBuild,
        executable: true);

    private readonly INativeValidationToolService _service;

    /// <summary>Initializes a new instance of the <see cref="TargetedTestTool"/> class.</summary>
    public TargetedTestTool(INativeValidationToolService service)
    {
        ArgumentNullException.ThrowIfNull(service);
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
        return new(result, [new ToolProvenanceSource("test", result.Test.Id.Value, result.EffectiveFilter)], result.IsTruncated);
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

        NativeValidationToolDefinitions.ValidateBounds(input.TimeoutSeconds, 1);
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
            result.IsTruncated);
    }

    /// <inheritdoc />
    protected override void ValidateInput(TRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.TargetPath);
        NativeValidationToolDefinitions.ValidateBounds(input.TimeoutSeconds, 1);
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

/// <summary>Creates common Plan-42 tool definitions and validates scalar bounds.</summary>
internal static class NativeValidationToolDefinitions
{
    /// <summary>Creates a bounded native validation tool definition.</summary>
    internal static ToolDefinition Create<TInput, TOutput>(
        string id,
        string description,
        RepositoryTrustLevel trust,
        bool executable)
    {
        return ToolDefinitionFactory.Create<TInput, TOutput>(
            id,
            description,
            executable ? ToolCategory.ProcessExecution : ToolCategory.RepositoryInspection,
            trust,
            ApprovalLevel.None,
            executable ? ToolSideEffect.ExecutesCode : ToolSideEffect.ReadOnly,
            executable ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(10),
            1024 * 1024);
    }

    /// <summary>Validates shared native operation scalar bounds.</summary>
    internal static void ValidateBounds(int timeoutSeconds, int maximumItems)
    {
        if (timeoutSeconds is < 1 or > 300 || maximumItems is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Native validation bounds are outside host limits.");
        }
    }
}
