namespace Threadsmith.Milestone3.Tests;

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

        string detail = Assert.IsType<string>(started?.ActivityDetail);
        Assert.InRange(detail.Length, 1, 240);
        Assert.EndsWith("...", detail, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', detail);
        Assert.DoesNotContain('\n', detail);
        _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetByteCount(detail);
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
        ITool readTool = new ReadFileTool();
        object readInput = readTool.DeserializeInput("{\"path\":\"src/Program.cs\"}");
        ITool processTool = new RunProcessTool(new UnusedProcessManager());
        object processInput = processTool.DeserializeInput("{\"command\":\"dotnet test src/Threadsmith.sln\"}");
        ITool symbolTool = new FindSymbolTool(new UnusedSemanticEngineResolver());
        object symbolInput = symbolTool.DeserializeInput("{\"query\":\"SectorEntityStandardizer\"}");
        ITool referencesTool = new FindReferencesTool(new UnusedSemanticEngineResolver());
        object referencesInput = referencesTool.DeserializeInput("{\"symbolId\":\"T:Demo.IRetriever\"}");
        ITool implementationsTool = new FindImplementationsTool(new UnusedSemanticEngineResolver());
        object implementationsInput = implementationsTool.DeserializeInput("{\"symbolId\":\"T:Demo.IRetriever\"}");

        Assert.Equal("lines 1-200, src/Program.cs", readTool.GetActivityDetail(readInput));
        Assert.Equal("dotnet test src/Threadsmith.sln", processTool.GetActivityDetail(processInput));
        Assert.Equal("SectorEntityStandardizer", symbolTool.GetActivityDetail(symbolInput));
        Assert.Equal("T:Demo.IRetriever", referencesTool.GetActivityDetail(referencesInput));
        Assert.Equal("T:Demo.IRetriever", implementationsTool.GetActivityDetail(implementationsInput));
    }

    /// <summary>File-read activity identifies the exact requested page.</summary>
    [Theory]
    [InlineData(201, 200, "lines 201-400, docs/implementation-plans/milestones.md")]
    [InlineData(1001, 1000, "lines 1001-2000, docs/implementation-plans/milestones.md")]
    public static void ReadFileTool_ActivityDetail_IncludesRequestedLineRange(
        int startLine,
        int maximumLines,
        string expected)
    {
        ITool readTool = new ReadFileTool();
        object input = readTool.DeserializeInput(JsonSerializer.Serialize(new ReadFileInput
        {
            Path = "docs/implementation-plans/milestones.md",
            StartLine = startLine,
            MaximumLines = maximumLines,
        }));

        Assert.Equal(expected, readTool.GetActivityDetail(input));
    }

    /// <summary>File-read activity preserves the requested page when a long path is truncated.</summary>
    [Fact]
    public static async Task ReadFileTool_LongPath_PublishesLineRangeBeforeTruncatedPath()
    {
        var timeProvider = new ManualTimeProvider();
        ITool readTool = new ReadFileTool();
        await using var events = new DomainEventStream();
        ToolInvocationStarted? started = null;
        await using var subscription = events.Subscribe((item, _) =>
        {
            started = item as ToolInvocationStarted ?? started;
            return Task.CompletedTask;
        });
        var pipeline = CreatePipeline(events, readTool, timeProvider);
        string path = $"docs/{new string('x', 260)}.md";
        var input = new ReadFileInput
        {
            Path = path,
            StartLine = 201,
            MaximumLines = 200,
        };

        _ = await pipeline.InvokeAsync(CreateRequest(
            readTool.Definition.Id,
            JsonSerializer.Serialize(input)));

        string detail = Assert.IsType<string>(started?.ActivityDetail);
        Assert.StartsWith("lines 201-400, docs/", detail, StringComparison.Ordinal);
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
        ITool processTool = new RunProcessTool(new UnusedProcessManager());
        object input = processTool.DeserializeInput(JsonSerializer.Serialize(new { command }));

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

    private static ToolInvocationPipeline CreatePipeline(
        IDomainEventStream events,
        ITool tool,
        TimeProvider timeProvider)
    {
        return new ToolInvocationPipeline(
            new ToolRegistry([tool]),
            new DefaultPolicyEngine(),
            new AllowApprovalPolicy(),
            events,
            new TestSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance,
            timeProvider: timeProvider);
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

    private sealed class AuthoritativeTimedTool(ManualTimeProvider timeProvider) : ITool
    {
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
            timeProvider.Advance(TimeSpan.FromMilliseconds(1500));
            return Task.FromResult(new ToolExecutionEnvelope(
                new TimedOutput(),
                [],
                IsTruncated: false,
                AuthoritativeElapsedMilliseconds: 1200));
        }
    }

    private sealed record TimedInput;

    private sealed record TimedOutput;

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        internal void Advance(TimeSpan duration)
        {
            _timestamp += duration.Ticks;
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

    private sealed class TestSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            return value;
        }
    }
}
