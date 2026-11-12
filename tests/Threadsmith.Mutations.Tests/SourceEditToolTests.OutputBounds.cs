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
    /// <summary>Serialization and sanitization preserve edit identity and disk outcome within configured limits.</summary>
    [Theory]
    [InlineData(65536, SourceEditStatus.Applied, true)]
    [InlineData(1024, SourceEditStatus.RecoveryRequired, false)]
    [InlineData(4096, SourceEditStatus.NotApplied, true)]
    [InlineData(131072, SourceEditStatus.Conflict, true)]
    [InlineData(4096, SourceEditStatus.Denied, true)]
    [InlineData(65536, SourceEditStatus.Applied, true, true)]
    public async Task OversizedReceiptPreservesOutcomeThroughConfiguredPipeline(int limit, SourceEditStatus status, bool durable, bool expand = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var text = expand ? "EXPAND" : string.Concat(Enumerable.Repeat("<escaped>\"\\🙂", 100));
        var receipt = new SourceEditReceipt(Guid.NewGuid(), MutationSetId.New(), status, ["Example.cs"], text)
        {
            DurableOutcomeRecorded = durable,
            Analysis = new()
            {
                EffectId = Guid.NewGuid(),
                Pending = true,
                Diagnostics = Enumerable.Range(0, 100).Select(index => new SourceEditDiagnostic("CS0246", text, "Example.cs", index + 1, "Example", "net10.0", "introduced")).ToArray(),
            },
        };
        var handler = new ReceiptHandler(receipt);
        var tool = new SourceEditTool(handler, TestPromptLoader.Instance);
        Assert.Equal(65536, tool.Definition.MaximumOutputBytes);
        var registry = new ToolRegistry([tool], runtimeOptions: new() { ByTool = [new() { ToolId = "edit_source", MaximumOutputBytes = limit }] });
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApproval(), events, expand ? new ExpandingSanitizer() : new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var request = new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "edit_source",
            ArgumentsJson = """{"rationale":"Rename","mutations":[{"type":"ReplaceText","relativePath":"Example.cs","expectedText":"Example","replacementText":"Changed"}]}""",
            Context = new() { RepositoryPath = Path.GetTempPath(), WorkspaceId = WorkspaceId.New(), TrustLevel = RepositoryTrustLevel.TrustedMutation, RequestedBy = "model" },
        };

        var result = await pipeline.InvokeAsync(request, ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, handler.Calls);
        Assert.True(result.IsTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(result.ResultJson!) <= limit);
        Assert.True(Encoding.UTF8.GetByteCount(result.ModelResultContent!) <= limit);
        using var output = JsonDocument.Parse(result.ModelResultContent!);
        Assert.Equal(status.ToString(), output.RootElement.GetProperty("Status").GetString());
        Assert.Equal(durable, output.RootElement.GetProperty("DurableOutcomeRecorded").GetBoolean());
        Assert.Equal(receipt.EffectId, output.RootElement.GetProperty("EffectId").GetGuid());
        Assert.Equal(receipt.MutationSetId.Value, output.RootElement.GetProperty("MutationSetId").GetProperty("Value").GetGuid());
        Assert.True(output.RootElement.GetProperty("OutputTruncated").GetBoolean());
        Assert.Contains("CS0246", result.RetainedEvidenceContent, StringComparison.Ordinal);
        Assert.DoesNotContain("RetainedEvidenceContent", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

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

    private sealed class ExpandingSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value.Replace("EXPAND", new string('x', 20000), StringComparison.Ordinal);
    }

    private sealed class DenyApproval : IApprovalPolicy
    {
        public Task<bool> IsApprovedAsync(string action, ApprovalLevel requiredLevel, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }
}
