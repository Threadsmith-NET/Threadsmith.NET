namespace Threadsmith.Milestone14.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;
using Threadsmith.Validation;
using Xunit;

/// <summary>Verifies Plan-42 package health, validation, diagnostics, and targeted-test contracts.</summary>
public sealed class Plan42NativeValidationToolTests
{
    /// <summary>Verifies existing restore assets produce direct/transitive inventory without execution.</summary>
    [Fact]
    public async Task NuGetHealth_ExistingAssets_ReportsDirectAndTransitiveWithoutProcess()
    {
        using var repository = new TemporaryRepository();
        repository.Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        repository.Write(
            "src/App/obj/project.assets.json",
            """
            {
              "project": { "frameworks": { "net10.0": { "dependencies": { "Direct": { "target": "Package", "version": "[1.0.0, )" } } } } },
              "targets": { "net10.0": {
                "Direct/1.0.0": { "dependencies": { "Transitive": "2.0.0" } },
                "Transitive/2.0.0": {}
              } }
            }
            """);
        var process = new RecordingProcessManager();
        var service = new NativeValidationToolService(process);

        var result = await service.InspectPackagesAsync(
            repository.Path,
            RunId.New(),
            new NuGetDependencyHealthRequest { ProjectPath = "src/App/App.csproj" },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Dependencies.Count);
        Assert.Contains(result.Dependencies, dependency => dependency.Id == "Direct" && dependency.IsDirect);
        Assert.Contains(result.Dependencies, dependency => dependency.Id == "Transitive" && !dependency.IsDirect);
        Assert.True(result.IsOffline);
        Assert.Equal(ValidationAuthority.Exploratory, result.Authority);
        Assert.Empty(process.Requests);
    }

    /// <summary>Verifies private advisory credentials are scoped to child environments and never arguments or results.</summary>
    [Fact]
    public async Task NuGetHealth_PrivateSource_ResolvesCredentialOnlyAtProcessBoundary()
    {
        const string secret = "private-token";
        using var repository = new TemporaryRepository();
        repository.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var secretReachedBoundary = false;
        var process = new RecordingProcessManager
        {
            ResultFactory = request =>
            {
                secretReachedBoundary |= request.EnvironmentVariables.Any(
                    item => item.Value.Contains(secret, StringComparison.Ordinal));
                return Successful(request, string.Empty);
            },
        };
        var service = new NativeValidationToolService(
            process,
            new FixedSecretStore(secret),
            [new NuGetAdvisorySourceOptions(
                "private",
                new Uri("https://packages.example.test/v3/index.json"),
                "token",
                "secrets:nuget:private")]);

        var result = await service.InspectPackagesAsync(
            repository.Path,
            RunId.New(),
            new NuGetDependencyHealthRequest
            {
                ProjectPath = "App.csproj",
                SourceMode = PackageHealthSourceMode.ConfiguredSources,
            },
            TestContext.Current.CancellationToken);

        Assert.True(secretReachedBoundary);
        Assert.Equal(3, process.Requests.Count);
        Assert.All(process.Requests, request =>
        {
            Assert.DoesNotContain(request.Arguments, argument => argument.Contains(secret, StringComparison.Ordinal));
            Assert.Empty(request.EnvironmentVariables);
            Assert.Contains("--configfile", request.Arguments);
        });
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Contains("secrets:nuget:private", service.ConfiguredSecretReferences);
    }

