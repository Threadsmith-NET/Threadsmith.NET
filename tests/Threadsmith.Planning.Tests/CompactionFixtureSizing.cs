namespace Threadsmith.Planning.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;
using Xunit;

/// <summary>Measures current prompt and schema overhead independently of compaction payloads.</summary>
internal static class CompactionFixtureSizing
{
    public static async Task<int> MeasureEnvelopeAsync(
        ContextAssembler assembler,
        ToolRegistry registry,
        ModelProfile profile,
        string repositoryPath,
        string intent,
        CancellationToken cancellationToken)
    {
        var context = await assembler.AssembleAsync(
            new ContextAssemblyRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Phase = RunPhase.EvidenceCollection,
                Task = new TaskSpecification(intent, []),
                RepositoryPath = repositoryPath,
                DefaultModelProfileId = profile.Id,
                ToolSchemas = registry.Definitions.Select(definition => new ContextToolSchema(
                    definition.Id,
                    definition.Description,
                    definition.InputSchema.JsonSchema,
                    definition.PreferStrictArguments)).ToArray(),
            },
            cancellationToken);
        return Assert.IsType<ModelWireEstimate>(context.WireEstimate).WireInputTokens;
    }
}
