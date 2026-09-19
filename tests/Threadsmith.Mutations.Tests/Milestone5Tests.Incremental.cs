namespace Threadsmith.Mutations.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Xunit;

public static partial class Milestone5Tests
{
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
