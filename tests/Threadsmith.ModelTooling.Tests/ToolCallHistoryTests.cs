namespace Threadsmith.ModelTooling.Tests;

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies exact-call admission, independent counts, and rejection metadata.</summary>
public static class ToolCallHistoryTests
{
    /// <summary>Verifies exact-call limits through the owning admission and configuration boundaries.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public static void ExactCalls_ConsumeOnlyAcceptedAllowance(int limit, int accepted)
    {
        var definition = new DateTimeTool(TestPromptLoader.Instance).Definition with { NumberOfExactToolInvocationsPerTurn = limit };
        var history = new ToolCallHistory();
        for (var index = 0; index < accepted; index++)
        {
            Assert.True(history.TryAdd(definition, "{}", out var rejection));
            Assert.Null(rejection);
        }

        for (var index = 0; index < 2; index++)
        {
            Assert.False(history.TryAdd(definition, "{}", out var rejection));
            Assert.NotNull(rejection);
            Assert.False(rejection.IsDuplicateSibling);
            Assert.Equal(accepted, rejection.InvocationCount);
        }

        Assert.True(history.TryAdd(definition, "{\"format\":\"other\"}"));
        Assert.True(new ToolCallHistory().TryAdd(definition, "{}"));
        Assert.Equal(limit == 0 ? 0 : 2, Counts(history).Count);
    }

    /// <summary>Verifies exact-call state and validation invariants.</summary>
    [Fact]
    public static void TentativeBatch_DoesNotConsumeAcceptedQuotaOrShareCounters()
    {
        var definition = new DateTimeTool(TestPromptLoader.Instance).Definition with { NumberOfExactToolInvocationsPerTurn = 2 };
        var history = new ToolCallHistory();
        Assert.True(history.TryAdd(definition, "{}"));
        var batch = new ToolCallHistory(history);
        Assert.True(batch.TryAdd(definition, "{}"));
        Assert.False(batch.TryAdd(definition, "{}", out var sibling));
        Assert.NotNull(sibling);
        Assert.True(sibling.IsDuplicateSibling);
        Assert.Equal(1, Counts(history).Single().Value);
        Assert.Equal(2, Counts(batch).Single().Value);
        Assert.True(history.TryAdd(definition, "{}"));
        Assert.False(history.TryAdd(definition, "{}"));
    }

    /// <summary>Verifies exact-call state and validation invariants.</summary>
    [Fact]
    public static void MaximumInteger_RejectsWithoutOverflow()
    {
        var definition = new DateTimeTool(TestPromptLoader.Instance).Definition with { NumberOfExactToolInvocationsPerTurn = int.MaxValue };
        var history = new ToolCallHistory();
        Assert.True(history.TryAdd(definition, "{}"));
        Counts(history)[("DATETIME", "{}")] = int.MaxValue - 1;
        Assert.True(history.TryAdd(definition, "{}"));
        Assert.False(history.TryAdd(definition, "{}", out var rejection));
        Assert.NotNull(rejection);
        Assert.Equal(int.MaxValue, rejection.InvocationCount);
        Assert.Equal(int.MaxValue, Counts(history).Single().Value);
    }

    /// <summary>Verifies exact-call state and validation invariants.</summary>
    [Fact]
    public static void Definition_RejectsNegativeLimit()
    {
        var definition = new DateTimeTool(TestPromptLoader.Instance).Definition;
        Assert.Throws<ArgumentOutOfRangeException>(() => definition with { NumberOfExactToolInvocationsPerTurn = -1 });
    }

    /// <summary>Only invalid arguments override reuse; hints, batch snapshots and new runs retain their existing identity rules.</summary>
    [Theory]
    [InlineData(ToolErrorClassification.InvalidArguments, false)]
    [InlineData(ToolErrorClassification.ExecutionFailure, true)]
    [InlineData(ToolErrorClassification.Timeout, true)]
    [InlineData(ToolErrorClassification.None, true)]
    public static void CompletedCalls_RejectOnlyUnchangedInvalidArguments(ToolErrorClassification classification, bool allowed)
    {
        var definition = new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer()).Definition;
        var history = new ToolCallHistory();
        const string arguments = "{\"path\":\"guardrails.md\",\"startPage\":1,\"concepts\":[\"csharp\"]}";
        Assert.True(history.TryAdd(definition, arguments));
        var beforeCompletion = new ToolCallHistory(history);
        history.ObserveResult(definition, arguments, new ToolInvocationResult
        {
            ToolInvocationId = ToolInvocationId.New(),
            ToolId = definition.Id,
            Succeeded = classification == ToolErrorClassification.None,
            ErrorClassification = classification,
        });

