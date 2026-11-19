namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Hint admission follows the existing native execution boundary.</summary>
public static class NativeConceptTransportTests
{
    /// <summary>Length and item bounds return the same useful correction as invalid phrases.</summary>
    [Fact]
    public static void Concept_bounds_have_actionable_preflight_errors()
    {
        var tool = new ListFilesTool(TestPromptLoader.Instance);
        Assert.IsType<ListFilesInput>(tool.DeserializeInput(JsonSerializer.Serialize(new { concepts = new[] { new string('a', 48) } })));
        string[][] invalidConcepts = [[new string('a', 49)], [.. Enumerable.Repeat("caching", 9)]];
        foreach (var concepts in invalidConcepts)
        {
            var error = Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(JsonSerializer.Serialize(new { concepts })));
            Assert.Contains("Invalid concepts", error.Message, StringComparison.Ordinal);
            Assert.Contains("re-emit the full intended batch", error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Operational identity ignores hints only for tools explicitly declaring them as metadata.</summary>
    [Fact]
    public static void Hint_changes_do_not_bypass_duplicate_call_admission()
    {
        var native = new ListFilesTool(TestPromptLoader.Instance).Definition;
        var history = new ToolCallHistory();
        Assert.True(history.TryAdd(native, "{\"path\":\".\",\"concepts\":[\"cancellation\"]}"));
        Assert.False(history.TryAdd(native, "{\"path\":\".\",\"concepts\":[\"logging\"]}"));
        Assert.True(history.TryAdd(native, "{\"path\":\"src\",\"concepts\":[\"logging\"]}"));
        var external = native with { Id = "mcp_external", ConceptsAreHints = false };
        Assert.True(history.TryAdd(external, "{\"concepts\":[\"cancellation\"]}"));
        Assert.True(history.TryAdd(external, "{\"concepts\":[\"logging\"]}"));
        var repeated = native with { NumberOfExactToolInvocationsPerTurn = int.MaxValue };
        var batch = new ToolCallHistory(history);
        Assert.True(batch.TryAdd(repeated, "{\"path\":\".\"}"));
        Assert.False(batch.TryAdd(repeated, "{\"path\":\".\",\"concepts\":[\"serialization\"]}"));
    }

    /// <summary>Unnormalizable calls reach ordinary argument handling with their original duplicate identity.</summary>
    [Theory]
    [InlineData("{\"path\":\"src\",\"path\":\"tests\"}")]
    [InlineData("{\"concepts\":[\"cancellation\"],\"concepts\":[\"logging\"]}")]
    [InlineData("{\"path\":")]
    public static void Unnormalizable_arguments_preserve_raw_call_identity(string argumentsJson)
    {
        var native = new ListFilesTool(TestPromptLoader.Instance).Definition;
        var batch = new ToolCallHistory(new ToolCallHistory());

        Assert.True(batch.TryAdd(native, argumentsJson));

        Assert.False(batch.TryAdd(native.Id, argumentsJson));
        Assert.True(batch.TryAdd(native, "{\"path\":\".\",\"concepts\":[\"cancellation\"]}"));
        Assert.False(batch.TryAdd(native, "{\"path\":\".\",\"concepts\":[\"logging\"]}"));
    }

    /// <summary>Duplicate-property validation failures remain ordinary tool results after preflight.</summary>
    [Theory]
    [InlineData("{\"path\":\"src\",\"path\":\"tests\",\"concepts\":null}")]
    [InlineData("{\"concepts\":null,\"concepts\":[\"logging\"]}")]
    public static async Task Duplicate_properties_reach_pipeline_argument_handling(string argumentsJson)
    {
        var tool = new ListFilesTool(TestPromptLoader.Instance);
        var preflight = new ToolCallHistory(new ToolCallHistory());
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.True(preflight.TryAdd(tool.Definition, argumentsJson));
        var result = await pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = tool.Definition.Id,
                ArgumentsJson = argumentsJson,
                Context = new ToolInvocationContext { RepositoryPath = Path.GetTempPath(), TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" },
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.InvalidArguments, result.ErrorClassification);
        Assert.Empty(result.Concepts);
    }
}
