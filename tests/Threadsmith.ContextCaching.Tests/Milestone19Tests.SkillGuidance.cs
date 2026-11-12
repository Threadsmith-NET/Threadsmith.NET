namespace Threadsmith.ContextCaching.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

public sealed partial class Milestone19Tests
{
    /// <summary>Skill routing follows invocation availability across transports and subsequent assemblies.</summary>
    [Theory]
    [InlineData(ToolTransportMode.Native, null)]
    [InlineData(ToolTransportMode.Native, "inspect_skill")]
    [InlineData(ToolTransportMode.Native, "invoke_skill")]
    [InlineData(ToolTransportMode.Text, null)]
    [InlineData(ToolTransportMode.Text, "inspect_skill")]
    [InlineData(ToolTransportMode.Text, "invoke_skill")]
    public async Task ContextAssembler_SkillGuidanceRequiresInvocationTool(
        ToolTransportMode transport,
        string? toolId)
    {
        var root = CreateTemporaryDirectory();
        var sanitizer = new PassthroughSanitizer();
        await using var events = new NullEventStream();
        var assembler = new ContextAssembler(
            new EmptyEvidenceStore(),
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            TestPromptLoader.Instance);

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
            var result = await assembler.AssembleAsync(
                request with
                {
                    ToolSchemas = toolId is null ? [] : [new ContextToolSchema(toolId, toolId, "{\"type\":\"object\"}")],
                },
                TestContext.Current.CancellationToken);

            var guidanceEnabled = toolId == "invoke_skill";
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

            var disabled = await assembler.AssembleAsync(request, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(disabled.Inspection.PromptAssets, asset => asset.Source == PromptFileNames.SystemSkillGuidance);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
