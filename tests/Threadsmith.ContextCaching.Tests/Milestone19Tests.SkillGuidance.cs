namespace Threadsmith.ContextCaching.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

public sealed partial class Milestone19Tests
{
    /// <summary>Skill routing follows tool and skill enablement across transports and subsequent assemblies.</summary>
    [Theory]
    [InlineData(ToolTransportMode.Native, null, true)]
    [InlineData(ToolTransportMode.Native, "inspect_skill", true)]
    [InlineData(ToolTransportMode.Native, "invoke_skill", true)]
    [InlineData(ToolTransportMode.Native, "invoke_skill", false)]
    [InlineData(ToolTransportMode.Text, null, true)]
    [InlineData(ToolTransportMode.Text, "inspect_skill", true)]
    [InlineData(ToolTransportMode.Text, "invoke_skill", true)]
    [InlineData(ToolTransportMode.Text, "invoke_skill", false)]
    public async Task ContextAssembler_SkillGuidanceRequiresInvocationTool(
        ToolTransportMode transport,
        string? toolId,
        bool availabilityKnown)
    {
        var root = CreateTemporaryDirectory();
        var sanitizer = new PassthroughSanitizer();
        await using var events = new NullEventStream();
        var skillEnabled = false;
        var availabilityChecks = 0;
        var assembler = new ContextAssembler(
            new EmptyEvidenceStore(),
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            TestPromptLoader.Instance,
            documentationSkillEnabled: availabilityKnown ? cancellationToken =>
            {
                Assert.Equal(TestContext.Current.CancellationToken, cancellationToken);
                availabilityChecks++;
                return Task.FromResult(skillEnabled);
            }
        : null);

        try
        {
            var request = new ContextAssemblyRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Phase = RunPhase.EvidenceCollection,
                Task = new TaskSpecification("inspect repository configuration", []),
                RepositoryPath = root,
                ToolTransportMode = transport,
            };
            var baseline = await assembler.AssembleAsync(request, TestContext.Current.CancellationToken);
            var toolRequest = request with
            {
                ToolSchemas = toolId is null ? [] : [new ContextToolSchema(toolId, toolId, "{\"type\":\"object\"}")],
            };

            // Keeping invoke_skill available must not expose routing for a disabled skill.
            var skillDisabled = await assembler.AssembleAsync(toolRequest, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(skillDisabled.Inspection.PromptAssets, asset => asset.Source == PromptFileNames.SystemSkillGuidance);
            Assert.DoesNotContain("threadsmith-docs-help", skillDisabled.Messages![0].GetModelVisibleContent(), StringComparison.Ordinal);

            skillEnabled = true;
            var result = await assembler.AssembleAsync(toolRequest, TestContext.Current.CancellationToken);

            var guidanceEnabled = toolId == "invoke_skill" && availabilityKnown;
            var assets = result.Inspection.PromptAssets.Where(asset => asset.Source == PromptFileNames.SystemSkillGuidance).ToArray();
            Assert.Equal(guidanceEnabled ? 1 : 0, assets.Length);
            var baselineMessages = baseline.Messages;
            var messages = result.Messages;
            Assert.NotNull(baselineMessages);
            Assert.NotNull(messages);
            Assert.Equal(ModelMessageRole.System, messages[0].Role);
            var addedCharacters = guidanceEnabled
                ? Environment.NewLine.Length + Assert.Single(assets).CharacterCount
                : 0;
            Assert.Equal(
                baselineMessages[0].GetModelVisibleContent().Length + addedCharacters,
                messages[0].GetModelVisibleContent().Length);

            skillEnabled = false;
            var disabled = await assembler.AssembleAsync(toolRequest, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(disabled.Inspection.PromptAssets, asset => asset.Source == PromptFileNames.SystemSkillGuidance);
            Assert.DoesNotContain("threadsmith-docs-help", disabled.Messages![0].GetModelVisibleContent(), StringComparison.Ordinal);
            Assert.Equal(guidanceEnabled ? 3 : 0, availabilityChecks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
