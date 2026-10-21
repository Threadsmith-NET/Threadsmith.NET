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
    /// <summary>Retired scoring blocks cannot activate a removed policy or replace a valid repository snapshot.</summary>
    [Theory]
    [InlineData("Recall")]
    [InlineData("Reconciliation")]
    [InlineData("Intelligence:Recall")]
    [InlineData("Intelligence:Reconciliation")]
    public static void Retired_scoring_configuration_is_rejected(string route)
    {
        var valid = new ConfigurationBuilder().Build();
        var retired = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"tools:config:memories:{route}:Scoring:Mode"] = "Weighted",
        }).Build();
        Assert.Throws<ArgumentException>(() => new RepositoryMemoryConfiguration(retired, valid, Path.GetTempPath()));
        var source = new RepositoryMemoryConfiguration(valid, valid, Path.GetTempPath());
        var before = source.CaptureCurrent();
        var exception = Assert.Throws<ArgumentException>(() => source.BindRepository(Path.GetTempPath(), retired));
        Assert.Contains("retired", exception.Message, StringComparison.Ordinal);
        Assert.Same(before, source.CaptureCurrent());
    }

    /// <summary>Memory operations may repeat across rounds, but identical siblings remain invalid.</summary>
    [Fact]
    public static void Memory_call_reuse_preserves_batch_duplicate_rejection()
    {
        var definition = new MemoriesTool(new MemoryService(), new Options(), TestPromptLoader.Instance).Definition;
        var history = new ToolCallHistory();
        const string arguments = "{\"action\":\"list\"}";
        Assert.True(history.TryAdd(definition, arguments));

        var batch = new ToolCallHistory(history);
        Assert.True(batch.TryAdd(definition, arguments));
        Assert.False(batch.TryAdd(definition, arguments));
    }

    /// <summary>All fuzzy consumers share a usable default without score cutoffs.</summary>
    [Fact]
    public static void Fuzzy_consumers_default_to_one_and_enable_without_extra_settings()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:Recall:RerankerEnabled"] = "true",
            ["tools:config:memories:Reconciliation:Enabled"] = "true",
            ["tools:config:memories:Recall:Concepts:Enabled"] = "true",
            ["tools:config:memories:Recall:Fuzzy:Enabled"] = "true",
            ["tools:config:memories:Reconciliation:Fuzzy:Enabled"] = "true",
        }).Build();
        var source = new RepositoryMemoryConfiguration(config, new ConfigurationBuilder().Build(), Path.GetTempPath());
        var options = source.CaptureCurrent();
        Assert.Equal(1, options.Lexical.FuzzyMaximumDistance);
        Assert.Equal(1, RepositoryMemorySearchOptions.ForRecall(options).ConceptFuzzyMaximumDistance);
        Assert.Equal(1, RepositoryMemorySearchOptions.ForReconciliation(options).ConceptFuzzyMaximumDistance);
        options.Validate();
    }

    /// <summary>Layered policy keeps both scenarios independent and shares fuzzy policy within each.</summary>
    [Fact]
    public static void Scenario_search_settings_follow_configuration_layers()
    {
        var fallback = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:Recall:Fuzzy:Enabled"] = "true",
            ["tools:config:memories:Recall:Fuzzy:MaximumDistance"] = "20",
            ["tools:config:memories:Reconciliation:Enabled"] = "true",
            ["tools:config:memories:Reconciliation:Concepts:CandidateLimit"] = "7",
            ["tools:config:memories:Reconciliation:Fuzzy:Enabled"] = "true",
            ["tools:config:memories:Reconciliation:Fuzzy:MaximumDistance"] = "10",
        }).Build();
        var current = new ConfigurationBuilder().AddConfiguration(fallback).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:Recall:Lexical:MaximumExpansions"] = "2",
            ["tools:config:memories:Recall:Concepts:CandidateLimit"] = "3",
        }).Build();
        var options = new RepositoryMemoryConfiguration(current, fallback, Path.GetTempPath()).CaptureCurrent();
        var recall = RepositoryMemorySearchOptions.ForRecall(options);
        var reconciliation = RepositoryMemorySearchOptions.ForReconciliation(options);
        Assert.NotEqual(recall.Lexical, reconciliation.Lexical);
        Assert.Equal(20, recall.ConceptFuzzyMaximumDistance);
        Assert.Equal(20, recall.Lexical.FuzzyMaximumDistance);
        Assert.Equal(10, reconciliation.Lexical.FuzzyMaximumDistance);
        Assert.Equal(16, reconciliation.Lexical.MaximumExpansions);
        Assert.True(recall.ConceptFuzzyEnabled);
        Assert.True(recall.Lexical.FuzzyEnabled);
        Assert.Equal(2, recall.Lexical.MaximumExpansions);
        Assert.Equal(3, recall.ConceptCandidateLimit);
        Assert.Equal(7, reconciliation.ConceptCandidateLimit);
        Assert.True(reconciliation.ConceptRecallEnabled);
        Assert.True(reconciliation.ConceptFuzzyEnabled);
        Assert.Equal(10, reconciliation.ConceptFuzzyMaximumDistance);
        Assert.False(recall.ConceptRecallEnabled);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepositoryMemoryLexicalOptions { MaximumExpansions = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new RepositoryMemoryLexicalOptions { FuzzyEnabled = true, FuzzyMaximumDistance = 0 }.Validate());
    }

    /// <summary>Required reconciliation ranking cannot be silently overridden or bypassed.</summary>
    [Fact]
    public static void Reconciliation_reranker_is_explicit_and_invalid_changes_preserve_binding()
    {
        var root = Path.GetTempPath();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:Recall:RerankerEnabled"] = "false",
            ["tools:config:memories:Recall:SemanticMinimum"] = "0.6",
            ["tools:config:memories:Recall:MaximumResults"] = "0",
            ["tools:config:memories:Reconciliation:Enabled"] = "true",
            ["tools:config:memories:Reconciliation:RerankerEnabled"] = "true",
            ["tools:config:memories:Reconciliation:SemanticMinimum"] = "0.3",
            ["tools:config:memories:Reconciliation:RerankerCandidateLimit"] = "12",
        }).Build();
        var source = new RepositoryMemoryConfiguration(config, config, root);
        var before = source.CaptureCurrent();
        var recall = RepositoryMemorySearchOptions.ForRecall(before);
        var reconciliation = RepositoryMemorySearchOptions.ForReconciliation(before);
        Assert.False(recall.RerankerEnabled);
        Assert.Equal(0, recall.MaximumResults);
        Assert.Equal(0.6, recall.SemanticMinimum);
        Assert.True(reconciliation.RerankerEnabled);
        Assert.Equal(0.3, reconciliation.SemanticMinimum);
        Assert.Equal(12, reconciliation.RerankerCandidateLimit);
        Assert.Throws<ArgumentException>(() => source.BindRepository(root, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:Reconciliation:RerankerEnabled"] = "false",
        }).Build()));
        Assert.Same(before, source.CaptureCurrent());
        source.BindRepository(root, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:Recall:Fuzzy:Enabled"] = "true",
            ["tools:config:memories:Recall:Fuzzy:MaximumDistance"] = "5",
        }).Build());
        Assert.True(source.CaptureCurrent().ConceptFuzzyEnabled);
        Assert.False(source.CaptureCurrent().ReconciliationLexical.FuzzyEnabled);
        source.BindRepository(root, new ConfigurationBuilder().Build());
        Assert.False(source.CaptureCurrent().Lexical.FuzzyEnabled);
    }

    /// <summary>Retired ambiguous keys must not silently fall back to different defaults.</summary>
    [Theory]
    [InlineData("SemanticMinimum", "0.8")]
    [InlineData("RerankerEnabled", "true")]
    [InlineData("MaxRepoMemoriesInContext", "2")]
    [InlineData("ConceptRecall:Enabled", "true")]
    [InlineData("Lexical:FuzzyEnabled", "true")]
    [InlineData("Reconciliation:CandidateLimit", "8")]
    [InlineData("Reconciliation:Concepts:FuzzyEnabled", "true")]
    public static void Retired_search_settings_report_migration(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"tools:config:memories:{key}"] = value,
        }).Build();
        var error = Assert.Throws<ArgumentException>(() => new RepositoryMemoryConfiguration(config, new ConfigurationBuilder().Build(), Path.GetTempPath()));
        Assert.Contains("Recall and Reconciliation", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Repository switches refresh advertised limits without changing runtime registration identity.</summary>
    [Fact]
    public static void Description_FollowsRepositoryLimitsThroughRuntimeOverride()
    {
        var first = Path.Combine(Path.GetTempPath(), "memory-first");
        var second = Path.Combine(Path.GetTempPath(), "memory-second");
        var empty = new ConfigurationBuilder().Build();
        var options = new RepositoryMemoryConfiguration(empty, empty, first);
        var tool = new MemoriesTool(new MemoryService(), options, TestPromptLoader.Instance);
        var registry = new ToolRegistry([tool], runtimeOptions: new ToolRuntimeOptions
        {
            Defaults = new ToolRuntimeOverride { TimeoutMilliseconds = 12345 },
        });
        var registered = registry.Get("memories");
        Assert.Contains("2000 characters", registered.Definition.Description, StringComparison.Ordinal);
        options.BindRepository(second, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:MaximumTextCharacters"] = "4321",
        }).Build());
        Assert.Same(registered, registry.Get("memories"));
        Assert.Contains("4321 characters", registered.Definition.Description, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMilliseconds(12345), registered.Definition.Timeout);
    }

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
        Assert.Equal(["action", "afterKey", "afterPath", "concepts", "confirmDistinctFrom", "coverage", "coveredPaths", "expectedRevision", "id", "kind", "memoryType", "proposalKey", "proposalOffset", "supportingPaths", "survey", "text"], fields.EnumerateObject().Select(field => field.Name).OrderBy(name => name));
        Assert.Equal(["add", "update", "remove", "list", "changes", "coverChanges", "coverage", "updateCoverage", "initializeCoverage"], fields.GetProperty("action").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("null", fields.GetProperty("id").GetProperty("type").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("null", fields.GetProperty("text").GetProperty("type").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ["situational", "standingPreference"],
            fields.GetProperty("memoryType").GetProperty("enum").EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()).OrderBy(value => value));
        Assert.Equal(8, fields.GetProperty("concepts").GetProperty("maxItems").GetInt32());
        Assert.Contains("null", fields.GetProperty("concepts").GetProperty("type").EnumerateArray().Select(value => value.GetString()));
        var confirmation = fields.GetProperty("confirmDistinctFrom").GetProperty("items");
        Assert.False(confirmation.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["id", "revision"], confirmation.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(1, fields.GetProperty("expectedRevision").GetProperty("minimum").GetInt32());
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
    [InlineData("{\"action\":\"add\",\"text\":\"x\",\"expectedRevision\":1}")]
    [InlineData("{\"action\":\"list\",\"concepts\":[\"cancellation\"]}")]
    [InlineData("{\"action\":\"add\",\"text\":\"x\",\"kind\":\"authority\"}")]
    [InlineData("{\"action\":\"add\",\"text\":\"x\",\"concepts\":[\"invalid space\"]}")]
    [InlineData("{\"action\":\"add\",\"text\":\"x\",\"confirmDistinctFrom\":[{\"id\":\"not-an-id\",\"revision\":1}]}")]
    [InlineData("{\"action\":\"add\",\"text\":\"x\",\"confirmDistinctFrom\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"revision\":0}]}")]
    public static void InvalidArguments_AreRejected(string json)
    {
        var tool = new MemoriesTool(new MemoryService(), new Options(), TestPromptLoader.Instance);
        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(json));
    }

    /// <summary>Advertising collection actions does not grant an ordinary model collection authority.</summary>
    [Theory]
    [InlineData("changes")]
    [InlineData("coverChanges")]
    [InlineData("coverage")]
    public static async Task PendingChangesRequireHostCollectionAuthority(string action)
    {
        var tool = new MemoriesTool(new MemoryService(), new Options(), TestPromptLoader.Instance);
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), Invocation(Path.GetTempPath()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(
            new MemoriesInput(action), context, TestContext.Current.CancellationToken));
    }

    /// <summary>Source paths cannot change ordinary write ownership without a host collection.</summary>
    [Fact]
    public static async Task SupportingPathsRequireHostCollectionAuthority()
    {
        var tool = new MemoriesTool(new MemoryService(), new Options(), TestPromptLoader.Instance);
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), Invocation(Path.GetTempPath()));

        await Assert.ThrowsAsync<ToolArgumentValidationException>(() => tool.ExecuteAsync(
            new MemoriesInput("add", Text: "Assertion") { SupportingPaths = ["src/Service.cs"] }, context, TestContext.Current.CancellationToken));
    }

    /// <summary>Collision previews bound escaped entry bytes while preserving a truthful non-write response.</summary>
    [Fact]
    public static async Task Collision_output_reports_truncation_and_omissions_without_false_success()
    {
        var memory = new MemoryService();
        var candidates = Enumerable.Range(0, 12)
            .Select(_ => memory.Entry with { Id = RepositoryMemoryId.New(), Text = new string('<', 3000) })
            .ToArray();
        memory.OperationResult = new RepositoryMemoryOperationResult("reconciliationRequired", null, null, candidates, [])
        {
            SearchDetails = new RepositoryMemorySearchDetails
            {
                Lexical = RepositoryMemorySearchBranchStatus.Completed,
                Reranker = RepositoryMemorySearchBranchStatus.Completed,
                HybridCandidates = 15,
                ComparedCandidates = 12,
                CandidateWindowOmissions = 3,
            },
        };
        var tool = new MemoriesTool(memory, new Options(), TestPromptLoader.Instance);
        var output = await tool.ExecuteAsync(
            new MemoriesInput("add", Text: "Proposed convention"),
            new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), Invocation(Path.GetTempPath())),
            TestContext.Current.CancellationToken);

        Assert.False(output.Value.Added);
        Assert.Equal("reconciliationRequired", output.Value.Outcome);
        Assert.NotEmpty(output.Value.Entries);
        Assert.True(output.Value.OmittedEntries > 0);
        Assert.Equal(candidates.Length, output.Value.Entries.Count + output.Value.OmittedEntries);
        Assert.Equal(3, output.Value.SearchDetails?.CandidateWindowOmissions);
        Assert.Contains("omitted 3 discovered candidates", output.Value.Resolution, StringComparison.Ordinal);
        Assert.Contains("Completed", JsonSerializer.Serialize(output.Value.SearchDetails), StringComparison.Ordinal);
        Assert.All(output.Value.Entries, entry =>
        {
            Assert.True(entry.TextTruncated);
            Assert.Equal(2000, entry.Text.Length);
            Assert.Contains(candidates, candidate => candidate.Id.Value.ToString("D") == entry.Id && candidate.Revision == entry.Revision);
        });
        Assert.True(output.Value.Entries.Sum(entry => JsonSerializer.SerializeToUtf8Bytes(entry).Length) <= 48 * 1024);
        Assert.Contains("expectedRevision", output.Value.Resolution, StringComparison.Ordinal);
        Assert.Contains("confirmDistinctFrom", output.Value.Resolution, StringComparison.Ordinal);
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
            ["tools:config:memories:Recall:SemanticMinimum"] = "0.35",
            ["tools:config:memories:Recall:RerankerEnabled"] = "true",
            ["tools:config:memories:Recall:RerankerCandidateLimit"] = "6",
            ["tools:config:memories:standingPreferenceWarningThreshold"] = "5",
        }).Build();
        var initial = new ConfigurationBuilder().AddConfiguration(fallback).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:Recall:MaximumResults"] = "2",
            ["tools:config:memories:Recall:SemanticMinimum"] = "0.65",
            ["tools:config:memories:Recall:RerankerEnabled"] = "false",
            ["tools:config:memories:Recall:RerankerCandidateLimit"] = "4",
        }).Build();
        var source = new RepositoryMemoryConfiguration(initial, fallback, first);
        Assert.Equal(2, source.Capture(RepositoryIdentity.Create(first)).MaxRepoMemoriesInContext);
        Assert.Equal(0.65, source.Capture(RepositoryIdentity.Create(first)).SemanticMinimum);
        Assert.False(source.Capture(RepositoryIdentity.Create(first)).RerankerEnabled);
        Assert.Equal(4, source.Capture(RepositoryIdentity.Create(first)).RerankerCandidateLimit);
        Assert.Equal(5, source.Capture(RepositoryIdentity.Create(first)).StandingPreferenceWarningThreshold);
        source.BindRepository(next, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:Recall:RerankerEnabled"] = "false",
            ["tools:config:memories:Recall:RerankerCandidateLimit"] = "3",
        }).Build());
        Assert.False(source.Capture(RepositoryIdentity.Create(next)).RerankerEnabled);
        Assert.Equal(3, source.Capture(RepositoryIdentity.Create(next)).RerankerCandidateLimit);
        source.BindRepository(next, new ConfigurationBuilder().Build());
        Assert.Equal(7, source.Capture(RepositoryIdentity.Create(next)).MaxNumberOfRepoMemories);
        Assert.Equal(3, source.Capture(RepositoryIdentity.Create(next)).MaxRepoMemoriesInContext);
        Assert.Equal(0.35, source.Capture(RepositoryIdentity.Create(next)).SemanticMinimum);
        Assert.True(source.Capture(RepositoryIdentity.Create(next)).RerankerEnabled);
        Assert.Equal(6, source.Capture(RepositoryIdentity.Create(next)).RerankerCandidateLimit);
        Assert.Equal(5, source.Capture(RepositoryIdentity.Create(next)).StandingPreferenceWarningThreshold);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.BindRepository(next, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["tools:config:memories:Recall:SemanticMinimum"] = "1.1" }).Build()));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.BindRepository(next, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["tools:config:memories:Recall:RerankerCandidateLimit"] = "0" }).Build()));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.BindRepository(next, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["tools:config:memories:standingPreferenceWarningThreshold"] = "-1" }).Build()));
        Assert.Equal(0.35, source.Capture(RepositoryIdentity.Create(next)).SemanticMinimum);
        Assert.True(source.Capture(RepositoryIdentity.Create(next)).RerankerEnabled);
        Assert.Throws<InvalidOperationException>(() => source.Capture(RepositoryIdentity.Create(first)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepositoryMemoryConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["tools:config:memories:MaxNumberOfRepoMemories"] = "0" }).Build(), fallback, first));
    }

    /// <summary>Retries/tool rounds account one run; deleted context resets provider history; denial withholds injection.</summary>
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(true, false, false, false, true)]
    public static async Task Conversation_AccountsSubmittedMemoryAndInvalidatesDeletedContext(bool remove, bool denied, bool accountingFails, bool explicitList, bool verifyRemoval)
    {
        var repository = Path.Combine(Path.GetTempPath(), "memory-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        try
        {
            await using var events = new DomainEventStream();
            var ct = TestContext.Current.CancellationToken;
            var sanitizer = new SecretOutputSanitizer();
            var memory = new MemoryService { FailAccounting = accountingFails, SuppressRetrieval = explicitList };
            var options = new Options();
            var tool = new MemoriesTool(memory, options, TestPromptLoader.Instance);
            var registry = new ToolRegistry([tool, new DateTimeTool(TestPromptLoader.Instance)]);
            var budget = new ExecutionBudget(new BudgetDimensions(100000, 30, TimeSpan.FromMinutes(1)));
            var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, budget);
            var evidence = new EvidenceStore(events, sanitizer);
            var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance, repositoryMemoryRetriever: new Retriever(memory));
            var model = new RecordingProvider(remove, memory.Entry.Id, explicitList, verifyRemoval);
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
            var session = await dispatcher.DispatchAsync(new CreateSessionCommand("memory runtime"), ct);
            var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, "Which release branch convention applies?"), ct);
            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), ct));
            Assert.Equal(verifyRemoval ? 4 : 2, model.Requests.Count);
            Assert.True(model.Requests[^1].ContainsSensitiveData);
            Assert.True(model.Requests[^1].SelectionConstraints.ContainsSensitiveData);
            if (verifyRemoval)
            {
                Assert.Equal(["list", "remove", "list"], memory.Actions);
                var results = model.Requests[^1].Messages.Where(message => message.SectionId == "tool-result").ToArray();
                Assert.Equal(3, results.Length);
                using var before = JsonDocument.Parse(results[0].GetModelVisibleContent());
                using var after = JsonDocument.Parse(results[2].GetModelVisibleContent());
                Assert.Equal("listed", after.RootElement.GetProperty("Outcome").GetString());
                Assert.Equal(1, before.RootElement.GetProperty("Entries").GetArrayLength());
                Assert.Equal(0, after.RootElement.GetProperty("Entries").GetArrayLength());
            }

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
            var eligibleSubmissions = model.Requests.Count(request => request.MemorySubmission is { Inclusions.Count: > 0 });
            Assert.Equal(eligibleSubmissions, memory.InclusionCalls.Count);
            Assert.All(memory.InclusionCalls, calledRun => Assert.Equal(run, calledRun));
            Assert.Equal(!denied && !explicitList, model.Requests[0].Messages.Any(message => message.SectionId == "repository-memory"));
            Assert.Equal(!denied && !remove && !explicitList, model.Requests[^1].Messages.Any(message => message.SectionId == "repository-memory"));
            Assert.Equal(!denied, model.Requests[0].Tools.Any(definition => definition.Name == "memories"));
            if (remove)
            {
                Assert.True(model.Requests[^1].HistoryRewriteGeneration > model.Requests[0].HistoryRewriteGeneration);
            }
            else
            {
                Assert.Equal(model.Requests[0].HistoryRewriteGeneration, model.Requests[^1].HistoryRewriteGeneration);
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
        Assert.Equal(16, schema.GetProperty("properties").EnumerateObject().Count());
        Assert.Equal(9, schema.GetProperty("properties").GetProperty("action").GetProperty("enum").GetArrayLength());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.DoesNotContain("SubmissionObserver", handler.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MemorySubmission", handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    private static ToolInvocationContext Invocation(string repository)
    {
        return new() { RepositoryPath = repository, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" };
    }

    private sealed class RequestHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    /// <summary>Verifies independent memory routing and intent eligibility.</summary>
    [Theory]
    [InlineData(null, SituationalConceptFilterMode.Off, false)]
    [InlineData("Shadow", SituationalConceptFilterMode.Shadow, false)]
    [InlineData("Enforce", SituationalConceptFilterMode.Enforce, false)]
    [InlineData("invalid", SituationalConceptFilterMode.Off, true)]
    [InlineData("99", SituationalConceptFilterMode.Off, true)]
    public static void Independent_settings_preserve_legacy_routes(string? value, SituationalConceptFilterMode expected, bool diagnostic)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:Recall:SituationalConceptFilterMode"] = value,
        }).Build();
        var options = new RepositoryMemoryConfiguration(configuration, new ConfigurationBuilder().Build(), Path.GetTempPath()).CaptureCurrent();
        Assert.Equal(expected, options.SituationalConceptFilterMode);
        Assert.Equal(diagnostic, options.ConfigurationDiagnostics.Count > 0);
        Assert.True(options.Intelligence.AutomaticRecallEnabled);
        Assert.False(options.Intelligence.Enabled);
        Assert.False(options.ConceptRecallEnabled);
    }

    /// <summary>Verifies independent memory routing and intent eligibility.</summary>
    [Theory]
    [InlineData(false, true, null, "disabled")]
    [InlineData(true, false, null, "not_collected")]
    [InlineData(true, true, "monitor failed", "error")]
    [InlineData(true, true, null, "no_matches")]
    public static async Task Search_uses_existing_availability_and_never_mutates(bool enabled, bool onboarded, string? fault, string outcome)
    {
        var path = Path.GetTempPath();
        var memory = new MemoryService();
        var search = new IntelligenceSearch();
        var options = new Options { IntelligenceEnabled = enabled };
        var tool = new RepositoryIntelligenceTool(new RepositoryIntelligenceQuery(search, options, new IntelligenceStatus(onboarded, fault)), options, TestPromptLoader.Instance);
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), Invocation(path));
        var result = await tool.ExecuteAsync(new RepositoryIntelligenceInput("architecture"), context, TestContext.Current.CancellationToken);
        Assert.Equal(outcome, result.Value.Outcome);
        Assert.Empty(memory.Actions);
        Assert.Equal(outcome == "no_matches", search.Request is not null);
        if (search.Request is { } request)
        {
            Assert.Equal(RepositoryIdentity.Create(path), request.RepositoryIdentity);
            Assert.Equal(RepositoryMemoryOwnership.Intelligence, request.Ownership);
            Assert.True(request.IncludeStandingPreferences);
            Assert.False(request.AllowEmbeddingRebuild);
            Assert.Equal(SituationalConceptFilterMode.Off, request.SituationalConceptFilterMode);
        }
    }

    /// <summary>Bounds full intelligence envelopes without changing retrieval semantics.</summary>
    [Theory]
    [InlineData(true, 'x')]
    [InlineData(true, '"')]
    [InlineData(true, '\u6f22')]
    [InlineData(false, 'x')]
    [InlineData(false, '"')]
    [InlineData(false, '\u6f22')]
    public static async Task Intelligence_bounds_complete_response_with_long_query(bool enabled, char character)
    {
        var options = new Options { IntelligenceEnabled = enabled };
        var search = new IntelligenceSearch
        {
            Result = new([], []) { SearchDetails = new RepositoryMemorySearchDetails { RerankerInputFailure = new string(character, 8000) } },
        };
        var tool = new RepositoryIntelligenceTool(new RepositoryIntelligenceQuery(search, options, new IntelligenceStatus(true, null)), options, TestPromptLoader.Instance);
        var registry = new ToolRegistry([tool], runtimeOptions: new ToolRuntimeOptions { Defaults = new ToolRuntimeOverride { MaximumOutputBytes = 4096 } });
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var query = new string(character, 8000);
        var result = await pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = SessionId.New(), RunId = RunId.New(), ToolId = tool.Definition.Id,
                ArgumentsJson = JsonSerializer.Serialize(new { query }), Context = Invocation(Path.GetTempPath()),
            },
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.IsTruncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.ResultJson!) <= 4096);
        using var document = JsonDocument.Parse(result.ResultJson!);
        Assert.Equal(enabled ? "no_matches" : "disabled", document.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal(enabled, document.RootElement.GetProperty("IsComplete").GetBoolean());
        Assert.True(document.RootElement.GetProperty("QueryTruncated").GetBoolean());
        if (enabled)
        {
            Assert.Equal(query, search.Request!.Query);
        }
    }

    /// <summary>Verifies independent memory routing and intent eligibility.</summary>
    [Theory]
    [InlineData("ordinary", "architecture")]
    [InlineData(null, "architecture")]
    [InlineData("intelligence", " ")]
    public static async Task Search_rejects_unbounded_or_wrong_ownership(string? ownership, string query)
    {
        var tool = new MemoriesTool(new MemoryService(), new Options(), TestPromptLoader.Instance);
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), Invocation(Path.GetTempPath()));
        var result = await tool.ExecuteAsync(new MemoriesInput("search", Query: query, Ownership: ownership), context, TestContext.Current.CancellationToken);
        Assert.Equal("legacy_search_removed", result.Value.ReasonCode);
        Assert.Contains("repository_intelligence", result.Value.Resolution, StringComparison.Ordinal);
        using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
        Assert.False(schema.RootElement.GetProperty("properties").TryGetProperty("query", out _));
        Assert.False(schema.RootElement.GetProperty("properties").TryGetProperty("ownership", out _));
    }

    /// <summary>Search preserves incompleteness, ownership isolation, bounds and cancellation.</summary>
    [Fact]
    public static async Task Search_reports_incomplete_dirty_evidence_and_rejects_cross_repository_results()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.GetTempPath();
        var memory = new MemoryService();
        var entry = memory.Entry with
        {
            RepositoryIdentity = RepositoryIdentity.Create(path), Ownership = RepositoryMemoryOwnership.Intelligence,
            Supports = [new RepositoryMemorySupport { Path = "source.cs", ContentHash = "observed", ObservedAt = DateTimeOffset.UnixEpoch, IsDirty = true }],
        };
        var search = new IntelligenceSearch { Result = new([new(entry, 1, 1, null, null)], []) { IsComplete = false } };
        var tool = new RepositoryIntelligenceTool(new RepositoryIntelligenceQuery(search, new Options { IntelligenceEnabled = true }, new IntelligenceStatus(true, null)), new Options { IntelligenceEnabled = true }, TestPromptLoader.Instance);
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), Invocation(path));
        var input = new RepositoryIntelligenceInput("architecture");
        var result = await tool.ExecuteAsync(input, context, ct);
        Assert.Equal("incomplete", result.Value.Outcome);
        Assert.False(result.Value.IsComplete);
        var info = Assert.Single(result.Value.Entries);
        Assert.Equal("dirty", info.Freshness);
        Assert.Equal("untrusted", info.Trust);
        Assert.DoesNotContain("Embedding\":", JsonSerializer.Serialize(info), StringComparison.Ordinal);
        search.Result = new([new(entry with { Text = new string('x', 50 * 1024) }, 1, 1, null, null)], []);
        var bounded = await tool.ExecuteAsync(input, context, ct);
        Assert.Empty(bounded.Value.Entries);
        Assert.Equal(1, bounded.Value.OmittedEntries);
        Assert.Equal("matches", bounded.Value.Outcome);
        search.Result = new([new(entry with { RepositoryIdentity = "another repository" }, 1, 1, null, null)], []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(input, context, ct));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(input, context, cancellation.Token));
    }

    /// <summary>Explicit search honors operator limits above and below the former fixed caps.</summary>
    [Theory]
    [InlineData(60 * 1024, 20)]
    [InlineData(3000, 1)]
    public static async Task Search_honors_configured_result_and_byte_limits(int maximumBytes, int expectedEntries)
    {
        var memory = new MemoryService();
        var path = Path.GetTempPath();
        var entries = Enumerable.Range(0, 25).Select(_ => memory.Entry with
        {
            Id = RepositoryMemoryId.New(), RepositoryIdentity = RepositoryIdentity.Create(path),
            Ownership = RepositoryMemoryOwnership.Intelligence, Text = new string('x', 1600),
        }).Select(entry => new RepositoryMemoryRetrievalCandidate(entry, 1, 1, null, null)).ToArray();
        var search = new IntelligenceSearch { Result = new(entries, []) };
        var options = new Options { IntelligenceEnabled = true, SearchMaximumResults = 25, MaximumListBytes = maximumBytes };
        var tool = new RepositoryIntelligenceTool(new RepositoryIntelligenceQuery(search, options, new IntelligenceStatus(true, null)), options, TestPromptLoader.Instance);
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), Invocation(path));
        var result = await tool.ExecuteAsync(new RepositoryIntelligenceInput("architecture"), context, TestContext.Current.CancellationToken);
        Assert.Equal(20, search.Request!.Options.MaximumResults);
        Assert.Equal(expectedEntries, result.Value.Entries.Count);
        Assert.Equal(25 - expectedEntries, result.Value.OmittedEntries);
    }

    /// <summary>Stale legacy payloads reach exactly one correlated rejection without querying.</summary>
    [Fact]
    public static async Task Legacy_search_payload_rejects_through_real_pipeline()
    {
        var ct = TestContext.Current.CancellationToken;
        var search = new IntelligenceSearch();
        var memory = new MemoryService();
        var tool = new MemoriesTool(memory, new Options { IntelligenceEnabled = true }, TestPromptLoader.Instance, new IntelligenceStatus(true, null), search);
        var registry = new ToolRegistry([tool]);
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var persisted = "{\"action\":\"search\",\"query\":\"architecture\",\"ownership\":\"intelligence\"}";
        var request = new ToolInvocationRequest
        {
            SessionId = SessionId.New(), RunId = RunId.New(), ToolId = "memories",
            ArgumentsJson = persisted, InvocationKey = "persisted-provider-call", Context = Invocation(Path.GetTempPath()),
        };
        var result = Assert.Single(await pipeline.InvokeBatchAsync([new ToolBatchRequest(0, "persisted-provider-call", request)], ct));
        Assert.Equal("persisted-provider-call", result.CorrelationId);
        Assert.True(result.Result.Succeeded, result.Result.Error);
        using var json = JsonDocument.Parse(result.Result.ResultJson!);
        Assert.Equal("legacy_search_removed", json.RootElement.GetProperty("ReasonCode").GetString());
        Assert.Null(search.Request);
        Assert.Empty(memory.Actions);
    }

    /// <summary>Readiness filters actual conversation snapshots independently of the memory tool.</summary>
    [Theory]
    [InlineData(false, false, "master_disabled")]
    [InlineData(true, false, "onboarding_required")]
    [InlineData(true, true, "ready")]
    public static async Task Repository_query_advertisement_uses_feature_readiness(bool enabled, bool onboarded, string reason)
    {
        var options = new Options { IntelligenceEnabled = enabled };
        var tool = new RepositoryIntelligenceTool(new RepositoryIntelligenceQuery(new IntelligenceSearch(), options, new IntelligenceStatus(onboarded, null)), options, TestPromptLoader.Instance);
        var registry = new ToolRegistry([tool]);
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var invocation = Invocation(Path.GetTempPath());
        var snapshot = ConversationToolAvailability.CreateSnapshot(pipeline, registry, SessionId.New(), RunId.New(), invocation, false);
        Assert.Equal(reason == "ready", snapshot.Definitions.Count == 1);
        Assert.Equal(reason, tool.CaptureAvailability(invocation.RepositoryPath).ReasonCode);
        Assert.Empty(ConversationToolAvailability.CreateSnapshot(pipeline, registry, SessionId.New(), RunId.New(), invocation with { DeniedToolIds = [tool.Definition.Id] }, false).Definitions);
        Assert.Single(registry.AllDefinitions);
        foreach (var invalid in new[] { "{}", "{\"query\":\" \"}", "{\"query\":\"x\",\"ownership\":\"intelligence\"}", "{", JsonSerializer.Serialize(new { query = new string('x', 8001) }) })
        {
            Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(invalid));
        }
    }

    private sealed class IntelligenceStatus : IRepositoryIntelligenceStatusProvider
    {
        private readonly bool _onboarded;
        private readonly string? _fault;

        public IntelligenceStatus(bool onboarded, string? fault)
        {
            _onboarded = onboarded;
            _fault = fault;
        }

        public RepositoryIntelligenceStatus Capture(string repositoryIdentity) => new()
        {
            RepositoryIdentity = repositoryIdentity, Enabled = true, Onboarded = _onboarded, Fault = _fault,
        };
    }

    private sealed class IntelligenceSearch : IRepositoryMemorySearch
    {
        public RepositoryMemoryRetrievalResult Result { get; set; } = new([], []);

        public RepositoryMemorySearchRequest? Request { get; private set; }

        public Task<RepositoryMemoryRetrievalResult> SearchAsync(RepositoryMemorySearchRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class Options : IRepositoryMemoryOptionsProvider
    {
        public bool IntelligenceEnabled { get; init; }

        public int SearchMaximumResults { get; init; } = 3;

        public int MaximumListBytes { get; init; } = 48 * 1024;

        public int StandingPreferenceWarningThreshold { get; init; } = 3;

        public RepositoryMemoryOptions CaptureCurrent()
        {
            return Capture(string.Empty);
        }

        public RepositoryMemoryOptions Capture(string repositoryIdentity)
        {
            return new()
            {
                StandingPreferenceWarningThreshold = StandingPreferenceWarningThreshold,
                MaximumListBytes = MaximumListBytes,
                Intelligence = new() { Enabled = IntelligenceEnabled, AutomaticRecallEnabled = false, Recall = new() { MaximumResults = SearchMaximumResults } },
            };
        }
    }

    private sealed class MemoryService : IManagedRepositoryMemoryService
    {
        private readonly HashSet<RunId> _runs = [];

        public RepositoryMemoryEntry Entry { get; } = new()
        {
            Id = RepositoryMemoryId.New(),
            RepositoryIdentity = "test",
            Text = "Use release branches for deployment",
            ContentHash = "test",
            Origin = RepositoryMemoryOrigin.Manual,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Sensitivity = ConversationSensitivity.Sensitive,
        };

        public bool Removed { get; private set; }

        public bool FailAccounting { get; init; }

        public bool FailOperation { get; init; }

        public bool SuppressRetrieval { get; init; }

        public int? StandingPreferenceCount { get; init; }

        public RepositoryMemoryOperationResult? OperationResult { get; set; }

        public int Receipts => _runs.Count;

        public List<RunId> InclusionCalls { get; } = [];

        public RepositoryMemoryOperationRequest? LastOperation { get; private set; }

        public List<string> Actions { get; } = [];

        public Task<RepositoryMemoryOperationResult> ExecuteAsync(RepositoryMemoryOperationRequest request, CancellationToken cancellationToken = default)
        {
            LastOperation = request;
            Actions.Add(request.Action);
            if (FailOperation)
            {
                return Task.FromException<RepositoryMemoryOperationResult>(new IOException("simulated memory operation failure"));
            }

            Removed = request.Action == "remove" || Removed;
            return Task.FromResult(OperationResult ?? new RepositoryMemoryOperationResult(request.Action == "remove" ? "removed" : "listed", request.Id, null, Removed ? [] : [Entry], [])
            {
                StandingPreferenceCount = StandingPreferenceCount,
            });
        }

        public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RepositoryMemoryReadSnapshot(repositoryIdentity, Removed ? 2 : 1, Removed ? [] : [Entry], [], []));
        }

        public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(string repositoryIdentity, RepositoryMemoryOptions options, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RepositoryMemoryId>>([]);
        }

        public Task RecordInclusionsAsync(string repositoryIdentity, RunId runId, IReadOnlyList<RepositoryMemoryInclusion> inclusions, CancellationToken cancellationToken = default)
        {
            InclusionCalls.Add(runId);
            _runs.Add(runId);
            return FailAccounting ? Task.FromException(new IOException("simulated receipt failure")) : Task.CompletedTask;
        }
    }

    private sealed class Retriever : IHybridRepositoryMemoryRetriever
    {
        private readonly MemoryService _memory;

        public Retriever(MemoryService memory)
        {
            _memory = memory;
        }

        public Task<RepositoryMemoryRetrievalResult> RetrieveAsync(RepositoryMemoryRetrievalRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RepositoryMemoryRetrievalResult(_memory.Removed || _memory.SuppressRetrieval ? [] : [new RepositoryMemoryRetrievalCandidate(_memory.Entry, 1, 1, null, null)], []));
        }
    }

    private sealed class RecordingProvider : IModelProvider
    {
        private readonly bool _remove;
        private readonly bool _explicitList;
        private readonly bool _verifyRemoval;
        private readonly RepositoryMemoryId _id;

        public RecordingProvider(bool remove, RepositoryMemoryId id, bool explicitList = false, bool verifyRemoval = false)
        {
            _remove = remove;
            _explicitList = explicitList;
            _verifyRemoval = verifyRemoval;
            _id = id;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            request.SubmissionObserver?.Invoke();
            request.SubmissionObserver?.Invoke();
            await Task.Yield();
            if (_verifyRemoval && Requests.Count <= 3)
            {
                var arguments = Requests.Count == 2
                    ? JsonSerializer.Serialize(new { action = "remove", id = _id.Value.ToString("D") })
                    : "{\"action\":\"list\"}";
                yield return new ModelChunk { Output = new ToolRequestModelOutput("memories", arguments) };
            }
            else if (Requests.Count == 1)
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
