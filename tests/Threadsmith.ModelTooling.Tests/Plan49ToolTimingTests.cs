namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies Plan-49 authoritative ordinary-tool timing and source projection.</summary>
public static class Plan49ToolTimingTests
{
    /// <summary>Execution timing uses the monotonic tool boundary and excludes earlier pipeline work.</summary>
    [Fact]
    public static async Task Pipeline_Success_ProjectsBuiltInExecutionDuration()
    {
        var timeProvider = new ManualTimeProvider();
        var tool = new TimedTool(timeProvider, shouldFail: false);
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var pipeline = CreatePipeline(events, tool, timeProvider);

        var result = await pipeline.InvokeAsync(CreateRequest(tool.Definition.Id));

        Assert.True(result.Succeeded);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), result.Duration);
        var started = Assert.IsType<ToolInvocationStarted>(observed[0]);
        var completed = Assert.IsType<ToolInvocationCompleted>(observed[^1]);
        Assert.Equal(ToolActivitySourceKind.BuiltIn, started.Source?.Kind);
        Assert.Equal("README.md", started.ActivityDetail);
        Assert.Equal(ToolActivitySourceKind.BuiltIn, completed.Source?.Kind);
        Assert.Equal(1500, completed.ElapsedMilliseconds);
        Assert.Equal(OperationActivityOutcome.Completed, completed.Outcome);
    }

    /// <summary>Activity detail is normalized and bounded before event publication.</summary>
    [Fact]
    public static async Task Pipeline_OversizedActivityDetail_PublishesBoundedSingleLineText()
    {
        var timeProvider = new ManualTimeProvider();
        var tool = new TimedTool(
            timeProvider,
            shouldFail: false,
            activityDetail: "  " + new string('x', 236) + "😀" + new string('y', 100) + "\r\n");
        await using var events = new DomainEventStream();
        ToolInvocationStarted? started = null;
        await using var subscription = events.Subscribe((item, _) =>
        {
            started = item as ToolInvocationStarted ?? started;
            return Task.CompletedTask;
        });
        var pipeline = CreatePipeline(events, tool, timeProvider);

        _ = await pipeline.InvokeAsync(CreateRequest(tool.Definition.Id));

        var detail = Assert.IsType<string>(started?.ActivityDetail);
        Assert.InRange(detail.Length, 1, 240);
        Assert.EndsWith("...", detail, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', detail);
        Assert.DoesNotContain('\n', detail);
        _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetByteCount(detail);
    }

    /// <summary>Configured ceilings bound text without driving allocation or overflowing derived capacities.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(240)]
    [InlineData(int.MaxValue)]
    public static async Task Pipeline_ConfiguredActivityDetailLimit_PreservesToolExecution(int maximumCharacters)
    {
        var timeProvider = new ManualTimeProvider();
        var tool = new TimedTool(timeProvider, shouldFail: false);
        await using var events = new DomainEventStream();
        ToolInvocationStarted? started = null;
        await using var subscription = events.Subscribe((item, _) =>
        {
            started = item as ToolInvocationStarted ?? started;
            return Task.CompletedTask;
        });
        var pipeline = CreatePipeline(events, tool, timeProvider, new ToolPresentationLimits
        {
            MaximumActivityDetailCharacters = maximumCharacters,
        });

        var result = await pipeline.InvokeAsync(CreateRequest(tool.Definition.Id));

        Assert.True(result.Succeeded);
        var detail = Assert.IsType<string>(started?.ActivityDetail);
        Assert.InRange(detail.Length, 1, maximumCharacters);
        Assert.Equal(maximumCharacters < 9 ? "..."[..maximumCharacters] : "README.md", detail);
    }

    /// <summary>Adapter-owned timing replaces the outer clock in results, events, and latency telemetry.</summary>
    [Fact]
    public static async Task Pipeline_AuthoritativeAdapterDuration_PropagatesConsistently()
    {
        var timeProvider = new ManualTimeProvider();
        var tool = new AuthoritativeTimedTool(timeProvider);
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var measurements = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Threadsmith.Tools", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "threadsmith.tool.duration", StringComparison.Ordinal))
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, "threadsmith.tool.id", StringComparison.Ordinal)
                    && string.Equals(tag.Value as string, tool.Definition.Id, StringComparison.Ordinal))
                {
                    measurements.Add(measurement);
                }
            }
        });
        listener.Start();
        var pipeline = CreatePipeline(events, tool, timeProvider);

        var result = await pipeline.InvokeAsync(CreateRequest(tool.Definition.Id));

        Assert.Equal(TimeSpan.FromMilliseconds(1200), result.Duration);
        var completed = Assert.IsType<ToolInvocationCompleted>(observed[^1]);
        Assert.Equal(1200, completed.ElapsedMilliseconds);
        Assert.Contains(1200, measurements);
    }

    /// <summary>Dynamic registrations cannot silently publish a tool with unknown activity origin.</summary>
    [Fact]
    public static void ToolRegistry_DynamicRegistration_RejectsUnknownSource()
    {
        var timeProvider = new ManualTimeProvider();
        var registry = new ToolRegistry([]);
        var tool = new TimedTool(timeProvider, shouldFail: false);

        var exception = Assert.Throws<ArgumentException>(() => registry.RegisterOrReplace(
            tool,
            new ToolActivitySource(ToolActivitySourceKind.Unknown)));

        Assert.Equal("source", exception.ParamName);
        Assert.Empty(registry.AllDefinitions);
    }

    /// <summary>Built-in file and process tools expose only their concise display fields.</summary>
    [Fact]
    public static void BuiltInTools_ValidatedInput_ProvidesActivityDetail()
    {
        ITool readTool = new ReadFileTool(TestPromptLoader.Instance, new Threadsmith.Telemetry.SecretOutputSanitizer());
        var readInput = readTool.DeserializeInput("{\"path\":\"src/Program.cs\"}");
        ITool processTool = new RunProcessTool(new UnusedProcessManager(), TestPromptLoader.Instance);
        var processInput = processTool.DeserializeInput("{\"command\":\"dotnet test src/Threadsmith.sln\"}");
        ITool searchTool = new SearchTextTool(TestPromptLoader.Instance);
        var searchInput = searchTool.DeserializeInput(
            "{\"query\":\"resultScope\",\"path\":\"container/source/AI.Inference.Fusion\"}");
        ITool symbolTool = new FindSymbolTool(
            new UnusedSemanticEngineResolver(),
            TestPromptLoader.Instance);
        var symbolInput = symbolTool.DeserializeInput("{\"query\":\"SectorEntityStandardizer\"}");
        ITool referencesTool = new FindReferencesTool(
            new UnusedSemanticEngineResolver(),
            TestPromptLoader.Instance);
        var referencesInput = referencesTool.DeserializeInput("{\"symbolId\":\"T:Demo.IRetriever\"}");
        ITool implementationsTool = new FindImplementationsTool(
            new UnusedSemanticEngineResolver(),
            TestPromptLoader.Instance);
        var implementationsInput = implementationsTool.DeserializeInput("{\"symbolId\":\"T:Demo.IRetriever\"}");

        Assert.Equal("src/Program.cs", readTool.GetActivityDetail(readInput));
        Assert.Equal("dotnet test src/Threadsmith.sln", processTool.GetActivityDetail(processInput));
        Assert.Equal(
            "resultScope in container/source/AI.Inference.Fusion",
            searchTool.GetActivityDetail(searchInput));
        Assert.Equal("SectorEntityStandardizer", symbolTool.GetActivityDetail(symbolInput));
        Assert.Equal("T:Demo.IRetriever", referencesTool.GetActivityDetail(referencesInput));
        Assert.Equal("T:Demo.IRetriever", implementationsTool.GetActivityDetail(implementationsInput));
    }

    /// <summary>File-read activity leaves classification to execution.</summary>
    [Theory]
    [InlineData(201, 200, "docs/implementation-plans/milestones.md")]
    [InlineData(1001, 1000, "docs/implementation-plans/milestones.md")]
    public static void ReadFileTool_ActivityDetail_DoesNotAssumeText(
        int startLine,
        int maximumLines,
        string expected)
    {
        ITool readTool = new ReadFileTool(TestPromptLoader.Instance, new Threadsmith.Telemetry.SecretOutputSanitizer());
        var input = readTool.DeserializeInput(JsonSerializer.Serialize(new ReadFileInput
        {
            Path = "docs/implementation-plans/milestones.md",
            StartLine = startLine,
            MaximumLines = maximumLines,
        }));

        Assert.Equal(expected, readTool.GetActivityDetail(input));
    }

    /// <summary>File-read activity bounds long paths before the file type is known.</summary>
    [Fact]
    public static async Task ReadFileTool_LongPath_PublishesBoundedPath()
    {
        var timeProvider = new ManualTimeProvider();
        ITool readTool = new ReadFileTool(TestPromptLoader.Instance, new Threadsmith.Telemetry.SecretOutputSanitizer());
        await using var events = new DomainEventStream();
        ToolInvocationStarted? started = null;
        await using var subscription = events.Subscribe((item, _) =>
        {
            started = item as ToolInvocationStarted ?? started;
            return Task.CompletedTask;
        });
        var pipeline = CreatePipeline(events, readTool, timeProvider);
        var path = $"docs/{new string('x', 260)}.md";
        var input = new ReadFileInput
        {
            Path = path,
            StartLine = 201,
            MaximumLines = 200,
        };

        _ = await pipeline.InvokeAsync(CreateRequest(
            readTool.Definition.Id,
            JsonSerializer.Serialize(input)));

        var detail = Assert.IsType<string>(started?.ActivityDetail);
        Assert.StartsWith("docs/", detail, StringComparison.Ordinal);
        Assert.EndsWith("...", detail, StringComparison.Ordinal);
        Assert.InRange(detail.Length, 1, 240);
    }

    /// <summary>Process activity masks common whitespace-separated and inline credential switches.</summary>
    [Theory]
    [InlineData("dotnet nuget push package.nupkg --api-key secret", "dotnet nuget push package.nupkg --api-key [REDACTED]")]
    [InlineData("mysql --password secret database", "mysql --password [REDACTED] database")]
    [InlineData("curl --authorization=BearerToken https://example.test", "curl --authorization=[REDACTED] https://example.test")]
    [InlineData("tool /client-secret:\"secret value\" --verbose", "tool /client-secret:[REDACTED] --verbose")]
    public static void RunProcessTool_ActivityDetail_RedactsNamedCredentialValues(
        string command,
        string expected)
    {
        ITool processTool = new RunProcessTool(new UnusedProcessManager(), TestPromptLoader.Instance);
        var input = processTool.DeserializeInput(JsonSerializer.Serialize(new { command }));

        Assert.Equal(expected, processTool.GetActivityDetail(input));
    }

    /// <summary>Synchronous execution failure still emits one authoritative timed completion.</summary>
    [Fact]
    public static async Task Pipeline_ExecutionFailure_ProjectsDurationAndOutcome()
    {
        var timeProvider = new ManualTimeProvider();
        var tool = new TimedTool(timeProvider, shouldFail: true);
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var pipeline = CreatePipeline(events, tool, timeProvider);

        var result = await pipeline.InvokeAsync(CreateRequest(tool.Definition.Id));

        Assert.False(result.Succeeded);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), result.Duration);
        var completed = Assert.IsType<ToolInvocationCompleted>(observed[^1]);
        Assert.Equal(1500, completed.ElapsedMilliseconds);
        Assert.Equal(OperationActivityOutcome.Failed, completed.Outcome);
    }

    /// <summary>Every execution terminal outcome attempts one canonical statistics entry.</summary>
    [Theory]
    [InlineData("success", ToolErrorClassification.None, 1200L)]
    [InlineData("returned-failure", ToolErrorClassification.ExecutionFailure, 1200L)]
    [InlineData("exception", ToolErrorClassification.ExecutionFailure, 1500L)]
    [InlineData("typed-exception", ToolErrorClassification.ExecutionFailure, 321L)]
    [InlineData("timeout", ToolErrorClassification.Timeout, 1500L)]
    [InlineData("deadline-cancellation", ToolErrorClassification.Timeout, 1500L)]
    [InlineData("caller-cancellation", ToolErrorClassification.Cancelled, 1500L)]
    [InlineData("output-limit", ToolErrorClassification.OutputLimitExceeded, 1200L)]
    public static async Task Pipeline_Telemetry_RecordsEachExecutionTerminalOnce(string scenario, ToolErrorClassification classification, long elapsedMilliseconds)
    {
        var clock = new ManualTimeProvider();
        var telemetry = new RecordingTelemetry();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var tool = new AuthoritativeTimedTool(
            clock,
            maximumOutputBytes: scenario == "output-limit" ? 1 : 1024,
            execute: async (_, executionToken) =>
        {
            switch (scenario)
            {
                case "exception":
                    throw new InvalidOperationException("Expected execution failure.");
                case "typed-exception":
                    throw new ToolExecutionException("Expected adapter failure.", ToolErrorClassification.ExecutionFailure, authoritativeElapsedMilliseconds: 321);
                case "timeout":
                    throw new TimeoutException("Expected timeout.");
                case "deadline-cancellation":
                    throw new OperationCanceledException("Expected deadline cancellation.");
                case "caller-cancellation":
                    await cancellation.CancelAsync();
                    throw new OperationCanceledException(executionToken);
                default:
                    return new ToolExecutionEnvelope(
                        new TimedOutput(),
                        [],
                        IsTruncated: false,
                        AuthoritativeElapsedMilliseconds: 1200,
                        Failure: scenario == "returned-failure" ? new(ToolErrorClassification.ExecutionFailure, "Expected failure.") : null);
            }
        });
        await using var events = new DomainEventStream(timeProvider: clock);
        var pipeline = CreatePipeline(events, tool, clock, telemetry: telemetry);
        var request = CreateRequest(tool.Definition.Id.ToUpperInvariant());
        if (scenario == "caller-cancellation")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.InvokeAsync(request, cancellation.Token));
        }
        else
        {
            var result = await pipeline.InvokeAsync(request, cancellation.Token);
            Assert.Equal(classification, result.ErrorClassification);
        }

        var entry = Assert.Single(telemetry.Entries);
        Assert.Equal(tool.Definition.Id, entry.ToolId);
        Assert.Equal(classification == ToolErrorClassification.None ? ToolTelemetryStatus.Succeeded : ToolTelemetryStatus.Failed, entry.Status);
        Assert.Equal(elapsedMilliseconds, entry.ElapsedMilliseconds);
        Assert.Equal(telemetry.Binding, entry.Binding);
        Assert.Equal(clock.GetUtcNow(), entry.CompletedAtUtc);
        Assert.Equal(1, telemetry.Attempts);
    }

    /// <summary>Early rejections count as failed without manufacturing duration measurements.</summary>
    [Theory]
    [InlineData("unknown", ToolErrorClassification.InvalidArguments)]
    [InlineData("validation", ToolErrorClassification.InvalidArguments)]
    [InlineData("policy", ToolErrorClassification.PolicyDenied)]
    [InlineData("approval", ToolErrorClassification.ApprovalDenied)]
    [InlineData("budget", ToolErrorClassification.PolicyDenied)]
    public static async Task Pipeline_Telemetry_EarlyRejectionsHaveUnknownDuration(string scenario, ToolErrorClassification classification)
    {
        var clock = new ManualTimeProvider();
        var tool = new TimedTool(clock, false);
        var telemetry = new RecordingTelemetry();
        await using var events = new DomainEventStream(timeProvider: clock);
        var budget = new ExecutionBudget(new BudgetDimensions(100000, 1, TimeSpan.MaxValue));
        budget.Accrue(new BudgetDimensions(0, 1, TimeSpan.Zero));
        var pipeline = CreatePipeline(
            events,
            tool,
            clock,
            telemetry: telemetry,
            approvalPolicy: new DenyApprovalPolicy(),
            budget: scenario == "budget" ? budget : null);
        var request = CreateRequest(scenario == "unknown" ? "missing_tool" : tool.Definition.Id, scenario == "validation" ? "[]" : "{}");
        request = request with
        {
            Context = request.Context with
            {
                DenyAllTools = scenario == "policy",
                RequireApprovalToolIds = scenario == "approval" ? [tool.Definition.Id] : [],
            },
        };
        var result = await pipeline.InvokeAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(classification, result.ErrorClassification);
        var entry = Assert.Single(telemetry.Entries);
        Assert.Equal(ToolTelemetryStatus.Failed, entry.Status);
        Assert.Null(entry.ElapsedMilliseconds);
    }

    /// <summary>Valid zero remains measured and negative adapter timing becomes unknown.</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1200L)]
    public static async Task Pipeline_Telemetry_PreservesZeroAndNormalizesInvalidTiming(long measurement)
    {
        var clock = new ManualTimeProvider();
        var tool = new AuthoritativeTimedTool(clock, measurement);
        var telemetry = new RecordingTelemetry();
        await using var events = new DomainEventStream(timeProvider: clock);
        var pipeline = CreatePipeline(events, tool, clock, telemetry: telemetry);
        Assert.True((await pipeline.InvokeAsync(CreateRequest(tool.Definition.Id), TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(measurement >= 0 ? (long?)measurement : null, Assert.Single(telemetry.Entries).ElapsedMilliseconds);
    }

    /// <summary>Rejected or broken statistics admission never changes a tool result.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Pipeline_TelemetryFailure_DoesNotChangeToolResult(bool throws)
    {
        var clock = new ManualTimeProvider();
        var tool = new TimedTool(clock, false);
        var telemetry = new RecordingTelemetry { Reject = true, Throws = throws };
        await using var events = new DomainEventStream(timeProvider: clock);
        var pipeline = CreatePipeline(events, tool, clock, telemetry: telemetry);
        Assert.True((await pipeline.InvokeAsync(CreateRequest(tool.Definition.Id), TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(1, telemetry.Attempts);
    }

    /// <summary>Prepared execution retains original database ownership; inspection counts nothing.</summary>
    [Fact]
    public static async Task Pipeline_PreparedTelemetry_RetainsOriginalBindingWithoutCountingPreflight()
    {
        var clock = new ManualTimeProvider();
        var tool = new TimedTool(clock, false);
        var telemetry = new RecordingTelemetry();
        var original = telemetry.Binding;
        await using var events = new DomainEventStream(timeProvider: clock);
        var pipeline = CreatePipeline(events, tool, clock, telemetry: telemetry);
        var preflight = pipeline.PreflightBatch([new(0, "first", CreateRequest(tool.Definition.Id))]);
        Assert.Empty(telemetry.Entries);
        telemetry.Binding = new("repository-b", "Data Source=b.db");
        var results = await pipeline.InvokePreparedBatchAsync(Assert.IsType<ToolBatchPreparation>(preflight.Preparation), TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(results).Result.Succeeded);
        Assert.Equal(original, Assert.Single(telemetry.Entries).Binding);
    }

    /// <summary>Started and queued requests each select one cancelled terminal entry.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "This test owns every task and explicitly cancels/joins the batch.")]
    public static async Task Telemetry_counts_queued_batch_cancellation(bool prepared)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var telemetry = new RecordingTelemetry();
        var original = telemetry.Binding;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tool = new AuthoritativeTimedTool(clock, execute: async (_, executionToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(executionToken);
            return new ToolExecutionEnvelope(new TimedOutput(), [], IsTruncated: false);
        });
        await using var events = new DomainEventStream(timeProvider: clock);
        var pipeline = CreatePipeline(events, tool, clock, telemetry: telemetry, parallelOptions: new() { MaximumConcurrency = 1 });
        ToolBatchRequest[] requests = [new(0, "first", CreateRequest(tool.Definition.Id)), new(1, "second", CreateRequest(tool.Definition.Id))];
        var batch = prepared
            ? pipeline.InvokePreparedBatchAsync(Assert.IsType<ToolBatchPreparation>(pipeline.PreflightBatch(requests).Preparation), cancellation.Token)
            : pipeline.InvokeBatchAsync(requests, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(ct);
            telemetry.Binding = new("repository-b", "Data Source=b.db");
            await cancellation.CancelAsync();
            var failure = await Assert.ThrowsAsync<ToolBatchCancelledException>(() => batch);
            Assert.Equal(2, failure.Results.Count);
            Assert.Equal(2, telemetry.Entries.Count);
            Assert.All(telemetry.Entries, entry =>
            {
                Assert.Equal(ToolTelemetryStatus.Failed, entry.Status);
                Assert.Equal(original, entry.Binding);
            });
            Assert.Single(telemetry.Entries, entry => entry.ElapsedMilliseconds == 1500);
            Assert.Single(telemetry.Entries, entry => entry.ElapsedMilliseconds is null);
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetResult();
            try
            {
                await batch;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static ToolInvocationPipeline CreatePipeline(
        IDomainEventStream events,
        ITool tool,
        TimeProvider timeProvider,
        ToolPresentationLimits? presentationLimits = null,
        RecordingTelemetry? telemetry = null,
        IApprovalPolicy? approvalPolicy = null,
        IBudget? budget = null,
        ToolParallelOptions? parallelOptions = null)
    {
        return new ToolInvocationPipeline(
            new ToolRegistry([tool]),
            new DefaultPolicyEngine(),
            approvalPolicy ?? new AllowApprovalPolicy(),
            events,
            new TestSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance,
            budget: budget,
            timeProvider: timeProvider,
            parallelOptions: parallelOptions,
            presentationLimits: presentationLimits,
            toolTelemetry: telemetry,
            storageBindings: telemetry);
    }

    private static ToolInvocationRequest CreateRequest(
        string toolId,
        string argumentsJson = "{}")
    {
        return new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = toolId,
            ArgumentsJson = argumentsJson,
            Context = new ToolInvocationContext
            {
                RepositoryPath = Environment.CurrentDirectory,
                TrustLevel = RepositoryTrustLevel.UntrustedInspection,
                RequestedBy = "test",
            },
        };
    }

    private sealed class TimedTool(
        ManualTimeProvider timeProvider,
        bool shouldFail,
        string activityDetail = "  README.md\r\n") : Tool<TimedInput, TimedOutput>
    {
        public override ToolDefinition Definition { get; } = new()
        {
            Id = "plan49_timed",
            DisplayName = "Plan 49 timed tool",
            Version = "1",
            Description = "Advances an injected monotonic clock.",
            InputSchema = new ToolSchema(nameof(TimedInput), 1, "{\"type\":\"object\"}"),
            OutputSchema = new ToolSchema(nameof(TimedOutput), 1, "{\"type\":\"object\"}"),
            Timeout = TimeSpan.FromMinutes(1),
            MaximumOutputBytes = 1024,
        };

        public override Task<ToolExecution<TimedOutput>> ExecuteAsync(
            TimedInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(1500));
            if (shouldFail)
            {
                throw new InvalidOperationException("expected failure");
            }

            return Task.FromResult(new ToolExecution<TimedOutput>(new TimedOutput(), []));
        }

        protected override string DescribeActivity(TimedInput input)
        {
            return activityDetail;
        }

        protected override void ValidateInput(TimedInput input)
        {
        }
    }

    private sealed class AuthoritativeTimedTool : ITool
    {
        private readonly ManualTimeProvider _timeProvider;
        private readonly long? _measurement;
        private readonly Func<ToolExecutionContext, CancellationToken, Task<ToolExecutionEnvelope>>? _execute;

        internal AuthoritativeTimedTool(ManualTimeProvider timeProvider, long? measurement = 1200, int maximumOutputBytes = 1024, Func<ToolExecutionContext, CancellationToken, Task<ToolExecutionEnvelope>>? execute = null)
        {
            _timeProvider = timeProvider;
            _measurement = measurement;
            _execute = execute;
            Definition = Definition with { MaximumOutputBytes = maximumOutputBytes };
        }

        public ToolDefinition Definition { get; } = new()
        {
            Id = "plan49_authoritative_timed",
            DisplayName = "Plan 49 authoritative timed tool",
            Version = "1",
            Description = "Returns adapter-owned timing after advancing an outer clock.",
            InputSchema = new ToolSchema(nameof(TimedInput), 1, "{\"type\":\"object\"}"),
            OutputSchema = new ToolSchema(nameof(TimedOutput), 1, "{\"type\":\"object\"}"),
            Timeout = TimeSpan.FromMinutes(1),
            MaximumOutputBytes = 1024,
        };

        public object DeserializeInput(string argumentsJson)
        {
            return new TimedInput();
        }

        public IReadOnlyList<string> GetResourcePaths(object input, ToolInvocationContext context)
        {
            return [];
        }

        public IReadOnlyList<string> GetSecretReferences(object input)
        {
            return [];
        }

        public string? GetExecutable(object input)
        {
            return null;
        }

        public IReadOnlyList<string> GetNetworkHosts(object input)
        {
            return [];
        }

        public Task<ToolExecutionEnvelope> ExecuteAsync(
            object input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            _timeProvider.Advance(TimeSpan.FromMilliseconds(1500));
            return _execute?.Invoke(context, cancellationToken) ?? Task.FromResult(new ToolExecutionEnvelope(
                new TimedOutput(),
                [],
                IsTruncated: false,
                AuthoritativeElapsedMilliseconds: _measurement));
        }
    }

    private sealed record TimedInput;

    private sealed record TimedOutput;

    private sealed class ManualTimeProvider : FakeTimeProvider
    {
        internal ManualTimeProvider()
            : base(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero))
        {
        }
    }

    private sealed class UnusedProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The activity-detail test must not execute a process.");
        }
    }

    private sealed class UnusedSemanticEngineResolver : ISemanticEngineResolver
    {
        public SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId)
        {
            throw new InvalidOperationException("The activity-detail test must not query semantic confidence.");
        }

        public Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(
            WorkspaceId workspaceId,
            string query,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The activity-detail test must not execute semantic search.");
        }

        public Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
            WorkspaceId workspaceId,
            string symbolId,
            bool allowTextFallback = false,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The activity-detail test must not execute semantic search.");
        }

        public Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> projectPaths,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The activity-detail test must not query diagnostics.");
        }

        public Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
            WorkspaceId workspaceId,
            string symbolId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The activity-detail test must not execute semantic search.");
        }
    }

    private sealed class AllowApprovalPolicy : IApprovalPolicy
    {
        public Task<bool> IsApprovedAsync(
            string action,
            ApprovalLevel requiredLevel,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingTelemetry : IToolTelemetryProducer, IRepositoryStorageBindingSource
    {
        private int _attempts;

        internal ConcurrentQueue<ToolCompletionEntry> Entries { get; } = new();

        internal RepositoryStorageBinding Binding { get; set; } = new("repository-a", "Data Source=a.db");

        internal bool Reject { get; init; }

        internal bool Throws { get; init; }

        internal int Attempts => Volatile.Read(ref _attempts);

        public RepositoryStorageBinding CaptureBinding(string? repositoryIdentity = null)
        {
            return Binding;
        }

        public bool TryRecord(ToolCompletionEntry entry)
        {
            Interlocked.Increment(ref _attempts);
            if (Throws)
            {
                throw new InvalidOperationException("Expected telemetry failure.");
            }

            if (Reject)
            {
                return false;
            }

            Entries.Enqueue(entry);
            return true;
        }
    }

    private sealed class TestSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            return value;
        }
    }
}