    /// <summary>Verifies closed no-restore validation arguments and diagnostic indexing.</summary>
    [Fact]
    public async Task BuildAnalyzerFormat_UseClosedNoRestoreArguments_AndIndexDiagnostics()
    {
        using var repository = new TemporaryRepository();
        repository.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var process = new RecordingProcessManager
        {
            ResultFactory = request => Successful(
                request,
                $"{repository.Path}\\Program.cs(2,3): warning CS0168: unused [{repository.Path}\\App.csproj]"),
        };
        var service = new NativeValidationToolService(process);
        var runId = RunId.New();

        var build = await service.BuildAsync(
            repository.Path,
            runId,
            new BuildToolRequest { TargetPath = "App.csproj", TargetFramework = "net10.0" },
            TestContext.Current.CancellationToken);
        var analyzer = await service.AnalyzeAsync(
            repository.Path,
            runId,
            new AnalyzerToolRequest { TargetPath = "App.csproj" },
            TestContext.Current.CancellationToken);
        var format = await service.CheckFormatAsync(
            repository.Path,
            runId,
            new FormatCheckRequest { TargetPath = "App.csproj" },
            TestContext.Current.CancellationToken);
        var query = await service.QueryDiagnosticsAsync(
            repository.Path,
            new DiagnosticQuery { Code = "CS0168", Origin = DiagnosticOrigin.Compiler },
            TestContext.Current.CancellationToken);

        Assert.All(process.Requests, request => Assert.Contains("--no-restore", request.Arguments));
        Assert.All(process.Requests, request => Assert.DoesNotContain(request.Arguments, argument => argument.StartsWith('@')));
        Assert.Contains("-property:RunAnalyzers=true", process.Requests[1].Arguments);
        Assert.Contains("--verify-no-changes", process.Requests[2].Arguments);
        Assert.Equal(ValidationAuthority.Exploratory, build.Authority);
        Assert.Equal(ValidationAuthority.Exploratory, analyzer.Authority);
        Assert.Equal(ValidationAuthority.Exploratory, format.Authority);
        Assert.Single(query.Items);
    }

