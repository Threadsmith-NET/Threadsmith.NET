namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises explicit memory admission and actual conversation dispatch boundaries.</summary>
public static class MemoriesToolTests
{
    /// <summary>Canonical provider definitions retain closed memory-type admission and four actions.</summary>
    [Fact]
    public static void Schema_IsClosedAndNullable()
    {
        var memory = new MemoryService();
        var tool = new MemoriesTool(memory, new Options(), TestPromptLoader.Instance);
        var definitions = ModelToolCanonicalizer.Canonicalize(
        [
            new ModelToolDefinition
            {
                Name = tool.Definition.Id,
                Description = tool.Definition.Description,
                ArgumentsJsonSchema = tool.Definition.InputSchema.JsonSchema,
            },
        ]);
        var definition = Assert.Single(definitions);
        using var schema = JsonDocument.Parse(definition.ArgumentsJsonSchema);
        var fields = schema.RootElement.GetProperty("properties");
        Assert.Equal(["action", "id", "memoryType", "text"], fields.EnumerateObject().Select(field => field.Name).OrderBy(name => name));
        Assert.Equal(["add", "update", "remove", "list"], fields.GetProperty("action").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("null", fields.GetProperty("id").GetProperty("type").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("null", fields.GetProperty("text").GetProperty("type").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ["situational", "standingPreference"],
            fields.GetProperty("memoryType").GetProperty("enum").EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()).OrderBy(value => value));
        Assert.Equal(ToolSideEffect.WritesRepositoryMemory, tool.Definition.SideEffect);
        Assert.Equal(ToolConcurrencyMode.SerializedPerResource, tool.Definition.Scheduling.ConcurrencyMode);
    }

    /// <summary>Malformed action combinations fail before reaching the memory service.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"action\":\"validate\"}")]
    [InlineData("{\"action\":\"list\",\"text\":\"x\"}")]
    [InlineData("{\"action\":\"add\",\"text\":\"x\",\"origin\":\"manual\"}")]
    [InlineData("{\"action\":\"update\",\"text\":\"x\"}")]
    [InlineData("{\"action\":\"remove\",\"id\":\"not-an-id\"}")]
    public static void InvalidArguments_AreRejected(string json)
    {
        var tool = new MemoriesTool(new MemoryService(), new Options(), TestPromptLoader.Instance);
        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(json));
    }

