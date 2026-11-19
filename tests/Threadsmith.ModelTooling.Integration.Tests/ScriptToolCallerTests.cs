namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises caller-owned script admission, evidence, and deadlines.</summary>
public sealed class ScriptToolCallerTests
{
    /// <summary>Only script-eligible tools are marked in the caller's model request when scripting is available.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ModelToolDescriptionsReflectCallerScriptAccess(bool scripting)
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new ScriptFixture();
        await using var events = new DomainEventStream();
        var prompts = TestPromptLoader.Instance;
        var read = new ReadFileTool(prompts, fixture.Sanitizer);
        List<ITool> tools = [read, new RecordingSearchTool()];
        await using var engine = fixture.CreateEngine(new ScriptProcessProbe(null, "{}", string.Empty));
        if (scripting)
        {
            tools.Add(new CSharpScriptTool(engine, prompts));
        }

        var model = new SequenceModelProvider(new ModelChunk { Text = "Done.", FinishReason = ModelFinishReason.Stop });
        var application = fixture.CreateApplication(events, fixture.CreatePipeline(events, tools, unlimited: true), model);
        try
        {
            var session = await application.HandleAsync(new CreateSessionCommand("script markers"), token);
            await RunAsync(application, session, "Inspect tools", token);
            var advertised = Assert.Single(model.Requests).Tools;
            foreach (var tool in tools)
            {
                var expected = tool.Definition.Description + (scripting && tool == read
                    ? " Callable from C# scripts."
                    : string.Empty);
                Assert.Equal(expected, Assert.Single(advertised, item => item.Name == tool.Definition.Id).Description);
            }
        }
        finally
        {
            await application.StopRunsAsync(CancellationToken.None);
        }
    }

    /// <summary>Later observations invalidate a script finding based on earlier bytes.</summary>
    [Fact]
    public static async Task LaterFileReadMarksScriptFindingStale()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new ScriptFixture();
        await using var events = new DomainEventStream();
        var evidence = new EvidenceStore(events, fixture.Sanitizer);
        const string original = "class Foo\n{\n    int value = 1;\n}\n";
        var file = Path.Combine(fixture.Root, "Foo.cs");
        await File.WriteAllTextAsync(file, original, cancellationToken);
        const string arguments = "{\"path\":\"Foo.cs\",\"startLine\":2,\"maximumLines\":2}";
        var process = new ScriptProcessProbe("read_file", arguments, "Foo.cs:L2-L3: value is 1");
        await using var engine = fixture.CreateEngine(process);
        var pipeline = fixture.CreatePipeline(
            events,
            [new CSharpScriptTool(engine, TestPromptLoader.Instance), new ReadFileTool(TestPromptLoader.Instance, fixture.Sanitizer)],
            unlimited: true);
        var model = new SequenceModelProvider(
            ToolCall("csharp_script", JsonSerializer.Serialize(new
            {
                code = "return await Tools.InvokeAsync(\"read_file\", " + JsonSerializer.Serialize(arguments) + ");",
                kind = "statement",
            })),
            new ModelChunk { Text = "Finding retained.", FinishReason = ModelFinishReason.Stop },
            ToolCall("read_file", arguments),
            new ModelChunk { Text = "New file observed.", FinishReason = ModelFinishReason.Stop });
        var application = fixture.CreateApplication(events, pipeline, model, evidence);
        try
        {
            var session = await application.HandleAsync(new CreateSessionCommand("script evidence"), cancellationToken);
            await RunAsync(application, session, "Inspect Foo", cancellationToken);

            var finding = Assert.Single(evidence.Snapshot(session), item => item.Provenance.Source == "tool:csharp_script");
            Assert.False(finding.IsStale);
            var dependency = Assert.Single(finding.FileDependencies, item => item.Path == "Foo.cs");
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original))), dependency.Sha256);
            Assert.Equal(new SourceRange(2, 1, 3, 1), dependency.Range);
            Assert.True(process.NestedReply!.Succeeded, process.NestedReply.Error);
            var read = JsonSerializer.Deserialize<ReadFileOutput>(process.NestedReply.ResultJson!)!;
            Assert.Equal(new[] { "{", "    int value = 1;" }, read.Lines);
            Assert.DoesNotContain(evidence.Snapshot(session), item => item.Provenance.Source == "tool:read_file");

            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await File.WriteAllTextAsync(file, original.Replace("value = 1", "value = 2", StringComparison.Ordinal), cancellationToken);
            await RunAsync(application, session, "Read the changed Foo", cancellationToken);

            var stale = evidence.Find(session, finding.EvidenceId)!;
            Assert.Equal(finding.EvidenceId, stale.EvidenceId);
            Assert.True(stale.IsStale, DescribeRequests(model));
            Assert.Equal(dependency, Assert.Single(stale.FileDependencies, item => item.Path == "Foo.cs"));
            var later = Assert.Single(evidence.Snapshot(session), item => item.Provenance.Source == "tool:read_file");
            Assert.NotEqual(dependency.Sha256, Assert.Single(later.FileDependencies).Sha256);
            var delayed = finding with { EvidenceId = EvidenceId.New(), CollectedAt = later.CollectedAt.AddTicks(1) };
            await evidence.AddAsync(delayed, cancellationToken);
            Assert.True(evidence.Find(session, delayed.EvidenceId)!.IsStale);
            Assert.False(evidence.Find(session, later.EvidenceId)!.IsStale);
            Assert.Equal(4, model.Requests.Count);
        }
        finally
        {
            await application.StopRunsAsync(CancellationToken.None);
        }
    }

    /// <summary>Scripts can read the documented fields from a nonempty semantic lookup.</summary>
    [Fact]
    public static async Task NestedSymbolLookupReturnsDocumentedFields()
    {
        using var fixture = new ScriptFixture();
        await using var events = new DomainEventStream();
        var process = new ScriptProcessProbe("find_symbol", "{\"query\":\"Widget\"}");
        await using var engine = fixture.CreateEngine(process);
        var resolver = new TestSemanticResolver(fixture.Workspace);
        var pipeline = fixture.CreatePipeline(events, [new CSharpScriptTool(engine, TestPromptLoader.Instance), new FindSymbolTool(resolver, TestPromptLoader.Instance)], unlimited: true);
        var session = SessionId.New();
        var run = RunId.New();
        var snapshots = new ConversationToolSnapshotStore();
        var snapshot = snapshots.Capture(session, run, pipeline.Registry.GetRegistrations(session, run));
        var request = new ToolInvocationRequest
        {
            SessionId = session,
            RunId = run,
            ToolId = "csharp_script",
            ArgumentsJson = "{\"code\":\"42\"}",
            Context = fixture.Invocation with { ModelVisibleToolSnapshotId = snapshot, ScriptCaller = new(snapshots, new ToolCallHistory()) },
        };
        var result = await pipeline.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        using var json = JsonDocument.Parse(process.NestedReply!.ResultJson!);
        var match = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("T:Widget", match.GetProperty("Symbol").GetProperty("Id").GetString());
        Assert.Equal((int)SemanticConfidenceLevel.FullSemantic, match.GetProperty("SemanticConfidence").GetInt32());
    }

    /// <summary>An admitted semantic attempt permits a text fallback in the owning turn.</summary>
    [Fact]
    public static async Task EmptyNestedSymbolLookupAllowsTextSearchInOwningTurn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new ScriptFixture();
        await using var events = new DomainEventStream();
        var resolver = new TestSemanticResolver(fixture.Workspace);
        var search = new RecordingSearchTool();
        const string arguments = "{\"query\":\"MissingWidget\"}";
        var process = new ScriptProcessProbe("find_symbol", arguments, "No candidates.");
        await using var engine = fixture.CreateEngine(process);
        var pipeline = fixture.CreatePipeline(
            events,
            [new CSharpScriptTool(engine, TestPromptLoader.Instance), new FindSymbolTool(resolver, TestPromptLoader.Instance), search],
            unlimited: true);
        var model = new SequenceModelProvider(
            ToolCall("csharp_script", JsonSerializer.Serialize(new
            {
                code = "return await Tools.InvokeAsync(\"find_symbol\", " + JsonSerializer.Serialize(arguments) + ");",
                kind = "statement",
            })),
            ToolCall("search", "{\"query\":\"MissingWidget\",\"glob\":\"*.cs\"}"),
            new ModelChunk { Text = "Search complete.", FinishReason = ModelFinishReason.Stop });
        var evidence = new EvidenceStore(events, fixture.Sanitizer);
        var application = fixture.CreateApplication(events, pipeline, model, evidence);
        try
        {
            var session = await application.HandleAsync(new CreateSessionCommand("semantic turn"), cancellationToken);
            await RunAsync(application, session, "Find MissingWidget", cancellationToken);

            Assert.True(process.NestedReply is { Succeeded: true }, process.NestedReply?.Error ?? DescribeRequests(model));
            Assert.Contains("semantic", Assert.Single(evidence.Snapshot(session), item => item.Provenance.Source == "tool:csharp_script").InvalidationKeys);
            Assert.Equal(1, resolver.Calls);
            Assert.True(process.NestedReply!.Succeeded, process.NestedReply.Error);
            Assert.Equal("[]", process.NestedReply.ResultJson);
            Assert.Equal(1, search.Calls);
            Assert.Equal(3, model.Requests.Count);
            Assert.Contains(model.Requests[0].Tools, item => item.Name == "find_symbol");
            Assert.Contains(model.Requests[0].Tools, item => item.Name == "search");
            Assert.Contains(model.Requests[0].Tools, item => item.Name == "csharp_script");
            Assert.Contains(model.Requests[2].Messages, message =>
                message.Role == ModelMessageRole.Tool && message.ToolName == "search"
                && message.Content.Any(part => part.Content.Contains("text-search-ran", StringComparison.Ordinal)));
            Assert.DoesNotContain(model.Requests.SelectMany(request => request.Messages), message =>
                message.Role == ModelMessageRole.Tool && message.ToolName == "search"
                && message.Content.Any(part => part.Content.Contains("Call find_symbol", StringComparison.Ordinal)));
        }
        finally
        {
            await application.StopRunsAsync(CancellationToken.None);
        }
    }

    /// <summary>Each worker gets only its owner's eligible registrations; expired authority cannot start a read.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ScriptCallsOnlyEnabledToolsFromTheCallerSnapshot(bool searchOptedIn)
    {
        using var fixture = new ScriptFixture();
        await using var events = new DomainEventStream();
        var process = new ScriptProcessProbe();
        await using var engine = fixture.CreateEngine(process);
        var pipeline = fixture.CreatePipeline(events, [new CSharpScriptTool(engine, TestPromptLoader.Instance), new ReadFileTool(TestPromptLoader.Instance, fixture.Sanitizer), new RecordingSearchTool(searchOptedIn)], unlimited: true);
        var snapshots = new ConversationToolSnapshotStore();
        var session = SessionId.New();
        var token = TestContext.Current.CancellationToken;
        foreach (var allowRead in new[] { true, false })
        {
            var run = RunId.New();
            var registrations = pipeline.Registry.GetRegistrations(session, run);
            var snapshot = snapshots.Capture(session, run, [.. registrations.Where(item => allowRead || item.Tool.Definition.Id != "read_file")]);
            var context = fixture.Invocation with { ModelVisibleToolSnapshotId = snapshot, ScriptCaller = new(snapshots, new ToolCallHistory()) };
            var result = await pipeline.InvokeAsync(new() { SessionId = session, RunId = run, ToolId = "csharp_script", ArgumentsJson = "{\"code\":\"42\"}", Context = context }, token);
            Assert.True(result.Succeeded, result.Error);
            var handlers = process.Requests[^1].ScriptHandlers!;
            Assert.False(handlers.ContainsKey("csharp_script"));
            Assert.Equal(allowRead, handlers.ContainsKey("read_file"));
            Assert.Equal(searchOptedIn, handlers.ContainsKey("search"));
            snapshots.Release(snapshot);
            if (allowRead)
            {
                var denied = await handlers["read_file"]("{\"path\":\"absent.txt\"}", token);
                Assert.False(denied.Succeeded);
                Assert.Equal("Denied", denied.ErrorClassification);
            }
        }
    }

    /// <summary>The worker receives the remaining host deadline after policy limits apply.</summary>
    [Fact]
    public static async Task ScriptTimeoutDefaultsToThirtySecondsAndHonorsShorterPolicy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new ScriptFixture();
        await using var events = new DomainEventStream();
        var process = new ScriptProcessProbe();
        await using var engine = fixture.CreateEngine(process);
        var probe = new BudgetProbeEngine(engine, fixture.Clock);
        var tool = new CSharpScriptTool(probe, TestPromptLoader.Instance);
        var pipeline = fixture.CreatePipeline(events, [tool], unlimited: false);
        Assert.Equal(TimeSpan.FromSeconds(30), tool.Definition.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), pipeline.Registry.GetRegistration("csharp_script").Tool.Definition.Timeout);
        var context = fixture.Invocation;
        var session = SessionId.New();
        var run = RunId.New();
        ToolInvocationRequest Request(ToolInvocationContext invocation) => new()
        {
            SessionId = session,
            RunId = run,
            ToolId = "csharp_script",
            ArgumentsJson = "{\"code\":\"42\"}",
            Context = invocation,
        };

        var defaults = await pipeline.InvokeAsync(Request(context), cancellationToken);
        Assert.True(defaults.Succeeded, defaults.Error);
        Assert.Equal(TimeSpan.FromSeconds(30), probe.ResolvedTimeout);
        AssertBudget(Assert.Single(process.Requests), 30000);

        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = fixture.Root,
            Fingerprint = "short-script-test-policy",
            AllowedExecutables = ["dotnet"],
            ScriptAssemblies = [.. PermissionConfiguration.ToolsConfigCsharpScriptSettings.DefaultAllowedAssemblies.Split(',')],
            ResourceLimits = new Dictionary<string, int>
            {
                ["tools:config:csharp_script:timeout_ms"] = 5000,
                ["tools:config:csharp_script:max_output_bytes"] = 65536,
                ["tools:config:csharp_script:max_code_characters"] = 65536,
                ["tools:config:csharp_script:max_assembly_name_characters"] = 128,
                ["tools:config:csharp_script:max_assembly_setting_characters"] = 4096,
                ["tools:runtime:byTool:csharp_script:timeoutMilliseconds"] = 5000,
            }.ToImmutableDictionary(),
        };
        var shorterContext = context with { SecurityPolicy = policy };
        var shorter = await pipeline.InvokeAsync(Request(shorterContext), cancellationToken);
        Assert.True(shorter.Succeeded, shorter.Error);
        Assert.Equal(TimeSpan.FromSeconds(5), probe.ResolvedTimeout);
        AssertBudget(process.Requests[1], 5000);

        probe.AdvanceBeforeExecution = TimeSpan.FromSeconds(2);
        var remaining = await pipeline.InvokeAsync(Request(shorterContext), cancellationToken);
        Assert.True(remaining.Succeeded, remaining.Error);
        Assert.Equal(TimeSpan.FromSeconds(5), probe.ResolvedTimeout);
        AssertBudget(process.Requests[2], 3000);
        Assert.Equal(3, process.Requests.Count);
    }

    private static ModelChunk ToolCall(string tool, string arguments) => new()
    {
        Output = new ToolRequestModelOutput(tool, arguments),
        FinishReason = ModelFinishReason.ToolCalls,
    };

    private static async Task RunAsync(SessionApplication application, SessionId session, string request, CancellationToken cancellationToken)
    {
        var run = await application.HandleAsync(new SubmitRequestCommand(session, request), cancellationToken);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(run), cancellationToken));
    }

    private static string DescribeRequests(SequenceModelProvider model) => string.Join("\n", model.Requests.SelectMany(request => request.Messages).SelectMany(message => message.Content).Select(part => part.Content));

    private static void AssertBudget(ProcessExecutionRequest request, int milliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), request.Timeout);
        using var payload = JsonDocument.Parse(request.StandardInput!);
        Assert.Equal(milliseconds, payload.RootElement.GetProperty("TimeoutMilliseconds").GetInt32());
    }

    private sealed class ScriptFixture : IDisposable
    {
        public ScriptFixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(TestTemporaryPath.PhysicalPath(), "threadsmith-script-caller-" + Guid.NewGuid().ToString("N"))).FullName;
            var scratchpad = Directory.CreateDirectory(Path.Combine(Root, ".threadsmith", ".scratchpad")).FullName;
            Invocation = new ToolInvocationContext
            {
                RepositoryPath = Root,
                WorkspaceId = Workspace,
                RequestedBy = "model",
                TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation,
                AllowedExecutables = ["dotnet"],
                Scratchpad = new ScratchpadSessionCapability
                {
                    IsActive = true,
                    RootPath = scratchpad,
                    DisabledReason = ScratchpadDisabledReason.None,
                },
            };
        }

        public string Root { get; }

        public WorkspaceId Workspace { get; } = WorkspaceId.New();

        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);

        public IOutputSanitizer Sanitizer { get; } = new PassthroughSanitizer();

        public ToolInvocationContext Invocation { get; }

        public CSharpScriptEngine CreateEngine(IProcessManager process) => new(
            process,
            new ToolConfig(new ConfigurationBuilder().Build()),
            Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"),
            workerOptions: new() { NumberOfWarmWorkersInPool = 0 },
            timeProvider: Clock);

        public ToolInvocationPipeline CreatePipeline(DomainEventStream events, IReadOnlyList<ITool> tools, bool unlimited)
        {
            var registry = new ToolRegistry(tools, runtimeOptions: unlimited
                ? new ToolRuntimeOptions { Defaults = new() { TimeoutMilliseconds = -1 } }
                : new ToolRuntimeOptions());
            return new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new AllowApprovalPolicy(), events, Sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: Clock);
        }

        public SessionApplication CreateApplication(DomainEventStream events, ToolInvocationPipeline pipeline, IModelProvider model, IEvidenceStore? evidence = null)
        {
            evidence ??= new EvidenceStore(events, Sanitizer);
            var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(Sanitizer), Sanitizer, events, TestPromptLoader.Instance, new ContextAssemblerOptions());
            return new(
            events,
            model,
            UnboundedBudget.Instance,
            Sanitizer,
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Invocation);
            },
            assembler,
            evidenceStore: evidence,
            toolRegistry: pipeline.Registry,
            conversationToolSnapshots: new ConversationToolSnapshotStore(),
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class PassthroughSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value;
    }

    private sealed class ScriptProcessProbe : IProcessManager
    {
        private readonly string? _nestedTool;
        private readonly string _arguments;
        private readonly string _output;

        public ScriptProcessProbe(string? nestedTool = null, string arguments = "{}", string output = "42")
        {
            _nestedTool = nestedTool;
            _arguments = arguments;
            _output = output;
        }

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public ScriptToolReply? NestedReply { get; private set; }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public async Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (_nestedTool is not null)
            {
                Assert.NotNull(request.ScriptHandlers);
                Assert.True(request.ScriptHandlers.TryGetValue(_nestedTool, out var handler));
                var remaining = ScriptWorkerProtocol.ResultBytes;
                NestedReply = ProcessManager.AdmitScriptReply(await handler(_arguments, cancellationToken), ref remaining);
                Assert.True(NestedReply.Succeeded, NestedReply.Error);
            }

            return new ProcessExecutionResult(1, 0, JsonSerializer.Serialize(new CSharpScriptOutput(true, _output, null, 0, false)), string.Empty, false, false, false, TimeSpan.Zero);
        }
    }

    private sealed class BudgetProbeEngine : ICSharpScriptEngine
    {
        private readonly ICSharpScriptEngine _inner;
        private readonly FakeTimeProvider _clock;

        public BudgetProbeEngine(ICSharpScriptEngine inner, FakeTimeProvider clock)
        {
            _inner = inner;
            _clock = clock;
        }

        public TimeSpan? ResolvedTimeout { get; private set; }

        public TimeSpan AdvanceBeforeExecution { get; set; }

        public Task<CSharpScriptOutput> ExecuteAsync(string code, ScriptKind kind, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedTimeout = context.ExecutionTimeout;
            _clock.Advance(AdvanceBeforeExecution);
            return _inner.ExecuteAsync(code, kind, context, cancellationToken);
        }
    }

    private sealed class SequenceModelProvider : IModelProvider
    {
        private readonly Queue<ModelChunk> _responses;

        public SequenceModelProvider(params ModelChunk[] responses)
        {
            _responses = new Queue<ModelChunk>(responses);
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            Assert.NotEmpty(_responses);
            yield return _responses.Dequeue();
        }
    }

    private sealed record SearchInput(string Query, string Glob);

    private sealed class RecordingSearchTool : Tool<SearchInput, string>
    {
        public RecordingSearchTool(bool visibleToScriptingHost = false)
        {
            Definition = ToolDefinitionFactory.Create<SearchInput, string>(
                "search",
                () => "Records a text search after semantic admission.",
                ToolCategory.RepositoryInspection,
                RepositoryTrustLevel.UntrustedInspection,
                ApprovalLevel.None,
                ToolSideEffect.ReadOnly,
                Timeout.InfiniteTimeSpan,
                4096) with { VisibleToScriptingHost = visibleToScriptingHost };
        }

        public override ToolDefinition Definition { get; }

        public int Calls { get; private set; }

        public override Task<ToolExecution<string>> ExecuteAsync(SearchInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("MissingWidget", input.Query);
            Assert.Equal("*.cs", input.Glob);
            Assert.NotNull(context.Invocation.WorkspaceId);
            Calls++;
            return Task.FromResult(new ToolExecution<string>("text-search-ran", []));
        }

        protected override void ValidateInput(SearchInput input) => ArgumentException.ThrowIfNullOrWhiteSpace(input.Query);
    }

    private sealed class TestSemanticResolver : ISemanticEngineResolver
    {
        private readonly WorkspaceId _workspace;

        public TestSemanticResolver(WorkspaceId workspace)
        {
            _workspace = workspace;
        }

        public int Calls { get; private set; }

        public SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId) => workspaceId == _workspace
            ? SemanticConfidenceLevel.FullSemantic : SemanticConfidenceLevel.None;

        public Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(WorkspaceId workspaceId, string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_workspace, workspaceId);
            Calls++;
            return Task.FromResult<IReadOnlyList<SymbolResult>>(query switch
            {
                "MissingWidget" => [],
                "Widget" => [new(new("T:Widget", "Widget", "Class"), new("App", "net10.0", "Widget.cs", new(1, 1, 1, 7), false, false), SemanticConfidenceLevel.FullSemantic)],
                _ => throw new InvalidOperationException("Unexpected symbol query."),
            });
        }

        public Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(WorkspaceId workspaceId, string symbolId, bool allowTextFallback = false, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Only find_symbol is expected.");

        public Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(WorkspaceId workspaceId, IReadOnlyList<string> projectPaths, IReadOnlyList<string> changedFiles, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Only find_symbol is expected.");

        public Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(WorkspaceId workspaceId, string symbolId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Only find_symbol is expected.");
    }
}
