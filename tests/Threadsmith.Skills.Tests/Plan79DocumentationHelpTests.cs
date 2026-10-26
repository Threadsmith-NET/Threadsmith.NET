namespace Threadsmith.Skills.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies the focused packaged-documentation scope and citation boundary.</summary>
public sealed class Plan79DocumentationHelpTests
{
    /// <summary>Both captured caller snapshots and host contexts reach bundle searches through the model-driven runner.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task DocumentationRunner_SearchesWithCurrentCallerAuthority(bool capturedCaller)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixtureName = $"policy-fixture-{Guid.NewGuid():N}";
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, PackagedDocumentationPolicy.BundleDirectoryName, fixtureName);
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "guide.md"), "# Guide\nA subagent follows its assignment.\n", cancellationToken);
            var caller = CreatePolicyContext(Path.GetTempPath());
            var sanitizer = new SecretOutputSanitizer();
            var registry = new ToolRegistry([new SearchTextTool(TestPromptLoader.Instance)]);
            var snapshots = new ConversationToolSnapshotStore();
            var request = new SkillInvocationRequest
            {
                InvocationId = SkillInvocationId.New(),
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Selector = PackagedDocumentationPolicy.SkillId,
                InputJson = "{}",
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
                HostBudget = new SkillBudget { ModelTurns = 2, ToolCalls = 1 },
            };
            if (capturedCaller)
            {
                request = request with
                {
                    CallerToolSnapshotId = snapshots.Capture(request.SessionId, request.RunId, [registry.GetRegistration("search")], caller),
                };
            }

            var plan = new SkillInvocationPlan
            {
                Request = request,
                Package = new SkillPackageIdentity(new SkillId(PackagedDocumentationPolicy.SkillId), "test.docs", "1.0.0", new SkillDigest("sha256", new string('a', 64)), "test"),
                Scope = SkillScope.Maintained,
                Verification = SkillVerificationState.DigestAllowlisted,
                Compatibility = new SkillCompatibilityResult { IsCompatible = true },
                ModelProfileId = ModelProfileId.New(),
                AvailableToolIds = ["search"],
                EffectiveBudget = request.HostBudget,
            };
            var model = new DocumentationSearchModelProvider(fixtureName);
            await using var events = new DomainEventStream();
            var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
            var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(caller), TestPromptLoader.Instance, snapshots: snapshots);

            await runner.RunAsync(plan, new SkillWorkflowStep { StepId = "inspect", Kind = SkillWorkflowStepKind.CollectEvidence }, 1, [], "{}", cancellationToken);

            Assert.Equal(2, model.Requests.Count);
            Assert.DoesNotContain(model.Requests[1].Messages, message => message.IsError == true);
            Assert.Contains(model.Requests[1].Messages, message => message.GetModelVisibleContent().Contains("A subagent follows its assignment.", StringComparison.Ordinal));
            if (request.CallerToolSnapshotId is { } snapshotId)
            {
                snapshots.Release(snapshotId);
            }
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    /// <summary>Real file tools can inspect the bundle under live repository authority without escaping its scope.</summary>
    [Theory]
    [InlineData("search", "{\"query\":\"subagent\",\"path\":\".\"}")]
    [InlineData("read_file", "{\"path\":\"guide.md\"}")]
    public static async Task BoundTools_ExecuteThroughRepositoryPolicy(string toolId, string arguments)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-docs-policy-{Guid.NewGuid():N}");
        var bundle = Path.Combine(root, PackagedDocumentationPolicy.BundleDirectoryName);
        Directory.CreateDirectory(bundle);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(bundle, "guide.md"), "# Guide\nA subagent follows its assignment.\n", cancellationToken);
            var caller = CreatePolicyContext(Path.Combine(root, "repository")) with
            {
                Scratchpad = new ScratchpadSessionCapability { IsActive = true, RootPath = root },
            };
            var context = PackagedDocumentationPolicy.BindToBundle(caller, root);
            var sanitizer = new SecretOutputSanitizer();
            var tools = new ToolRegistry([new SearchTextTool(TestPromptLoader.Instance), new ReadFileTool(TestPromptLoader.Instance, sanitizer)]);
            await using var events = new DomainEventStream();
            var pipeline = new ToolInvocationPipeline(tools, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
            var request = new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Phase = RunPhase.EvidenceCollection,
                ToolId = toolId,
                ArgumentsJson = arguments,
                Context = context,
            };

            var result = await pipeline.InvokeAsync(request, cancellationToken);

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("subagent", result.ResultJson, StringComparison.Ordinal);
            Assert.Same(caller.PolicyBinding, context.PolicyBinding);
            Assert.Equal(caller.SecurityPolicy!.BindingId, context.SecurityPolicy!.BindingId);
            Assert.Equal(caller.SecurityPolicy.Generation, context.SecurityPolicy.Generation);
            Assert.NotEqual(caller.SecurityPolicy.Fingerprint, context.SecurityPolicy.Fingerprint);
            Assert.Equal(caller.SecurityPolicy.Fingerprint, context.SecurityPolicy.ParentFingerprint);
            foreach (var path in new[] { "../outside.md", "implementation-plans/guide.md" })
            {
                var denied = await pipeline.InvokeAsync(
                    request with
                    {
                        ToolId = "read_file",
                        ArgumentsJson = JsonSerializer.Serialize(new ReadFileInput { Path = path }),
                    },
                    cancellationToken);
                Assert.False(denied.Succeeded);
                Assert.Equal(ToolErrorClassification.PolicyDenied, denied.ErrorClassification);
            }

            caller.PolicyBinding!.Invalidate();
            var stale = await pipeline.InvokeAsync(request, cancellationToken);
            Assert.False(stale.Succeeded);
            Assert.Contains("PolicyGenerationStale", stale.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Bundle authority keeps parent tool denials and cannot admit writes or stale callers.</summary>
    [Fact]
    public static void BindToBundle_PreservesRestrictionsAndPolicyFence()
    {
        var caller = CreatePolicyContext(Path.GetTempPath());
        var policy = caller.SecurityPolicy! with { DeniedTools = ["search"] };
        caller.PolicyBinding!.Publish(policy);
        caller = caller with { SecurityPolicy = caller.PolicyBinding.Current };

        var bound = PackagedDocumentationPolicy.BindToBundle(caller, AppContext.BaseDirectory);

        Assert.False(bound.SecurityPolicy!.AllowsTool("search"));
        Assert.True(bound.SecurityPolicy.AllowsTool("read_file"));
        Assert.False(bound.SecurityPolicy.AllowsTool("write_file"));
        Assert.False(bound.SecurityPolicy.AllowsTool("run_process"));
        Assert.Empty(bound.SecurityPolicy.WriteFolders);
        Assert.Empty(bound.SecurityPolicy.EditableRoots);
        Assert.Empty(bound.SecurityPolicy.MutationRoots);
        Assert.False(bound.AllowDirectWrites);
        caller.PolicyBinding.Publish(caller.SecurityPolicy! with { Generation = caller.SecurityPolicy!.Generation + 1 });
        Assert.Throws<UnauthorizedAccessException>(() => caller.PolicyBinding.Validate(bound.SecurityPolicy));
        Assert.Throws<UnauthorizedAccessException>(() => PackagedDocumentationPolicy.BindToBundle(caller, AppContext.BaseDirectory));
    }

    /// <summary>A bundle read scope cannot turn an empty intersection or a deny-all caller into unrestricted authority.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void BindToBundle_PreservesCallerToolCeilings(bool denyAll)
    {
        var caller = CreatePolicyContext(Path.GetTempPath()) with
        {
            AllowedToolIds = denyAll ? [] : ["invoke_skill"],
            DenyAllTools = denyAll,
        };

        var bound = PackagedDocumentationPolicy.BindToBundle(caller, AppContext.BaseDirectory);

        Assert.True(bound.DenyAllTools);
    }

    /// <summary>The documentation selector binds existing file tools to an application-owned read-only root.</summary>
    [Fact]
    public static void BindToBundle_UsesOnlyPackagedDocumentationRoot()
    {
        var context = new ToolInvocationContext
        {
            WorkspaceId = WorkspaceId.New(),
            RepositoryPath = Path.GetTempPath(),
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            AllowedExecutables = ["pwsh"],
            AllowedNetworkHosts = ["example.com"],
            RequestedBy = "test",
        };

        var bound = PackagedDocumentationPolicy.BindToBundle(context, AppContext.BaseDirectory);

        Assert.Null(bound.WorkspaceId);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ThreadsmithDocs")),
            bound.RepositoryPath);
        Assert.Empty(bound.AllowedExecutables);
        Assert.Empty(bound.AllowedNetworkHosts);
        Assert.True(PackagedDocumentationPolicy.IsDocumentationSkill(
            SkillScope.Maintained,
            "threadsmith-docs-help"));
        Assert.False(PackagedDocumentationPolicy.IsDocumentationSkill(
            SkillScope.Repository,
            "threadsmith-docs-help"));
    }

    /// <summary>Cited answers must resolve inside confined shipped documentation despite presentation variance.</summary>
    [Fact]
    public static async Task ValidateAnswerAsync_RequiresExactConfinedCitation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "guide.md"),
                "# Guide\nUse /compact to compact the current conversation.\n");
            var excludedDirectory = Path.Combine(root, "docs", "implementation-plans");
            Directory.CreateDirectory(excludedDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(excludedDirectory, "guide.md"),
                "# Guide\nUse /compact to compact the current conversation.\n");
            var valid = """
                {"status":"answered","answer":"Use /compact.","citations":[{"path":"guide.md","heading":"# Guide","lineStart":1,"lineEnd":2,"snippet":"Use /compact"}],"gaps":[]}
                """;

            var normalized = await PackagedDocumentationPolicy.ValidateAnswerAsync(valid, root);
            using (var normalizedDocument = System.Text.Json.JsonDocument.Parse(normalized))
            {
                Assert.Equal(
                    "# Guide",
                    normalizedDocument.RootElement.GetProperty("citations")[0].GetProperty("heading").GetString());
            }

            var escaping = valid.Replace("guide.md", "../guide.md", StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PackagedDocumentationPolicy.ValidateAnswerAsync(escaping, root));

            var wrongHeading = valid.Replace("# Guide", "# Other", StringComparison.Ordinal);
            var normalizedHeading = await PackagedDocumentationPolicy.ValidateAnswerAsync(wrongHeading, root);
            using (var normalizedDocument = System.Text.Json.JsonDocument.Parse(normalizedHeading))
            {
                Assert.Equal(
                    "# Guide",
                    normalizedDocument.RootElement.GetProperty("citations")[0].GetProperty("heading").GetString());
            }

            var elidedSnippet = valid.Replace(
                "Use /compact\"",
                "Use ... current conversation.\"",
                StringComparison.Ordinal);
            var normalizedSnippet = await PackagedDocumentationPolicy.ValidateAnswerAsync(elidedSnippet, root);
            using (var normalizedDocument = System.Text.Json.JsonDocument.Parse(normalizedSnippet))
            {
                Assert.Equal(
                    "Use /compact to compact the current conversation.",
                    normalizedDocument.RootElement.GetProperty("citations")[0].GetProperty("snippet").GetString());
            }

            var fabricatedSnippet = valid.Replace("Use /compact\"", "Use /missing\"", StringComparison.Ordinal);
            var normalizedPresentation = await PackagedDocumentationPolicy.ValidateAnswerAsync(
                fabricatedSnippet,
                root);
            using (var normalizedDocument = System.Text.Json.JsonDocument.Parse(normalizedPresentation))
            {
                Assert.Equal(
                    "Use /missing",
                    normalizedDocument.RootElement.GetProperty("citations")[0].GetProperty("snippet").GetString());
            }

            var contradictoryStatus = valid.Replace("answered", "unavailable", StringComparison.Ordinal);
            var normalizedStatus = await PackagedDocumentationPolicy.ValidateAnswerAsync(contradictoryStatus, root);
            using (var normalizedDocument = System.Text.Json.JsonDocument.Parse(normalizedStatus))
            {
                Assert.Equal("partial", normalizedDocument.RootElement.GetProperty("status").GetString());
            }

            await File.WriteAllTextAsync(
                Path.Combine(root, "sections.md"),
                "# Guide\nFirst section.\n# Other\nSecond section.\n");
            var crossingSection =
                """
                {"status":"answered","answer":"Second section.","citations":[{"path":"sections.md","heading":"# Guide","lineStart":2,"lineEnd":4,"snippet":"Second section."}],"gaps":[]}
                """;
            var normalizedRange = await PackagedDocumentationPolicy.ValidateAnswerAsync(crossingSection, root);
            using (var normalizedDocument = System.Text.Json.JsonDocument.Parse(normalizedRange))
            {
                Assert.Equal(
                    "# Guide",
                    normalizedDocument.RootElement.GetProperty("citations")[0].GetProperty("heading").GetString());
            }

            var excluded = valid.Replace(
                "guide.md",
                "docs/implementation-plans/guide.md",
                StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PackagedDocumentationPolicy.ValidateAnswerAsync(excluded, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ToolInvocationContext CreatePolicyContext(string repositoryPath)
    {
        var binding = new SessionPolicyBinding();
        binding.Publish(new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)),
            Fingerprint = "documentation-test",
            WriteFolders = [repositoryPath],
            EditableRoots = [repositoryPath],
            MutationRoots = [repositoryPath],
            ResourceLimits = System.Collections.Immutable.ImmutableDictionary<string, int>.Empty.Add("tools:parallel:maximumConcurrency", 4),
        });
        return new ToolInvocationContext
        {
            RepositoryPath = repositoryPath,
            SecurityPolicy = binding.Current,
            PolicyBinding = binding,
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            RequestedBy = "test",
        };
    }

    private sealed class DocumentationSearchModelProvider : IModelProvider
    {
        private readonly string _path;

        public DocumentationSearchModelProvider(string path)
        {
            _path = path;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1)
            {
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput("search", JsonSerializer.Serialize(new SearchTextInput { Query = "subagent", Path = _path })),
                };
            }
            else
            {
                yield return new ModelChunk
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        status = "answered",
                        answer = "A subagent follows its assignment.",
                        citations = new[] { new { path = _path + "/guide.md", heading = "# Guide", lineStart = 1, lineEnd = 2, snippet = "A subagent follows its assignment." } },
                        gaps = Array.Empty<string>(),
                    }),
                };
            }
        }
    }
}
