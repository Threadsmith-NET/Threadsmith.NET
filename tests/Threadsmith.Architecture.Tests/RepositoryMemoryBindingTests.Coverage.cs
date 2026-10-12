namespace Threadsmith.Architecture.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class RepositoryMemoryBindingTests
{
    /// <summary>Checklist operations preserve isolation, actual read evidence, and byte-bounded paging.</summary>
    [Fact]
    public static async Task Coverage_tracks_evidence_and_pages_without_writing_memories()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        await using var operations = intelligence.CreateOperations();
        await using var run = await operations.BeginAsync(SessionId.New(), RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct);
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), new ToolInvocationContext
        {
            RepositoryPath = fixture.InitialRoot, RequestedBy = "model", IntelligenceRun = run, TrustLevel = RepositoryTrustLevel.TrustedRead,
        });
        var tool = new MemoriesTool(new UnusedMemories(), intelligence.Options, TestPromptLoader.Instance);
        var before = run.Collection!.Snapshot();
        var update = new MemoryCoverageInput("area-4", "Feature", "How does a feature join shared output?", "investigated", "No reusable convention established from this example.", SupportingPaths: ["guide.txt"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(new MemoriesInput("updateCoverage", Coverage: update), context, ct));
        Assert.Empty(run.Collection.Coverage);
        await File.WriteAllTextAsync(Path.Combine(fixture.InitialRoot, "guide.txt"), "A single implementation example.", ct);
        await new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer()).ExecuteAsync(new ReadFileInput { Path = "guide.txt" }, context, ct);
        var changed = (await tool.ExecuteAsync(new MemoriesInput("updateCoverage", Coverage: update), context, ct)).Value.Coverage!;
        Assert.Equal("area-4", Assert.Single(changed.Items).Key);
        Assert.Equal("guide.txt", Assert.Single(changed.Items[0].Supports).Path);
        await Assert.ThrowsAsync<ToolArgumentValidationException>(() => tool.ExecuteAsync(new MemoriesInput("updateCoverage", Coverage: update with { MemoryIds = [Guid.NewGuid().ToString()] }), context, ct));
        await tool.ExecuteAsync(new MemoriesInput("updateCoverage", Coverage: update with { Status = "investigating", Gaps = "Compare another caller." }), context, ct);
        Assert.Equal("investigating", Assert.Single(run.Collection.Coverage).Status);
        Assert.Equal(before, run.Collection.Snapshot());
        for (var index = 0; index < 5; index++)
        {
            var large = update with
            {
                Key = $"area-{index}", Area = new string('界', 160), Question = new string('界', 600),
                Conclusion = new string('界', 1200), Limitations = new string('界', 600),
            };
            await tool.ExecuteAsync(new MemoriesInput("updateCoverage", Coverage: large), context, ct);
        }

        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new AllowApprovalPolicy(), intelligence.Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var invocation = new ToolInvocationRequest
        {
            SessionId = context.SessionId, RunId = context.RunId, ToolId = "memories", ArgumentsJson = "{\"action\":\"coverage\"}", Context = context.Invocation,
        };
        var page = await pipeline.InvokeAsync(invocation, ct);
        Assert.True(page.Succeeded, page.Error);
        Assert.True(Encoding.UTF8.GetByteCount(page.ResultJson!) < tool.Definition.MaximumOutputBytes);
        using var json = JsonDocument.Parse(page.ResultJson!);
        var coverage = json.RootElement.GetProperty("Coverage");
        var cursor = coverage.GetProperty("NextAfterKey").GetString();
        Assert.NotNull(cursor);
        var firstCount = coverage.GetProperty("Items").GetArrayLength();
        Assert.InRange(firstCount, 1, 3);
        var remainder = await pipeline.InvokeAsync(invocation with { ArgumentsJson = JsonSerializer.Serialize(new { action = "coverage", afterKey = cursor }) }, ct);
        Assert.True(remainder.Succeeded, remainder.Error);
        using var rest = JsonDocument.Parse(remainder.ResultJson!);
        Assert.Equal(5 - firstCount, rest.RootElement.GetProperty("Coverage").GetProperty("Items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, rest.RootElement.GetProperty("Coverage").GetProperty("NextAfterKey").ValueKind);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(new MemoriesInput("coverage"), context with { Invocation = context.Invocation with { IntelligenceRun = null } }, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(new MemoriesInput("updateCoverage", Coverage: update), context with { Invocation = context.Invocation with { IntelligenceRun = null } }, ct));
    }

    /// <summary>Completion labels cannot substitute for evidence or a concrete blocked reason.</summary>
    [Theory]
    [InlineData("investigated")]
    [InlineData("blocked")]
    [InlineData("complete")]
    public static void Coverage_rejects_unsupported_completion_and_false_statuses(string status)
    {
        var collection = new RepositoryMemoryCollection(new RepositoryMemoryReadSnapshot("repo", 1, [], [], []), true, 0);
        Assert.Throws<ArgumentException>(() => collection.UpdateCoverage(new RepositoryIntelligenceCoverage
        {
            Key = "output", Area = "Presentation", Question = "How is output formatted?", Status = status,
        }));
        Assert.Empty(collection.Coverage);
    }
}
