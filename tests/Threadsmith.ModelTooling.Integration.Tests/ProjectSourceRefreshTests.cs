namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises source additions against evaluated MSBuild project membership.</summary>
public static class ProjectSourceRefreshTests
{
    /// <summary>The saved repository preference controls the next direct load and full reload.</summary>
    [Fact]
    public static async Task WorkloadResolverPreferenceControlsSemanticReload()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var project = Path.Combine(fixture.Root, "App", "App.csproj");
        var linked = Path.Combine(fixture.Root, "Shared", "Linked.cs");
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="../Shared/Linked.cs" Condition="'$(MSBuildEnableWorkloadResolver)' == 'false'" />
              </ItemGroup>
            </Project>
            """,
            token);
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, ".threadsmith"));
        var config = Path.Combine(directory.FullName, "config.json");
        await File.WriteAllTextAsync(config, """{"solution":{"path":"App/App.csproj"}}""", token);
        const int maximumBytes = 4096;
        Assert.Null(RepositoryWorkloadResolverSetting.Read(fixture.Root, maximumBytes));

        await RepositoryWorkloadResolverSetting.SaveAsync(fixture.Root, false, maximumBytes, token);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root) with { SolutionPath = project }, token);
        Assert.Single(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(linked));

        await RepositoryWorkloadResolverSetting.SaveAsync(fixture.Root, true, maximumBytes, token);
        await engine.RefreshFullAsync(token);
        Assert.Empty(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(linked));
        Assert.True(RepositoryWorkloadResolverSetting.Read(fixture.Root, maximumBytes));
        Assert.Equal("App/App.csproj", ConfigurationDocument.Read(config)["solution:path"].GetString());
    }

    /// <summary>A scoped refresh verifies the referenced project without changing it in the published solution.</summary>
    [Fact]
    public static async Task SolutionAdditionRefreshesOnlyOwningProject()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        await using var coordinator = new SemanticRefreshCoordinator(
            new RegistrySemanticRefreshBackend(registry), events, NullLogger<SemanticRefreshCoordinator>.Instance, watchFileSystem: false);
        var contracts = Path.Combine(fixture.Root, "Contracts");
        await File.WriteAllTextAsync(
            Path.Combine(contracts, "Contracts.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <Target Name="ObserveDesignTimeLoad" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/obj/loads.txt" Lines="loaded" />
              </Target>
            </Project>
            """,
            token);
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await registry.LoadAsync(request, token);
        await coordinator.BindAsync(request, token);
        var engine = registry.GetEngine(request.WorkspaceId);
        var before = engine.CaptureAdvancedSnapshot().Solution;
        var owner = Assert.Single(before.Projects, project => project.Name == "App");
        var reference = Assert.Single(owner.ProjectReferences);
        var probe = Path.Combine(contracts, "obj", "loads.txt");
        Assert.True(File.Exists(probe));
        File.Delete(probe);

        var completion = new TaskCompletionSource<SemanticRefreshCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticRefreshCompleted completed && completed.WorkspaceId == request.WorkspaceId)
            {
                completion.TrySetResult(completed);
            }

            return Task.CompletedTask;
        });
        var addition = await WriteSourceAsync(Path.Combine(fixture.Root, "App", "NewSource.cs"), token);
        await coordinator.ObserveChangeAsync(new(request.SessionId, addition.Path, SemanticFileChangeKind.Created), token);
        await coordinator.EnsureCurrentAsync(request.SessionId, SemanticRefreshReason.UserAdmission, token);

        Assert.Equal(SemanticRefreshMode.Incremental, (await completion.Task.WaitAsync(token)).Mode);
        var after = engine.CaptureAdvancedSnapshot().Solution;
        Assert.Equal(owner.Id, Assert.Single(after.GetDocumentIdsWithFilePath(addition.Path)).ProjectId);
        Assert.Equal(reference, Assert.Single(after.GetProject(owner.Id)!.ProjectReferences));
        Assert.Equal(owner.Id, Assert.Single(after.GetChanges(before).GetProjectChanges()).ProjectId);
        Assert.True(File.Exists(probe));
    }

    /// <summary>A non-owner's changed compiler settings must be published before the workspace is current.</summary>
    [Fact]
    public static async Task SourceAdditionChangingNonOwnerParseOptionsUsesFullRefresh()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        await using var coordinator = new SemanticRefreshCoordinator(
            new RegistrySemanticRefreshBackend(registry), events, NullLogger<SemanticRefreshCoordinator>.Instance, watchFileSystem: false);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "Contracts", "Contracts.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="Exists('../App/NewSource.cs')">
                <DefineConstants>$(DefineConstants);APP_SOURCE_EXISTS</DefineConstants>
              </PropertyGroup>
            </Project>
            """,
            cancellationToken);
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await registry.LoadAsync(request, cancellationToken);
        await coordinator.BindAsync(request, cancellationToken);
        var engine = registry.GetEngine(request.WorkspaceId);
        const string symbol = "APP_SOURCE_EXISTS";
        var before = Assert.Single(engine.CaptureAdvancedSnapshot().Solution.Projects, project => project.Name == "Contracts");
        Assert.DoesNotContain(symbol, Assert.IsType<CSharpParseOptions>(before.ParseOptions).PreprocessorSymbolNames);
        var completion = new TaskCompletionSource<SemanticRefreshCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticRefreshCompleted completed && completed.WorkspaceId == request.WorkspaceId)
            {
                completion.TrySetResult(completed);
            }

            return Task.CompletedTask;
        });

        // Act
        var addition = await WriteSourceAsync(Path.Combine(fixture.Root, "App", "NewSource.cs"), cancellationToken);
        await coordinator.ObserveChangeAsync(new(request.SessionId, addition.Path, SemanticFileChangeKind.Created), cancellationToken);
        await coordinator.EnsureCurrentAsync(request.SessionId, SemanticRefreshReason.UserAdmission, cancellationToken);

        // Assert
        Assert.Equal(SemanticRefreshMode.Full, (await completion.Task.WaitAsync(cancellationToken)).Mode);
        Assert.True(coordinator.IsCurrent(request.SessionId));
        var solution = engine.CaptureAdvancedSnapshot().Solution;
        var contracts = Assert.Single(solution.Projects, project => project.Name == "Contracts");
        Assert.Contains(symbol, Assert.IsType<CSharpParseOptions>(contracts.ParseOptions).PreprocessorSymbolNames);
        var added = Assert.Single(solution.GetDocumentIdsWithFilePath(addition.Path));
        Assert.Equal("App", solution.GetProject(added.ProjectId)?.Name);
    }

    /// <summary>A linked addition with candidates covering the entire graph does not run separate design-time loads.</summary>
    [Fact]
    public static async Task BroadCandidateSetUsesOneFullLoadForMixedBatch()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        await using var coordinator = new SemanticRefreshCoordinator(
            new RegistrySemanticRefreshBackend(registry), events, NullLogger<SemanticRefreshCoordinator>.Instance, watchFileSystem: false);
        var project = Path.Combine(fixture.Root, "App", "App.csproj");
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../Contracts/Contracts.csproj" />
              </ItemGroup>
              <Import Project="../SharedInputs.props" Condition="'$(TargetFramework)' == 'net10.0'" />
              <Target Name="RecordDesignTimeLoad" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/obj/scope-probe.txt" Lines="loaded" />
              </Target>
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
        var probe = Path.Combine(fixture.Root, "App", "obj", "scope-probe.txt");
        Assert.Single(await File.ReadAllLinesAsync(probe, token));
        File.Delete(probe);
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
        Assert.Equal(SemanticRefreshMode.Full, refresh.Mode);
        Assert.Equal(SemanticRefreshReason.HostMutation, refresh.Reason);
        Assert.Single(await File.ReadAllLinesAsync(probe, token));
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

    /// <summary>A linked addition updates its independent owners without replacing loaded projects.</summary>
    [Fact]
    public static async Task SharedAdditionRefreshesIndependentOwnersInPlace()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var solutionPath = Path.Combine(fixture.Root, "LinkedOwners.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="FirstOwner/FirstOwner.csproj" />
              <Project Path="SecondOwner/SecondOwner.csproj" />
              <Project Path="FirstRetained/FirstRetained.csproj" />
              <Project Path="SecondRetained/SecondRetained.csproj" />
            </Solution>
            """,
            cancellationToken);
        string[] owners = ["FirstOwner", "SecondOwner"];
        foreach (var name in owners.Concat(["FirstRetained", "SecondRetained"]))
        {
            var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, name));
            var items = owners.Contains(name)
                ? """<ItemGroup><Compile Include="../Shared/New*.cs" Link="Shared/%(Filename)%(Extension)" /></ItemGroup>"""
                : string.Empty;
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, name + ".csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  {items}
                </Project>
                """,
                cancellationToken);
        }

        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root) with { SolutionPath = solutionPath }, cancellationToken);
        var before = engine.CaptureAdvancedSnapshot().Solution;
        var ownerIds = before.Projects.Where(project => owners.Contains(project.Name)).Select(project => project.Id).ToHashSet();
        Assert.Equal(2, ownerIds.Count);
        var addition = await WriteSourceAsync(Path.Combine(fixture.Root, "Shared", "NewLinked.cs"), cancellationToken);

        // Act
        var result = await engine.TryRefreshProjectsAsync([addition], cancellationToken);

        // Assert
        Assert.NotNull(result);
        var after = engine.CaptureAdvancedSnapshot().Solution;
        var changes = after.GetChanges(before);
        Assert.Empty(changes.GetAddedProjects());
        Assert.Empty(changes.GetRemovedProjects());
        Assert.True(ownerIds.SetEquals(changes.GetProjectChanges().Select(change => change.ProjectId)));
        var linked = after.GetDocumentIdsWithFilePath(addition.Path);
        Assert.Equal(2, linked.Length);
        Assert.True(ownerIds.SetEquals(linked.Select(id => id.ProjectId)));
        foreach (var id in linked)
        {
            Assert.Equal(["Shared"], after.GetDocument(id)!.Folders);
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
    [Theory]
    [InlineData("CustomAfterMicrosoftCommonProps")]
    [InlineData("NuGetPropsFile")]
    [InlineData("BeforeMicrosoftNETSdkTargets")]
    [InlineData("CodeAnalysisTargets")]
    public static async Task SdkImportHookUsesFullRefresh(string importProperty)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var contracts = Path.Combine(fixture.Root, "Contracts");
        await File.WriteAllTextAsync(Path.Combine(contracts, "Contracts.csproj"), "<Project Sdk='Microsoft.NET.Sdk' />", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(contracts, "Directory.Build.props"),
            $"""
            <Project>
              <Import Project="../Directory.Build.props" />
              <PropertyGroup>
                <{importProperty}>$(MSBuildThisFileDirectory)ScopeHook.props</{importProperty}>
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

    /// <summary>A new SDK wildcard match can introduce a linked owner after an earlier refresh.</summary>
    [Fact]
    public static async Task NewSdkWildcardImportUsesFullRefreshAfterEarlierAddition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var app = Path.Combine(fixture.Root, "App");
        var contracts = Path.Combine(fixture.Root, "Contracts");
        await File.WriteAllTextAsync(
            Path.Combine(contracts, "Contracts.csproj"),
            """
            <Project>
              <PropertyGroup>
                <MSBuildUserExtensionsPath>$(MSBuildThisFileDirectory)UserExtensions</MSBuildUserExtensionsPath>
                <ImportUserLocationsByWildcardBeforeMicrosoftCommonProps>true</ImportUserLocationsByWildcardBeforeMicrosoftCommonProps>
              </PropertyGroup>
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
            </Project>
            """,
            cancellationToken);
        var imports = Path.Combine(contracts, "UserExtensions", "Current", "Imports", "Microsoft.Common.props", "ImportBefore");
        Directory.CreateDirectory(imports);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), cancellationToken);
        var first = await WriteSourceAsync(Path.Combine(app, "SharedFirst.cs"), cancellationToken);
        Assert.NotNull(await engine.TryRefreshProjectsAsync([first], cancellationToken));
        Assert.Single(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(first.Path));

        await File.WriteAllTextAsync(Path.Combine(imports, "LinkAppSources.props"), LinkedImport, cancellationToken);
        var second = await WriteSourceAsync(Path.Combine(app, "SharedSecond.cs"), cancellationToken);

        Assert.Null(await engine.TryRefreshProjectsAsync([second], cancellationToken));
        Assert.Empty(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(second.Path));
        await engine.RefreshFullAsync(cancellationToken);
        var solution = engine.CaptureAdvancedSnapshot().Solution;
        Assert.Equal(2, solution.GetDocumentIdsWithFilePath(first.Path).Length);
        Assert.Equal(2, solution.GetDocumentIdsWithFilePath(second.Path).Length);
    }

    /// <summary>New and rewritten implicit imports invalidate a directly opened project's cached inputs.</summary>
    [Theory]
    [InlineData("App.csproj.user", false)]
    [InlineData("obj/App.csproj.custom.props", false)]
    [InlineData("obj/App.csproj.nuget.g.targets", false)]
    [InlineData("obj/App.csproj.nuget.g.targets", true)]
    public static async Task ImplicitImportAddedAfterScopeCaptureUsesFullRefresh(string importPath, bool rewrite)
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var app = Path.Combine(fixture.Root, "App");
        var project = Path.Combine(app, "App.csproj");
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <Target Name="RecordDesignTimeLoad" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/obj/scope-probe.txt" Lines="loaded" />
              </Target>
            </Project>
            """,
            token);
        var import = Path.Combine(app, importPath);
        Directory.CreateDirectory(Path.Combine(app, "obj"));
        const string addLink = "<Project><ItemGroup><Compile Include='../Shared/Lin*.cs' /></ItemGroup></Project>";
        if (rewrite)
        {
            await File.WriteAllTextAsync(import, addLink.Replace("Lin*", "Not*", StringComparison.Ordinal), token);
        }

        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root) with { SolutionPath = project }, token);
        var first = await WriteSourceAsync(Path.Combine(app, "First.cs"), token);
        Assert.NotNull(await engine.TryRefreshProjectsAsync([first], token));
        var probe = Path.Combine(app, "obj", "scope-probe.txt");
        Assert.True(File.Exists(probe));
        File.Delete(probe);

        var lastWrite = rewrite ? File.GetLastWriteTimeUtc(import) : default;
        await File.WriteAllTextAsync(import, addLink, token);
        if (rewrite)
        {
            File.SetLastWriteTimeUtc(import, lastWrite);
        }

        var second = await WriteSourceAsync(Path.Combine(app, "Second.cs"), token);

        Assert.Null(await engine.TryRefreshProjectsAsync([second], token));
        Assert.False(File.Exists(probe));
        Assert.Empty(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(second.Path));
        await engine.RefreshFullAsync(token);
        var solution = engine.CaptureAdvancedSnapshot().Solution;
        Assert.Single(solution.GetDocumentIdsWithFilePath(second.Path));
        Assert.Single(solution.GetDocumentIdsWithFilePath(Path.Combine(fixture.Root, "Shared", "Linked.cs")));
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

    /// <summary>A directly opened single project can publish successive additions without replacing the project.</summary>
    [Fact]
    public static async Task DirectSingleProjectPublishesSuccessiveAdditions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var app = Path.Combine(fixture.Root, "App");
        var project = Path.Combine(app, "App.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk='Microsoft.NET.Sdk' />", cancellationToken);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root) with { SolutionPath = project }, cancellationToken);
        var owner = Assert.Single(engine.CaptureAdvancedSnapshot().Solution.Projects);
        var first = await WriteSourceAsync(Path.Combine(app, "First.cs"), cancellationToken);
        Assert.NotNull(await engine.TryRefreshProjectsAsync([first], cancellationToken));
        var firstId = Assert.Single(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(first.Path));
        var second = await WriteSourceAsync(Path.Combine(app, "Second.cs"), cancellationToken);

        Assert.NotNull(await engine.TryRefreshProjectsAsync([second], cancellationToken));

        var solution = engine.CaptureAdvancedSnapshot().Solution;
        Assert.Equal(owner.Id, Assert.Single(solution.GetDocumentIdsWithFilePath(second.Path)).ProjectId);
        Assert.Equal(firstId, Assert.Single(solution.GetDocumentIdsWithFilePath(first.Path)));
    }

    /// <summary>Oversized imports and changed logical identities both require authoritative reloading.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task UnverifiedProjectDeclarationsOrRetainedMetadataUseFullRefresh(bool oversizedImport)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        const int xmlLimit = 1024;
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            resourceLimits: new SemanticResourceLimits { MaximumInventoryXmlBytes = xmlLimit },
            timeProvider: new FakeTimeProvider());
        var app = Path.Combine(fixture.Root, "App");
        var project = Path.Combine(app, "App.csproj");
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="Linked.props" />
              <Target Name="RecordDesignTimeLoad" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/obj/scope-probe.txt" Lines="loaded" />
              </Target>
            </Project>
            """,
            cancellationToken);
        const string renamedLink = """
            <Link Condition="Exists('NewSource.cs')">After/Renamed.cs</Link>
            """;
        var linkMetadata = oversizedImport ? string.Empty : renamedLink;
        var padding = oversizedImport ? new string(' ', xmlLimit) : string.Empty;
        await File.WriteAllTextAsync(
            Path.Combine(app, "Linked.props"),
            $"""
            <Project>
              <!-- {padding} -->
              <ItemGroup>
                <Compile Include="../Shared/Linked.cs">
                  <Link>Before/Original.cs</Link>
                  {linkMetadata}
                </Compile>
              </ItemGroup>
            </Project>
            """,
            cancellationToken);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root) with { SolutionPath = project }, cancellationToken);
        var probe = Path.Combine(app, "obj", "scope-probe.txt");
        Assert.True(File.Exists(probe));
        File.Delete(probe);
        var linked = Path.Combine(fixture.Root, "Shared", "Linked.cs");
        Assert.Equal("Original.cs", ReadLinkedName());
        var addition = await WriteSourceAsync(Path.Combine(app, "NewSource.cs"), cancellationToken);

        Assert.Null(await engine.TryRefreshProjectsAsync([addition], cancellationToken));
        Assert.Equal(!oversizedImport, File.Exists(probe));
        Assert.Equal("Original.cs", ReadLinkedName());
        await engine.RefreshFullAsync(cancellationToken);
        Assert.Single(engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(addition.Path));
        Assert.Equal(oversizedImport ? "Original.cs" : "Renamed.cs", ReadLinkedName());

        string ReadLinkedName()
        {
            var loaded = Assert.Single(engine.CaptureAdvancedSnapshot().Solution.Projects);
            return Assert.Single(loaded.Documents, document => document.FilePath == linked).Name;
        }
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
