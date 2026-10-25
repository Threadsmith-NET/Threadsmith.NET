namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Real child requests bind role and tool prose to their receiving profile, including capacity fallback.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunAsync_MixedReceivingModelsAndFallback_UseTheirOwnPolicyAndToolDescriptions(bool renameSecondProfile, bool forceCapacityFallback)
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-child-prompts-" + Guid.NewGuid().ToString("N"));
        var application = Path.Combine(root, "application");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(Path.Combine(application, "prompts"));
        try
        {
            foreach (var definition in PromptAssetCatalog.All)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(application, "prompts", definition.FileName),
                    TestPromptLoader.Instance.Get(definition.FileName),
                    TestContext.Current.CancellationToken);
            }

            var firstProfile = CreateProfile() with { ContextWindow = forceCapacityFallback ? 4096 : 16384 };
            var secondProfile = CreateProfile() with { Name = renameSecondProfile ? "renamed-profile" : "second-profile" };
            var userConfiguration = Path.Combine(root, "user", "config.json");
            var userRoot = Path.Combine(Path.GetDirectoryName(new RepositoryPermissionStore(userConfiguration).GetPath(repository))!, "prompts");
            foreach (var (profile, marker) in new[] { (firstProfile, "A"), (secondProfile, "B") })
            {
                foreach (var file in new[] { PromptFileNames.SystemChildAgentHostPolicy, PromptFileNames.SystemChildAgentExplorer, PromptFileNames.ToolListFilesDescription })
                {
                    var directory = Path.Combine(userRoot, PromptAssetCatalog.Get(file).Owner, "models", profile.Id.Value.ToString("D"));
                    Directory.CreateDirectory(directory);
                    var content = $"{marker}:{file}";
                    if (forceCapacityFallback && profile.Id == firstProfile.Id && file == PromptFileNames.SystemChildAgentExplorer)
                    {
                        content += new string('x', 24576);
                    }

                    await File.WriteAllTextAsync(Path.Combine(directory, file), content, TestContext.Current.CancellationToken);
                }
            }

            var prompts = await DeployedPromptLoader.LoadAsync(application, TestContext.Current.CancellationToken);
            prompts.PublishRepository(await prompts.LoadRepositoryAsync(repository, userConfiguration, TestContext.Current.CancellationToken));
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var tool = new ListFilesTool(prompts);
            var registry = new ToolRegistry([tool]);
            var pipeline = CreatePipeline(registry, events, sanitizer);
            var provider = new PromptCapturingProvider();
            var first = CreateAssignment(firstProfile.Id, [tool.Definition.Id]) with { Deadline = DateTimeOffset.MaxValue };
            var second = CreateAssignment(secondProfile.Id, [tool.Definition.Id]) with { Deadline = DateTimeOffset.MaxValue };
            var plan = CreatePlan(first) with { Assignments = [first, second] };
            var parent = CreateParentContext(plan, [tool.Definition.Id]);
            parent = parent with
            {
                Invocation = parent.Invocation with { RepositoryPath = repository, VisibleSourceFrontier = null },
            };
            var catalog = new ConfiguredModelCatalog([firstProfile, secondProfile]);
            var runner = new ModelExplorerAssignmentRunner(
                new AgentContextAssembler(evidence),
                new AgentFindingAdmission(evidence),
                new AgentModelSelector(catalog, new DefaultModelSelectionPolicy(catalog)),
                provider,
                pipeline,
                evidence,
                new StubInstructionProvider(),
                sanitizer,
                CreateOptions(),
                parent,
                registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId),
                prompts);

            using var parentScope = prompts.BeginModelScope(firstProfile.Id);
            var outcomes = await Task.WhenAll(
                runner.RunAsync(plan, first, TestContext.Current.CancellationToken),
                runner.RunAsync(plan, second, TestContext.Current.CancellationToken));

            Assert.All(outcomes, outcome => Assert.Equal(AgentRunStatus.Completed, outcome.Status));
            Assert.Contains(outcomes, outcome => outcome.ModelProfileId == secondProfile.Id);
            if (forceCapacityFallback)
            {
                Assert.All(outcomes, outcome => Assert.Equal(secondProfile.Id, outcome.ModelProfileId));
            }

            Assert.Equal(2, provider.Requests.Count);
            foreach (var request in provider.Requests)
            {
                var marker = request.ResolvedProfileId == firstProfile.Id ? "A" : "B";
                Assert.Contains($"{marker}:{PromptFileNames.SystemChildAgentHostPolicy}", request.Messages[0].GetModelVisibleContent(), StringComparison.Ordinal);
                Assert.Contains($"{marker}:{PromptFileNames.SystemChildAgentExplorer}", request.Messages[1].GetModelVisibleContent(), StringComparison.Ordinal);
                Assert.Equal($"{marker}:{PromptFileNames.ToolListFilesDescription}", Assert.Single(request.Tools, item => item.Name == "list_files").Description);
                Assert.True(request.WireEstimate?.NativeToolTokens > 0);
            }

            Assert.Equal($"A:{PromptFileNames.SystemChildAgentHostPolicy}", prompts.Get(PromptFileNames.SystemChildAgentHostPolicy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PromptCapturingProvider : IModelProvider
    {
        public ConcurrentQueue<ModelStreamRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            await Task.Yield();
            yield return new ModelChunk { Output = new TextModelOutput("Investigation complete."), Usage = new ModelUsage(20, 10) };
        }
    }
}
