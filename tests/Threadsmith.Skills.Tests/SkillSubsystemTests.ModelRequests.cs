namespace Threadsmith.Skills.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Skill turns inherit supported profile defaults independently of reasoning visibility.</summary>
    [Theory]
    [InlineData("high", false)]
    [InlineData("medium", true)]
    [InlineData("none", true)]
    public async Task SkillProcedure_UsesValidatedProfileReasoningAcrossToolRounds(
        string defaultName, bool supportsOff)
    {
        var defaultLevel = new ReasoningLevel(defaultName);
        var plan = PermissionPlan();
        ReasoningLevel[] levels = supportsOff
            ? [ReasoningLevel.None, ReasoningLevel.Medium]
            : [ReasoningLevel.High];
        var profile = new ModelProfile
        {
            Id = plan.ModelProfileId!.Value,
            Name = "Skill reasoning fixture",
            Provider = "fixture",
            Endpoint = new Uri("https://example.test/messages"),
            ModelId = "skill-reasoning",
            ContextWindow = 32_000,
            MaximumOutputTokens = 1024,
            DefaultReasoningLevel = defaultLevel,
            SupportedReasoningLevels = levels,
            ReasoningCapability = new EffectiveReasoningCapability
            {
                SchemaVersion = 1,
                Controllability = ReasoningControllability.Selectable,
                SupportedLevels = levels,
                DefaultLevel = defaultLevel,
                SupportsReasoningOff = supportsOff,
            },
        };
        var model = new PermissionModelProvider();
        var tool = new PermissionProbeTool();
        await using var events = new DomainEventStream();
        var runner = CreatePermissionRunner(
            PermissionContext, tool, model, events, new ConfiguredModelCatalog([profile]));

        var result = await runner.RunAsync(plan, PermissionStep(), 1, [], "{}");

        Assert.Equal(2, result.ModelTurns);
        Assert.All(model.Requests, request =>
        {
            Assert.Equal(defaultLevel, request.ReasoningLevel);
            Assert.True(profile.SupportsReasoningLevel(request.ReasoningLevel));
            Assert.False(request.IncludeReasoningText);
        });
    }

    /// <summary>Successful and denied tools have one structured result and a compatible legacy continuation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillProcedure_ToolContinuationPreservesStructuredAndLegacyProjections(bool denyTool)
    {
        var model = new PermissionModelProvider();
        var tool = new PermissionProbeTool();
        await using var events = new DomainEventStream();
        var runner = CreatePermissionRunner(
            () => PermissionContext() with { DenyAllTools = denyTool }, tool, model, events);

        await runner.RunAsync(PermissionPlan(), PermissionStep(), 1, [], "{}");

        Assert.Equal(2, model.Requests.Count);
        Assert.Single(model.Requests[0].Messages);
        var continuation = model.Requests[1];
        Assert.Equal(
            [ModelMessageRole.User, ModelMessageRole.Assistant, ModelMessageRole.Tool],
            continuation.Messages.Select(message => message.Role));
        Assert.Equal(continuation.Messages[1].ToolCallId, continuation.Messages[2].ToolCallId);
        Assert.Equal(denyTool, continuation.Messages[2].IsError);
        var instructions = Assert.Single(continuation.Messages[0].Content).Content;
        Assert.Contains("Return only JSON matching the declared output schema.", instructions, StringComparison.Ordinal);
        Assert.Contains("Continue the declared procedure. Return only output-schema JSON.", continuation.Input, StringComparison.Ordinal);
        var toolResult = Assert.Single(continuation.Messages[2].Content).Content;
        Assert.Contains(toolResult, continuation.Input, StringComparison.Ordinal);
        Assert.Single(continuation.Messages, message => message.GetModelVisibleContent().Contains(toolResult, StringComparison.Ordinal));
        Assert.Equal(ReasoningLevel.None, continuation.ReasoningLevel);
    }
}
