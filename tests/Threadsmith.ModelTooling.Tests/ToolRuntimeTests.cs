namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Tui;
using Xunit;

/// <summary>Verifies the plan-08 tool runtime, policy, persistence, UI, and process lifecycle.</summary>
public static class ToolRuntimeTests
{
    private const string SanitizerExpansionMarker = "token=x";

    /// <summary>A model-requested tool produces attributable durable output and visible activity.</summary>
    [Fact]
    public static async Task ModelToolRequest_IsTypedPersistedAndVisible()
    {
        var repository = CreateTemporaryDirectory();
        var databasePath = Path.Combine(repository, "events.db");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "sample.txt"), "sample");
            await using var events = new DomainEventStream();
            var store = new SqliteEventStore($"Data Source={databasePath};Pooling=False");
            await store.InitializeAsync();
            var projections = new InMemoryProjectionStore();
            await using var persistenceSubscription = events.Subscribe(store.AppendAsync);
            await using var projectionSubscription = events.Subscribe(projections.ApplyAsync);
            await using var failingEvidenceSubscription = events.Subscribe((domainEvent, _) =>
                domainEvent is EvidenceAdded
                    ? Task.FromException(new InvalidOperationException("evidence observer failed"))
                    : Task.CompletedTask);
            var pipeline = CreatePipeline(events, [new ListFilesTool(TestPromptLoader.Instance)]);
            var sanitizer = new TestSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var model = new FakeModelProvider(new ScriptedSession
            {
                Turns =
                [
                    new ScriptedTurn
                    {
                        ToolName = "list_files",
                        ArgumentsJson = "{\"path\":\".\",\"maximumEntries\":10}",
                    },
                ],
            });
            var application = new SessionApplication(
                events,
                model,
                CreateBudget(),
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(CreateContext(repository)),
                evidenceStore: evidence,
                toolRegistry: pipeline.Registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await application.HandleAsync(new CreateSessionCommand("tools"));
            var runId = await application.HandleAsync(
                new SubmitRequestCommand(sessionId, "inspect"));

            Assert.True(await application.HandleAsync(new WaitForRunCommand(runId)));
            var stored = await store.ReadAsync(sessionId);
            var started = Assert.IsType<ToolInvocationStarted>(
                Assert.Single(stored, item => item is ToolInvocationStarted));
            var completed = Assert.IsType<ToolInvocationCompleted>(
                Assert.Single(stored, item => item is ToolInvocationCompleted));
            Assert.Equal("model", started.RequestedBy);
            Assert.True(completed.Succeeded);
            Assert.Contains("sample.txt", completed.ResultJson, StringComparison.Ordinal);
            Assert.Single(evidence.Snapshot(sessionId), item => item.RunId == runId);

            var snapshot = await new TuiPresenter(dispatcher, projections).RenderAsync(sessionId);
            Assert.Contains("Tool list_files (model): succeeded", snapshot.Workspace, StringComparison.Ordinal);
            Assert.Contains("sample.txt", snapshot.Workspace, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Invalid input and escaped paths are rejected before a tool executes.</summary>
    [Fact]
    public static async Task Pipeline_InvalidArgumentsAndEscapedPaths_DoNotExecute()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var secretDirectory = Path.Combine(repository, ".threadsmith", "secrets");
            Directory.CreateDirectory(secretDirectory);
            await File.WriteAllTextAsync(Path.Combine(secretDirectory, "key.txt"), "hidden-marker");
            await File.WriteAllTextAsync(Path.Combine(repository, ".env"), "hidden-marker");
            await File.WriteAllTextAsync(Path.Combine(repository, "visible.txt"), "visible-marker");
            await using var events = new DomainEventStream();
            var countingTool = new CountingTool();
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                1,
                0,
                string.Empty,
                string.Empty,
                false,
                false,
                false,
                TimeSpan.Zero));
            ITool[] tools =
            [
                countingTool,
                new ReadFileTool(TestPromptLoader.Instance),
                new ListFilesTool(TestPromptLoader.Instance),
                new SearchTextTool(TestPromptLoader.Instance),
                new RunProcessTool(processManager, TestPromptLoader.Instance),
            ];
            var pipeline = CreatePipeline(events, tools);
            var invalid = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "counting",
                ArgumentsJson = "{\"value\":\"\"}",
                Context = CreateContext(repository),
            });
            var escaped = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "read_file",
                ArgumentsJson = "{\"path\":\"../outside.txt\"}",
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                },
            });
            var approvalDenied = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "counting",
                ArgumentsJson = "{\"value\":\"valid\"}",
                Context = CreateContext(repository) with
                {
                    RequireApprovalToolIds = ["counting"],
                },
            });
            var recursiveContext = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
                ProhibitedPaths = [".threadsmith/secrets/", "**/*.env"],
            };
            var listed = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "list_files",
                ArgumentsJson = "{\"path\":\".\"}",
                Context = recursiveContext,
            });
            var searched = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "search",
                ArgumentsJson = "{\"query\":\"hidden-marker\"}",
                Context = recursiveContext,
            });
            var qualifiedExecutable = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "run_process",
                ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput
                {
                    Command = "dotnet --version",
                }),
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    AllowedExecutables = ["dotnet"],
                },
            });

            Assert.Equal(ToolErrorClassification.InvalidArguments, invalid.ErrorClassification);
            Assert.Equal(0, countingTool.ExecutionCount);
            Assert.Equal(ToolErrorClassification.PolicyDenied, escaped.ErrorClassification);
            Assert.Equal(ToolErrorClassification.ApprovalDenied, approvalDenied.ErrorClassification);
            Assert.DoesNotContain("key.txt", listed.ResultJson, StringComparison.Ordinal);
            Assert.DoesNotContain(".env", listed.ResultJson, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden-marker", searched.ResultJson, StringComparison.Ordinal);
            Assert.Equal(
                ToolErrorClassification.PolicyDenied,
                qualifiedExecutable.ErrorClassification);
            Assert.Equal(0, processManager.ExecutionCount);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Code exploration is advertised without a workspace so it can return explicit no-workspace availability.</summary>
    [Fact]
    public static async Task CodeExplore_NoWorkspace_ReturnsAvailabilityThroughPolicyPipeline()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var service = new ThrowingCodeExploreService();
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                1,
                0,
                string.Empty,
                string.Empty,
                false,
                false,
                false,
                TimeSpan.Zero));
            var tool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(service, TestPromptLoader.Instance, processManager),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Structured),
                TestPromptLoader.Instance);
            var pipeline = CreatePipeline(events, [tool]);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
            };
            var input = (CodeExploreInput)tool.DeserializeInput("{\"query\":\"inspect source\"}");

            var claims = tool.GetSchedulingClaims(input, context);
            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "code_explore",
                ArgumentsJson = "{\"query\":\"inspect source\"}",
                Context = context,
            });

            Assert.False(tool.Definition.RequiresWorkspace);
            Assert.DoesNotContain(claims, claim => claim.ResourceKind == ToolResourceKind.ProcessPool);
            Assert.True(result.Succeeded, result.Error);
            Assert.False(service.WasCalled);
            Assert.Equal(0, processManager.ExecutionCount);
            var resultJson = result.ResultJson
                ?? throw new InvalidOperationException("Expected structured no-workspace result.");
            var structured = JsonSerializer.Deserialize<CodeExploreResult>(resultJson)
                ?? throw new InvalidOperationException("Expected deserialized code_explore result.");
            Assert.Equal(CodeExploreAvailabilityStatus.NoWorkspaceOpen, structured.Availability?.Status);
            Assert.Empty(structured.FileSections);
            Assert.Contains(
                structured.Availability?.RecommendedActions ?? [],
                action => action.Kind == CodeExploreNextActionKind.OpenWorkspace);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Code exploration exposes only the CodeGraph-style query and optional file hint.</summary>
    [Fact]
    public static void CodeExplore_Definition_UsesMinimalModelFacingSchema()
    {
        var tool = new CodeExploreTool(new NoopCodeExploreService(), TestPromptLoader.Instance);

        using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
        var properties = schema.RootElement.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(properties.SetEquals(["query", "maxFiles"]));
        Assert.Equal(
            "query",
            Assert.Single(schema.RootElement.GetProperty("required").EnumerateArray()).GetString());
        Assert.True(tool.Definition.PreferStrictArguments);
        Assert.Contains("host owns all traversal", tool.Definition.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("limits", tool.Definition.InputSchema.JsonSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("mode", tool.Definition.InputSchema.JsonSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("pathAnchors", tool.Definition.InputSchema.JsonSchema, StringComparison.Ordinal);
        var strictSchemaJson = ModelToolStrictSchemaProjector.TryCreateStrictFunctionSchema(
            tool.Definition.Id,
            tool.Definition.InputSchema.JsonSchema);
        using var strictSchema = JsonDocument.Parse(Assert.IsType<string>(strictSchemaJson));
        var strictProperties = strictSchema.RootElement.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(strictProperties.SetEquals(["query", "maxFiles"]));
    }

    /// <summary>The optional file-count hint is clamped by the host without a corrective turn.</summary>
    [Fact]
    public static async Task CodeExplore_MaxFilesHint_IsHostClampedWithoutPreflightFailure()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var service = new CapturingCodeExploreService();
            var tool = new CodeExploreTool(service, TestPromptLoader.Instance);
            var defaultInput = Assert.IsType<CodeExploreInput>(tool.DeserializeInput(
                "{\"query\":\"inspect source\",\"maxFiles\":null}"));
            Assert.Equal(8, defaultInput.MaxFiles);
            var pipeline = CreatePipeline(events, [tool]);

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "code_explore",
                ArgumentsJson = "{\"query\":\"inspect source\",\"maxFiles\":100000}",
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    WorkspaceId = WorkspaceId.New(),
                },
            });

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(16, service.Request?.Limits.MaximumFiles);
            Assert.Equal(CodeExploreMode.Auto, service.Request?.Mode);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The minimal adapter derives single-anchor impact intent from the model-visible query.</summary>
    [Fact]
    public static async Task CodeExplore_ImpactIntent_DerivesInternalImpactMode()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var service = new CapturingCodeExploreService();
            var tool = new CodeExploreTool(service, TestPromptLoader.Instance);
            var input = (CodeExploreInput)tool.DeserializeInput("{\"query\":\"what depends on Worker?\"}");

            var execution = await tool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(repository));

            Assert.IsType<CodeExploreResult>(execution.Value);
            Assert.Equal("what depends on Worker?", service.Request?.Query);
            Assert.Equal(CodeExploreMode.Impact, service.Request?.Mode);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Internal traversal and budget controls are not accepted as model tool arguments.</summary>
    [Fact]
    public static async Task CodeExplore_InternalControls_AreRejectedByMinimalSchema()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var pipeline = CreatePipeline(
                events,
                [new CodeExploreTool(new NoopCodeExploreService(), TestPromptLoader.Instance)]);

            var preflight = pipeline.PreflightBatch(
            [
                new ToolBatchRequest(
                    0,
                    "call-1",
                    new ToolInvocationRequest
                    {
                        SessionId = SessionId.New(),
                        RunId = RunId.New(),
                        ToolId = "code_explore",
                        ArgumentsJson = "{\"query\":\"inspect source\",\"limits\":{\"maximumFiles\":20}}",
                        Context = CreateContext(repository) with
                        {
                            TrustLevel = RepositoryTrustLevel.TrustedBuild,
                        },
                    }),
            ]);

            Assert.False(preflight.Succeeded);
            Assert.Equal(0, preflight.FailedOrdinal);
            Assert.Equal("code_explore", preflight.FailedToolId);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Direct typed-tool schema errors bound model-controlled JSON path text.</summary>
    [Fact]
    public static async Task Pipeline_DirectInvalidArguments_BoundsSchemaPathInReturnedAndDurableError()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var observed = new List<IDomainEvent>();
            await using var subscription = events.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });
            var pipeline = CreatePipeline(events, [new CountingTool()]);
            var longPropertyName = new string('x', 1_000);

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "counting",
                ArgumentsJson = "{\"" + longPropertyName + "\":true}",
                Context = CreateContext(repository),
            });

            Assert.Equal(ToolErrorClassification.InvalidArguments, result.ErrorClassification);
            var error = result.Error ?? throw new InvalidOperationException("Expected returned validation error.");
            Assert.True(error.Length <= 512, error);
            Assert.Contains("Tool arguments do not match the declared input schema at $.", error, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 200), error, StringComparison.Ordinal);
            Assert.Contains("...", error, StringComparison.Ordinal);
            var completed = Assert.Single(observed.OfType<ToolInvocationCompleted>());
            var durableError = completed.Error ?? throw new InvalidOperationException("Expected durable validation error.");
            Assert.True(durableError.Length <= 512, durableError);
            Assert.DoesNotContain(new string('x', 200), durableError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Search guidance rejects pasted source-sized queries with actionable bounded-query text.</summary>
    [Fact]
    public static void SearchTextTool_QueryTooLong_DocumentsConciseQueryRequirement()
    {
        var tool = new SearchTextTool(TestPromptLoader.Instance);

        Assert.Contains("query is limited to 500 characters", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("do not paste source blocks", tool.Definition.Description, StringComparison.Ordinal);
        var error = Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
            "{\"query\":\"" + new string('x', 501) + "\"}"));
        Assert.Contains("use a concise literal or regex", error.Message, StringComparison.Ordinal);
        Assert.Contains("not pasted source/tool output", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Host-owned artifact discovery declares optional Git inventory use to policy and scheduling.</summary>
    [Fact]
    public static async Task CodeExplore_HostOwnedArtifactDiscovery_RequiresGitExecutablePolicy()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                1,
                0,
                string.Empty,
                string.Empty,
                false,
                false,
                false,
                TimeSpan.Zero));
            var tool = new CodeExploreTool(
                new NoopCodeExploreService(),
                TestPromptLoader.Instance,
                processManager);
            var input = (CodeExploreInput)tool.DeserializeInput("{\"query\":\"artifact prompt\"}");
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                WorkspaceId = WorkspaceId.New(),
            };

            var claims = tool.GetSchedulingClaims(input, context);
            Assert.Contains(claims, claim => claim.ResourceKind == ToolResourceKind.ProcessPool
                && claim.AccessMode == ToolAccessMode.Execute);

            var pipeline = CreatePipeline(events, [tool]);
            var denied = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "code_explore",
                ArgumentsJson = "{\"query\":\"artifact prompt\"}",
                Context = context,
            });
            var allowed = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "code_explore",
                ArgumentsJson = "{\"query\":\"artifact prompt\"}",
                Context = context with { AllowedExecutables = ["git"] },
            });

            Assert.Equal(ToolErrorClassification.PolicyDenied, denied.ErrorClassification);
            Assert.Contains("git", denied.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.True(allowed.Succeeded, allowed.Error);
            Assert.Equal(0, processManager.ExecutionCount);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Legacy persistent output-shape configuration cannot restore metadata-heavy model output.</summary>
    [Fact]
    public static void CodeExploreOutputOptions_LegacyPersistentFormat_DoesNotOverrideCompactDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tools:codeExplore:outputFormat"] = "structured",
                ["tools:codeExplore:inspectCodeExploreOutput"] = "true",
            })
            .Build();

        var options = CodeExploreOutputOptions.FromConfiguration(configuration);
        var snapshot = options.GetSnapshot(SessionId.New());

        Assert.Equal(CodeExploreOutputFormat.Markdown, snapshot.OutputFormat);
        Assert.True(snapshot.InspectCodeExploreOutput);
    }

    /// <summary>The code_explore output decorator is a pass-through in structured mode.</summary>
    [Fact]
    public static async Task CodeExploreOutputFormattingTool_StructuredMode_DoesNotSetModelResultContent()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var tool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(new NoopCodeExploreService(), TestPromptLoader.Instance),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Structured),
                TestPromptLoader.Instance);
            var input = (CodeExploreInput)tool.DeserializeInput("{\"query\":\"inspect source\"}");
            var execution = await tool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(repository));

            Assert.IsType<CodeExploreResult>(execution.Value);
            Assert.Null(execution.ModelResultContent);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The code_explore output decorator provides Markdown in markdown mode without replacing typed output.</summary>
    [Fact]
    public static async Task CodeExploreOutputFormattingTool_MarkdownMode_AddsModelResultContent()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var tool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(new NoopCodeExploreService(), TestPromptLoader.Instance),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Markdown),
                TestPromptLoader.Instance);
            var input = (CodeExploreInput)tool.DeserializeInput("{\"query\":\"inspect source\"}");
            var execution = await tool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(repository));

            Assert.IsType<CodeExploreResult>(execution.Value);
            Assert.NotNull(execution.ModelResultContent);
            Assert.Contains("**Exploration:** `inspect source`", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("Found 1 symbol across 1 file.", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Selected evidence**", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("Matched the distinctive Worker identifier.", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Blast radius — what depends on these**", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("Caller reaches Worker.Run", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Project:** `Example.Dependent`", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Test:** `Example.Dependent.Tests`", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Source Code**", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Equal(
                1,
                CountOccurrences(
                    execution.ModelResultContent,
                    "Artifact note: omitted range: L5-L10 omitted by artifact character bounds."));
            Assert.Contains("Artifact note: Artifact could not be read safely because it exceeded host bounds.", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Candidate summaries", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Adaptive envelope", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.DoesNotContain("sha256", execution.ModelResultContent, StringComparison.OrdinalIgnoreCase);
            AssertAppearsBefore(execution.ModelResultContent, "**Selected evidence**", "**Source Code**");
            AssertAppearsBefore(execution.ModelResultContent, "**Source Code**", "**Blast radius");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Markdown uses the same selected-model UTF-8 ceiling as the authoritative structured result.</summary>
    [Fact]
    public static async Task CodeExploreOutputFormattingTool_ModelBudget_BoundsRenderedMarkdown()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            const int effectiveInputTokens = 400;
            const int maximumResultBytes = effectiveInputTokens * 3;
            var tool = new CodeExploreOutputFormattingTool(
                new StaticCodeExploreResultTool(CreateRichCodeExploreResult()),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Markdown),
                TestPromptLoader.Instance);
            var execution = await tool.ExecuteAsync(
                new CodeExploreInput { Query = "inspect source" },
                CreateCodeExploreExecutionContext(repository, modelEffectiveInputBudgetTokens: effectiveInputTokens));
            var markdown = execution.ModelResultContent
                ?? throw new InvalidOperationException("Expected Markdown code_explore content.");

            Assert.True(
                Encoding.UTF8.GetByteCount(markdown) <= maximumResultBytes,
                "The rendered Markdown exceeded the selected-model result ceiling.");
            Assert.Contains(
                "additional Markdown was omitted to fit the selected model input budget",
                markdown,
                StringComparison.Ordinal);
            Assert.Equal(0, CountFenceLines(markdown) % 2);
            Assert.True(execution.IsTruncated);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The production pipeline reapplies code-explore bounds after output sanitization expands content.</summary>
    [Fact]
    public static async Task CodeExplorePipeline_ModelBudget_BoundsSanitizedStructuredAndMarkdownOutput()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            const int effectiveInputTokens = 1_400;
            const int maximumResultBytes = effectiveInputTokens * 3;
            await using var events = new DomainEventStream();
            var observed = new List<IDomainEvent>();
            await using var subscription = events.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });
            var tool = new CodeExploreOutputFormattingTool(
                new StaticCodeExploreResultTool(CreateSanitizerExpansionCodeExploreResult()),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Markdown),
                TestPromptLoader.Instance);
            var input = new CodeExploreInput { Query = "inspect source" };
            var directExecution = await tool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(
                    repository,
                    modelEffectiveInputBudgetTokens: effectiveInputTokens));
            var directMarkdown = directExecution.ModelResultContent
                ?? throw new InvalidOperationException("Expected direct Markdown code_explore content.");
            Assert.False(directExecution.IsTruncated);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(directExecution.Value).Length <= maximumResultBytes);
            Assert.True(Encoding.UTF8.GetByteCount(directMarkdown) <= maximumResultBytes);

            var pipeline = CreatePipeline(
                events,
                [tool],
                sanitizer: new ExpandingCodeExploreSanitizer());
            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "code_explore",
                ArgumentsJson = "{\"query\":\"inspect source\"}",
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    WorkspaceId = WorkspaceId.New(),
                    ModelEffectiveInputBudgetTokens = effectiveInputTokens,
                },
            });

            Assert.True(result.Succeeded, result.Error);
            Assert.True(result.IsTruncated);
            Assert.NotNull(result.ResultJson);
            Assert.NotNull(result.ModelResultContent);
            Assert.True(Encoding.UTF8.GetByteCount(result.ResultJson) <= maximumResultBytes);
            Assert.True(Encoding.UTF8.GetByteCount(result.ModelResultContent) <= maximumResultBytes);
            _ = JsonSerializer.Deserialize<CodeExploreResult>(result.ResultJson)
                ?? throw new InvalidOperationException("Expected bounded structured code_explore output.");
            var completed = Assert.Single(observed.OfType<ToolInvocationCompleted>());
            Assert.True(completed.IsTruncated);
            Assert.Equal(result.ResultJson, completed.ResultJson);
            Assert.Equal(result.ModelResultContent, completed.ModelResultContent);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The production adapter returns a guaranteed bounded DTO and Markdown envelope at the model-budget floor.</summary>
    [Fact]
    public static async Task CodeExploreOutputFormattingTool_FloorModelBudget_BoundsLongQueryResult()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            const int maximumResultBytes = 1024;
            var tool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(
                    new LongQueryResultCodeExploreService(),
                    TestPromptLoader.Instance),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Markdown),
                TestPromptLoader.Instance);
            var execution = await tool.ExecuteAsync(
                new CodeExploreInput { Query = new string('q', 1024) },
                CreateCodeExploreExecutionContext(repository, modelEffectiveInputBudgetTokens: 1));
            var result = Assert.IsType<CodeExploreResult>(execution.Value);
            var markdown = execution.ModelResultContent
                ?? throw new InvalidOperationException("Expected Markdown code_explore content.");

            Assert.True(
                JsonSerializer.SerializeToUtf8Bytes(result).Length <= maximumResultBytes,
                "The terminal structured result exceeded the model-budget floor.");
            Assert.True(
                Encoding.UTF8.GetByteCount(markdown) <= maximumResultBytes,
                "The terminal Markdown result exceeded the model-budget floor.");
            Assert.False(result.Coverage.OutputComplete);
            Assert.True(execution.IsTruncated);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Artifact continuation origin metadata does not replace the established positional constructor/deconstructor contract.</summary>
    [Fact]
    public static void CodeExploreArtifactContinuationTarget_OriginMetadata_PreservesPositionalContract()
    {
        var target = new CodeExploreArtifactContinuationTarget(
            "prompts/worker.md",
            5,
            10,
            new string('a', 64),
            1,
            "Continue artifact content.")
        {
            OriginSymbolId = "symbol:example.worker",
            OriginFilePath = "src/Worker.cs",
            OriginRange = new SourceRange(3, 1, 8, 2),
        };

        var (filePath, startLine, endLine, digest, generation, reason) = target;

        Assert.Equal("prompts/worker.md", filePath);
        Assert.Equal(5, startLine);
        Assert.Equal(10, endLine);
        Assert.Equal(new string('a', 64), digest);
        Assert.Equal(1, generation);
        Assert.Equal("Continue artifact content.", reason);
        Assert.Equal("symbol:example.worker", target.OriginSymbolId);
    }

    /// <summary>Markdown impact output reports complete host totals while showing balanced representative evidence.</summary>
    [Fact]
    public static async Task CodeExploreOutputFormattingTool_ManyImpactItems_RendersRepresentativeSummary()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var tool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(new ManyImpactCodeExploreService(), TestPromptLoader.Instance),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Markdown),
                TestPromptLoader.Instance);
            var input = (CodeExploreInput)tool.DeserializeInput("{\"query\":\"what depends on Worker?\"}");
            var execution = await tool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(repository));
            var markdown = execution.ModelResultContent
                ?? throw new InvalidOperationException("Expected Markdown code_explore content.");

            Assert.Contains(
                "6 of 12 callers, 4 of 4 implementations, 20 of 35 projects, and 20 of 40 tests",
                markdown,
                StringComparison.Ordinal);
            Assert.Equal(2, CountOccurrences(markdown, "- **Caller:**"));
            Assert.Equal(2, CountOccurrences(markdown, "- **Implementation:**"));
            Assert.Equal(2, CountOccurrences(markdown, "- **Project:**"));
            Assert.Equal(2, CountOccurrences(markdown, "- **Test:**"));
            Assert.Contains("42 impact items not shown", markdown, StringComparison.Ordinal);
            AssertAppearsBefore(markdown, "- **Caller:**", "- **Project:**");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Impact coverage remains visible when model-budget trimming removes every detailed item.</summary>
    [Fact]
    public static async Task CodeExploreOutputFormattingTool_ZeroVisibleImpactItems_PreservesCoverage()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var tool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(
                    new ZeroItemImpactCodeExploreService(),
                    TestPromptLoader.Instance),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Markdown),
                TestPromptLoader.Instance);
            var input = (CodeExploreInput)tool.DeserializeInput("{\"query\":\"what depends on Worker?\"}");
            var execution = await tool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(repository));
            var markdown = execution.ModelResultContent
                ?? throw new InvalidOperationException("Expected Markdown code_explore content.");

            Assert.Contains("**Blast radius — what depends on these**", markdown, StringComparison.Ordinal);
            Assert.Contains("1 of 1 caller", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("- **Caller:**", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Markdown follow-up cursors replay exact source, impact, and artifact continuations through the minimal query schema.</summary>
    [Fact]
    public static async Task CodeExploreOutputFormattingTool_MarkdownContinuations_AreReplayableQueries()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var formattingTool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(new NoopCodeExploreService(), TestPromptLoader.Instance),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Markdown),
                TestPromptLoader.Instance);
            var input = (CodeExploreInput)formattingTool.DeserializeInput("{\"query\":\"what depends on Worker?\"}");
            var execution = await formattingTool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(repository));
            var markdown = execution.ModelResultContent
                ?? throw new InvalidOperationException("Expected Markdown code_explore content.");
            var sourceCursor = ExtractContinuationCursor(markdown, "Source");
            var impactCursor = ExtractContinuationCursor(markdown, "Impact");
            var artifactCursor = ExtractContinuationCursor(markdown, "Artifact");

            var sourceService = new CapturingCodeExploreService();
            _ = await new CodeExploreTool(sourceService, TestPromptLoader.Instance).ExecuteAsync(
                new CodeExploreInput { Query = sourceCursor },
                CreateCodeExploreExecutionContext(repository));
            var sourceAnchor = Assert.Single(sourceService.Request?.PathAnchors ?? []);
            Assert.Equal("src/Worker.cs", sourceAnchor.Path);
            Assert.Equal(5, sourceAnchor.Line);
            Assert.Equal(8, sourceAnchor.EndLine);
            Assert.Equal(CodeExplorePathSelectionMode.ExactLineRange, sourceAnchor.SelectionMode);
            Assert.Equal(CodeExploreMode.Auto, sourceService.Request?.Mode);

            var impactService = new CapturingCodeExploreService();
            _ = await new CodeExploreTool(impactService, TestPromptLoader.Instance).ExecuteAsync(
                new CodeExploreInput { Query = impactCursor },
                CreateCodeExploreExecutionContext(repository));
            Assert.Equal(CodeExploreMode.Impact, impactService.Request?.Mode);
            Assert.Equal("symbol:example.worker", Assert.Single(impactService.Request?.SymbolIds ?? []));

            var artifactService = new CapturingCodeExploreService();
            _ = await new CodeExploreTool(artifactService, TestPromptLoader.Instance).ExecuteAsync(
                new CodeExploreInput { Query = artifactCursor },
                CreateCodeExploreExecutionContext(repository));
            Assert.Equal(CodeExploreAssociatedArtifactsMode.Enabled, artifactService.Request?.AssociatedArtifacts);
            Assert.Equal("symbol:example.worker", Assert.Single(artifactService.Request?.SymbolIds ?? []));
            var artifactAnchor = Assert.Single(artifactService.Request?.AssociatedArtifactPathAnchors ?? []);
            Assert.Equal("prompts/worker.md", artifactAnchor.Path);
            Assert.Equal(5, artifactAnchor.Line);
            Assert.Equal(10, artifactAnchor.EndLine);
            Assert.Equal(new string('a', 64), artifactAnchor.ExpectedFileSha256);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Long questions retain an artifact replay cursor by dropping optional embedded query text.</summary>
    [Fact]
    public static async Task CodeExploreOutputFormattingTool_LongQueryArtifactContinuation_RemainsReplayable()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var formattingTool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(
                    new LongArtifactContinuationCodeExploreService(),
                    TestPromptLoader.Instance),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Markdown),
                TestPromptLoader.Instance);
            var query = new string('q', 240);
            var inputJson = JsonSerializer.Serialize(new { query });
            var input = (CodeExploreInput)formattingTool.DeserializeInput(inputJson);
            var execution = await formattingTool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(repository));
            var markdown = execution.ModelResultContent
                ?? throw new InvalidOperationException("Expected Markdown code_explore content.");
            var artifactCursor = ExtractContinuationCursor(markdown, "Artifact");

            var artifactService = new CapturingCodeExploreService();
            _ = await new CodeExploreTool(artifactService, TestPromptLoader.Instance).ExecuteAsync(
                new CodeExploreInput { Query = artifactCursor },
                CreateCodeExploreExecutionContext(repository));

            Assert.Equal(CreateLongArtifactOriginSymbolId(), artifactService.Request?.Query);
            Assert.Equal(CodeExploreAssociatedArtifactsMode.Enabled, artifactService.Request?.AssociatedArtifacts);
            Assert.Equal("prompts/worker.md", Assert.Single(
                artifactService.Request?.AssociatedArtifactPathAnchors ?? []).Path);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Markdown keeps many follow-up targets readable by bounding embedded retry cursors.</summary>
    [Fact]
    public static async Task CodeExploreOutputFormattingTool_ManyContinuations_BoundsRetryCursorNoise()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var formattingTool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(
                    new ManyContinuationCodeExploreService(),
                    TestPromptLoader.Instance),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Markdown),
                TestPromptLoader.Instance);
            var input = (CodeExploreInput)formattingTool.DeserializeInput("{\"query\":\"inspect more source\"}");
            var execution = await formattingTool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(repository));
            var markdown = execution.ModelResultContent
                ?? throw new InvalidOperationException("Expected Markdown code_explore content.");

            Assert.Contains("**Follow-up targets**", markdown, StringComparison.Ordinal);
            Assert.Contains("src/WorkerExtra1.cs", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("src/WorkerExtra2.cs", markdown, StringComparison.Ordinal);
            Assert.Equal(3, CountOccurrences(markdown, "Retry query:"));
            Assert.Contains("1 retry query cursor omitted", markdown, StringComparison.Ordinal);
            Assert.Contains("4 follow-up targets not shown", markdown, StringComparison.Ordinal);

            var sourceCursor = ExtractContinuationCursor(markdown, "Source");
            var impactCursor = ExtractContinuationCursor(markdown, "Impact");
            var artifactCursor = ExtractContinuationCursor(markdown, "Artifact");

            var sourceService = new CapturingCodeExploreService();
            _ = await new CodeExploreTool(sourceService, TestPromptLoader.Instance).ExecuteAsync(
                new CodeExploreInput { Query = sourceCursor },
                CreateCodeExploreExecutionContext(repository));
            Assert.Equal("src/WorkerExtra0.cs", Assert.Single(sourceService.Request?.PathAnchors ?? []).Path);

            var impactService = new CapturingCodeExploreService();
            _ = await new CodeExploreTool(impactService, TestPromptLoader.Instance).ExecuteAsync(
                new CodeExploreInput { Query = impactCursor },
                CreateCodeExploreExecutionContext(repository));
            Assert.Equal(CodeExploreMode.Impact, impactService.Request?.Mode);

            var artifactService = new CapturingCodeExploreService();
            _ = await new CodeExploreTool(artifactService, TestPromptLoader.Instance).ExecuteAsync(
                new CodeExploreInput { Query = artifactCursor },
                CreateCodeExploreExecutionContext(repository));
            Assert.Equal(CodeExploreAssociatedArtifactsMode.Enabled, artifactService.Request?.AssociatedArtifacts);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Session output overrides do not affect other host sessions.</summary>
    [Fact]
    public static async Task CodeExploreOutputFormattingTool_SessionOverride_DoesNotAffectOtherSessions()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var markdownSession = SessionId.New();
            var structuredSession = SessionId.New();
            var options = new CodeExploreOutputOptions();
            _ = options.SetSessionOutputFormat(structuredSession, CodeExploreOutputFormat.Structured);
            var tool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(new NoopCodeExploreService(), TestPromptLoader.Instance),
                options,
                TestPromptLoader.Instance);
            var input = (CodeExploreInput)tool.DeserializeInput("{\"query\":\"inspect source\"}");

            var markdown = await tool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(repository, markdownSession));
            var structured = await tool.ExecuteAsync(
                input,
                CreateCodeExploreExecutionContext(repository, structuredSession));

            Assert.NotNull(markdown.ModelResultContent);
            Assert.Null(structured.ModelResultContent);
            Assert.Equal(CodeExploreOutputFormat.Markdown, options.GetOutputFormat(markdownSession));
            Assert.Equal(CodeExploreOutputFormat.Structured, options.GetOutputFormat(structuredSession));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The pipeline publishes both structured and model-visible code_explore output when markdown mode is enabled.</summary>
    [Fact]
    public static async Task CodeExplorePipeline_MarkdownMode_PreservesStructuredResultAndPublishesModelContent()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var observed = new List<IDomainEvent>();
            await using var subscription = events.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });
            var tool = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(new NoopCodeExploreService(), TestPromptLoader.Instance),
                new CodeExploreOutputOptions(CodeExploreOutputFormat.Markdown),
                TestPromptLoader.Instance);
            var pipeline = CreatePipeline(events, [tool]);

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "code_explore",
                ArgumentsJson = "{\"query\":\"inspect source\"}",
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                },
            });

            Assert.True(result.Succeeded, result.Error);
            Assert.NotNull(result.ResultJson);
            Assert.NotNull(result.ModelResultContent);
            Assert.Contains("**Exploration:** `inspect source`", result.ModelResultContent, StringComparison.Ordinal);
            var structured = JsonSerializer.Deserialize<CodeExploreResult>(result.ResultJson);
            Assert.NotNull(structured);
            var completed = Assert.Single(observed.OfType<ToolInvocationCompleted>());
            Assert.Equal(result.ResultJson, completed.ResultJson);
            Assert.Equal(result.ModelResultContent, completed.ModelResultContent);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Unix path containment does not ignore case.</summary>
    [Fact]
    public static async Task Policy_LinuxPathContainment_IsCaseSensitive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var parent = CreateTemporaryDirectory();
        var repository = Path.Combine(parent, "Repo");
        var outside = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(outside);
        try
        {
            var outsideFile = Path.Combine(outside, "outside.txt");
            await File.WriteAllTextAsync(outsideFile, "outside");
            await using var events = new DomainEventStream();
            var pipeline = CreatePipeline(events, [new ReadFileTool(TestPromptLoader.Instance)]);

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "read_file",
                ArgumentsJson = JsonSerializer.Serialize(new ReadFileInput { Path = outsideFile }),
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                },
            });

            Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    /// <summary>Structured tool-result redaction preserves the JSON envelope returned to the model.</summary>
    [Fact]
    public static async Task ToolPipeline_SanitizesStructuredValuesWithoutCorruptingJson()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repository, "source.cs"),
                "Call(cancellationToken: cancellationToken);\nvar apiKey = \"sk-abcdefghijklmnopqrstuvwxyz\";\n");
            await using var events = new DomainEventStream();
            var pipeline = CreatePipeline(
                events,
                [new ReadFileTool(TestPromptLoader.Instance)],
                sanitizer: new SecretOutputSanitizer());

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "read_file",
                ArgumentsJson = "{\"path\":\"source.cs\",\"startLine\":1,\"maximumLines\":10}",
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                },
            });

            Assert.True(result.Succeeded, result.Error);
            using var document = JsonDocument.Parse(Assert.IsType<string>(result.ResultJson));
            var lines = document.RootElement.GetProperty("Lines")
                .EnumerateArray()
                .Select(line => line.GetString() ?? string.Empty)
                .ToArray();
            Assert.Contains(lines, line => line.Contains("cancellationToken: cancellationToken", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("[REDACTED]", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("sk-abcdefghijklmnopqrstuvwxyz", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>A process-manager timeout becomes a failed tool invocation.</summary>
    [Fact]
    public static async Task ProcessTool_ManagerTimeout_IsNormalizedAsTimeout()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var manager = new StubProcessManager(new ProcessExecutionResult(
                1,
                null,
                string.Empty,
                string.Empty,
                false,
                false,
                true,
                TimeSpan.FromSeconds(1)));
            var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
            var pipeline = new ToolInvocationPipeline(
                new ToolRegistry([new RunProcessTool(
                    manager,
                    TestPromptLoader.Instance,
                    allowedExecutables: [shell],
                    shellExecutable: shell)]),
                new DefaultPolicyEngine(),
                new AllowApprovalPolicy(),
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget());

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "run_process",
                ArgumentsJson = "{\"command\":\"dotnet --version\",\"timeoutSeconds\":1}",
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    AllowedExecutables = [shell],
                },
            });

            Assert.False(result.Succeeded);
            Assert.Equal(ToolErrorClassification.Timeout, result.ErrorClassification);
            Assert.Equal(1, manager.ExecutionCount);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Exhausted budgets reject before approval without consuming additional usage.</summary>
    [Fact]
    public static async Task Pipeline_ExhaustedBudget_DoesNotRequestApproval()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var approval = new CountingApprovalPolicy();
            var tool = new CountingTool();
            var pipeline = new ToolInvocationPipeline(
                new ToolRegistry([tool]),
                new DefaultPolicyEngine(),
                approval,
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                new ExecutionBudget(new BudgetDimensions(100, 0, TimeSpan.FromMinutes(1))));

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "counting",
                ArgumentsJson = "{\"value\":\"valid\"}",
                Context = CreateContext(repository) with
                {
                    RequireApprovalToolIds = ["counting"],
                },
            });

            Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
            Assert.Equal(0, approval.RequestCount);
            Assert.Equal(0, tool.ExecutionCount);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Approval cancellation closes both the approval and invocation event lifecycles.</summary>
    [Fact]
    public static async Task Pipeline_ApprovalCancellation_PublishesDenialAndCompletion()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var observed = new List<IDomainEvent>();
            await using var subscription = events.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });
            var pipeline = new ToolInvocationPipeline(
                new ToolRegistry([new CountingTool()]),
                new DefaultPolicyEngine(),
                new CancellingApprovalPolicy(),
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.InvokeAsync(
                new ToolInvocationRequest
                {
                    SessionId = SessionId.New(),
                    RunId = RunId.New(),
                    ToolId = "counting",
                    ArgumentsJson = "{\"value\":\"valid\"}",
                    Context = CreateContext(repository) with
                    {
                        RequireApprovalToolIds = ["counting"],
                    },
                }));

            Assert.Contains(observed, item => item is ToolInvocationStarted);
            Assert.Contains(observed, item => item is ApprovalRequested requested
                && requested.Kind == ApprovalRequestKind.ToolInvocation
                && requested.SchemaVersion == 2);
            Assert.Contains(observed, item => item is ApprovalDenied);
            Assert.Contains(observed, item => item is ToolInvocationCompleted);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Logical secret references resolve only through the final secrets scope.</summary>
    [Fact]
    public static async Task SecretStore_ResolvesLogicalReferencesOnly()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["secrets:models:test"] = "credential",
            })
            .Build();
        var store = new ConfigurationSecretStore(configuration);

        Assert.Equal("credential", await store.GetAsync("secrets:models:test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetAsync("model:profiles:0:apiKey"));
    }

    /// <summary>File, search, and Git inspection tools return bounded results with provenance.</summary>
    [Fact]
    public static async Task BuiltInInspectionTools_ReturnAttributableResults()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repository, "visible.txt"),
                "first line\nneedle line");
            await using var events = new DomainEventStream();
            var manager = new ProcessManager(
                new TestSanitizer(),
                NullLogger<ProcessManager>.Instance);
            var pipeline = CreatePipeline(
                events,
                [new ReadFileTool(TestPromptLoader.Instance), new SearchTextTool(TestPromptLoader.Instance), new GitStatusTool(manager, TestPromptLoader.Instance)]);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };
            var read = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "read_file",
                ArgumentsJson = "{\"path\":\"visible.txt\",\"maximumLines\":2}",
                Context = context,
            });
            var search = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "search",
                ArgumentsJson = "{\"query\":\"needle\",\"glob\":\"*.txt\"}",
                Context = context,
            });
            var sourceRepository = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var git = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "git_status",
                ArgumentsJson = "{}",
                Context = CreateContext(sourceRepository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                    AllowedExecutables = ["git"],
                },
            });

            Assert.True(read.Succeeded);
            Assert.Contains("needle line", read.ResultJson, StringComparison.Ordinal);
            Assert.Contains(read.Sources, source => source.Kind == "file");
            Assert.True(search.Succeeded);
            Assert.Contains(search.Sources, source => source.Range == "L2");
            Assert.True(git.Succeeded, git.Error);
            Assert.Contains(git.Sources, source => source.Kind == "git");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Literal repository search uses bounded ripgrep output when the host executable is available.</summary>
    [Fact]
    public static async Task SearchTextTool_LiteralSearch_UsesRipgrepFastPath()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                42,
                0,
                "{\"type\":\"match\",\"data\":{\"path\":{\"text\":\"./.hidden/endpoint.cs\"},\"lines\":{\"text\":\"route needle\\n\"},\"line_number\":12,\"submatches\":[{\"start\":7}]}}\n"
                    + "{\"type\":\"match\",\"data\":{\"path\":{\"text\":\"./second.cs\"},\"lines\":{\"text\":\"needle\\n\"},\"line_number\":4,\"submatches\":[{\"start\":1}]}}\n",
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(10)));
            var bundledRipgrep = Path.Combine(repository, "bundled tools", "rg.exe");
            var tool = new SearchTextTool(
                TestPromptLoader.Instance,
                processManager: processManager,
                ripgrepExecutable: bundledRipgrep);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle", MaximumMatches = 1 },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal(".hidden/endpoint.cs", match.Path);
            Assert.Equal(12, match.Line);
            Assert.Equal(8, match.Column);
            Assert.True(result.Value.IsTruncated);
            Assert.Null(result.Value.Warning);
            var request = Assert.IsType<ProcessExecutionRequest>(processManager.LastRequest);
            Assert.Equal(bundledRipgrep, request.FileName);
            Assert.Equal(ProcessRequestOrigin.Host, request.Origin);
            Assert.Equal(ProcessStandardOutputFormat.RipgrepJsonLines, request.StandardOutputFormat);
            Assert.Contains("--json", request.Arguments);
            Assert.DoesNotContain("--null", request.Arguments);
            Assert.Contains("--fixed-strings", request.Arguments);
            Assert.Contains("--ignore-case", request.Arguments);
            Assert.Contains("--max-filesize=1048576", request.Arguments);
            Assert.Contains("--iglob=!**/*.db", request.Arguments);
            Assert.Contains("--iglob=!**/*.sqlite", request.Arguments);
            Assert.Contains("--iglob=!**/*.sqlite3", request.Arguments);
            string[] excludedDirectoryGlobs =
            [
                "--iglob=!**/.codegraph/**",
                "--iglob=!**/.git/**",
                "--iglob=!**/.idea/**",
                "--iglob=!**/.vs/**",
                "--iglob=!**/artifacts/**",
                "--iglob=!**/bin/**",
                "--iglob=!**/node_modules/**",
                "--iglob=!**/obj/**",
                "--iglob=!**/TestResults/**",
            ];
            Assert.All(
                excludedDirectoryGlobs,
                glob => Assert.Contains(glob, request.Arguments));
            Assert.DoesNotContain("--glob=!**/*.sqlite", request.Arguments);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Host-truncated ripgrep output retains complete JSON records before an incomplete final record.</summary>
    [Fact]
    public static async Task SearchTextTool_TruncatedRipgrepJson_PreservesCompleteMatches()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            const string completeRecord = "{\"type\":\"match\",\"data\":{\"path\":{\"text\":\"./first.cs\"},\"lines\":{\"text\":\"needle\\n\"},\"line_number\":3,\"submatches\":[{\"start\":0}]}}\n";
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                42,
                0,
                completeRecord + "{\"type\":\"match\",\"data\":{\"path\":{\"text\":\"./second.cs\"}",
                string.Empty,
                StandardOutputTruncated: true,
                StandardErrorTruncated: false,
                TimedOut: false,
                Duration: TimeSpan.FromMilliseconds(10)));
            var tool = new SearchTextTool(TestPromptLoader.Instance, processManager: processManager);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("first.cs", match.Path);
            Assert.Equal(3, match.Line);
            Assert.True(result.Value.IsTruncated);
            Assert.True(result.IsTruncated);
            Assert.Contains(result.Sources, source => source.Identifier == "first.cs");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Real ripgrep output remains parseable after the process sanitizer removes unsafe controls.</summary>
    [Fact]
    public static async Task SearchTextTool_RipgrepFastPath_ReturnsMatchesAfterProcessSanitization()
    {
        if (!IsExecutableAvailable("rg"))
        {
            Assert.Skip("ripgrep is not available on PATH.");
        }

        var repository = CreateTemporaryDirectory();
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(repository, "src")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "Nested.cs"),
                "// token: abc\npublic static class Nested;\n");
            var processManager = new ProcessManager(
                new SecretOutputSanitizer(),
                NullLogger<ProcessManager>.Instance);
            var tool = new SearchTextTool(TestPromptLoader.Instance, processManager: processManager, ripgrepExecutable: "rg");
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput
                {
                    Query = "token",
                    Glob = "*.cs",
                    MaximumMatches = 5,
                },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("src/Nested.cs", match.Path);
            Assert.Equal(1, match.Line);
            Assert.Contains("[REDACTED]", match.Text, StringComparison.Ordinal);
            Assert.Null(result.Value.Warning);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>A real host-truncated ripgrep JSON record does not discard preceding complete matches.</summary>
    [Fact]
    public static async Task SearchTextTool_RealRipgrepTruncation_PreservesCompleteMatches()
    {
        if (!IsExecutableAvailable("rg"))
        {
            Assert.Skip("ripgrep is not available on PATH.");
        }

        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repository, "large.txt"),
                "needle small\nneedle " + new string('x', 300_000) + "\n");
            var processManager = new ProcessManager(
                new SecretOutputSanitizer(),
                NullLogger<ProcessManager>.Instance);
            var tool = new SearchTextTool(TestPromptLoader.Instance, processManager: processManager, ripgrepExecutable: "rg");
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle", MaximumMatches = 5 },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("large.txt", match.Path);
            Assert.Equal("needle small", match.Text);
            Assert.True(result.Value.IsTruncated);
            Assert.True(result.IsTruncated);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Regex and narrowed-glob searches use ripgrep without literal-mode projection.</summary>
    [Fact]
    public static async Task SearchTextTool_RegexAndGlob_UseRipgrepFastPath()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                42,
                0,
                "{\"type\":\"match\",\"data\":{\"path\":{\"text\":\"src/Retriever.cs\"},\"lines\":{\"text\":\"internal sealed class Retriever : IRetriever\\n\"},\"line_number\":7,\"submatches\":[{\"start\":13}]}}\n",
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(10)));
            var tool = new SearchTextTool(TestPromptLoader.Instance, processManager: processManager);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput
                {
                    Query = @"class\s+\w+\s*:\s*IRetriever",
                    UseRegularExpression = true,
                    Glob = "*.cs",
                },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("src/Retriever.cs", match.Path);
            Assert.Null(result.Value.Warning);
            var request = Assert.IsType<ProcessExecutionRequest>(processManager.LastRequest);
            Assert.DoesNotContain("--fixed-strings", request.Arguments);
            Assert.DoesNotContain("--ignore-case", request.Arguments);
            Assert.Contains("--iglob=*.cs", request.Arguments);
            Assert.Contains("--regexp", request.Arguments);
            Assert.Contains(@"class\s+\w+\s*:\s*IRetriever", request.Arguments);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Ripgrep exclusion dialect differences cannot bypass authoritative prohibited-path filtering.</summary>
    [Fact]
    public static async Task SearchTextTool_ProhibitedPaths_FilterRipgrepResultsAuthoritatively()
    {
        if (!IsExecutableAvailable("rg"))
        {
            Assert.Skip("ripgrep is not available on PATH.");
        }

        var repository = CreateTemporaryDirectory();
        try
        {
            var docs = Directory.CreateDirectory(Path.Combine(repository, "docs")).FullName;
            const string prohibitedName = "[token=abc].md";
            await File.WriteAllTextAsync(Path.Combine(docs, prohibitedName), "needle prohibited\n");
            await File.WriteAllTextAsync(Path.Combine(docs, "final.md"), "needle allowed\n");
            var processManager = new ProcessManager(
                new SecretOutputSanitizer(),
                NullLogger<ProcessManager>.Instance);
            var tool = new SearchTextTool(TestPromptLoader.Instance, processManager: processManager, ripgrepExecutable: "rg");
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
                ProhibitedPaths = [$"docs/{prohibitedName}"],
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle", Glob = "*.md" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("docs/final.md", match.Path);
            Assert.DoesNotContain(result.Sources, source => source.Identifier == $"docs/{prohibitedName}");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The managed regular-expression fallback preserves case-sensitive matching.</summary>
    [Fact]
    public static async Task SearchTextTool_ManagedRegexSearch_IsCaseSensitive()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repository, "markers.txt"),
                "TODO\ntodo\n");
            var tool = new SearchTextTool(TestPromptLoader.Instance);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput
                {
                    Query = "TODO",
                    UseRegularExpression = true,
                    Glob = "*.txt",
                },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("TODO", match.Text);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Failed ripgrep execution fails visibly instead of silently using a slower fallback.</summary>
    [Fact]
    public static async Task SearchTextTool_RipgrepFailure_FailsVisibly()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var processManager = new RoutingProcessManager(request =>
                request.FileName.Equals("git", StringComparison.OrdinalIgnoreCase)
                    ? CreateProcessResult("src/visible.txt\0")
                    : CreateProcessResult(string.Empty, exitCode: 2));
            var tool = new SearchTextTool(TestPromptLoader.Instance, processManager: processManager);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(
                new SearchTextInput { Query = "needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None));

            Assert.Contains("Ripgrep exited with code 2", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                processManager.Requests,
                request => request.FileName.Equals("git", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The managed fallback treats simple extension globs like ripgrep and matches nested files.</summary>
    [Fact]
    public static async Task SearchTextTool_ManagedSimpleGlob_MatchesNestedFiles()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(repository, "src", "nested")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "Marker.cs"),
                "public const string Marker = \"transparency\";\n");
            var tool = new SearchTextTool(TestPromptLoader.Instance);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput
                {
                    Query = "transparency",
                    Glob = "*.cs",
                    MaximumMatches = 5,
                },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("src/nested/Marker.cs", match.Path);
            Assert.Contains("transparency", match.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>A Git-inventoried symbolic-link leaf cannot expose content outside the repository.</summary>
    [Fact]
    public static async Task SearchTextTool_GitInventorySymlink_IsNotRead()
    {
        var repository = CreateTemporaryDirectory();
        var outsideDirectory = CreateTemporaryDirectory();
        try
        {
            var outsidePath = Path.Combine(outsideDirectory, "outside.txt");
            await File.WriteAllTextAsync(outsidePath, "outside needle");
            var linkPath = Path.Combine(repository, "linked.txt");
            try
            {
                File.CreateSymbolicLink(linkPath, outsidePath);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException
                    or PlatformNotSupportedException
                    or IOException)
            {
                Assert.Skip($"Symbolic-link creation is unavailable: {exception.GetType().Name}.");
            }

            var processManager = new RoutingProcessManager(request =>
            {
                if (request.FileName.Equals("git", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateProcessResult("linked.txt\0");
                }

                throw new Win32Exception(2);
            });
            var tool = new SearchTextTool(TestPromptLoader.Instance, processManager: processManager);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "outside needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            Assert.Empty(result.Value.Matches);
            Assert.DoesNotContain(
                result.Sources,
                source => source.Identifier.Equals("linked.txt", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    /// <summary>A native missing-executable start failure uses the confined managed scanner.</summary>
    [Fact]
    public static async Task SearchTextTool_MissingRipgrepProcessStart_UsesManagedFallback()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "visible.txt"), "needle");
            var processManager = new RoutingProcessManager(request =>
            {
                if (request.FileName.Equals("git", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateProcessResult("visible.txt\0");
                }

                throw new Win32Exception(2);
            });
            var tool = new SearchTextTool(
                TestPromptLoader.Instance,
                processManager: processManager,
                ripgrepExecutable: $"missing-ripgrep-{Guid.NewGuid():N}");
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("visible.txt", match.Path);
            Assert.Contains("Ripgrep was not found", result.Value.Warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Text search prunes generated and Git metadata subtrees before reading files.</summary>
    [Fact]
    public static async Task SearchTextTool_GeneratedAndGitSubtrees_ArePruned()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "visible.txt"), "needle");
            string[] excludedDirectories =
            [
                ".codegraph",
                ".git",
                ".idea",
                ".vs",
                "artifacts",
                "bin",
                "node_modules",
                "obj",
                "TestResults",
            ];
            foreach (var excludedDirectory in excludedDirectories)
            {
                var directory = Directory.CreateDirectory(Path.Combine(repository, excludedDirectory)).FullName;
                await File.WriteAllTextAsync(Path.Combine(directory, "excluded.txt"), "needle");
            }

            var hostStateDirectory = Directory.CreateDirectory(Path.Combine(repository, ".threadsmith")).FullName;
            await File.WriteAllTextAsync(Path.Combine(hostStateDirectory, "threadsmith.db"), "needle");

            var tool = new SearchTextTool(TestPromptLoader.Instance);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("visible.txt", match.Path);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Model-facing tool descriptions enforce semantic-first selection.</summary>
    [Fact]
    public static void SemanticToolDescriptions_RequireSemanticFirstSelection()
    {
        var resolver = new DescriptionSemanticResolver();
        string[] descriptions =
        [
            new FindSymbolTool(resolver, TestPromptLoader.Instance).Definition.Description,
            new FindReferencesTool(resolver, TestPromptLoader.Instance).Definition.Description,
            new FindImplementationsTool(resolver, TestPromptLoader.Instance).Definition.Description,
        ];

        Assert.All(descriptions, description =>
        {
            Assert.Contains("MUST use", description, StringComparison.Ordinal);
            Assert.Contains("before search", description, StringComparison.Ordinal);
        });
        Assert.Contains(
            "interface implementations",
            new FindImplementationsTool(resolver, TestPromptLoader.Instance).Definition.Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "MUST NOT replace an advertised semantic tool",
            new SearchTextTool(TestPromptLoader.Instance).Definition.Description,
            StringComparison.Ordinal);
    }

    /// <summary>Host-owned tool definitions use exact source assets, including rendered tokens.</summary>
    [Fact]
    public static void HostOwnedToolDescriptions_EqualSourcePromptAssets()
    {
        var processManager = new DirectProcessStartManager();
        var resolver = new DescriptionSemanticResolver();
        ITool[] tools =
        [
            new ListFilesTool(TestPromptLoader.Instance),
            new ReadFileTool(TestPromptLoader.Instance),
            new SearchTextTool(TestPromptLoader.Instance),
            new GitStatusTool(processManager, TestPromptLoader.Instance),
            new FindSymbolTool(resolver, TestPromptLoader.Instance),
            new FindReferencesTool(resolver, TestPromptLoader.Instance),
            new FindImplementationsTool(resolver, TestPromptLoader.Instance),
            new DateTimeTool(TestPromptLoader.Instance),
            new CSharpScriptTool(new StubCSharpScriptEngine(), TestPromptLoader.Instance),
            new CodeExploreTool(new NoopCodeExploreService(), TestPromptLoader.Instance),
        ];
        var assets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["list_files"] = PromptFileNames.ToolListFilesDescription,
            ["read_file"] = PromptFileNames.ToolReadFileDescription,
            ["search"] = PromptFileNames.ToolSearchDescription,
            ["git_status"] = PromptFileNames.ToolGitStatusDescription,
            ["find_symbol"] = PromptFileNames.ToolFindSymbolDescription,
            ["find_references"] = PromptFileNames.ToolFindReferencesDescription,
            ["find_implementations"] = PromptFileNames.ToolFindImplementationsDescription,
            ["datetime"] = PromptFileNames.ToolDatetimeDescription,
            ["csharp_script"] = PromptFileNames.ToolCsharpScriptDescription,
            ["code_explore"] = PromptFileNames.ToolCodeExploreDescription,
        };

        Assert.All(
            tools,
            tool => Assert.Equal(
                TestPromptLoader.Instance.Get(assets[tool.Definition.Id]),
                tool.Definition.Description));

        var runProcess = new RunProcessTool(
            processManager,
            TestPromptLoader.Instance,
            shellExecutable: "pwsh");
        var expectedRunProcessDescription = TestPromptLoader.Instance.Render(
            PromptFileNames.ToolRunProcessDescription,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ShellLanguage"] = "PowerShell",
            });
        Assert.Equal(expectedRunProcessDescription, runProcess.Definition.Description);
    }

    /// <summary>A locked file does not prevent text search from returning matches in readable files.</summary>
    [Fact]
    public static async Task SearchTextTool_LockedFile_IsSkipped()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "visible.txt"), "needle");
            var lockedPath = Path.Combine(repository, "locked.txt");
            await File.WriteAllTextAsync(lockedPath, "other content");
            using var lockedFile = new FileStream(
                lockedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var tool = new SearchTextTool(TestPromptLoader.Instance);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("visible.txt", match.Path);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Windows reserved device-name entries are skipped without aborting recursive inspection.</summary>
    [Fact]
    public static async Task BuiltInInspectionTools_WindowsReservedDeviceEntry_IsSkipped()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repository = CreateTemporaryDirectory();
        string[] reservedNames = ["nul", "COM¹", "COM².log", "COM³", "LPT¹", "LPT².log", "LPT³"];
        string[] extendedReservedPaths =
        [
            .. reservedNames.Select(name => @"\\?\" + Path.Combine(repository, name)),
        ];
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "visible.txt"), "needle");
            foreach (var extendedReservedPath in extendedReservedPaths)
            {
                await File.WriteAllTextAsync(extendedReservedPath, "needle");
            }

            await using var events = new DomainEventStream();
            var pipeline = CreatePipeline(
                events,
                [new ListFilesTool(TestPromptLoader.Instance), new SearchTextTool(TestPromptLoader.Instance)]);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var listed = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "list_files",
                ArgumentsJson = "{}",
                Context = context,
            });
            var searched = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "search",
                ArgumentsJson = "{\"query\":\"needle\"}",
                Context = context,
            });

            Assert.True(listed.Succeeded, listed.Error);
            Assert.Contains("visible.txt", listed.ResultJson, StringComparison.Ordinal);
            Assert.True(searched.Succeeded, searched.Error);
            Assert.Contains("visible.txt", searched.ResultJson, StringComparison.Ordinal);
            foreach (var reservedName in reservedNames)
            {
                Assert.DoesNotContain(reservedName, listed.ResultJson, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(reservedName, searched.ResultJson, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            foreach (var extendedReservedPath in extendedReservedPaths)
            {
                if (File.Exists(extendedReservedPath))
                {
                    File.Delete(extendedReservedPath);
                }
            }

            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>An allow-listed process requires approval and returns bounded, attributable output.</summary>
    [Fact]
    public static async Task ProcessTool_RequiresApprovalAndReturnsStructuredResult()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var observed = new List<IDomainEvent>();
            await using var subscription = events.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });
            var manager = new ProcessManager(
                new TestSanitizer(),
                NullLogger<ProcessManager>.Instance);
            var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
            var pipeline = new ToolInvocationPipeline(
                new ToolRegistry([new RunProcessTool(
                    manager,
                    TestPromptLoader.Instance,
                    allowedExecutables: [shell],
                    shellExecutable: shell)]),
                new DefaultPolicyEngine(),
                new AllowApprovalPolicy(),
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget());

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "run_process",
                ArgumentsJson = "{\"command\":\"dotnet --version\"}",
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    AllowedExecutables = [shell],
                },
            });

            Assert.True(result.Succeeded);
            Assert.Contains(result.Sources, source => source.Kind == "process");
            Assert.Contains(observed, item => item is ApprovalRequested requested
                && requested.Kind == ApprovalRequestKind.ToolInvocation
                && requested.SchemaVersion == 2);
            Assert.Contains(observed, item => item is ApprovalGranted);
            Assert.Empty(manager.ActiveProcesses);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Run-process exposes its schema but is withheld when the host cannot obtain required approval.</summary>
    [Fact]
    public static void RunProcess_ApprovalRequired_IsNotConversationAvailable()
    {
        var tool = new RunProcessTool(
            new DirectProcessStartManager(),
            TestPromptLoader.Instance,
            allowedExecutables: ["pwsh"],
            shellExecutable: "pwsh");

        using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
        var properties = schema.RootElement.GetProperty("properties");

        Assert.True(properties.TryGetProperty("command", out _));
        Assert.True(properties.TryGetProperty("timeoutSeconds", out _));
        Assert.Contains("PowerShell", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Equal(2, properties.EnumerateObject().Count());
        Assert.False(tool.Definition.ConversationAvailable);
        Assert.Equal(ApprovalLevel.User, tool.Definition.RequiredApproval);
    }

    /// <summary>Shell executables with a normal platform extension use the policy-normalized basename.</summary>
    [Fact]
    public static void RunProcess_ExtendedShellName_UsesNormalizedAllowlistBasename()
    {
        var tool = new RunProcessTool(
            new DirectProcessStartManager(),
            TestPromptLoader.Instance,
            allowedExecutables: ["powershell"],
            requireApproval: false,
            shellExecutable: "powershell.exe");

        Assert.True(tool.Definition.ConversationAvailable);
        Assert.Contains("PowerShell", tool.Definition.Description, StringComparison.Ordinal);
    }

    /// <summary>Unsupported or non-bare shells are never advertised to the model.</summary>
    [Theory]
    [InlineData("fish", "fish")]
    [InlineData("C:\\Tools\\powershell.exe", "powershell")]
    [InlineData("/usr/bin/bash", "bash")]
    public static void RunProcess_InvalidShellConfiguration_IsNotConversationAvailable(
        string shellExecutable,
        string allowedExecutable)
    {
        var tool = new RunProcessTool(
            new DirectProcessStartManager(),
            TestPromptLoader.Instance,
            allowedExecutables: [allowedExecutable],
            requireApproval: false,
            shellExecutable: shellExecutable);

        Assert.False(tool.Definition.ConversationAvailable);
    }

    /// <summary>Trusted composition can remove per-call approval without weakening executable restrictions.</summary>
    [Fact]
    public static void RunProcess_TrustedApprovalOption_RemovesPromptRequirement()
    {
        var tool = new RunProcessTool(
            new DirectProcessStartManager(),
            TestPromptLoader.Instance,
            allowedExecutables: ["pwsh"],
            requireApproval: false,
            shellExecutable: "pwsh");

        Assert.True(tool.Definition.ConversationAvailable);
        Assert.Equal(ApprovalLevel.None, tool.Definition.RequiredApproval);
        Assert.Contains("command", tool.Definition.InputSchema.JsonSchema, StringComparison.Ordinal);
    }

    /// <summary>Unix shells use non-login command mode and preserve the repository working directory.</summary>
    [Fact]
    public static async Task RunProcess_UnixShell_UsesNonLoginCommandModeAtRepositoryRoot()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                1,
                0,
                string.Empty,
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(1)));
            var tool = new RunProcessTool(
                processManager,
                TestPromptLoader.Instance,
                allowedExecutables: ["bash"],
                requireApproval: false,
                shellExecutable: "bash");
            var context = new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                CreateContext(repository));
            var input = tool.DeserializeInput("{\"command\":\"pwd\"}");

            await tool.ExecuteAsync(input, context);

            var request = Assert.IsType<ProcessExecutionRequest>(processManager.LastRequest);
            Assert.Equal(repository, request.WorkingDirectory);
            Assert.Equal(["-c", "pwd"], request.Arguments);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Cancelling a process request terminates its child process tree.</summary>
    [Fact]
    public static async Task ProcessManager_Cancellation_KillsChildTree()
    {
        var repository = CreateTemporaryDirectory();
        var processIdPath = Path.Combine(repository, "child.pid");
        using var cancellation = new CancellationTokenSource();
        Process? child = null;
        try
        {
            var manager = new ProcessManager(
                new TestSanitizer(),
                NullLogger<ProcessManager>.Instance);
            var request = CreateTreeProcessRequest(repository, processIdPath);
            var running = manager.RunAsync(request, cancellation.Token);
            await WaitForFileAsync(processIdPath, TimeSpan.FromSeconds(10));
            var processIdText = await File.ReadAllTextAsync(processIdPath);
            var processId = int.Parse(processIdText, System.Globalization.CultureInfo.InvariantCulture);
            child = Process.GetProcessById(processId);

            await cancellation.CancelAsync();
            var cancellationObserved = false;
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }

            await WaitForTerminationAsync(child, TimeSpan.FromSeconds(10));

            Assert.True(cancellationObserved);
            Assert.True(HasTerminated(child));
            Assert.Empty(manager.ActiveProcesses);
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
            }

            child?.Dispose();
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>NUL-delimited process output is parsed before the generic sanitizer removes control characters.</summary>
    [Fact]
    public static async Task ProcessManager_NullDelimitedJsonArray_PreservesRecordBoundariesThroughSanitization()
    {
        var shell = OperatingSystem.IsWindows()
            ? IsExecutableAvailable("pwsh") ? "pwsh" : "powershell.exe"
            : "sh";
        if (!IsExecutableAvailable(shell))
        {
            Assert.Skip($"{shell} is not available on PATH.");
        }

        var repository = CreateTemporaryDirectory();
        try
        {
            var manager = new ProcessManager(
                new SecretOutputSanitizer(),
                NullLogger<ProcessManager>.Instance);
            string[] arguments = OperatingSystem.IsWindows()
                ?
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$bytes = [byte[]](102,105,114,115,116,0,115,101,99,111,110,100,0); [Console]::OpenStandardOutput().Write($bytes, 0, $bytes.Length)",
                ]
                : ["-c", "printf 'first\\000second\\000'"];

            var result = await manager.RunAsync(new ProcessExecutionRequest
            {
                ToolInvocationId = ToolInvocationId.New(),
                RunId = RunId.New(),
                FileName = shell,
                Arguments = arguments,
                WorkingDirectory = repository,
                Timeout = TimeSpan.FromSeconds(10),
                MaximumOutputCharacters = 1024,
                StandardOutputFormat = ProcessStandardOutputFormat.NullDelimitedJsonArray,
                Origin = ProcessRequestOrigin.Host,
            });

            var records = JsonSerializer.Deserialize<string[]>(result.StandardOutput) ?? [];
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(["first", "second"], records);
            Assert.DoesNotContain("\0", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The default read window returns an ordinary source file in one complete result.</summary>
    [Fact]
    public static async Task ReadFileTool_DefaultWindow_ReturnsOrdinaryFileCompletely()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var lines = Enumerable.Range(1, 430).Select(index => $"line {index}").ToArray();
            await File.WriteAllLinesAsync(Path.Combine(repository, "source.cs"), lines);
            var tool = new ReadFileTool(TestPromptLoader.Instance);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new ReadFileInput { Path = "source.cs" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            Assert.Equal(1, result.Value.StartLine);
            Assert.Equal(430, result.Value.EndLine);
            Assert.Equal(430, result.Value.TotalLines);
            Assert.Equal(lines, result.Value.Lines);
            Assert.False(result.Value.IsTruncated);
            Assert.Null(result.Value.NextStartLine);
            Assert.Null(result.Value.TruncationReason);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The default line ceiling returns deterministic continuation metadata.</summary>
    [Fact]
    public static async Task ReadFileTool_DefaultLineCeiling_ReturnsNextRange()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(repository, "long.cs"),
                Enumerable.Range(1, 2001).Select(static index => index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)));
            var tool = new ReadFileTool(TestPromptLoader.Instance);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new ReadFileInput { Path = "long.cs" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            Assert.Equal(2000, result.Value.Lines.Count);
            Assert.Equal(2000, result.Value.EndLine);
            Assert.Equal(2001, result.Value.TotalLines);
            Assert.True(result.Value.IsTruncated);
            Assert.Equal(2001, result.Value.NextStartLine);
            Assert.Equal(ReadFileTruncationReason.LineLimit, result.Value.TruncationReason);
            Assert.Throws<ToolArgumentValidationException>(() =>
                tool.DeserializeInput("{\"path\":\"long.cs\",\"maximumLines\":2001}"));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The textual byte bound stops only between lines and identifies the exact continuation.</summary>
    [Fact]
    public static async Task ReadFileTool_ContentByteLimit_ReturnsNextRange()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(repository, "content.txt"), ["1234", "5678", "9"]);
            var tool = new ReadFileTool(TestPromptLoader.Instance, new ToolLimits
            {
                ReadFileMaximumContentBytes = 9,
            });
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new ReadFileInput { Path = "content.txt" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            Assert.Equal(["1234", "5678"], result.Value.Lines);
            Assert.Equal(2, result.Value.EndLine);
            Assert.Equal(3, result.Value.TotalLines);
            Assert.True(result.Value.IsTruncated);
            Assert.Equal(3, result.Value.NextStartLine);
            Assert.Equal(ReadFileTruncationReason.ContentByteLimit, result.Value.TruncationReason);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Injected tool limits override the compiled defaults for read_file and list_files.</summary>
    [Fact]
    public static async Task ConfiguredToolLimits_OverrideCompiledDefaults()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var bigFile = Path.Combine(repository, "big.txt");
            await File.WriteAllLinesAsync(bigFile, Enumerable.Repeat(new string('x', 256), 8));
            var smallFile = Path.Combine(repository, "small.txt");
            await File.WriteAllLinesAsync(smallFile, ["a", "b", "c", "d"]);

            var tightLimits = new ToolLimits
            {
                ReadFileMaximumBytes = 1024,
                ReadFileDefaultLines = 2,
                ListFilesMaxEntries = 5,
            };
            var readTool = new ReadFileTool(TestPromptLoader.Instance, tightLimits);
            var listTool = new ListFilesTool(TestPromptLoader.Instance, tightLimits);

            // Custom ReadFileMaximumBytes rejects a file that exceeds the configured bound.
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                readTool.ExecuteAsync(
                    new ReadFileInput { Path = "big.txt" },
                    new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                    CancellationToken.None));

            // Custom ReadFileDefaultLines is applied when the model omits maximumLines (sentinel 0).
            var readResult = await readTool.ExecuteAsync(
                new ReadFileInput { Path = "small.txt" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);
            Assert.Equal(2, readResult.Value.Lines.Count);

            // Custom ListFilesMaxEntries rejects an explicit value over the configured cap.
            Assert.Throws<ToolArgumentValidationException>(() =>
                listTool.DeserializeInput("{\"path\":\".\",\"maximumEntries\":6}"));

            // Default tool limits (no injection) still apply the compiled 1 MiB read bound, so big.txt reads fine.
            var defaultReadTool = new ReadFileTool(TestPromptLoader.Instance);
            var defaultResult = await defaultReadTool.ExecuteAsync(
                new ReadFileInput { Path = "big.txt", MaximumLines = 4 },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);
            Assert.Equal(4, defaultResult.Value.Lines.Count);
            Assert.Equal(4, defaultResult.Value.EndLine);
            Assert.Equal(8, defaultResult.Value.TotalLines);
            Assert.Equal(5, defaultResult.Value.NextStartLine);
            Assert.Equal(ReadFileTruncationReason.LineLimit, defaultResult.Value.TruncationReason);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The datetime built-in returns consistent UTC, local, timezone, and offset values.</summary>
    [Fact]
    public static async Task DateTimeTool_ReturnsCurrentHostClockInformation()
    {
        var before = DateTimeOffset.UtcNow;
        var tool = new DateTimeTool(TestPromptLoader.Instance);
        var result = await tool.ExecuteAsync(
            new DateTimeInput(),
            new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), CreateContext(Environment.CurrentDirectory)),
            CancellationToken.None);
        var utc = DateTimeOffset.Parse(result.Value.UtcNow, System.Globalization.CultureInfo.InvariantCulture);
        var local = DateTimeOffset.Parse(result.Value.LocalNow, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(utc, before, DateTimeOffset.UtcNow);
        Assert.Equal(TimeZoneInfo.Local.Id, result.Value.TimeZoneId);
        Assert.Equal(utc.UtcDateTime, local.UtcDateTime);
        Assert.Equal(local.Offset.ToString("c"), result.Value.OffsetFromUtc);
        Assert.Equal("Date/Time", tool.Definition.DisplayName);
    }

    /// <summary>Tool-level configuration binds typed scalar values and exposes one tool's scalar map.</summary>
    [Fact]
    public static void ToolConfig_ReadsTypedAndCompleteToolValues()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tools:config:csharp_script:timeout_ms"] = "1234",
                ["tools:config:csharp_script:max_output_bytes"] = "2048",
            })
            .Build();
        var config = new ToolConfig(configuration);
        Assert.Equal(1234, config.Get("csharp_script", "timeout_ms", 5000));
        Assert.Equal(99, config.Get("csharp_script", "missing", 99));
        var all = config.GetAll("csharp_script");
        Assert.Equal("2048", all["max_output_bytes"]);
        Assert.Equal(2, all.Count);
    }

    /// <summary>The optional C# tool executes, truncates, rejects forbidden APIs, and kills a timed-out worker.</summary>
    [Fact]
    public static async Task CSharpScriptEngine_EnforcesExecutionBoundaries()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["tools:config:csharp_script:timeout_ms"] = "5000",
                    ["tools:config:csharp_script:max_output_bytes"] = "256",
                    ["tools:config:csharp_script:allowed_assemblies"] = "System.Linq,System.Collections,System.Collections.Generic",
                })
                .Build();
            var processManager = new ProcessManager(new TestSanitizer(), NullLogger<ProcessManager>.Instance);
            var engine = new CSharpScriptEngine(
                processManager,
                new ToolConfig(configuration),
                Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"));
            var context = new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                CreateContext(repository) with { TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation });
            var expression = await engine.ExecuteAsync(
                "Enumerable.Range(1, 4).Sum()",
                ScriptKind.Expression,
                context);
            Assert.True(expression.Success, expression.Error);
            Assert.Equal("10", expression.Output);
            var statement = await engine.ExecuteAsync(
                "var value = 6 * 7; return value;",
                ScriptKind.Statement,
                context);
            Assert.True(statement.Success, statement.Error);
            Assert.Equal("42", statement.Output);
            var invalid = await engine.ExecuteAsync(
                "var value = ;",
                ScriptKind.Statement,
                context);
            Assert.False(invalid.Success);
            Assert.NotNull(invalid.Error);
            Assert.NotEmpty(invalid.Error);
            var oversized = await engine.ExecuteAsync(
                "new string('x', 1000)",
                ScriptKind.Expression,
                context);
            Assert.True(oversized.Success, oversized.Error);
            Assert.True(oversized.IsTruncated);
            Assert.Equal(256, System.Text.Encoding.UTF8.GetByteCount(oversized.Output ?? string.Empty));
            var forbidden = await engine.ExecuteAsync(
                "System.IO.File.Exists(\"anything\")",
                ScriptKind.Expression,
                context);
            Assert.False(forbidden.Success);
            Assert.Contains("prohibited", forbidden.Error, StringComparison.OrdinalIgnoreCase);
            var escapedForbidden = await engine.ExecuteAsync(
                "System.\\u0049O.\\u0046ile.ReadAllText(\"anything\")",
                ScriptKind.Expression,
                context);
            Assert.False(escapedForbidden.Success);
            Assert.Contains("prohibited", escapedForbidden.Error, StringComparison.OrdinalIgnoreCase);
            var disallowedAssembly = await engine.ExecuteAsync(
                "new System.Text.StringBuilder().Append(42).ToString()",
                ScriptKind.Expression,
                context);
            Assert.False(disallowedAssembly.Success);
            Assert.Contains("allowed_assemblies", disallowedAssembly.Error, StringComparison.OrdinalIgnoreCase);

            IConfiguration timeoutConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["tools:config:csharp_script:timeout_ms"] = "500",
                    ["tools:config:csharp_script:max_output_bytes"] = "256",
                })
                .Build();
            var timeoutEngine = new CSharpScriptEngine(
                processManager,
                new ToolConfig(timeoutConfiguration),
                Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"));
            var timeout = await timeoutEngine.ExecuteAsync(
                "while (true) { }",
                ScriptKind.Statement,
                context);
            Assert.False(timeout.Success);
            Assert.Contains("terminated", timeout.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(processManager.ActiveProcesses);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>A staged self-contained worker apphost is launched directly without the dotnet muxer.</summary>
    [Fact]
    public static async Task CSharpScriptEngine_LaunchesWorkerAppHostDirectly()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var workerPath = Path.Combine(
                repository,
                OperatingSystem.IsWindows() ? "Threadsmith.Scripting.Worker.exe" : "Threadsmith.Scripting.Worker");
            await File.WriteAllTextAsync(workerPath, string.Empty);
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                1,
                0,
                "{\"success\":true,\"output\":\"42\",\"executionMs\":1,\"isTruncated\":false}",
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(1)));
            var engine = new CSharpScriptEngine(
                processManager,
                new ToolConfig(new ConfigurationBuilder().Build()),
                workerPath);
            var context = new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                CreateContext(repository) with { TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation });

            var result = await engine.ExecuteAsync("6 * 7", ScriptKind.Expression, context);

            Assert.True(result.Success);
            Assert.Equal("42", result.Output);
            var request = Assert.IsType<ProcessExecutionRequest>(processManager.LastRequest);
            Assert.Equal(workerPath, request.FileName);
            Assert.Empty(request.Arguments);
            Assert.Equal(ProcessRequestOrigin.Host, request.Origin);

            var actualWorkerPath = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(workerPath));
            Assert.True(File.Exists(actualWorkerPath), $"Worker apphost was not copied to '{actualWorkerPath}'.");
            var actualEngine = new CSharpScriptEngine(
                new ProcessManager(new TestSanitizer(), NullLogger<ProcessManager>.Instance),
                new ToolConfig(new ConfigurationBuilder().Build()),
                actualWorkerPath);
            var actualResult = await actualEngine.ExecuteAsync(
                "6 * 7",
                ScriptKind.Expression,
                context);
            Assert.True(actualResult.Success, actualResult.Error);
            Assert.Equal("42", actualResult.Output);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The script tool is registered but unavailable until explicitly enabled.</summary>
    [Fact]
    public static async Task CSharpScriptTool_IsDisabledByDefaultAndCanBeEnabled()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(repository, ".threadsmith", "config.json");
            IConfiguration configuration = new ConfigurationBuilder().Build();
            var engine = new StubCSharpScriptEngine();
            ITool[] tools = [new DateTimeTool(TestPromptLoader.Instance), new CSharpScriptTool(engine, TestPromptLoader.Instance)];
            var state = new ToolStateManager(tools.Select(tool => tool.Definition), configuration, configPath);
            var registry = new ToolRegistry(tools, state);
            Assert.Contains(registry.Definitions, definition => definition.Id == "datetime");
            Assert.DoesNotContain(registry.Definitions, definition => definition.Id == "csharp_script");
            Assert.Throws<KeyNotFoundException>(() => registry.Get("csharp_script"));
            await state.EnableAsync("csharp_script");
            var registeredScript = registry.Get("csharp_script");
            Assert.Equal("csharp_script", registeredScript.Definition.Id);
            var execution = await registeredScript.ExecuteAsync(
                registeredScript.DeserializeInput("{\"code\":\"6 * 7\",\"kind\":\"expression\"}"),
                new ToolExecutionContext(
                    ToolInvocationId.New(),
                    SessionId.New(),
                    RunId.New(),
                    CreateContext(repository) with { TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation }),
                CancellationToken.None);
            Assert.Equal("stub", Assert.IsType<CSharpScriptOutput>(execution.Value).Output);
            var restored = new ToolStateManager(
                tools.Select(tool => tool.Definition),
                new ConfigurationBuilder().AddJsonFile(configPath).Build(),
                configPath);
            Assert.True(restored.IsEnabled("csharp_script"));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Independent reviewed tools enter their bodies concurrently and retain model ordinal order.</summary>
    [Fact]
    public static async Task Batch_IndependentParallelSafeTools_OverlapAndJoinInOrdinalOrder()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var gate = new ParallelToolGate(2);
            var pipeline = CreatePipeline(
                events,
                [new BarrierReadTool("parallel_a", gate), new BarrierReadTool("parallel_b", gate)]);
            var context = CreateContext(repository);
            ToolBatchRequest[] requests =
            [
                CreateBatchRequest(2, "call-b", "parallel_b", context),
                CreateBatchRequest(1, "call-a", "parallel_a", context),
            ];

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = pipeline.InvokeBatchAsync(requests, timeout.Token);
            await gate.AllEntered.Task.WaitAsync(timeout.Token);
            Assert.Equal(2, gate.PeakActive);
            gate.Release.TrySetResult();
            var results = await execution;

            Assert.Equal([1, 2], results.Select(result => result.Ordinal));
            Assert.All(results, result => Assert.True(result.Result.Succeeded));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>An invalid sibling becomes an ordinary result without preventing valid work.</summary>
    [Fact]
    public static async Task Batch_InvalidSibling_ReturnsFailureAndRunsValidSibling()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var executionOrder = new ConcurrentQueue<string>();
            var pipeline = CreatePipeline(
                events,
                [new OrderedReadTool("valid_read", ToolConcurrencyMode.ParallelSafe, executionOrder)]);
            var context = CreateContext(repository);
            ToolBatchRequest[] requests =
            [
                CreateBatchRequest(1, "call-missing", "missing_tool", context),
                CreateBatchRequest(2, "call-malformed", "valid_read", context, string.Empty),
                CreateBatchRequest(3, "call-valid", "valid_read", context),
            ];

            var results = await pipeline.InvokeBatchAsync(requests);

            Assert.Equal([1, 2, 3], results.Select(result => result.Ordinal));
            Assert.Equal(ToolErrorClassification.InvalidArguments, results[0].Result.ErrorClassification);
            Assert.Equal(ToolErrorClassification.InvalidArguments, results[1].Result.ErrorClassification);
            Assert.True(results[2].Result.Succeeded);
            Assert.Equal(["valid_read"], executionOrder);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Conversation preflight rejects one invalid sibling before any sibling can execute.</summary>
    [Fact]
    public static async Task BatchPreflight_InvalidSibling_RejectsBeforeExecution()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var executionOrder = new ConcurrentQueue<string>();
            var pipeline = CreatePipeline(
                events,
                [new OrderedReadTool("valid_read", ToolConcurrencyMode.ParallelSafe, executionOrder)]);
            var context = CreateContext(repository);
            ToolBatchRequest[] requests =
            [
                CreateBatchRequest(0, "call-invalid", "valid_read", context, string.Empty),
                CreateBatchRequest(1, "call-valid", "valid_read", context),
            ];

            var preflight = pipeline.PreflightBatch(requests);

            Assert.False(preflight.Succeeded);
            Assert.Equal(0, preflight.FailedOrdinal);
            Assert.Equal("valid_read", preflight.FailedToolId);
            Assert.Equal(ToolErrorClassification.InvalidArguments, preflight.ErrorClassification);
            Assert.Empty(executionOrder);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Prepared batch invocation consumes the exact preflight registration snapshot.</summary>
    [Fact]
    public static async Task BatchPreparedInvocation_UsesPreflightRegistrationSnapshot()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var executionOrder = new ConcurrentQueue<string>();
            var registry = new ToolRegistry([]);
            var oldTool = new LabeledDynamicTool("dynamic_read", "old", executionOrder);
            var newTool = new LabeledDynamicTool("dynamic_read", "new", executionOrder);
            var source = new ToolActivitySource(ToolActivitySourceKind.Extension, "test-extension");
            registry.RegisterOrReplace(oldTool, source);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget());
            var context = CreateContext(repository);
            ToolBatchRequest[] requests =
            [
                CreateBatchRequest(0, "call-dynamic", "dynamic_read", context),
            ];
            var preflight = pipeline.PreflightBatch(requests);
            var preparation = Assert.IsType<ToolBatchPreparation>(preflight.Preparation);
            registry.RegisterOrReplace(newTool, source, oldTool);

            var preparedResults = await pipeline.InvokePreparedBatchAsync(preparation);
            var ordinaryResults = await pipeline.InvokeBatchAsync(requests);

            Assert.True(preflight.Succeeded);
            Assert.True(Assert.Single(preparedResults).Result.Succeeded);
            Assert.True(Assert.Single(ordinaryResults).Result.Succeeded);
            Assert.Equal(["old", "new"], executionOrder);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>A request-fenced registration is rejected before replacement validation or claims execute.</summary>
    [Fact]
    public static async Task BatchPreflight_ReplacedExpectedRegistration_RunsNoReplacementCallbacks()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var registry = new ToolRegistry([]);
            var executionOrder = new ConcurrentQueue<string>();
            var original = new LabeledDynamicTool("dynamic_read", "original", executionOrder);
            var replacement = new ValidationCountingTool("dynamic_read");
            var source = new ToolActivitySource(ToolActivitySourceKind.Extension, "test-extension");
            registry.RegisterOrReplace(original, source);
            var expected = registry.GetRegistration(original.Definition.Id);
            registry.RegisterOrReplace(replacement, source, original);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget());

            var preflight = pipeline.PreflightBatch(
                [CreateBatchRequest(
                    0,
                    "call-dynamic",
                    original.Definition.Id,
                    CreateContext(repository),
                    expectedRegistration: expected)]);

            Assert.False(preflight.Succeeded);
            Assert.Equal(0, replacement.ValidationCalls);
            Assert.Empty(executionOrder);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Fail-fast drains and returns the cancelled outcome of every started sibling.</summary>
    [Fact]
    public static async Task Batch_FailFastCancellation_PreservesStartedResults()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var gate = new CoordinatedFailureGate();
            var options = new ToolParallelOptions
            {
                FailureMode = ToolBatchFailureMode.CancelBatchOnFailure,
            };
            var pipeline = CreatePipeline(
                events,
                [
                    new CoordinatedFailureTool("fails", gate, fails: true),
                    new CoordinatedFailureTool("cancelled", gate, fails: false),
                ],
                options);
            var context = CreateContext(repository);

            var results = await pipeline.InvokeBatchAsync(
                [
                    CreateBatchRequest(1, "call-fails", "fails", context),
                    CreateBatchRequest(2, "call-cancelled", "cancelled", context),
                ]);

            Assert.Equal(2, results.Count);
            Assert.Equal(ToolErrorClassification.ExecutionFailure, results[0].Result.ErrorClassification);
            Assert.Equal(ToolErrorClassification.Cancelled, results[1].Result.ErrorClassification);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Fail-fast returns cancelled terminal results for siblings that were never admitted.</summary>
    [Fact]
    public static async Task Batch_FailFastCancellation_ReturnsSkippedResultsForLaterWaves()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var executionOrder = new ConcurrentQueue<string>();
            var options = new ToolParallelOptions
            {
                FailureMode = ToolBatchFailureMode.CancelBatchOnFailure,
            };
            var pipeline = CreatePipeline(
                events,
                [
                    new OrderedReadTool("later", ToolConcurrencyMode.SerializedPerRegistration, executionOrder),
                ],
                options);
            var context = CreateContext(repository);

            var results = await pipeline.InvokeBatchAsync(
                [
                    CreateBatchRequest(1, "call-fails", "missing", context),
                    CreateBatchRequest(2, "call-later", "later", context),
                ]);

            Assert.Equal([1, 2], results.Select(result => result.Ordinal));
            Assert.Equal(ToolErrorClassification.InvalidArguments, results[0].Result.ErrorClassification);
            Assert.Equal(ToolErrorClassification.Cancelled, results[1].Result.ErrorClassification);
            Assert.Contains("Skipped", results[1].Result.Error, StringComparison.Ordinal);
            Assert.Empty(executionOrder);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>A configured batch cap cannot broaden a source's compiled concurrency cap.</summary>
    [Fact]
    public static async Task Batch_SourceConcurrencyCap_NarrowsConfiguredMaximum()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var gate = new ParallelToolGate(4);
            ITool[] tools = [.. Enumerable.Range(1, 5)
                .Select(index => new BarrierReadTool($"parallel_{index}", gate))];
            var pipeline = CreatePipeline(
                events,
                tools,
                new ToolParallelOptions { MaximumConcurrency = 8 });
            var context = CreateContext(repository);
            ToolBatchRequest[] requests = [.. Enumerable.Range(1, 5)
                .Select(index => CreateBatchRequest(index, $"call-{index}", $"parallel_{index}", context))];

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = pipeline.InvokeBatchAsync(requests, timeout.Token);
            await gate.AllEntered.Task.WaitAsync(timeout.Token);
            Assert.Equal(4, gate.PeakActive);
            gate.Release.TrySetResult();
            var results = await execution;

            Assert.Equal(5, results.Count);
            Assert.Equal(4, gate.PeakActive);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The source cap is shared by sibling batches using the same pipeline.</summary>
    [Fact]
    public static async Task Batch_SourceConcurrencyCap_AppliesAcrossConcurrentBatches()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var gate = new ParallelToolGate(4);
            ITool[] tools = [.. Enumerable.Range(1, 8)
                .Select(index => new BarrierReadTool($"parallel_{index}", gate))];
            var pipeline = CreatePipeline(
                events,
                tools,
                new ToolParallelOptions { MaximumConcurrency = 8 });
            var context = CreateContext(repository);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = pipeline.InvokeBatchAsync(
                [.. Enumerable.Range(1, 4)
                    .Select(index => CreateBatchRequest(index, $"first-{index}", $"parallel_{index}", context))],
                timeout.Token);
            var second = pipeline.InvokeBatchAsync(
                [.. Enumerable.Range(5, 4)
                    .Select(index => CreateBatchRequest(index, $"second-{index}", $"parallel_{index}", context))],
                timeout.Token);

            await gate.AllEntered.Task.WaitAsync(timeout.Token);
            Assert.Equal(4, gate.PeakActive);
            gate.Release.TrySetResult();
            await Task.WhenAll(first, second);
            Assert.Equal(4, gate.PeakActive);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Caller cancellation during the final wave remains observable at the batch boundary.</summary>
    [Fact]
    public static async Task Batch_CallerCancellationDuringFinalWave_IsPropagated()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var gate = new ParallelToolGate(1);
            var pipeline = CreatePipeline(
                events,
                [new BarrierReadTool("parallel", gate)]);
            using var cancellation = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = pipeline.InvokeBatchAsync(
                [CreateBatchRequest(1, "call", "parallel", CreateContext(repository))],
                cancellation.Token);

            await gate.AllEntered.Task.WaitAsync(timeout.Token);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await execution.WaitAsync(timeout.Token));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Text search stays serialized because an invocation may launch ripgrep.</summary>
    [Fact]
    public static void Search_SchedulingDescriptor_SerializesPotentialProcessExecution()
    {
        var scheduling = new SearchTextTool(TestPromptLoader.Instance).Definition.Scheduling;

        Assert.Equal(ToolConcurrencyMode.SerializedPerRegistration, scheduling.ConcurrencyMode);
        Assert.Equal(1, scheduling.MaximumSourceConcurrency);
    }

    /// <summary>Later calls never backfill ahead of an earlier invocation they conflict with.</summary>
    [Fact]
    public static async Task Batch_ConflictAcrossWaves_PreservesDependencyOrder()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var executionOrder = new ConcurrentQueue<string>();
            var pipeline = CreatePipeline(
                events,
                [
                    new OrderedReadTool("first", ToolConcurrencyMode.ParallelSafe, executionOrder),
                    new OrderedReadTool("serialized", ToolConcurrencyMode.SerializedPerRegistration, executionOrder),
                    new OrderedReadTool("third", ToolConcurrencyMode.ParallelSafe, executionOrder),
                ]);
            var context = CreateContext(repository);

            var results = await pipeline.InvokeBatchAsync(
                [
                    CreateBatchRequest(1, "call-first", "first", context),
                    CreateBatchRequest(2, "call-serialized", "serialized", context),
                    CreateBatchRequest(3, "call-third", "third", context),
                ]);

            Assert.Equal([1, 2, 3], results.Select(result => result.Ordinal));
            Assert.Equal(["first", "serialized", "third"], executionOrder);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    private static ToolBatchRequest CreateBatchRequest(
        int ordinal,
        string correlationId,
        string toolId,
        ToolInvocationContext context,
        string argumentsJson = "{}",
        ToolRegistration? expectedRegistration = null)
    {
        return new ToolBatchRequest(
            ordinal,
            correlationId,
            new ToolInvocationRequest
            {
                ExpectedRegistration = expectedRegistration,
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = toolId,
                ArgumentsJson = argumentsJson,
                Context = context,
            });
    }

    private static ProcessExecutionResult CreateProcessResult(
        string standardOutput,
        int exitCode = 0)
    {
        return new ProcessExecutionResult(
            42,
            exitCode,
            standardOutput,
            string.Empty,
            false,
            false,
            false,
            TimeSpan.FromMilliseconds(1));
    }

    private static ToolInvocationPipeline CreatePipeline(
        IDomainEventStream events,
        IEnumerable<ITool> tools,
        ToolParallelOptions? parallelOptions = null,
        IOutputSanitizer? sanitizer = null)
    {
        return new(
                new ToolRegistry(tools),
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer ?? new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget(),
                parallelOptions: parallelOptions);
    }

    private static ExecutionBudget CreateBudget()
    {
        return new(
        new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(5)));
    }

    private static ToolExecutionContext CreateCodeExploreExecutionContext(
        string repository,
        SessionId? sessionId = null,
        int? modelEffectiveInputBudgetTokens = null)
    {
        return new ToolExecutionContext(
            ToolInvocationId.New(),
            sessionId ?? SessionId.New(),
            RunId.New(),
            CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                WorkspaceId = WorkspaceId.New(),
                ModelEffectiveInputBudgetTokens = modelEffectiveInputBudgetTokens,
            });
    }

    private static ToolInvocationContext CreateContext(string repository)
    {
        return new()
        {
            RepositoryPath = repository,
            TrustLevel = RepositoryTrustLevel.UntrustedInspection,
            ApprovedRoots = ["."],
            RequestedBy = "model",
        };
    }

    private static string ExtractContinuationCursor(string markdown, string label)
    {
        const string prefix = "code_explore:continue:";
        var labelStart = markdown.IndexOf($"- **{label}:**", StringComparison.Ordinal);
        Assert.True(labelStart >= 0, $"Expected {label} follow-up target in Markdown.{Environment.NewLine}{markdown}");
        var tokenStart = markdown.IndexOf(prefix, labelStart, StringComparison.Ordinal);
        Assert.True(tokenStart >= 0, $"Expected {label} retry query cursor in Markdown.{Environment.NewLine}{markdown}");
        var tokenEnd = tokenStart;
        while (tokenEnd < markdown.Length && IsBase64UrlCursorCharacter(markdown[tokenEnd]))
        {
            tokenEnd++;
        }

        return markdown[tokenStart..tokenEnd];
    }

    private static bool IsBase64UrlCursorCharacter(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character is ':' or '-' or '_';
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;
        while (startIndex < text.Length)
        {
            var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            startIndex = index + value.Length;
        }

        return count;
    }

    private static int CountFenceLines(string markdown)
    {
        return markdown
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Count(line => line.StartsWith("```", StringComparison.Ordinal));
    }

    private static void AssertAppearsBefore(string text, string first, string second)
    {
        var firstIndex = text.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = text.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"Expected '{first}' in text.");
        Assert.True(secondIndex >= 0, $"Expected '{second}' in text.");
        Assert.True(firstIndex < secondIndex, $"Expected '{first}' before '{second}'.");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-m3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static ProcessExecutionRequest CreateTreeProcessRequest(
        string repository,
        string processIdPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var escapedPath = processIdPath.Replace("'", "''", StringComparison.Ordinal);
            var script = "$child = Start-Process -FilePath powershell.exe "
                + "-ArgumentList @('-NoProfile','-NonInteractive','-Command',"
                + "'Start-Sleep -Seconds 60') -PassThru; "
                + $"[IO.File]::WriteAllText('{escapedPath}', $child.Id.ToString()); "
                + "Wait-Process -Id $child.Id";
            return new ProcessExecutionRequest
            {
                ToolInvocationId = ToolInvocationId.New(),
                RunId = RunId.New(),
                FileName = "powershell.exe",
                Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
                WorkingDirectory = repository,
                Timeout = TimeSpan.FromMinutes(1),
                Origin = ProcessRequestOrigin.Host,
            };
        }

        var shellScript = $"sleep 60 & child=$!; echo $child > '{processIdPath}'; wait $child";
        return new ProcessExecutionRequest
        {
            ToolInvocationId = ToolInvocationId.New(),
            RunId = RunId.New(),
            FileName = "sh",
            Arguments = ["-c", shellScript],
            WorkingDirectory = repository,
            Timeout = TimeSpan.FromMinutes(1),
            Origin = ProcessRequestOrigin.Host,
        };
    }

    private static bool IsExecutableAvailable(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathExtensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        string[] candidateNames = OperatingSystem.IsWindows() && !Path.HasExtension(fileName)
            ? [.. pathExtensions.Select(extension => fileName + extension)]
            : [fileName];
        return pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"'))
            .Where(Path.IsPathFullyQualified)
            .Any(directory => candidateNames.Any(candidate => File.Exists(Path.Combine(directory, candidate))));
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path) && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(25);
        }

        Assert.True(File.Exists(path), $"Timed out waiting for process marker '{path}'.");
    }

    private static async Task WaitForTerminationAsync(Process process, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!HasTerminated(process) && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(25);
            process.Refresh();
        }

        Assert.True(HasTerminated(process), $"Timed out waiting for process {process.Id} to terminate.");
    }

    private static bool HasTerminated(Process process)
    {
        if (process.HasExited)
        {
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var statusPath = $"/proc/{process.Id}/stat";
        if (!File.Exists(statusPath))
        {
            return true;
        }

        var status = File.ReadAllText(statusPath);
        var commandEnd = status.LastIndexOf(')');
        return commandEnd >= 0
            && status.Length > commandEnd + 2
            && status[commandEnd + 2] == 'Z';
    }

    private sealed class NoopCodeExploreService : ICodeExploreService
    {
        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateRichCodeExploreResult());
        }
    }

    private sealed class StaticCodeExploreResultTool : Tool<CodeExploreInput, CodeExploreResult>
    {
        private static readonly ToolDefinition _definition = new CodeExploreTool(
            new NoopCodeExploreService(),
            TestPromptLoader.Instance).Definition;

        private readonly CodeExploreResult _result;

        internal StaticCodeExploreResultTool(CodeExploreResult result)
        {
            _result = result;
        }

        public override ToolDefinition Definition => _definition;

        public override Task<ToolExecution<CodeExploreResult>> ExecuteAsync(
            CodeExploreInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ToolExecution<CodeExploreResult>(
                _result,
                []));
        }

        protected override void ValidateInput(CodeExploreInput input)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Query);
        }
    }

    private sealed class LongQueryResultCodeExploreService : ICodeExploreService
    {
        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = CreateRichCodeExploreResult() with
            {
                QueryInterpretation = new CodeExploreQueryInterpretation(
                    [],
                    [],
                    [],
                    [],
                    [request.Query],
                    [],
                    []),
                Omissions = [request.Query],
            };
            return Task.FromResult(result);
        }
    }

    private sealed class ManyContinuationCodeExploreService : ICodeExploreService
    {
        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateManyContinuationCodeExploreResult());
        }
    }

    private sealed class ManyImpactCodeExploreService : ICodeExploreService
    {
        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateManyImpactCodeExploreResult());
        }
    }

    private sealed class ZeroItemImpactCodeExploreService : ICodeExploreService
    {
        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateZeroItemImpactCodeExploreResult());
        }
    }

    private sealed class LongArtifactContinuationCodeExploreService : ICodeExploreService
    {
        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateLongArtifactContinuationCodeExploreResult());
        }
    }

    private sealed class CapturingCodeExploreService : ICodeExploreService
    {
        public CodeExploreRequest? Request { get; private set; }

        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(CreateRichCodeExploreResult());
        }
    }

    private sealed class ThrowingCodeExploreService : ICodeExploreService
    {
        public bool WasCalled { get; private set; }

        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            WasCalled = true;
            throw new InvalidOperationException("The no-workspace branch should not call the semantic service.");
        }
    }

    private static CodeExploreResult CreateRichCodeExploreResult()
    {
        var worker = new SemanticSymbolIdentity("symbol:example.worker", "Worker", "class");
        var caller = new SemanticSymbolIdentity("symbol:example.caller", "Caller.Run", "method");
        var continuationDigest = new string('a', 64);
        var location = new CodeExploreLocation(
            "Example.Project",
            "net10.0",
            "src/Worker.cs",
            new SourceRange(1, 1, 8, 2),
            IsGenerated: false,
            IsLinked: false);
        var artifactContent = new CodeExploreArtifactContent(
            new SourceRange(1, 1, 4, 1),
            ["1: # Worker prompt", "2: Use the worker safely."],
            continuationDigest,
            "artifact-range-sha256",
            CodeExploreSourceCompleteness.Partial,
            ["L5-L10 omitted by artifact character bounds."],
            "prompts/worker.md",
            48);
        return new CodeExploreResult(
            1,
            SemanticConfidenceLevel.FullSemantic,
            [new CodeExploreAnchorResolution(
                "Worker",
                CodeExploreAnchorKind.SymbolName,
                CodeExploreResolutionOutcome.Resolved,
                worker,
                location,
                [],
                "Resolved exact Worker anchor.")],
            [new CodeExploreFileSection(
                location.FilePath,
                location.ProjectName,
                location.TargetFramework,
                [worker],
                new CodeExploreSourceRange(
                    location.Range,
                    ["1: public sealed class Worker", "2: {", "3:     public string Run() => \"ok\";", "4: }"],
                    "file-sha256",
                    "range-sha256",
                    CodeExploreSourceCompleteness.Complete,
                    [],
                    null),
                IsGenerated: false,
                IsLinked: false,
                "Selected exact Worker declaration.")],
            new CodeExploreCoverage(true, true, true, true, []),
            [],
            [new CodeExploreContinuationTarget(
                CodeExploreAnchorKind.Path,
                "src/Worker.cs",
                "src/Worker.cs",
                5,
                8,
                false,
                CodeExplorePathSelectionMode.ExactLineRange,
                continuationDigest,
                1,
                "Retry with this exact Worker source range.")],
            BlastRadius: new CodeExploreBlastRadius(
                [
                    new CodeExploreBlastRadiusItem(
                        worker.Id,
                        ImpactKind.Caller,
                        caller,
                        location,
                        location.ProjectName,
                        "Caller reaches Worker.Run through compiler-known call evidence."),
                    new CodeExploreBlastRadiusItem(
                        worker.Id,
                        ImpactKind.Project,
                        null,
                        null,
                        "Example.Dependent",
                        "Project directly or transitively depends on a project containing primary anchor evidence."),
                    new CodeExploreBlastRadiusItem(
                        worker.Id,
                        ImpactKind.Test,
                        null,
                        null,
                        "Example.Dependent.Tests",
                        "Test project directly or transitively depends on a project containing primary anchor evidence."),
                ],
                ReturnedCallers: 1,
                TotalCallers: 1,
                ReturnedImplementations: 0,
                TotalImplementations: 0,
                ReturnedProjects: 1,
                TotalProjects: 1,
                ReturnedTests: 1,
                TotalTests: 1,
                Omissions: [],
                ContinuationTargets:
                [new CodeExploreContinuationTarget(
                    CodeExploreAnchorKind.SymbolId,
                    worker.Id,
                    null,
                    null,
                    null,
                    false,
                    null,
                    null,
                    1,
                    "Retry symbol_impact for additional compact impact evidence.")]),
            CandidateSummaries:
            [
                new CodeExploreCandidateSummary(
                    worker,
                    location,
                    location.FilePath,
                    CodeExploreCandidateTier.DistinctiveIdentifier,
                    CodeExploreSelectionReason.ExactIdentifier,
                    1,
                    true,
                    "Matched the distinctive Worker identifier.",
                    null),
            ],
            AssociatedArtifacts:
            [
                new CodeExploreAssociatedArtifact(
                    "prompts/worker.md",
                    CodeExploreArtifactMediaKind.Markdown,
                    "Example.Project",
                    worker.Id,
                    "src/Worker.cs",
                    location.Range,
                    CodeExploreArtifactRelationshipKind.PromptReference,
                    CodeExploreArtifactEvidenceLevel.CompilerProven,
                    ["Selected from Worker source evidence."],
                    artifactContent,
                    ["L5-L10 omitted by artifact character bounds."]),
                new CodeExploreAssociatedArtifact(
                    "prompts/worker.unreadable.md",
                    CodeExploreArtifactMediaKind.Markdown,
                    "Example.Project",
                    worker.Id,
                    "src/Worker.cs",
                    location.Range,
                    CodeExploreArtifactRelationshipKind.PromptReference,
                    CodeExploreArtifactEvidenceLevel.CompilerProven,
                    ["Selected from Worker source evidence but content was omitted."],
                    null,
                    ["Artifact could not be read safely because it exceeded host bounds."]),
            ],
            ArtifactCoverage: new CodeExploreArtifactCoverage(
                InspectedSourceAnchors: 1,
                InspectedProjects: 1,
                InspectedDirectories: 1,
                CandidateCount: 1,
                ReturnedCount: 1,
                OmittedCount: 1,
                SpentCharacters: artifactContent.ReturnedCharacters,
                Complete: false,
                CandidateLimitReached: false,
                FileLimitReached: false,
                CharacterLimitReached: true,
                TimeLimitReached: false,
                Omissions: ["Associated artifact character-output bounds were reached; use artifact continuation targets for focused follow-up."],
                ContinuationTargets:
                [new CodeExploreArtifactContinuationTarget(
                    "prompts/worker.md",
                    5,
                    10,
                    continuationDigest,
                    1,
                    "Retry with this explicit associated artifact path anchor and digest to continue omitted artifact content.")]));
    }

    private static CodeExploreResult CreateSanitizerExpansionCodeExploreResult()
    {
        var symbol = new SemanticSymbolIdentity("symbol:example.worker", "Worker", "class");
        var location = new CodeExploreLocation(
            "Example.Project",
            "net10.0",
            "src/Worker.cs",
            new SourceRange(1, 1, 48, 1),
            IsGenerated: false,
            IsLinked: false);
        var lines = Enumerable.Range(1, 48)
            .Select(line => $"{line}: {SanitizerExpansionMarker}")
            .ToArray();
        return new CodeExploreResult(
            1,
            SemanticConfidenceLevel.FullSemantic,
            [new CodeExploreAnchorResolution(
                "Worker",
                CodeExploreAnchorKind.SymbolName,
                CodeExploreResolutionOutcome.Resolved,
                symbol,
                location,
                [],
                "Resolved exact Worker anchor.")],
            [new CodeExploreFileSection(
                location.FilePath,
                location.ProjectName,
                location.TargetFramework,
                [symbol],
                new CodeExploreSourceRange(
                    location.Range,
                    lines,
                    "file-sha256",
                    "range-sha256",
                    CodeExploreSourceCompleteness.Complete,
                    [],
                    null),
                IsGenerated: false,
                IsLinked: false,
                "Selected exact Worker declaration.")],
            new CodeExploreCoverage(true, true, true, true, []),
            [],
            []);
    }

    private static CodeExploreResult CreateManyContinuationCodeExploreResult()
    {
        var result = CreateRichCodeExploreResult();
        var continuationDigest = new string('a', 64);
        var continuations = Enumerable.Range(0, 6)
            .Select(index => new CodeExploreContinuationTarget(
                CodeExploreAnchorKind.Path,
                $"src/WorkerExtra{index}.cs",
                $"src/WorkerExtra{index}.cs",
                index + 1,
                index + 3,
                false,
                CodeExplorePathSelectionMode.ExactLineRange,
                continuationDigest,
                1,
                $"Retry with Worker extra source range {index}."))
            .ToArray();
        return result with { ContinuationTargets = continuations };
    }

    private static CodeExploreResult CreateManyImpactCodeExploreResult()
    {
        var result = CreateRichCodeExploreResult();
        var location = result.ResolvedAnchors[0].SelectedLocation
            ?? throw new InvalidOperationException("Expected a resolved source location.");
        var anchorSymbolId = result.ResolvedAnchors[0].SelectedSymbol?.Id
            ?? throw new InvalidOperationException("Expected a resolved symbol.");
        var items = new List<CodeExploreBlastRadiusItem>();
        for (var index = 0; index < 6; index++)
        {
            items.Add(new CodeExploreBlastRadiusItem(
                anchorSymbolId,
                ImpactKind.Caller,
                new SemanticSymbolIdentity($"symbol:caller.{index}", $"Caller{index}.Run", "method"),
                location,
                location.ProjectName,
                "Compiler-resolved caller evidence."));
        }

        for (var index = 0; index < 4; index++)
        {
            items.Add(new CodeExploreBlastRadiusItem(
                anchorSymbolId,
                ImpactKind.Implementation,
                new SemanticSymbolIdentity($"symbol:implementation.{index}", $"Worker{index}.Run", "method"),
                location,
                location.ProjectName,
                "Compiler-resolved implementation evidence."));
        }

        for (var index = 0; index < 20; index++)
        {
            items.Add(new CodeExploreBlastRadiusItem(
                anchorSymbolId,
                ImpactKind.Project,
                null,
                null,
                $"Example.Dependent{index}",
                "Dependent project evidence."));
        }

        for (var index = 0; index < 20; index++)
        {
            items.Add(new CodeExploreBlastRadiusItem(
                anchorSymbolId,
                ImpactKind.Test,
                null,
                null,
                $"Example.Dependent{index}.Tests",
                "Dependent test project evidence."));
        }

        var existingBlastRadius = result.BlastRadius
            ?? throw new InvalidOperationException("Expected blast-radius data.");
        return result with
        {
            BlastRadius = new CodeExploreBlastRadius(
                items,
                ReturnedCallers: 6,
                TotalCallers: 12,
                ReturnedImplementations: 4,
                TotalImplementations: 4,
                ReturnedProjects: 20,
                TotalProjects: 35,
                ReturnedTests: 20,
                TotalTests: 40,
                Omissions: existingBlastRadius.Omissions,
                ContinuationTargets: existingBlastRadius.ContinuationTargets),
        };
    }

    private static CodeExploreResult CreateLongArtifactContinuationCodeExploreResult()
    {
        var result = CreateRichCodeExploreResult();
        var artifacts = result.AssociatedArtifacts
            ?? throw new InvalidOperationException("Expected associated artifacts.");
        var artifactCoverage = result.ArtifactCoverage
            ?? throw new InvalidOperationException("Expected artifact coverage.");
        var continuation = artifactCoverage.ContinuationTargets[0] with
        {
            OriginSymbolId = CreateLongArtifactOriginSymbolId(),
            OriginFilePath = "src/TargetWorker.cs",
            OriginRange = new SourceRange(10, 1, 20, 1),
        };
        return result with
        {
            AssociatedArtifacts =
            [
                artifacts[0] with { OriginSymbolId = "symbol:legacy-artifact-origin" },
                .. artifacts.Skip(1),
            ],
            ArtifactCoverage = artifactCoverage with { ContinuationTargets = [continuation] },
        };
    }

    private static CodeExploreResult CreateZeroItemImpactCodeExploreResult()
    {
        var result = CreateRichCodeExploreResult();
        var blastRadius = result.BlastRadius
            ?? throw new InvalidOperationException("Expected blast-radius data.");
        return result with { BlastRadius = blastRadius with { Items = [] } };
    }

    private static string CreateLongArtifactOriginSymbolId()
    {
        return "symbol:example.long-artifact-origin." + new string('x', 180);
    }

    private sealed class StubCSharpScriptEngine : ICSharpScriptEngine
    {
        public Task<CSharpScriptOutput> ExecuteAsync(
            string code,
            ScriptKind kind,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CSharpScriptOutput(true, "stub", null, 0, false));
        }
    }

    private sealed class TestSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            return value;
        }
    }

    private sealed class ExpandingCodeExploreSanitizer : IOutputSanitizer
    {
        private static readonly string _replacement = $"[REDACTED:{new string('x', 96)}]";

        public string Sanitize(string value)
        {
            return value.Replace(
                SanitizerExpansionMarker,
                _replacement,
                StringComparison.Ordinal);
        }
    }

    private sealed class CountingApprovalPolicy : IApprovalPolicy
    {
        public int RequestCount { get; private set; }

        public Task<bool> IsApprovedAsync(
            string action,
            ApprovalLevel requiredLevel,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class CancellingApprovalPolicy : IApprovalPolicy
    {
        public Task<bool> IsApprovedAsync(
            string action,
            ApprovalLevel requiredLevel,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<bool>(new OperationCanceledException(cancellationToken));
        }
    }

    private sealed class DescriptionSemanticResolver : ISemanticEngineResolver
    {
        public SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId)
        {
            return SemanticConfidenceLevel.FullSemantic;
        }

        public Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(
            WorkspaceId workspaceId,
            string query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SymbolResult>>([]);
        }

        public Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
            WorkspaceId workspaceId,
            string symbolId,
            bool allowTextFallback = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ReferenceResult>>([]);
        }

        public Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
            WorkspaceId workspaceId,
            string symbolId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ImplementationResult>>([]);
        }

        public Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> projectPaths,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Diagnostic>>([]);
        }
    }

    private sealed class RoutingProcessManager : IProcessManager
    {
        private readonly Func<ProcessExecutionRequest, ProcessExecutionResult> _handler;

        public RoutingProcessManager(Func<ProcessExecutionRequest, ProcessExecutionResult> handler)
        {
            _handler = handler;
        }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class StubProcessManager : IProcessManager
    {
        private readonly ProcessExecutionResult _result;

        public StubProcessManager(ProcessExecutionResult result)
        {
            _result = result;
        }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public int ExecutionCount { get; private set; }

        public ProcessExecutionRequest? LastRequest { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class DirectProcessStartManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = request.FileName,
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("The test process unexpectedly failed to return a process handle.");
            throw new InvalidOperationException("The deliberately missing test executable unexpectedly started.");
        }
    }

    private sealed record CountingInput
    {
        public required string Value { get; init; }
    }

    private sealed record CountingOutput(string Value);

    private sealed class ParallelToolGate
    {
        private readonly int _expected;
        private int _active;
        private int _entered;
        private int _peakActive;

        public ParallelToolGate(int expected)
        {
            _expected = expected;
        }

        public TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PeakActive => Volatile.Read(ref _peakActive);

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            int observedPeak;
            do
            {
                observedPeak = Volatile.Read(ref _peakActive);
            }
            while (active > observedPeak
                && Interlocked.CompareExchange(ref _peakActive, active, observedPeak) != observedPeak);
            if (Interlocked.Increment(ref _entered) == _expected)
            {
                AllEntered.TrySetResult();
            }

            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed record BarrierReadInput;

    private sealed record BarrierReadOutput(string ToolId);

    private sealed class CoordinatedFailureGate
    {
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public async Task EnterAsync(bool fails, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entered) == 2)
            {
                _bothEntered.TrySetResult();
            }

            await _bothEntered.Task.WaitAsync(cancellationToken);
            if (fails)
            {
                throw new ToolExecutionException("Expected test failure.");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CoordinatedFailureTool : Tool<BarrierReadInput, BarrierReadOutput>
    {
        private readonly ToolDefinition _definition;
        private readonly bool _fails;
        private readonly CoordinatedFailureGate _gate;

        public CoordinatedFailureTool(string id, CoordinatedFailureGate gate, bool fails)
        {
            _gate = gate;
            _fails = fails;
            _definition = CreateReadDefinition(id, ToolConcurrencyMode.ParallelSafe, 4);
        }

        public override ToolDefinition Definition => _definition;

        public override async Task<ToolExecution<BarrierReadOutput>> ExecuteAsync(
            BarrierReadInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            await _gate.EnterAsync(_fails, cancellationToken);
            return new ToolExecution<BarrierReadOutput>(new BarrierReadOutput(Definition.Id), []);
        }

        protected override void ValidateInput(BarrierReadInput input)
        {
        }
    }

    private sealed class OrderedReadTool : Tool<BarrierReadInput, BarrierReadOutput>
    {
        private readonly ToolDefinition _definition;
        private readonly ConcurrentQueue<string> _executionOrder;

        public OrderedReadTool(
            string id,
            ToolConcurrencyMode concurrencyMode,
            ConcurrentQueue<string> executionOrder)
        {
            _executionOrder = executionOrder;
            _definition = CreateReadDefinition(
                id,
                concurrencyMode,
                concurrencyMode == ToolConcurrencyMode.ParallelSafe ? 4 : 1);
        }

        public override ToolDefinition Definition => _definition;

        public override Task<ToolExecution<BarrierReadOutput>> ExecuteAsync(
            BarrierReadInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _executionOrder.Enqueue(Definition.Id);
            return Task.FromResult(new ToolExecution<BarrierReadOutput>(
                new BarrierReadOutput(Definition.Id),
                []));
        }

        protected override void ValidateInput(BarrierReadInput input)
        {
        }
    }

    private sealed class LabeledDynamicTool : Tool<BarrierReadInput, BarrierReadOutput>
    {
        private readonly ToolDefinition _definition;
        private readonly string _label;
        private readonly ConcurrentQueue<string> _executionOrder;

        public LabeledDynamicTool(
            string id,
            string label,
            ConcurrentQueue<string> executionOrder)
        {
            _label = label;
            _executionOrder = executionOrder;
            _definition = CreateReadDefinition(id, ToolConcurrencyMode.SerializedPerRegistration, 1);
        }

        public override ToolDefinition Definition => _definition;

        public override Task<ToolExecution<BarrierReadOutput>> ExecuteAsync(
            BarrierReadInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _executionOrder.Enqueue(_label);
            return Task.FromResult(new ToolExecution<BarrierReadOutput>(
                new BarrierReadOutput(_label),
                []));
        }

        protected override void ValidateInput(BarrierReadInput input)
        {
        }
    }

    private sealed class ValidationCountingTool : Tool<BarrierReadInput, BarrierReadOutput>
    {
        private readonly ToolDefinition _definition;

        public ValidationCountingTool(string id)
        {
            _definition = CreateReadDefinition(id, ToolConcurrencyMode.SerializedPerRegistration, 1);
        }

        public override ToolDefinition Definition => _definition;

        public int ValidationCalls { get; private set; }

        public override Task<ToolExecution<BarrierReadOutput>> ExecuteAsync(
            BarrierReadInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("A replaced tool must not execute.");
        }

        protected override void ValidateInput(BarrierReadInput input)
        {
            ValidationCalls++;
        }
    }

    private sealed class BarrierReadTool : Tool<BarrierReadInput, BarrierReadOutput>
    {
        private readonly ToolDefinition _definition;
        private readonly ParallelToolGate _gate;

        public BarrierReadTool(string id, ParallelToolGate gate)
        {
            _gate = gate;
            _definition = CreateReadDefinition(id, ToolConcurrencyMode.ParallelSafe, 4);
        }

        public override ToolDefinition Definition => _definition;

        public override async Task<ToolExecution<BarrierReadOutput>> ExecuteAsync(
            BarrierReadInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            await _gate.EnterAsync(cancellationToken);
            return new ToolExecution<BarrierReadOutput>(new BarrierReadOutput(Definition.Id), []);
        }

        protected override void ValidateInput(BarrierReadInput input)
        {
        }
    }

    private static ToolDefinition CreateReadDefinition(
        string id,
        ToolConcurrencyMode concurrencyMode,
        int maximumSourceConcurrency)
    {
        return new ToolDefinition
        {
            Id = id,
            Version = "1.0",
            Description = "Test-only scheduled read.",
            Category = ToolCategory.FileRead,
            InputSchema = new ToolSchema(nameof(BarrierReadInput), 1, "{\"type\":\"object\"}"),
            OutputSchema = new ToolSchema(nameof(BarrierReadOutput), 1, "{\"type\":\"object\"}"),
            RequiredTrust = RepositoryTrustLevel.UntrustedInspection,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOutputBytes = 1024,
            Scheduling = new ToolSchedulingDescriptor
            {
                ConcurrencyMode = concurrencyMode,
                ClaimResolverId = "test-scheduled-read-v1",
                MaximumSourceConcurrency = maximumSourceConcurrency,
            },
        };
    }

    private sealed class CountingTool : Tool<CountingInput, CountingOutput>
    {
        private static readonly ToolDefinition _definition = new()
        {
            Id = "counting",
            Version = "1.0",
            Description = "Counts successful executions.",
            Category = ToolCategory.RepositoryInspection,
            InputSchema = new ToolSchema(nameof(CountingInput), 1, "{\"type\":\"object\"}"),
            OutputSchema = new ToolSchema(nameof(CountingOutput), 1, "{\"type\":\"object\"}"),
            Timeout = TimeSpan.FromSeconds(1),
            MaximumOutputBytes = 1024,
            SupportsCancellation = true,
        };

        public int ExecutionCount { get; private set; }

        public override ToolDefinition Definition => _definition;

        public override Task<ToolExecution<CountingOutput>> ExecuteAsync(
            CountingInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(new ToolExecution<CountingOutput>(
                new CountingOutput(input.Value),
                []));
        }

        protected override void ValidateInput(CountingInput input)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Value);
        }
    }
}