    /// <summary>Optional nulls and omitted fields resolve identically and never generate list embeddings.</summary>
    [Theory]
    [InlineData("{\"action\":\"list\"}")]
    [InlineData("{\"action\":\"list\",\"id\":null,\"text\":null}")]
    public static async Task List_IsExplicitAndBounded(string json)
    {
        var memory = new MemoryService();
        var tool = new MemoriesTool(memory, new Options(), TestPromptLoader.Instance);
        var invocation = Invocation(Path.GetTempPath());
        var output = await tool.ExecuteAsync((MemoriesInput)tool.DeserializeInput(json), new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), invocation));
        Assert.Single(output.Value.Entries);
        Assert.Equal(0, memory.Receipts);
        Assert.Equal(RepositoryMemoryOrigin.Model, memory.LastOperation?.Origin);
        Assert.Equal("list", ((ITool)tool).GetActivityDetail(new MemoriesInput("list")));
    }

    /// <summary>Adds and promotions use the committed count and strict configured threshold; ordinary edits do not warn.</summary>
    [Theory]
    [InlineData("add", "standingPreference", 4, 3, true)]
    [InlineData("add", "situational", 4, 3, true)]
    [InlineData("add", null, 3, 3, false)]
    [InlineData("add", "standingPreference", 1, 0, true)]
    [InlineData("add", "standingPreference", null, 3, false)]
    [InlineData("update", "standingPreference", 4, 3, true)]
    [InlineData("update", "standingPreference", 4, 4, false)]
    [InlineData("update", null, 4, 3, false)]
    [InlineData("update", "situational", 4, 3, false)]
    public static async Task Memory_writes_project_the_committed_warning(string action, string? type, int? count, int threshold, bool expected)
    {
        var memory = new MemoryService { StandingPreferenceCount = count };
        var tool = new MemoriesTool(memory, new Options { StandingPreferenceWarningThreshold = threshold }, TestPromptLoader.Instance);
        var invocation = Invocation(Path.GetTempPath());
        var input = new MemoriesInput(action, Id: action == "update" ? Guid.NewGuid().ToString("D") : null, Text: "Keep pull requests small", MemoryType: type);

        var output = await tool.ExecuteAsync(input, new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), invocation));

        var expectedType = type switch
        {
            "standingPreference" => RepositoryMemoryType.StandingPreference,
            "situational" => RepositoryMemoryType.Situational,
            _ => (RepositoryMemoryType?)null,
        };
        Assert.Equal(expectedType, memory.LastOperation?.MemoryType);
        Assert.Equal(
            expected ? $"You now have {count} preference memories. You may want to consider adding some of these to AGENTS.md for the repo." : null,
            output.Value.StandingPreferenceWarning);
        Assert.Throws<ToolArgumentValidationException>(() =>
            tool.DeserializeInput("{\"action\":\"add\",\"text\":\"x\",\"memoryType\":\"permanent\"}"));
    }

    /// <summary>The pipeline sanitizes live requested text while the durable memory result names its operation and entries.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Pipeline_SanitizesRequestedMemoryTextAndProjectsMemoryOutput(bool fail)
    {
        var memory = new MemoryService { FailOperation = fail };
        var tool = new MemoriesTool(memory, new Options(), TestPromptLoader.Instance);
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var pipeline = new ToolInvocationPipeline(
            new ToolRegistry([tool]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance);
        const string secret = "sk-abcdefghijklmnop";

        var result = await pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "memories",
            ArgumentsJson = JsonSerializer.Serialize(new { action = "add", text = "Remember " + secret }),
            Context = Invocation(Path.GetTempPath()),
        });

        var started = Assert.IsType<ToolInvocationStarted>(observed[0]);
        var completed = Assert.IsType<ToolInvocationCompleted>(observed[^1]);
        var detail = Assert.IsType<string>(started.TransientActivityDetail);
        Assert.Contains("Remember", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, detail, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Remember", DomainEventJson.Serialize(started), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, DomainEventJson.Serialize(completed), StringComparison.Ordinal);
        Assert.Equal(!fail, result.Succeeded);
        Assert.Equal(!fail, completed.Succeeded);
        if (!fail)
        {
            var output = Assert.IsType<string>(completed.ResultJson);
            Assert.Contains("\"Action\":\"add\"", output, StringComparison.Ordinal);
            Assert.Contains("\"Entries\"", output, StringComparison.Ordinal);
        }
    }

    /// <summary>Repository rebind uses fallback layering and rejects stale repository identities.</summary>
    [Fact]
    public static void Options_RebindAndRejectInvalidBounds()
    {
        var first = Path.Combine(Path.GetTempPath(), "memory-first");
        var next = Path.Combine(Path.GetTempPath(), "memory-next");
        var fallback = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:MaxNumberOfRepoMemories"] = "7",
            ["tools:config:memories:SemanticMinimum"] = "0.35",
            ["tools:config:memories:RerankerEnabled"] = "true",
            ["tools:config:memories:RerankerCandidateLimit"] = "6",
            ["tools:config:memories:RerankerMinimumScore"] = "-2.5",
            ["tools:config:memories:standingPreferenceWarningThreshold"] = "5",
        }).Build();
        var initial = new ConfigurationBuilder().AddConfiguration(fallback).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:MaxRepoMemoriesInContext"] = "2",
            ["tools:config:memories:SemanticMinimum"] = "0.65",
            ["tools:config:memories:RerankerEnabled"] = "false",
            ["tools:config:memories:RerankerCandidateLimit"] = "4",
        }).Build();
        var source = new RepositoryMemoryConfiguration(initial, fallback, first);
        Assert.Equal(2, source.Capture(RepositoryIdentity.Create(first)).MaxRepoMemoriesInContext);
        Assert.Equal(0.65, source.Capture(RepositoryIdentity.Create(first)).SemanticMinimum);
        Assert.False(source.Capture(RepositoryIdentity.Create(first)).RerankerEnabled);
        Assert.Equal(4, source.Capture(RepositoryIdentity.Create(first)).RerankerCandidateLimit);
        Assert.Equal(-2.5, source.Capture(RepositoryIdentity.Create(first)).RerankerMinimumScore);
        Assert.Equal(5, source.Capture(RepositoryIdentity.Create(first)).StandingPreferenceWarningThreshold);
        source.BindRepository(next, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:RerankerEnabled"] = "false",
            ["tools:config:memories:RerankerCandidateLimit"] = "3",
            ["tools:config:memories:RerankerMinimumScore"] = "0.25",
        }).Build());
        Assert.False(source.Capture(RepositoryIdentity.Create(next)).RerankerEnabled);
        Assert.Equal(3, source.Capture(RepositoryIdentity.Create(next)).RerankerCandidateLimit);
        Assert.Equal(0.25, source.Capture(RepositoryIdentity.Create(next)).RerankerMinimumScore);
        using var explicitNull = new MemoryStream(Encoding.UTF8.GetBytes("""
            { "tools": { "config": { "memories": { "RerankerMinimumScore": null } } } }
            """));
        source.BindRepository(next, new ConfigurationBuilder().AddJsonStream(explicitNull).Build());
        Assert.Null(source.Capture(RepositoryIdentity.Create(next)).RerankerMinimumScore);
        source.BindRepository(next, new ConfigurationBuilder().Build());
        Assert.Equal(7, source.Capture(RepositoryIdentity.Create(next)).MaxNumberOfRepoMemories);
        Assert.Equal(3, source.Capture(RepositoryIdentity.Create(next)).MaxRepoMemoriesInContext);
        Assert.Equal(0.35, source.Capture(RepositoryIdentity.Create(next)).SemanticMinimum);
        Assert.True(source.Capture(RepositoryIdentity.Create(next)).RerankerEnabled);
        Assert.Equal(6, source.Capture(RepositoryIdentity.Create(next)).RerankerCandidateLimit);
        Assert.Equal(-2.5, source.Capture(RepositoryIdentity.Create(next)).RerankerMinimumScore);
        Assert.Equal(5, source.Capture(RepositoryIdentity.Create(next)).StandingPreferenceWarningThreshold);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.BindRepository(next, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["tools:config:memories:SemanticMinimum"] = "1.1" }).Build()));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.BindRepository(next, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["tools:config:memories:RerankerCandidateLimit"] = "0" }).Build()));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.BindRepository(next, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["tools:config:memories:RerankerMinimumScore"] = "NaN" }).Build()));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.BindRepository(next, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["tools:config:memories:standingPreferenceWarningThreshold"] = "-1" }).Build()));
        Assert.Equal(0.35, source.Capture(RepositoryIdentity.Create(next)).SemanticMinimum);
        Assert.True(source.Capture(RepositoryIdentity.Create(next)).RerankerEnabled);
        Assert.Throws<InvalidOperationException>(() => source.Capture(RepositoryIdentity.Create(first)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepositoryMemoryConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["tools:config:memories:MaxNumberOfRepoMemories"] = "0" }).Build(), fallback, first));
    }

    /// <summary>Retries/tool rounds account one run; deleted context resets provider history; denial withholds injection.</summary>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public static async Task Conversation_AccountsSubmittedMemoryAndInvalidatesDeletedContext(bool remove, bool denied, bool accountingFails, bool explicitList)
    {
        var repository = Path.Combine(Path.GetTempPath(), "memory-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        try
        {
            await using var events = new DomainEventStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sanitizer = new SecretOutputSanitizer();
            var memory = new MemoryService { FailAccounting = accountingFails, SuppressRetrieval = explicitList };
            var options = new Options();
            var tool = new MemoriesTool(memory, options, TestPromptLoader.Instance);
            var registry = new ToolRegistry([tool, new DateTimeTool(TestPromptLoader.Instance)]);
            var budget = new ExecutionBudget(new BudgetDimensions(100000, 30, TimeSpan.FromMinutes(1)));
            var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, budget);
            var evidence = new EvidenceStore(events, sanitizer);
            var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance, repositoryMemoryRetriever: new Retriever(memory));
            var model = new RecordingProvider(remove, memory.Entry.Id, explicitList);
            var application = new SessionApplication(
                events,
                model,
                budget,
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(Invocation(repository) with { DeniedToolIds = denied ? ["memories"] : [] }),
                assembler,
                evidence,
                registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance,
                repositoryMemories: memory,
                repositoryMemoryOptions: options);
            var dispatcher = new CommandDispatcher([application]);
            var session = await dispatcher.DispatchAsync(new CreateSessionCommand("memory runtime"), timeout.Token);
            var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, "Which release branch convention applies?"), timeout.Token);
            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), timeout.Token));
            Assert.Equal(2, model.Requests.Count);
            Assert.True(model.Requests[1].ContainsSensitiveData);
            Assert.True(model.Requests[1].SelectionConstraints.ContainsSensitiveData);
            if (explicitList)
            {
                Assert.False(model.Requests[0].ContainsSensitiveData);
            }

            var inspection = assembler.GetInspection(run);
            if (!denied && !remove && !explicitList)
            {
                Assert.Equal(accountingFails ? "failed" : "recorded", inspection?.RepositoryMemoryDispatch?.Outcome);
            }

            Assert.Equal(denied || explicitList ? 0 : 1, memory.Receipts);
            Assert.Equal(!denied && !explicitList, model.Requests[0].Messages.Any(message => message.SectionId == "repository-memory"));
            Assert.Equal(!denied && !remove && !explicitList, model.Requests[1].Messages.Any(message => message.SectionId == "repository-memory"));
            Assert.Equal(!denied, model.Requests[0].Tools.Any(definition => definition.Name == "memories"));
            if (remove)
            {
                Assert.True(model.Requests[1].HistoryRewriteGeneration > model.Requests[0].HistoryRewriteGeneration);
            }
            else
            {
                Assert.Equal(model.Requests[0].HistoryRewriteGeneration, model.Requests[1].HistoryRewriteGeneration);
            }
        }
        finally
        {
            if (Path.GetDirectoryName(repository) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !Path.GetFileName(repository).StartsWith("memory-runtime-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected test cleanup path.");
            }

            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The real transport keeps the memory schema closed and observes only admitted requests.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public static async Task Provider_ObservesTransportAndPreservesMemorySchema(bool rejectBeforeTransport, bool cancelled, bool sensitiveProhibited)
    {
        using var handler = new RequestHandler();
        using var client = new HttpClient(handler);
        var id = ModelProfileId.New();
        var catalog = new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration
            {
                DefaultProviderId = "memory-tests",
                DefaultModelId = id,
                Providers =
                [
                    new OpenAiCompatibleProviderConfiguration
                    {
                        Id = "memory-tests",
                        Name = "Memory tests",
                        BaseUri = new Uri("https://models.example/v1/"),
                        Models =
                        [
                            new OpenAiCompatibleModelConfiguration
                            {
                                Id = id,
                                Name = "Memory tests",
                                ModelId = "memory-test",
                                ContextWindow = 32000,
                                MaximumOutputTokens = 4000,
                                SensitiveDataPolicy = ModelSensitiveDataPolicy.Prohibited,
                                Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
                                SupportedReasoningLevels = [ReasoningLevel.None],
                            },
                        ],
                    },
                ],
            },
            new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]));
        var provider = new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>(null));
        var tool = new MemoriesTool(new MemoryService(), new Options(), TestPromptLoader.Instance);
        var submissions = 0;
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "Test memory",
            ContainsSensitiveData = sensitiveProhibited,
            MaximumOutputTokens = rejectBeforeTransport ? 5000 : 4000,
            SubmissionObserver = () => submissions++,
            Tools = [new ModelToolDefinition { Name = tool.Definition.Id, Description = tool.Definition.Description, ArgumentsJsonSchema = tool.Definition.InputSchema.JsonSchema }],
        };
        using var cancellation = new CancellationTokenSource();
        if (cancelled)
        {
            await cancellation.CancelAsync();
        }

        async Task ConsumeAsync()
        {
            await foreach (var chunk in provider.StreamAsync(request, cancellation.Token))
            {
                Assert.Null(chunk.Output);
            }
        }

        if (rejectBeforeTransport || cancelled || sensitiveProhibited)
        {
            Assert.NotNull(await Record.ExceptionAsync(ConsumeAsync));
            Assert.Equal(0, submissions);
            Assert.Null(handler.Body);
            return;
        }

        await ConsumeAsync();
        Assert.Equal(1, submissions);
        Assert.NotNull(handler.Body);
        using var body = JsonDocument.Parse(handler.Body);
        var function = Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray()).GetProperty("function");
        var schema = function.GetProperty("parameters");
        Assert.Equal(4, schema.GetProperty("properties").EnumerateObject().Count());
        Assert.Equal(4, schema.GetProperty("properties").GetProperty("action").GetProperty("enum").GetArrayLength());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.DoesNotContain("SubmissionObserver", handler.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MemorySubmission", handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    private static ToolInvocationContext Invocation(string repository) => new() { RepositoryPath = repository, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" };

    private sealed class RequestHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class Options : IRepositoryMemoryOptionsProvider
    {
        public int StandingPreferenceWarningThreshold { get; init; } = 3;

        public RepositoryMemoryOptions Capture(string repositoryIdentity) => new()
        {
            StandingPreferenceWarningThreshold = StandingPreferenceWarningThreshold,
        };
    }

    private sealed class MemoryService : IManagedRepositoryMemoryService
    {
        private readonly HashSet<RunId> _runs = [];

        public RepositoryMemoryEntry Entry { get; } = new()
        {
            Id = RepositoryMemoryId.New(), RepositoryIdentity = "test", Text = "Use release branches for deployment", ContentHash = "test", Origin = RepositoryMemoryOrigin.Manual,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Sensitivity = ConversationSensitivity.Sensitive,
        };

        public bool Removed { get; private set; }

        public bool FailAccounting { get; init; }

        public bool FailOperation { get; init; }

        public bool SuppressRetrieval { get; init; }

        public int? StandingPreferenceCount { get; init; }

        public int Receipts => _runs.Count;

        public RepositoryMemoryOperationRequest? LastOperation { get; private set; }

        public Task<RepositoryMemoryOperationResult> ExecuteAsync(RepositoryMemoryOperationRequest request, CancellationToken cancellationToken = default)
        {
            LastOperation = request;
            if (FailOperation)
            {
                return Task.FromException<RepositoryMemoryOperationResult>(new IOException("simulated memory operation failure"));
            }

            Removed = request.Action == "remove" || Removed;
            return Task.FromResult(new RepositoryMemoryOperationResult(Removed ? "removed" : "listed", request.Id, null, Removed ? [] : [Entry], [])
            {
                StandingPreferenceCount = StandingPreferenceCount,
            });
        }

        public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
            => Task.FromResult(new RepositoryMemoryReadSnapshot(repositoryIdentity, Removed ? 2 : 1, Removed ? [] : [Entry], [], []));

        public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(string repositoryIdentity, RepositoryMemoryOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RepositoryMemoryId>>([]);

        public Task RecordInclusionsAsync(string repositoryIdentity, RunId runId, IReadOnlyList<RepositoryMemoryInclusion> inclusions, CancellationToken cancellationToken = default)
        {
            _runs.Add(runId);
            return FailAccounting ? Task.FromException(new IOException("simulated receipt failure")) : Task.CompletedTask;
        }
    }

    private sealed class Retriever : IHybridRepositoryMemoryRetriever
    {
        private readonly MemoryService _memory;

        public Retriever(MemoryService memory) => _memory = memory;

        public Task<RepositoryMemoryRetrievalResult> RetrieveAsync(RepositoryMemoryRetrievalRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RepositoryMemoryRetrievalResult(_memory.Removed || _memory.SuppressRetrieval ? [] : [new RepositoryMemoryRetrievalCandidate(_memory.Entry, 1, 1, null, null)], []));
    }

    private sealed class RecordingProvider : IModelProvider
    {
        private readonly bool _remove;
        private readonly bool _explicitList;
        private readonly RepositoryMemoryId _id;

        public RecordingProvider(bool remove, RepositoryMemoryId id, bool explicitList = false)
        {
            _remove = remove;
            _explicitList = explicitList;
            _id = id;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1)
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput(_remove || _explicitList ? "memories" : "datetime", _remove ? JsonSerializer.Serialize(new { action = "remove", id = _id.Value.ToString("D") }) : _explicitList ? "{\"action\":\"list\"}" : "{}") };
            }
            else
            {
                yield return new ModelChunk { Text = "Done." };
            }
        }
    }
}
