namespace Threadsmith.Mutations.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SourceEditToolTests
{
    /// <summary>Oversized evidence is omitted without losing the bounded disk outcome.</summary>
    [Fact]
    public async Task OversizedOriginalEvidenceFallsBackToTruncatedOutcome()
    {
        var diagnostic = new SourceEditDiagnostic("CS0246", new string('x', 1024), "Example.cs", 1, "Example", "net10.0", "introduced");
        var receipt = new SourceEditReceipt(Guid.NewGuid(), MutationSetId.New(), SourceEditStatus.Applied, ["Example.cs"], "Applied")
        {
            Analysis = new() { EffectId = Guid.NewGuid(), Diagnostics = Enumerable.Repeat(diagnostic, 20000).ToArray() },
        };
        var handler = new ReceiptHandler(receipt);
        var tool = new SourceEditTool(handler, TestPromptLoader.Instance);

        var result = await tool.ExecuteAsync(
            new() { Rationale = "Rename", Mutations = [] },
            new(ToolInvocationId.New(), SessionId.New(), RunId.New(), new() { RepositoryPath = Path.GetTempPath(), RequestedBy = "model", WorkspaceId = WorkspaceId.New() }),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
        Assert.Null(result.RetainedEvidenceContent);
        Assert.True(result.Value.OutputTruncated);
        Assert.Equal(receipt.EffectId, result.Value.EffectId);
        Assert.Equal(receipt.Status, result.Value.Status);
        Assert.True(result.Value.DurableOutcomeRecorded);
        Assert.True(Encoding.UTF8.GetByteCount(result.ModelResultContent!) <= tool.Definition.MaximumOutputBytes);
    }

    /// <summary>An unusable output limit cannot cause an unreportable mutation.</summary>
    [Fact]
    public async Task TooSmallLimitRejectsBeforeCallingWriter()
    {
        var handler = new ReceiptHandler(new(Guid.NewGuid(), MutationSetId.New(), SourceEditStatus.Applied, [], "Applied"));
        var tool = new SourceEditTool(handler, TestPromptLoader.Instance);
        await Assert.ThrowsAsync<ToolArgumentValidationException>(() => tool.ExecuteAsync(
            new() { Rationale = "Rename", Mutations = [] },
            new(ToolInvocationId.New(), SessionId.New(), RunId.New(), new() { RepositoryPath = Path.GetTempPath(), RequestedBy = "model", WorkspaceId = WorkspaceId.New() }) { MaximumOutputBytes = 1023 },
            TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }
}