    /// <summary>Verifies targeted tests accept only host-issued identity and filters.</summary>
    [Fact]
    public async Task TestDiscovery_ThenTargetedRun_UsesOnlyIssuedIdentityAndGeneratedFilter()
    {
        using var repository = new TemporaryRepository();
        repository.Write(
            "Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup></Project>");
        var process = new RecordingProcessManager();
        process.Results.Enqueue(new ProcessExecutionResult(
            1,
            0,
            "The following Tests are available:\n  Example.Tests.CalculatorTests.Add(1,2)",
            string.Empty,
            false,
            false,
            false,
            TimeSpan.FromMilliseconds(1)));
        process.Results.Enqueue(new ProcessExecutionResult(
            2,
            0,
            "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1",
            string.Empty,
            false,
            false,
            false,
            TimeSpan.FromMilliseconds(1)));
        var service = new NativeValidationToolService(process);
        var runId = RunId.New();

        var discovery = await service.DiscoverTestsAsync(
            repository.Path,
            runId,
            new TestDiscoveryRequest
            {
                ProjectPath = "Tests.csproj",
                TraitName = "Category",
                TraitValue = "Fast",
            },
            TestContext.Current.CancellationToken);
        var result = await service.RunTargetedTestAsync(
            repository.Path,
            runId,
            new TargetedTestRequest { TestId = discovery.Tests.Single().Id },
            TestContext.Current.CancellationToken);

        Assert.Single(discovery.Tests);
        Assert.Equal("trait=Category AND traitValue=Fast", discovery.EffectiveFilter);
        Assert.Equal("Fast", discovery.Tests[0].Traits["Category"]);
        Assert.Contains("Category=Fast", process.Requests[0].Arguments);
        Assert.Equal("FullyQualifiedName=Example.Tests.CalculatorTests.Add\\(1%2C2\\)", result.EffectiveFilter);
        Assert.Contains("--filter", process.Requests[1].Arguments);
        Assert.Contains(result.EffectiveFilter, process.Requests[1].Arguments);
        Assert.Equal(ValidationAuthority.Exploratory, result.Authority);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunTargetedTestAsync(
            repository.Path,
            runId,
            new TargetedTestRequest { TestId = new DiscoveredTestId("model-supplied") },
            TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies MTP discovery and targeted execution use runner-native filter options.</summary>
    [Fact]
    public async Task MtpDiscovery_AndTargetedRun_UseMtpFilterOptions()
    {
        using var repository = new TemporaryRepository();
        repository.Write(
            "Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.Testing.Platform\" /></ItemGroup></Project>");
        var process = new RecordingProcessManager();
        process.Results.Enqueue(new ProcessExecutionResult(
            1,
            0,
            "  Example.Tests.CalculatorTests.Add",
            string.Empty,
            false,
            false,
            false,
            TimeSpan.FromMilliseconds(1)));
        process.Results.Enqueue(SuccessfulRequestResult("Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1"));
        var service = new NativeValidationToolService(process);
        var runId = RunId.New();

        var discovery = await service.DiscoverTestsAsync(
            repository.Path,
            runId,
            new TestDiscoveryRequest
            {
                ProjectPath = "Tests.csproj",
                TraitName = "Category",
                TraitValue = "Fast",
            },
            TestContext.Current.CancellationToken);
        var result = await service.RunTargetedTestAsync(
            repository.Path,
            runId,
            new TargetedTestRequest { TestId = discovery.Tests.Single().Id },
            TestContext.Current.CancellationToken);

        Assert.Contains("--filter-trait", process.Requests[0].Arguments);
        Assert.DoesNotContain("--filter", process.Requests[0].Arguments);
        Assert.Contains("Category=Fast", process.Requests[0].Arguments);
        Assert.Contains("--filter-method", process.Requests[1].Arguments);
        Assert.DoesNotContain("--filter", process.Requests[1].Arguments);
        Assert.Contains("Example.Tests.CalculatorTests.Add", process.Requests[1].Arguments);
        Assert.Equal("Example.Tests.CalculatorTests.Add", result.EffectiveFilter);
    }

    /// <summary>Verifies retained diagnostics and discovered-test identities remain repository-bound.</summary>
    [Fact]
    public async Task RetainedIndexes_PartitionStateByRepository()
    {
        using var firstRepository = new TemporaryRepository();
        using var secondRepository = new TemporaryRepository();
        firstRepository.Write("Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup></Project>");
        secondRepository.Write("Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup></Project>");
        var process = new RecordingProcessManager
        {
            ResultFactory = request => Successful(
                request,
                $"{request.WorkingDirectory}\\Program.cs(2,3): warning CS0168: unused [{request.WorkingDirectory}\\Tests.csproj]"),
        };
        var service = new NativeValidationToolService(process);
        var firstRunId = RunId.New();
        var secondRunId = RunId.New();

        await service.BuildAsync(
            firstRepository.Path,
            firstRunId,
            new BuildToolRequest { TargetPath = "Tests.csproj" },
            TestContext.Current.CancellationToken);
        await service.BuildAsync(
            secondRepository.Path,
            secondRunId,
            new BuildToolRequest { TargetPath = "Tests.csproj" },
            TestContext.Current.CancellationToken);
        var firstDiagnostics = await service.QueryDiagnosticsAsync(
            firstRepository.Path,
            new DiagnosticQuery(),
            TestContext.Current.CancellationToken);
        var secondDiagnostics = await service.QueryDiagnosticsAsync(
            secondRepository.Path,
            new DiagnosticQuery(),
            TestContext.Current.CancellationToken);

        process.ResultFactory = null;
        process.Results.Enqueue(SuccessfulRequestResult("The following Tests are available:\n  Example.Tests.SharedTest"));
        process.Results.Enqueue(SuccessfulRequestResult("The following Tests are available:\n  Example.Tests.SharedTest"));
        var firstDiscovery = await service.DiscoverTestsAsync(
            firstRepository.Path,
            RunId.New(),
            new TestDiscoveryRequest { ProjectPath = "Tests.csproj" },
            TestContext.Current.CancellationToken);
        var secondDiscovery = await service.DiscoverTestsAsync(
            secondRepository.Path,
            RunId.New(),
            new TestDiscoveryRequest { ProjectPath = "Tests.csproj" },
            TestContext.Current.CancellationToken);

        Assert.Single(firstDiagnostics.Items);
        Assert.Single(secondDiagnostics.Items);
        Assert.Equal(firstRunId, firstDiagnostics.Items[0].RunId);
        Assert.Equal(secondRunId, secondDiagnostics.Items[0].RunId);
        Assert.NotEqual(firstDiagnostics.Items[0].InvocationId, secondDiagnostics.Items[0].InvocationId);
        Assert.NotEqual(firstDiscovery.Tests.Single().Id, secondDiscovery.Tests.Single().Id);
        Assert.Equal("Tests.csproj", service.ResolveTestProjectPath(firstRepository.Path, firstDiscovery.Tests.Single().Id));
        Assert.Equal("Tests.csproj", service.ResolveTestProjectPath(secondRepository.Path, secondDiscovery.Tests.Single().Id));
    }

    /// <summary>Verifies incomplete configured-source output cannot appear as complete clean advisory evidence.</summary>
    [Fact]
    public async Task NuGetHealth_TruncatedOrMalformedAdvisoryJson_IsIncomplete()
    {
        using var repository = new TemporaryRepository();
        repository.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repository.Write("obj/project.assets.json", "{\"project\":{\"frameworks\":{}},\"targets\":{}}");
        var process = new RecordingProcessManager();
        process.Results.Enqueue(new ProcessExecutionResult(
            1,
            0,
            "{\"projects\":[",
            string.Empty,
            true,
            false,
            false,
            TimeSpan.FromMilliseconds(1)));
        process.Results.Enqueue(SuccessfulRequestResult("not-json"));
        process.Results.Enqueue(SuccessfulRequestResult("{}"));
        var service = new NativeValidationToolService(process, [new Uri("https://packages.example.test/v3/index.json")]);

        var result = await service.InspectPackagesAsync(
            repository.Path,
            RunId.New(),
            new NuGetDependencyHealthRequest
            {
                ProjectPath = "App.csproj",
                SourceMode = PackageHealthSourceMode.ConfiguredSources,
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.True(result.IsTruncated);
        Assert.Contains(result.Omissions, omission => omission.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Omissions, omission => omission.Contains("malformed JSON", StringComparison.Ordinal));
    }

    /// <summary>Verifies Plan-42 tools expose distinct closed schemas.</summary>
    [Fact]
    public void Tools_ExposeDistinctTypedSchemasAndExploratoryReadOnlyAuthority()
    {
        var service = new NativeValidationToolService(new RecordingProcessManager());
        ITool[] tools =
        [
            new NuGetHealthTool(service),
            new DotNetBuildTool(service),
            new DotNetAnalyzerTool(service),
            new DotNetFormatCheckTool(service),
            new DiagnosticQueryTool(service),
            new TestDiscoveryTool(service),
            new TargetedTestTool(service),
        ];

        Assert.Equal(7, tools.Select(tool => tool.Definition.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(tools, tool => Assert.DoesNotContain("command", tool.Definition.InputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase));
        Assert.All(tools, tool => Assert.DoesNotContain("arguments", tool.Definition.InputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RepositoryTrustLevel.TrustedBuild, tools.Single(tool => tool.Definition.Id == "test_run_targeted").Definition.RequiredTrust);
        Assert.Equal(ToolSideEffect.ExecutesCode, tools.Single(tool => tool.Definition.Id == "dotnet_format_check").Definition.SideEffect);
        Assert.Contains("testId", tools.Single(tool => tool.Definition.Id == "test_run_targeted").Definition.InputSchema.JsonSchema);
        _ = JsonDocument.Parse(tools[0].Definition.InputSchema.JsonSchema);
    }

    private static ProcessExecutionResult Successful(ProcessExecutionRequest request, string output)
    {
        return new ProcessExecutionResult(
            request.ToolInvocationId.Value.GetHashCode(),
            0,
            output,
            string.Empty,
            false,
            false,
            false,
            TimeSpan.FromMilliseconds(1));
    }

    private static ProcessExecutionResult SuccessfulRequestResult(string output)
    {
        return new ProcessExecutionResult(
            1,
            0,
            output,
            string.Empty,
            false,
            false,
            false,
            TimeSpan.FromMilliseconds(1));
    }

    private sealed class FixedSecretStore(string value) : ISecretStore
    {
        public Task<string?> GetAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(value);
        }
    }

    private sealed class RecordingProcessManager : IProcessManager
    {
        public Func<ProcessExecutionRequest, ProcessExecutionResult>? ResultFactory { get; set; }

        public Queue<ProcessExecutionResult> Results { get; } = new();

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var result = Results.Count > 0
                ? Results.Dequeue()
                : ResultFactory?.Invoke(request) ?? Successful(request, string.Empty);
            return Task.FromResult(result);
        }
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"threadsmith-plan42-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }

        public void Write(string relativePath, string content)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath) ?? Path);
            File.WriteAllText(fullPath, content);
        }
    }
}
