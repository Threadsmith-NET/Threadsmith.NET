namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises source additions against evaluated MSBuild project membership.</summary>
public static class ProjectSourceRefreshTests
{
    /// <summary>A linked addition and an existing edit share one immutable solution update.</summary>
    [Fact]
    public static async Task ProjectAdditionPublishesMixedBatchIncrementally()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        await using var coordinator = new SemanticRefreshCoordinator(new RegistrySemanticRefreshBackend(registry), events, NullLogger<SemanticRefreshCoordinator>.Instance);
        var project = Path.Combine(fixture.Root, "App", "App.csproj");
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../Contracts/Contracts.csproj" />
              </ItemGroup>
              <Import Project="../SharedInputs.props" Condition="'$(TargetFramework)' == 'net10.0'" />
            </Project>
            """,
            token);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "SharedInputs.props"),
            """
            <Project>
              <ItemGroup>
                <Compile Include="$(MSBuildThisFileDirectory)Contracts/New*.cs" Link="Shared/%(Filename)%(Extension)" />
              </ItemGroup>
            </Project>
            """,
            token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Contracts", "Contracts.csproj"), "<Project Sdk='Microsoft.NET.Sdk' />", token);
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await registry.LoadAsync(request, token);
        await coordinator.BindAsync(request, token);
        var engine = registry.GetEngine(request.WorkspaceId);
        var added = Path.Combine(fixture.Root, "Contracts", "New.cs");
        var changed = Path.Combine(fixture.Root, "App", "Program.cs");
        const string newText = "class Added {}";
        const string changedText = "class Updated {}";
        var completion = new TaskCompletionSource<SemanticRefreshCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticRefreshCompleted refresh)
            {
                completion.TrySetResult(refresh);
            }

            return Task.CompletedTask;
        });
        var registration = await coordinator.RegisterExpectedWritesAsync(
            request.SessionId,
            request.WorkspaceId,
            MutationSetId.New(),
            [
                new("Contracts/New.cs", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newText))), true, ExistedBefore: false),
                new("App/Program.cs", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changedText))), false),
            ],
            token);
        Assert.NotNull(registration);
        await File.WriteAllTextAsync(added, newText, token);
        await File.WriteAllTextAsync(changed, changedText, token);
        await coordinator.CompleteExpectedWritesAsync(registration, ["Contracts/New.cs", "App/Program.cs"], token);
        await coordinator.EnsureCurrentAsync(request.SessionId, SemanticRefreshReason.UserAdmission, token);
        var refresh = await completion.Task.WaitAsync(token);
        Assert.Equal(SemanticRefreshMode.Incremental, refresh.Mode);
        Assert.Equal(SemanticRefreshReason.HostMutation, refresh.Reason);
        var solution = engine.CaptureAdvancedSnapshot().Solution;
        Assert.Contains(added, engine.GetRefreshInventory().SourceDocuments);
        Assert.Equal(changedText, (await solution.GetDocument(Assert.Single(solution.GetDocumentIdsWithFilePath(changed)))!.GetTextAsync(token)).ToString());
        var addedIds = solution.GetDocumentIdsWithFilePath(added);
        Assert.Equal(2, addedIds.Length);
        foreach (var id in addedIds)
        {
            Assert.Equal(newText, (await solution.GetDocument(id)!.GetTextAsync(token)).ToString());
        }
    }

    /// <summary>A source addition must not block admission in a text-only workspace.</summary>
    [Fact]
    public static async Task TextOnlyAdditionUsesFullRefresh()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        await using var coordinator = new SemanticRefreshCoordinator(
            new RegistrySemanticRefreshBackend(registry), events, NullLogger<SemanticRefreshCoordinator>.Instance, watchFileSystem: false);
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root) with { TrustLevel = RepositoryTrustLevel.UntrustedInspection };
        await registry.LoadAsync(request, token);
        await coordinator.BindAsync(request, token);
        var completion = new TaskCompletionSource<SemanticRefreshCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticRefreshCompleted refresh)
            {
                completion.TrySetResult(refresh);
            }

            return Task.CompletedTask;
        });
        const string relativePath = "App/NewSource.cs";
        await WriteSourceAsync(Path.Combine(fixture.Root, relativePath), token);

        await coordinator.ObserveChangeAsync(
            new SemanticFileChange(request.SessionId, relativePath, SemanticFileChangeKind.Created, SemanticRefreshReason.ExternalChange), token);
        await coordinator.EnsureCurrentAsync(request.SessionId, SemanticRefreshReason.UserAdmission, token);

        var refresh = await completion.Task.WaitAsync(token);
        Assert.Equal(SemanticRefreshMode.Full, refresh.Mode);
        Assert.True(coordinator.IsCurrent(request.SessionId));
        Assert.Equal(SemanticConfidenceLevel.TextOnly, registry.GetConfidence(request.WorkspaceId));
    }

    /// <summary>A stock SDK may import a source link without an explicit import in the project.</summary>
    [Fact]
    public static async Task SdkImportHookUsesFullRefresh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var contracts = Path.Combine(fixture.Root, "Contracts");
        await File.WriteAllTextAsync(Path.Combine(contracts, "Contracts.csproj"), "<Project Sdk='Microsoft.NET.Sdk' />", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(contracts, "Directory.Build.props"),
            """
            <Project>
              <Import Project="../Directory.Build.props" />
              <PropertyGroup>
                <CustomAfterMicrosoftCommonProps>$(MSBuildThisFileDirectory)ScopeHook.props</CustomAfterMicrosoftCommonProps>
              </PropertyGroup>
            </Project>
            """,
            cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(contracts, "ScopeHook.props"), LinkedImport, cancellationToken);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), cancellationToken);
        var linked = await WriteSourceAsync(Path.Combine(fixture.Root, "App", "SharedNew.cs"), cancellationToken);

        Assert.Null(await engine.TryRefreshProjectsAsync([linked], cancellationToken));
        Assert.Empty(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(linked.Path));
        await engine.RefreshFullAsync(cancellationToken);
        Assert.Equal(2, engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(linked.Path).Length);
    }

    /// <summary>New and rewritten implicit imports can reveal linked owners after the candidate index is warm.</summary>
    [Theory]
    [InlineData("Contracts.csproj.user", false)]
    [InlineData("obj/Contracts.csproj.custom.props", false)]
    [InlineData("obj/Contracts.csproj.nuget.g.targets", false)]
    [InlineData("obj/Contracts.csproj.nuget.g.targets", true)]
    public static async Task ImplicitImportAddedAfterScopeCaptureUsesFullRefresh(string importPath, bool rewrite)
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        const string plainProject = "<Project Sdk='Microsoft.NET.Sdk' />";
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "App", "App.csproj"), plainProject, token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Contracts", "Contracts.csproj"), plainProject, token);
        var import = Path.Combine(fixture.Root, "Contracts", importPath);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Contracts", "obj"));
        if (rewrite)
        {
            await File.WriteAllTextAsync(import, LinkedImport.Replace("Shared*", "Unseen*", StringComparison.Ordinal), token);
        }

        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), token);
        var first = await WriteSourceAsync(Path.Combine(fixture.Root, "App", "First.cs"), token);
        Assert.NotNull(await engine.TryRefreshProjectsAsync([first], token));

        var lastWrite = rewrite ? File.GetLastWriteTimeUtc(import) : default;
        await File.WriteAllTextAsync(import, LinkedImport, token);
        if (rewrite)
        {
            File.SetLastWriteTimeUtc(import, lastWrite);
        }

        var linked = await WriteSourceAsync(Path.Combine(fixture.Root, "App", "SharedNew.cs"), token);

        Assert.Null(await engine.TryRefreshProjectsAsync([linked], token));
        Assert.Empty(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(linked.Path));
        await engine.RefreshFullAsync(token);
        Assert.Equal(2, engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(linked.Path).Length);
    }

    /// <summary>Incremental evaluation must match a direct project reopen without SolutionDir.</summary>
    [Fact]
    public static async Task DirectProjectDoesNotInventSolutionDirectory()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var project = Path.Combine(fixture.Root, "App", "App.csproj");
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Remove="New*.cs" />
                <Compile Include="New*.cs" Condition="'$(SolutionDir)' == '$(MSBuildThisFileDirectory)'" />
              </ItemGroup>
            </Project>
            """,
            token);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root) with { SolutionPath = project }, token);
        var addition = await WriteSourceAsync(Path.Combine(fixture.Root, "App", "NewSource.cs"), token);

        Assert.Null(await engine.TryRefreshProjectsAsync([addition], token));
        await engine.RefreshFullAsync(token);
        Assert.Empty(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(addition.Path));
    }

    /// <summary>A same-path assembly-info change cannot reuse the old compilation text.</summary>
    [Fact]
    public static async Task ChangedGeneratedAssemblyInfoUsesFullRefresh()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var project = Path.Combine(fixture.Root, "App", "App.csproj");
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <AssemblyVersion>1.0.0.0</AssemblyVersion>
                <AssemblyVersion Condition="Exists('NewSource.cs')">2.0.0.0</AssemblyVersion>
              </PropertyGroup>
            </Project>
            """,
            token);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root) with { SolutionPath = project }, token);
        Assert.Equal(new Version(1, 0, 0, 0), await ReadAssemblyVersionAsync(token));
        var addition = await WriteSourceAsync(Path.Combine(fixture.Root, "App", "NewSource.cs"), token);

        Assert.Null(await engine.TryRefreshProjectsAsync([addition], token));
        Assert.Equal(new Version(1, 0, 0, 0), await ReadAssemblyVersionAsync(token));
        await engine.RefreshFullAsync(token);
        Assert.Equal(new Version(2, 0, 0, 0), await ReadAssemblyVersionAsync(token));

        async Task<Version> ReadAssemblyVersionAsync(CancellationToken cancellationToken)
        {
            var loaded = Assert.Single(engine.CaptureAdvancedSnapshot().Solution.Projects);
            var compilation = await loaded.GetCompilationAsync(cancellationToken);
            Assert.NotNull(compilation);
            return compilation.Assembly.Identity.Version;
        }
    }

    /// <summary>Checks that both cold and cached selection skip a nonmatching multi-target project.</summary>
    [Fact]
    public static async Task UnrelatedMultiTargetProjectDoesNotBlockSuccessiveAdditions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var contracts = Path.Combine(fixture.Root, "Contracts", "Contracts.csproj");
        await File.WriteAllTextAsync(
            contracts,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework />
                <TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'netstandard2.1'">
                <Compile Include="../App/Shared*.cs" Link="Shared/%(Filename)%(Extension)" />
              </ItemGroup>
              <Target Name="RecordDesignTimeLoad" BeforeTargets="CoreCompile" Condition="'$(TargetFramework)' == 'net10.0'">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/obj/scope-probe.txt" Lines="loaded" Overwrite="true" />
              </Target>
            </Project>
            """,
            cancellationToken);
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, cancellationToken);
        var before = engine.CaptureAdvancedSnapshot().Solution;
        Assert.Equal(2, before.Projects.Count(project => project.FilePath == contracts));
        var app = Assert.Single(before.Projects, project => Path.GetFileName(project.FilePath) == "App.csproj");
        var appReference = Assert.Single(app.ProjectReferences);
        var probe = Path.Combine(fixture.Root, "Contracts", "obj", "scope-probe.txt");
        Assert.True(File.Exists(probe));
        File.Delete(probe);

        foreach (var name in new[] { "First", "Second" })
        {
            var addition = await WriteSourceAsync(Path.Combine(fixture.Root, "App", name + ".cs"), cancellationToken);
            var result = await engine.TryRefreshProjectsAsync([addition], cancellationToken);

            Assert.True(result is not null, $"{name} addition unexpectedly required a full refresh.");
            var solution = engine.CaptureAdvancedSnapshot().Solution;
            Assert.Equal(app.Id, Assert.Single(solution.GetDocumentIdsWithFilePath(addition.Path)).ProjectId);
            Assert.Equal(appReference, Assert.Single(solution.GetProject(app.Id)!.ProjectReferences));
            Assert.False(File.Exists(probe), $"{name} addition ran design-time targets for Contracts.");
        }

        // This file does match the conditional link; it must use the existing full-refresh path.
        var linked = await WriteSourceAsync(Path.Combine(fixture.Root, "App", "SharedInput.cs"), cancellationToken);
        Assert.Null(await engine.TryRefreshProjectsAsync([linked], cancellationToken));
        Assert.Empty(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(linked.Path));
    }

    private const string LinkedImport = """
        <Project>
          <ItemGroup>
            <Compile Include="../App/Shared*.cs" Link="Shared/%(Filename)%(Extension)" />
          </ItemGroup>
        </Project>
        """;

    private static async Task<SemanticDocumentRefresh> WriteSourceAsync(string path, CancellationToken cancellationToken)
    {
        var text = $"class {Path.GetFileNameWithoutExtension(path)} {{}}";
        await File.WriteAllTextAsync(path, text, cancellationToken);
        return new(path, text, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }
}
