namespace Threadsmith.Mutations.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Xunit;

public static partial class Milestone5Tests
{
    /// <summary>Replanning is an exclusive implementation decision with no staging side effects.</summary>
    [Theory]
    [InlineData(RunPhase.ImplementationModelTurn)]
    [InlineData(RunPhase.CorrectionModelTurn)]
    public static async Task Replan_ReturnsDecisionWithoutStagingOrWriting(RunPhase phase)
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["src/Example.cs"] = "old" });
        var model = new QueueModelProvider(new ModelChunk
        {
            Output = new ToolRequestModelOutput("request_replan", "{\"Reason\":\"Inspect the implementation before changing its interface.\"}"),
        });
        await using var scenario = await MutationScenario.CreateAsync(repository, model);

        var result = await scenario.ProposeIncrementalAsync(IncrementalTestPlan(), phase);

        Assert.True(result.ReplanRequested);
        Assert.False(result.IsCompletionOnly);
        Assert.Null(result.StagedMutationSet);
        Assert.Null(result.StepComplete);
        Assert.Contains("Inspect the implementation", result.Rationale, StringComparison.Ordinal);
        Assert.Contains(Assert.Single(model.Requests).Tools, tool => tool.Name == "request_replan");
        Assert.DoesNotContain(model.Requests[0].Tools, tool => tool.Name == "code_explore");
        Assert.Empty(scenario.Events<MutationSetProposed>());
        Assert.Equal("old", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
    }

    /// <summary>A semantic correction may abandon the invalid candidate and return to planning.</summary>
    [Fact]
    public static async Task Replan_AfterPreMutationFailureReturnsWithoutStagingOrWriting()
    {
        const string source = "namespace Demo;\npublic sealed class Example\n{\n}\n";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = source,
        });
        var offset = source.IndexOf("}\n", StringComparison.Ordinal);
        var badProposal = JsonSerializer.Serialize(new
        {
            mutationSet = new
            {
                rationale = "Add a member.",
                mutations = new[]
                {
                    new
                    {
                        type = "ReplaceText",
                        relativePath = "src/Example.cs",
                        startOffset = offset,
                        expectedText = string.Empty,
                        replacementText = "public void Broken( { }\n",
                    },
                },
            },
        });
        var model = new QueueModelProvider(
            new ModelChunk { Output = new ToolRequestModelOutput("propose_mutations", badProposal) },
            new ModelChunk
            {
                Output = new ToolRequestModelOutput(
                    "request_replan",
                    "{\"reason\":\"Inspect the missing dependency before changing the implementation.\"}"),
            });
        var preMutationAnalyzer = new FakePreMutationAnalyzer();
        await using var scenario = await MutationScenario.CreateWithPreMutationAnalyzerAsync(
            repository,
            model,
            preMutationAnalyzer);

        var result = await scenario.ProposeIncrementalAsync(
            IncrementalTestPlan(),
            RunPhase.ImplementationModelTurn);

        Assert.True(result.ReplanRequested);
        Assert.False(result.IsCompletionOnly);
        Assert.Null(result.StagedMutationSet);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(model.Requests[1].Tools, tool => tool.Name == "request_replan");
        Assert.Single(preMutationAnalyzer.Requests);
        var correction = Assert.Single(scenario.Events<ModelCorrectionAttempted>());
        Assert.Equal(ModelCorrectionCategory.PreMutationAnalysis, correction.Category);
        Assert.Empty(scenario.Events<MutationSetProposed>());
        Assert.Equal(source, await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
    }

    /// <summary>Malformed requests use the existing mutation corrective feedback.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reason\":\" \"}")]
    [InlineData("{\"reason\":\"investigate\",\"Reason\":\"competing\"}")]
    [InlineData("{\"reason\":\"investigate\",\"mutations\":[]}")]
    public static async Task Replan_MalformedDecisionUsesExistingCorrection(string invalid)
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["src/Example.cs"] = "old" });
        var model = new QueueModelProvider(
            new ModelChunk { Output = new ToolRequestModelOutput("request_replan", invalid) },
            new ModelChunk { Output = new ToolRequestModelOutput("request_replan", "{\"reason\":\"Investigate the missing dependency.\"}") });
        await using var scenario = await MutationScenario.CreateWithLimitsAsync(
            repository, model, ExecutionLimits.Default with { MaxCorrectiveTurns = 1 });

        var result = await scenario.ProposeIncrementalAsync(IncrementalTestPlan(), RunPhase.ImplementationModelTurn);

        Assert.True(result.ReplanRequested);
        Assert.Single(scenario.Events<ModelCorrectionAttempted>());
        Assert.Contains("nonempty reason", GetCorrectionMessageText(model.Requests[1], "active-turn-mutation-correction:"), StringComparison.Ordinal);
        Assert.Empty(scenario.Events<MutationSetProposed>());
    }

    /// <summary>Replanning cannot smuggle mutations or bypass tool availability.</summary>
    [Fact]
    public static async Task Replan_CannotAccompanyMutationsOrBypassAvailability()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["src/Example.cs"] = "old" });
        var request = new ModelChunk { Output = new ToolRequestModelOutput("request_replan", "{\"reason\":\"Investigate dependencies.\"}") };
        var mutation = new ModelChunk
        {
            Output = new ToolRequestModelOutput(
                "propose_mutations",
                "{\"mutationSet\":{\"rationale\":\"Update\",\"mutations\":[{\"type\":\"ReplaceText\",\"relativePath\":\"src/Example.cs\",\"expectedText\":\"old\",\"replacementText\":\"new\"}]}}"),
        };
        foreach (var replanFirst in new[] { true, false })
        {
            var model = replanFirst ? new ChunkModelProvider(request, mutation) : new ChunkModelProvider(mutation, request);
            await using var scenario = await MutationScenario.CreateWithLimitsAsync(
                repository, model, ExecutionLimits.Default with { MaxCorrectiveTurns = 0 });
            await Assert.ThrowsAsync<MalformedModelOutputException>(() => scenario.ProposeIncrementalAsync(IncrementalTestPlan(), RunPhase.ImplementationModelTurn));
            Assert.Empty(scenario.Events<MutationSetProposed>());
        }

        var unavailable = new QueueModelProvider(request);
        await using var disabled = await MutationScenario.CreateWithLimitsAsync(
            repository, unavailable, ExecutionLimits.Default with { MaxCorrectiveTurns = 0 });
        await Assert.ThrowsAsync<MalformedModelOutputException>(() => disabled.ProposeIncrementalAsync(
            IncrementalTestPlan(), RunPhase.ImplementationModelTurn, allowReplanning: false));
        Assert.DoesNotContain(unavailable.Requests[0].Tools, tool => tool.Name == "request_replan");
        Assert.Equal("old", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
    }

    /// <summary>Malformed operation aliases receive conversation-native corrective feedback.</summary>
    [Theory]
    [InlineData("42", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    [InlineData("true", false)]
    [InlineData("42", true)]
    [InlineData("{}", true)]
    public static async Task MutationKind_InvalidStructureUsesExistingCorrectiveTurn(string kindJson, bool structuredText)
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        var valid = JsonSerializer.Serialize(new
        {
            rationale = "Update the requested value.",
            mutations = new { kind = "ReplaceText", path = "src/Example.cs", expectedText = "old", replacementText = "new" },
        });
        var invalid = valid.Replace("\"ReplaceText\"", kindJson, StringComparison.Ordinal);
        var model = new QueueModelProvider(
            structuredText ? new ModelChunk { Text = invalid } : new ModelChunk { Output = new ToolRequestModelOutput("propose_mutations", invalid) },
            structuredText ? new ModelChunk { Text = valid } : new ModelChunk { Output = new ToolRequestModelOutput("propose_mutations", valid) });
        await using var scenario = await MutationScenario.CreateWithLimitsAsync(
            repository, model, ExecutionLimits.Default with { MaxCorrectiveTurns = 1 });
        var plan = IncrementalTestPlan();

        var staged = await scenario.ProposeAsync(
            RunId.New(),
            new TaskSpecification("Update example", []),
            plan,
            structuredText ? RunPhase.MutationPreparation : RunPhase.ImplementationModelTurn);

        Assert.Equal(2, model.Requests.Count);
        Assert.Single(scenario.Events<ModelCorrectionAttempted>());
        Assert.Single(scenario.Events<MutationSetProposed>());
        Assert.Single(staged.MutationSet.Mutations);
        Assert.Contains("+new", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        Assert.NotEmpty(GetCorrectionMessageText(model.Requests[1], "active-turn-mutation-correction:"));
        Assert.Empty(scenario.ContextRequests[1].Task.UserConstraints ?? []);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(repository.Root, "src/Example.cs")));
    }

    /// <summary>Unsupported no-change claims are corrected before staging.</summary>
    [Fact]
    public static async Task CompletionOnly_WithoutStepEvidenceReceivesActionableCorrection()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        var noChanges = "{\"mutationSet\":{\"rationale\":\"Done\",\"stepComplete\":true,\"mutations\":[]}}";
        var changes = "{\"mutationSet\":{\"rationale\":\"Update value\",\"stepComplete\":true,\"mutations\":[{\"type\":\"ReplaceText\",\"relativePath\":\"src/Example.cs\",\"expectedText\":\"old\",\"replacementText\":\"new\"}]}}";
        var model = new QueueModelProvider(
            new ModelChunk { Output = new ToolRequestModelOutput("propose_mutations", noChanges) },
            new ModelChunk { Output = new ToolRequestModelOutput("propose_mutations", changes) });
        await using var scenario = await MutationScenario.CreateWithLimitsAsync(
            repository, model, ExecutionLimits.Default with { MaxCorrectiveTurns = 1 });

        var staged = await scenario.ProposeAsync(
            RunId.New(), new TaskSpecification("Update example", []), IncrementalTestPlan(), RunPhase.ImplementationModelTurn);

        Assert.Single(staged.MutationSet.Mutations);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("no fully applied batch", GetCorrectionMessageText(model.Requests[1], "active-turn-mutation-correction:"), StringComparison.Ordinal);
        Assert.Single(scenario.Events<MutationSetProposed>());
    }

    private static ImplementationPlan IncrementalTestPlan() => new()
    {
        Summary = "Update example.",
        Steps =
        [
            new ImplementationPlanStep
            {
                StepId = StepId.New(),
                Title = "Update example",
                Description = "Update the value.",
                FileIntents = ModifyIntents("src/Example.cs"),
                ExpectedOutcome = "Example updated.",
            },
        ],
    };
}