        var nextBatch = new ToolCallHistory(history);
        Assert.Equal(allowed, nextBatch.TryAdd(definition, "{\"path\":\"guardrails.md\",\"startPage\":1,\"concepts\":[\"dotnet\"]}"));
        Assert.True(nextBatch.TryAdd(definition, "{\"path\":\"guardrails.md\"}"));
        Assert.True(beforeCompletion.TryAdd(definition, arguments));
        Assert.True(new ToolCallHistory().TryAdd(definition, arguments));
    }

    /// <summary>The logged PDF-option failure is bounded while corrected reads observe new file contents.</summary>
    [Fact]
    public static async Task ReadFile_InvalidPdfOptionIsBlockedButCorrectedReadsSeeEdits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-invalid-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var path = Path.Combine(root, "guardrails.md");
            await File.WriteAllTextAsync(path, "original", cancellationToken);
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var tool = new ReadFileTool(TestPromptLoader.Instance, sanitizer);
            var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var history = new ToolCallHistory();
            var request = new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = tool.Definition.Id,
                ArgumentsJson = "{\"path\":\"guardrails.md\",\"startLine\":15,\"startPage\":1}",
                Context = new ToolInvocationContext { RepositoryPath = root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" },
            };
            Assert.True(history.TryAdd(tool.Definition, request.ArgumentsJson));
            var invalid = await pipeline.InvokeAsync(request, cancellationToken);
            Assert.Equal(ToolErrorClassification.InvalidArguments, invalid.ErrorClassification);
            Assert.Equal("Page options are only valid for PDF files.", invalid.Error);
            history.ObserveResult(tool.Definition, request.ArgumentsJson, invalid);
            Assert.False(new ToolCallHistory(history).TryAdd(tool.Definition, request.ArgumentsJson));

            request = request with { ArgumentsJson = "{\"path\":\"guardrails.md\"}" };
            foreach (var content in new[] { "original", "edited" })
            {
                await File.WriteAllTextAsync(path, content, cancellationToken);
                Assert.True(new ToolCallHistory(history).TryAdd(tool.Definition, request.ArgumentsJson));
                Assert.True(history.TryAdd(tool.Definition, request.ArgumentsJson));
                var result = await pipeline.InvokeAsync(request, cancellationToken);
                history.ObserveResult(tool.Definition, request.ArgumentsJson, result);
                Assert.True(result.Succeeded, result.Error);
                Assert.Contains(content, result.ResultJson, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Post-read processing cannot advance the observation time of captured bytes.</summary>
    [Fact]
    public static async Task ReadFile_ObservationPrecedesPostReadSanitization()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("threadsmith-read-observation-");
        var started = DateTimeOffset.UnixEpoch;
        var clock = new FakeTimeProvider(started);
        var readSanitizer = new PostReadClockSanitizer(clock);
        var sanitizer = new SecretOutputSanitizer();
        await using var events = new DomainEventStream(timeProvider: clock);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "source.txt"), "captured source", cancellationToken);
            var tool = new ReadFileTool(TestPromptLoader.Instance, readSanitizer);
            var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
            var request = new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = tool.Definition.Id,
                ArgumentsJson = "{\"path\":\"source.txt\"}",
                Context = new ToolInvocationContext { RepositoryPath = root.FullName, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" },
            };

            var result = await pipeline.InvokeAsync(request, cancellationToken);

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("captured source", result.ResultJson, StringComparison.Ordinal);
            Assert.Equal(started.AddSeconds(1), clock.GetUtcNow());
            Assert.Equal(started, result.ReadObservedAt);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static Dictionary<(string ToolName, string ArgumentsJson), int> Counts(ToolCallHistory history)
        => Assert.IsType<Dictionary<(string ToolName, string ArgumentsJson), int>>(typeof(ToolCallHistory).GetField("_counts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history));

    private sealed class PostReadClockSanitizer : IOutputSanitizer
    {
        private readonly FakeTimeProvider _clock;

        public PostReadClockSanitizer(FakeTimeProvider clock)
        {
            _clock = clock;
        }

        public string Sanitize(string value)
        {
            if (value == "captured source")
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
            }

            return value;
        }
    }
}
