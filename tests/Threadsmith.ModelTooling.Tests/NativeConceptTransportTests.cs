namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Hint admission follows the existing native execution boundary.</summary>
public static class NativeConceptTransportTests
{
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
        var repeated = native with { AllowDuplicateInvocations = true };
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
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);

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

    /// <summary>Denied or invalid requests contribute no hints; admitted execution carries normalized hints without serializing them.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public static async Task Pipeline_emits_hints_only_after_admission(bool denied, bool invalid)
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-concepts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var tool = new ListFilesTool(TestPromptLoader.Instance);
            var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
            var result = await pipeline.InvokeAsync(
                new ToolInvocationRequest
            {
                SessionId = SessionId.New(), RunId = RunId.New(), ToolId = tool.Definition.Id,
                ArgumentsJson = invalid ? "{\"concepts\":[\"not a concept\"]}" : "{\"concepts\":[\" Cancellation \",\"cancellation\"]}",
                Context = new ToolInvocationContext { RepositoryPath = root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model", DeniedToolIds = denied ? [tool.Definition.Id] : [] },
            },
                TestContext.Current.CancellationToken);
            Assert.Equal(!denied && !invalid, result.Succeeded);
            Assert.Equal(denied || invalid ? [] : new[] { "cancellation" }, result.Concepts);
            using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.False(serialized.RootElement.TryGetProperty("Concepts", out _));
        }
        finally
        {
            Directory.Delete(root); // The empty fixture has no tool-created content.
        }
    }
}
