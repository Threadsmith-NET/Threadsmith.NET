namespace Threadsmith.Skills.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Skills;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Valid JSON is recovered from model prose or a fence before schema validation.</summary>
    [Theory]
    [InlineData("The write succeeded. {\"response\":\"Saved\"}")]
    [InlineData("The write succeeded.\n```json\n{\"response\":\"Saved\"}\n```")]
    [InlineData("{\"response\":\"Saved\"}\nThe report is ready.")]
    public async Task SkillOutputCorrection_ExtractsJsonWithoutAnotherModelTurn(string modelOutput)
    {
        // Arrange
        var writer = new SkillBatchWriteFileTool();
        var model = new SkillBatchModelProvider
        {
            Calls = [new("write_file", "{\"path\":\".inbox/report.md\",\"content\":\"Saved report\"}")],
            FinalResponses = [modelOutput],
        };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [writer], events);
        var plan = PermissionPlan() with
        {
            AvailableToolIds = ["write_file"],
            EffectiveBudget = new SkillBudget { ModelTurns = 3, ToolCalls = 1 },
        };
        var step = PermissionStep() with { OutputSchemaAsset = "output.json" };
        SkillContextSegment[] content =
        [
            new()
            {
                Package = plan.Package,
                StepId = step.StepId,
                AssetPath = "output.json",
                Sha256 = "fixture",
                Content = "{\"type\":\"object\",\"required\":[\"response\"],\"properties\":{\"response\":{\"type\":\"string\"}}}",
            },
        ];

        // Act
        var result = await runner.RunAsync(plan, step, 1, content, "{}");

        // Assert
        using var output = JsonDocument.Parse(result.OutputJson);
        Assert.Equal("Saved", output.RootElement.GetProperty("response").GetString());
        Assert.Equal(2, result.ModelTurns);
        Assert.Equal(1, writer.Writes);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(".inbox/report.md", Assert.Single(result.SideEffects!).Path);
    }

    /// <summary>Final JSON and schema repair reuses completed writes without tool access.</summary>
    [Theory]
    [InlineData("The write succeeded.", false)]
    [InlineData("{\"response\":42}", false)]
    [InlineData("", false)]
    [InlineData("The write succeeded.", true)]
    public async Task SkillOutputCorrection_RepairsOnlyFinalOutputAndPreservesWrite(string invalidOutput, bool replay)
    {
        // Arrange
        var writer = new SkillBatchWriteFileTool();
        var model = new SkillBatchModelProvider
        {
            Calls = [new("write_file", "{\"path\":\".inbox/report.md\",\"content\":\"Saved report\"}")],
            FinalResponses = [invalidOutput, "{\"response\":\"Saved\"}"],
            Replay = replay,
        };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [writer], events);
        var plan = PermissionPlan() with
        {
            AvailableToolIds = ["write_file"],
            EffectiveBudget = new SkillBudget { ModelTurns = 3, ToolCalls = 1 },
        };
        var step = PermissionStep() with { OutputSchemaAsset = "output.json" };
        SkillContextSegment[] content =
        [
            new()
            {
                Package = plan.Package,
                StepId = step.StepId,
                AssetPath = "output.json",
                Sha256 = "fixture",
                Content = "{\"type\":\"object\",\"required\":[\"response\"],\"properties\":{\"response\":{\"type\":\"string\"}}}",
            },
        ];

        // Act
        var result = await runner.RunAsync(plan, step, 1, content, "{}");

        // Assert
        using var output = JsonDocument.Parse(result.OutputJson);
        Assert.Equal("Saved", output.RootElement.GetProperty("response").GetString());
        Assert.Equal(3, result.ModelTurns);
        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(1, writer.Writes);
        Assert.Equal(".inbox/report.md", Assert.Single(result.SideEffects!).Path);
        Assert.Equal(3, model.Requests.Count);
        var correction = model.Requests[2];
        Assert.Empty(correction.Tools);
        Assert.False(correction.RequiredCapabilities.ToolCalls);
        Assert.Contains(correction.Messages, message => message.SectionId == "skill-output-correction");
        Assert.Contains(correction.Messages, message => message.SectionId == "skill-tool-result");
        Assert.Contains("Repair only the final response", correction.Input, StringComparison.Ordinal);
    }

    /// <summary>Exhaustion and attempted tool use retain artifact records without a second write.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillOutputCorrection_FailurePreservesArtifactWithoutRepeatingTools(bool requestTool)
    {
        // Arrange
        var writer = new SkillBatchWriteFileTool();
        var model = new SkillBatchModelProvider
        {
            Calls = [new("write_file", "{\"path\":\".inbox/report.md\",\"content\":\"Saved report\"}")],
            FinalResponses = ["Not JSON"],
            RequestToolDuringCorrection = requestTool,
        };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [writer], events);
        var plan = PermissionPlan() with
        {
            AvailableToolIds = ["write_file"],
            EffectiveBudget = new SkillBudget { ModelTurns = 3, ToolCalls = 2 },
        };

        // Act
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => runner.RunAsync(plan, PermissionStep(), 1, [], "{}"));

        // Assert
        Assert.Equal(1, writer.Writes);
        Assert.Equal(3, model.Requests.Count);
        Assert.Empty(model.Requests[2].Tools);
        Assert.Equal(".inbox/report.md", Assert.Single(SkillProcedureInterruption.GetSideEffects(error)).Path);
        Assert.Contains(requestTool ? "no tools were executed" : "model-turn budget was exhausted", error.Message, StringComparison.Ordinal);
    }
}
