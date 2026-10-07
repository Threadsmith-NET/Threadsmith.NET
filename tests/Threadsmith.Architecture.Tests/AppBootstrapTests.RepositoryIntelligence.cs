namespace Threadsmith.Architecture.Tests;

using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Threadsmith.App;
using Threadsmith.Cli;
using Threadsmith.Core;
using Xunit;

/// <summary>Exercises the normal App composition and repository lifecycle with the optional assembly installed.</summary>
public static partial class AppBootstrapTests
{
    /// <summary>Repository open, restart restoration, tool inventory, and shutdown leave the feature unresolved.</summary>
    [Fact]
    public static async Task RepositoryIntelligenceRemainsDormantAcrossOpenRestoreAndShutdownAsync()
    {
        using var temporary = new TemporaryDirectory("repository-intelligence-dormant");
        var paths = CreatePaths(temporary.Root);
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(temporary.GetPath("README.md"), "fixture repository", cancellationToken);

        var sessionId = await WithComposedHostAsync(
            paths,
            async (foundation, applications, token) =>
        {
            Assert.False(IsFeatureCreated(applications));
            var definitions = foundation.ToolRegistry.AllDefinitions;
            Assert.Contains(definitions, definition => definition.Id == "read_file");
            Assert.Contains(definitions, definition => definition.Id == "code_explore");
            Assert.Contains(definitions, definition => definition.Id == "memories");
            Assert.DoesNotContain(definitions, definition => definition.Id == "repository_intelligence");

            var shell = new HeadlessShell(
                applications.Dispatcher,
                foundation.Projections,
                TextWriter.Null,
                foundation.WebFetchAuthorization,
                paths.RepositoryRoot);
            var created = await shell.CreateNewSessionAsync(token);
            var opened = await applications.Dispatcher.DispatchAsync(
                new OpenRepositoryCommand(
                    created.ActiveSession.SessionId,
                    paths.RepositoryRoot,
                    RepositoryTrustLevel.UntrustedInspection),
                token);

            Assert.Equal(paths.RepositoryRoot, opened.RepositoryPath);
            Assert.False(IsFeatureCreated(applications));
            return created.ActiveSession.SessionId;
        },
            cancellationToken);

        await WithComposedHostAsync(
            paths,
            async (foundation, applications, token) =>
        {
            var shell = new HeadlessShell(
                applications.Dispatcher,
                foundation.Projections,
                TextWriter.Null,
                foundation.WebFetchAuthorization,
                paths.RepositoryRoot);
            var restored = await shell.ResumeSessionAsync(sessionId, token);

            Assert.Equal(sessionId, restored.ActiveSession.SessionId);
            Assert.False(IsFeatureCreated(applications));
            Assert.DoesNotContain(
                foundation.ToolRegistry.AllDefinitions,
                definition => definition.Id == "repository_intelligence");
            return true;
        },
            cancellationToken);

        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(temporary.Root, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains("repository-intelligence", StringComparison.OrdinalIgnoreCase));
        SqliteConnection.ClearAllPools();
    }

    private static bool IsFeatureCreated(ApplicationServices applications)
    {
        var property = typeof(ApplicationServices).GetProperty(
            "RepositoryIntelligence",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        var lazy = property.GetValue(applications);
        Assert.NotNull(lazy);
        var created = lazy.GetType().GetProperty(nameof(Lazy<>.IsValueCreated));
        Assert.NotNull(created);
        return Assert.IsType<bool>(created.GetValue(lazy));
    }

    private static async Task<TResult> WithComposedHostAsync<TResult>(
        ConfigurationPaths paths,
        Func<HostFoundation, ApplicationServices, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        var configuration = ConfigurationBootstrap.Build([], paths);
        var trusted = ConfigurationBootstrap.BuildTrusted(paths);
        using var loggerFactory = LoggerFactory.Create(_ => { });
        await using var foundation = await HostFoundation.CreateAsync(
            configuration, trusted, paths, loggerFactory, TestPromptLoader.Instance);
        using var models = await ModelComposition.CreateAsync(
            configuration, paths, foundation.SecretResolver, loggerFactory, trustedConfiguration: trusted);
        await using var mcp = await IntegrationComposition.CreateMcpManagerAsync(
            trusted,
            useInteractiveTerminal: false,
            foundation.SecretResolver,
            foundation.Sanitizer,
            foundation.ToolRegistry,
            foundation.ToolStateManager,
            foundation.ToolPipeline,
            paths.RepositoryRoot,
            loggerFactory,
            foundation.PromptLoader,
            foundation.HookCoordinator,
            cancellationToken);
        await using var applications = await ApplicationComposition.CreateAsync(
            new ApplicationCompositionInputs
            {
                Host = new HostCompositionInputs
                {
                    Configuration = configuration,
                    TrustedConfiguration = trusted,
                    Paths = paths,
                    LoggerFactory = loggerFactory,
                    Events = foundation.Events,
                    Projections = foundation.Projections,
                    ExecutionLimits = foundation.ExecutionLimits,
                    OperationalLimits = foundation.OperationalLimits,
                    Sanitizer = foundation.Sanitizer,
                    PromptAppendLoader = foundation.PromptAppendLoader,
                    PromptLoader = foundation.PromptLoader,
                    Budget = foundation.Budget,
                },
                Persistence = new PersistenceCompositionInputs
                {
                    ConversationStore = foundation.ConversationStore,
                    RepositoryMemoryStore = foundation.RepositoryMemoryStore,
                    SessionLifecycleStore = foundation.SessionLifecycleStore,
                    SessionRestorer = foundation.SessionRestorer,
                    ArtifactStore = foundation.ArtifactStore,
                    ExecutionCheckpoints = foundation.ExecutionCheckpoints,
                    DelegationCheckpoints = foundation.DelegationCheckpoints,
                    SkillStateStore = foundation.SkillStateStore,
                    HookStore = foundation.HookStore,
                    EvidenceStore = foundation.EvidenceStore,
                    RepositoryFacts = foundation.RepositoryFacts,
                },
                Tools = new ToolPolicyCompositionInputs
                {
                    ToolPipeline = foundation.ToolPipeline,
                    ToolRegistry = foundation.ToolRegistry,
                    ToolStateManager = foundation.ToolStateManager,
                    CodeExploreOutputOptions = foundation.CodeExploreOutputOptions,
                    WebFetchAuthorization = foundation.WebFetchAuthorization,
                    RepositorySecretProvider = foundation.RepositorySecretProvider,
                    ProcessManager = foundation.ProcessManager,
                    HookCoordinator = foundation.HookCoordinator,
                },
                Semantic = new SemanticCompositionInputs
                {
                    SemanticEngines = foundation.SemanticEngines,
                    SemanticMutations = foundation.SemanticMutations,
                    SemanticRefreshCoordinator = foundation.SemanticRefreshCoordinator,
                    SemanticRefreshPublicationGate = foundation.SemanticRefreshPublicationGate,
                },
                Integration = new IntegrationCompositionInputs
                {
                    McpManager = mcp,
                    Models = models,
                },
            });

        return await action(foundation, applications, cancellationToken);
    }
}
