namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

public static partial class ToolRuntimeTests
{
    /// <summary>Captured completions keep exact live source without persisted/model duplication.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("compile")]
    [InlineData("runtime")]
    [InlineData("exception")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("output-limit")]
    public static async Task ScriptSourceFollowsExistingCompletionAndIsNeverSerialized(string outcome)
    {
        const string source = "// private-source-marker\r\n\treturn \"é😀[red]\";  \r";
        await using var events = new DomainEventStream();
        var completions = new List<ToolInvocationCompleted>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is ToolInvocationCompleted completed)
            {
                completions.Add(completed);
            }

            return Task.CompletedTask;
        });
        var engine = new SourceProbeEngine(outcome);
        var pipeline = CreatePipeline(events, [new CSharpScriptTool(engine, TestPromptLoader.Instance)], runtimeOptions: new() { Defaults = new() { TimeoutMilliseconds = -1 } });
        var request = CreateBatchRequest(
            0,
            "script-source",
            "csharp_script",
            CreateContext(Path.GetFullPath(".")) with { TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation, AllowedExecutables = ["dotnet"] },
            JsonSerializer.Serialize(new CSharpScriptInput { Code = source, Kind = "statement" })).Invocation;

        var result = await pipeline.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(source, engine.Submitted);
        var completion = Assert.Single(completions);
        Assert.Equal(source, completion.SourceDetail!.Source);
        Assert.Equal(result.ToolInvocationId, completion.SourceDetail.ToolInvocationId);
        Assert.Equal(ToolSourceAvailability.Available, completion.SourceDetail.Availability);
        Assert.Same(completion.SourceDetail, result.SourceDetail);
        Assert.DoesNotContain("private-source-marker", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var persisted = DomainEventJson.Serialize(completion);
        Assert.DoesNotContain("private-source-marker", persisted, StringComparison.Ordinal);
        var restored = Assert.IsType<ToolInvocationCompleted>(DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(completion), 1, persisted));
        Assert.Null(restored.SourceDetail);
        Assert.DoesNotContain("private-source-marker", completion.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Denied invocations do not call the engine or capture source.</summary>
    [Fact]
    public static async Task RejectedScriptDoesNotCaptureSourceOrCallEngine()
    {
        await using var events = new DomainEventStream();
        var engine = new SourceProbeEngine("success");
        var pipeline = CreatePipeline(events, [new CSharpScriptTool(engine, TestPromptLoader.Instance)], runtimeOptions: new() { Defaults = new() { TimeoutMilliseconds = -1 } });
        var request = CreateBatchRequest(0, "rejected", "csharp_script", CreateContext(Path.GetFullPath(".")), "{\"code\":\"42\"}").Invocation;

        var result = await pipeline.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Null(result.SourceDetail);
        Assert.Null(engine.Submitted);
    }

    private sealed class SourceProbeEngine : ICSharpScriptEngine
    {
        private readonly string _outcome;

        public SourceProbeEngine(string outcome)
        {
            _outcome = outcome;
        }

        public string? Submitted { get; private set; }

        public Task<CSharpScriptOutput> ExecuteAsync(string code, ScriptKind kind, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Submitted = code;
            return _outcome switch
            {
                "exception" => throw new InvalidOperationException("worker failed"),
                "timeout" => throw new TimeoutException("worker timeout"),
                "cancel" => throw new OperationCanceledException("worker cancelled"),
                "output-limit" => Task.FromResult(new CSharpScriptOutput(true, new string('x', 8 * 1024 * 1024), null, 0, false)),
                "compile" => Task.FromResult(new CSharpScriptOutput(false, null, "compile failed", 0, false)),
                "runtime" => Task.FromResult(new CSharpScriptOutput(false, null, "runtime failed", 0, false)),
                _ => Task.FromResult(new CSharpScriptOutput(true, "42", null, 0, false)),
            };
        }
    }
}
