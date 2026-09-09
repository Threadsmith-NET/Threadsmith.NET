namespace Threadsmith.Mutations.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Xunit;

public static partial class Milestone5Tests
{
    /// <summary>Unique source anchors stage with real line breaks and no model offset arithmetic.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public static async Task MutationPreview_OptionalOffsets_StagesOnce(string ending)
    {
        var source = "// header 😀" + ending + "internal static class ShellRunner" + ending + "{" + ending
            + "    /// <summary>Run commands.</summary>" + ending + "}" + ending;
        const string expected = "{\r\n    /// <summary>Run commands.</summary>";
        const string replacement = "{\r\n    private const string _test = \"tested!\";\r\n\r\n    /// <summary>Run commands.</summary>";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["src/Example.cs"] = source });
        var arguments = PreviewArguments(expected, replacement);
        var model = new QueueModelProvider(new ModelChunk { Output = new ToolRequestModelOutput("propose_mutations", arguments) });
        await using var scenario = await MutationScenario.CreateWithLimitsAsync(repository, model, new ExecutionLimits { MaxCorrectiveTurns = 0 });

        var staged = await scenario.ProposeAsync(RunId.New(), new TaskSpecification("Add field", []), PreviewPlan(), RunPhase.ImplementationModelTurn);

        Assert.Single(model.Requests);
        Assert.Empty(scenario.Events<ModelCorrectionAttempted>());
        var change = Assert.Single(staged.MutationSet.Mutations);
        var actualExpected = expected.ReplaceLineEndings(ending);
        Assert.Equal(actualExpected, change.ExpectedText);
        Assert.Equal(source.IndexOf(actualExpected, StringComparison.Ordinal), change.StartOffset);
        Assert.Equal(actualExpected.Length, change.Length);
        Assert.Equal(replacement.ReplaceLineEndings(ending), change.ReplacementText);
        Assert.Equal(source, await File.ReadAllTextAsync(Path.Combine(repository.Root, "src/Example.cs")));
        await scenario.ExecuteAsync(
            staged.MutationSet.MutationSetId,
            new MutationApproval { Level = MutationApprovalLevel.EntireSet, ApprovalId = staged.ApprovalId });
        Assert.Equal(
            source.Replace(actualExpected, replacement.ReplaceLineEndings(ending), StringComparison.Ordinal),
            await File.ReadAllTextAsync(Path.Combine(repository.Root, "src/Example.cs")));
    }

    /// <summary>Real source escape sequences must never be decoded when the original anchor matches.</summary>
    [Fact]
    public static async Task MutationPreview_LiteralBackslashes_ArePreserved()
    {
        const string source = "// header\nvar value = \"a\\nb\";\n";
        const string expected = "var value = \"a\\nb\";";
        const string replacement = "var value = \"c\\nd\";";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["src/Example.cs"] = source });
        var model = new QueueModelProvider(new ModelChunk { Output = new ToolRequestModelOutput("propose_mutations", PreviewArguments(expected, replacement)) });
        await using var scenario = await MutationScenario.CreateWithLimitsAsync(repository, model, new ExecutionLimits { MaxCorrectiveTurns = 0 });
        var staged = await scenario.ProposeAsync(RunId.New(), new TaskSpecification("Replace literal", []), PreviewPlan(), RunPhase.ImplementationModelTurn);
        var change = Assert.Single(staged.MutationSet.Mutations);
        Assert.Equal(expected, change.ExpectedText);
        Assert.Equal(replacement, change.ReplacementText);
    }

    /// <summary>Missing offsets cannot pick an ambiguous anchor or silently place an empty insertion at zero.</summary>
    [Theory]
    [InlineData("same\nsame", "same", "new")]
    [InlineData("one\ntwo\none\ntwo", "one\\ntwo", "new\\ntext")]
    [InlineData("source", "", "new")]
    [InlineData("one\ntwo", "one\\ntwo", "mixed\nencoding")]
    public static async Task MutationPreview_UnsafeRecovery_DoesNotStage(string source, string expected, string replacement)
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["src/Example.cs"] = source });
        var model = new QueueModelProvider(new ModelChunk { Output = new ToolRequestModelOutput("propose_mutations", PreviewArguments(expected, replacement)) });
        await using var scenario = await MutationScenario.CreateWithLimitsAsync(repository, model, new ExecutionLimits { MaxCorrectiveTurns = 0 });
        await Assert.ThrowsAnyAsync<MalformedModelOutputException>(() => scenario.ProposeAsync(
            RunId.New(), new TaskSpecification("Replace", []), PreviewPlan(), RunPhase.ImplementationModelTurn));
        Assert.Empty(scenario.Events<MutationSetProposed>());
        Assert.Equal(source, await File.ReadAllTextAsync(Path.Combine(repository.Root, "src/Example.cs")));
    }

    private static string PreviewArguments(string expected, string replacement) => JsonSerializer.Serialize(new
    {
        mutationSet = new
        {
            rationale = "Make the approved edit.",
            mutations = new[] { new { type = "ReplaceText", relativePath = "src/Example.cs", expectedText = expected, replacementText = replacement } },
        },
    });

    private static ImplementationPlan PreviewPlan() => new()
    {
        Summary = "Make an edit.",
        Steps = [new ImplementationPlanStep { StepId = StepId.New(), Title = "Edit", Description = "Edit source.", FileIntents = ModifyIntents("src/Example.cs"), ExpectedOutcome = "Source is updated." }],
    };
}
