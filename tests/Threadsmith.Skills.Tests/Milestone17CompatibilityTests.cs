namespace Threadsmith.Skills.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Verifies Milestone 17 skill compatibility and repository model selection contracts.</summary>
public sealed class Milestone17CompatibilityTests
{
    /// <summary>Verifies discovery reads frontmatter but leaves supporting resources unopened.</summary>
    [Fact]
    public async Task ClaudeCatalog_Refresh_IsMetadataOnly()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var skillRoot = CreateSkill(directory.Path, "portable-review", "allowed-tools: Read Grep");
        var resource = Path.Combine(skillRoot, "reference.txt");
        await File.WriteAllTextAsync(resource, "private body");
        await using var locked = new FileStream(resource, FileMode.Open, FileAccess.Read, FileShare.None);
        var catalog = CreateClaudeCatalog(directory.Path);

        // Act
        var candidates = await catalog.RefreshAsync();

        // Assert
        var candidate = Assert.Single(candidates);
        Assert.Equal(ClaudeSkillCompatibilityStatus.Compatible, candidate.Status);
        Assert.Equal(["read_file", "search_text"], candidate.MappedTools);
        Assert.Null(candidate.Identity.Digest);
    }

    /// <summary>Verifies immutable activation digests every eligible byte and confines loaded text.</summary>
    [Fact]
    public async Task ClaudeCatalog_Activate_DigestChangesWithResource()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var skillRoot = CreateSkill(directory.Path, "portable-review", string.Empty);
        var resource = Path.Combine(skillRoot, "reference.txt");
        await File.WriteAllTextAsync(resource, "first");
        var catalog = CreateClaudeCatalog(directory.Path);
        var candidate = Assert.Single(await catalog.RefreshAsync());

        // Act
        var first = await catalog.ActivateAsync(candidate);
        await File.WriteAllTextAsync(resource, "second");
        var second = await catalog.ActivateAsync(candidate);

        // Assert
        Assert.NotEqual(first.Candidate.Identity.Digest, second.Candidate.Identity.Digest);
        Assert.Equal("first", first.Resources["reference.txt"]);
        Assert.Contains("Review this repository.", first.Instructions, StringComparison.Ordinal);
    }

    /// <summary>Verifies one malformed skill remains diagnostic without hiding valid candidates.</summary>
    [Fact]
    public async Task ClaudeCatalog_Refresh_IsolatesMalformedCandidate()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var skillRoot = Path.Combine(directory.Path, "unsafe-skill");
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(
            Path.Combine(skillRoot, "SKILL.md"),
            "---\nname: unsafe-skill\ndescription: &shared unsafe\nmetadata: *shared\n---\nbody");
        _ = CreateSkill(directory.Path, "valid-skill", string.Empty);
        var catalog = CreateClaudeCatalog(directory.Path);

        // Act
        var candidates = await catalog.RefreshAsync();

        // Assert
        Assert.Equal(2, candidates.Count);
        var invalid = Assert.Single(
            candidates,
            candidate => candidate.Identity.Name == "unsafe-skill");
        Assert.Equal(ClaudeSkillCompatibilityStatus.Unsupported, invalid.Status);
        Assert.Equal(["invalid-metadata"], invalid.ReasonCodes);
        Assert.Contains(candidates, candidate => candidate.Identity.Name == "valid-skill"
            && candidate.Status == ClaudeSkillCompatibilityStatus.Compatible);
    }

    /// <summary>Activation rejects metadata that changed after the candidate generation was refreshed.</summary>
    [Fact]
    public async Task ClaudeCatalog_Activate_RevalidatesFrontmatter()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var skillRoot = CreateSkill(directory.Path, "portable-review", "allowed-tools: Read");
        var catalog = CreateClaudeCatalog(directory.Path);
        var candidate = Assert.Single(await catalog.RefreshAsync());
        await File.WriteAllTextAsync(
            Path.Combine(skillRoot, "SKILL.md"),
            "---\nname: portable-review\ndescription: Portable repository review\nhooks: unsafe\n---\nChanged.\n");

        // Act and assert
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ActivateAsync(candidate));
    }

    /// <summary>Activation never follows a nested linked directory outside the configured skill root.</summary>
    [Fact]
    public async Task ClaudeCatalog_Activate_RejectsNestedDirectoryLink()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        var skillRoot = CreateSkill(directory.Path, "portable-review", string.Empty);
        await File.WriteAllTextAsync(Path.Combine(external.Path, "outside.txt"), "outside secret");
        var linkPath = Path.Combine(skillRoot, "references");
        try
        {
            _ = Directory.CreateSymbolicLink(linkPath, external.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var catalog = CreateClaudeCatalog(directory.Path);
        var candidate = Assert.Single(await catalog.RefreshAsync());

        // Act and assert
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ActivateAsync(candidate));
    }

    /// <summary>Activation validates the opened resource handle and rejects a linked external file.</summary>
    [Fact]
    public async Task ClaudeCatalog_Activate_RejectsLinkedResourceFile()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        var skillRoot = CreateSkill(directory.Path, "portable-review", string.Empty);
        var externalPath = Path.Combine(external.Path, "outside.txt");
        await File.WriteAllTextAsync(externalPath, "outside secret");
        try
        {
            _ = File.CreateSymbolicLink(Path.Combine(skillRoot, "reference.txt"), externalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var catalog = CreateClaudeCatalog(directory.Path);
        var candidate = Assert.Single(await catalog.RefreshAsync());

        // Act and assert
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ActivateAsync(candidate));
    }

    /// <summary>Activation rejects an oversized tree as soon as the configured file bound is exceeded.</summary>
    [Fact]
    public async Task ClaudeCatalog_Activate_RejectsFileCountAboveBound()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var skillRoot = CreateSkill(directory.Path, "portable-review", string.Empty);
        for (var index = 0; index < 2; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(skillRoot, $"resource-{index:D2}.txt"), "content");
        }

        var catalog = new ClaudeSkillCompatibilityCatalog(
            [new ClaudeSkillRoot(SkillScope.Repository, directory.Path, "repository:.claude/skills", true)],
            new ClaudeSkillCompatibilityOptions { MaximumFiles = 2 });
        var candidate = Assert.Single(await catalog.RefreshAsync());

        // Act and assert
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ActivateAsync(candidate));
    }

    /// <summary>Repository rebinding removes candidates controlled by the previously active repository.</summary>
    [Fact]
    public async Task ClaudeCatalog_BindRepository_ReplacesRepositoryRoot()
    {
        // Arrange
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        _ = CreateSkill(Path.Combine(first.Path, ".claude", "skills"), "first-skill", string.Empty);
        _ = CreateSkill(Path.Combine(second.Path, ".claude", "skills"), "second-skill", string.Empty);
        var catalog = CreateClaudeCatalog(Path.Combine(first.Path, ".claude", "skills"));
        _ = await catalog.RefreshAsync();

        // Act
        await catalog.BindRepositoryAsync(second.Path);

        // Assert
        var candidate = Assert.Single(catalog.Candidates);
        Assert.Equal("second-skill", candidate.Identity.Name);
    }

    /// <summary>Exact external enablement permits Claude invocation only through Plan-39 workflow contracts.</summary>
    [Fact]
    public async Task ClaudeAdapter_ExactEnablement_InvokesThroughPlan39Workflow()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var claudeRoot = Path.Combine(directory.Path, "claude");
        _ = CreateSkill(claudeRoot, "portable-review", "allowed-tools: Read");
        var nativeRoot = Path.Combine(directory.Path, "native");
        Directory.CreateDirectory(nativeRoot);
        var nativeCatalog = new SkillCatalog(
            [new SkillCatalogSource(SkillScope.User, nativeRoot, "user:test")]);
        var claudeCatalog = CreateClaudeCatalog(claudeRoot);
        var catalog = new CompatibleSkillCatalog(nativeCatalog, claudeCatalog);
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(
            Path.Combine(directory.Path, "policy.json"),
            new SkillTrustPolicySnapshot());
        var verifier = new CompatibleSkillPackageVerifier(
            new SkillPackageVerifier(policy),
            catalog,
            policy);
        var selected = await catalog.ResolveAsync(
            "claude:Repository:portable-review");
        var initiallyVerified = await verifier.VerifyAsync(selected);
        await policy.SetEnabledAsync(initiallyVerified, enabled: true);
        var runner = new CapturingProcedureRunner();
        var state = new TestSkillStateStore();
        await using var events = new DomainEventStream();
        await using var orchestrator = new SkillWorkflowOrchestrator(
            catalog,
            verifier,
            new AlwaysCompatibleEvaluator(),
            new CompatibleSkillContentLoader(
                new SkillContentLoader(new SecretOutputSanitizer()),
                catalog,
                new SecretOutputSanitizer()),
            new BoundedJsonSchemaValidator(),
            runner,
            TestPromptLoader.Instance,
            state,
            (_, _) => Task.FromResult(new SkillInvocationHostContext
            {
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
            }),
            events);

        // Act
        var result = await orchestrator.InvokeAsync(
            new SkillInvocationRequest
            {
                InvocationId = SkillInvocationId.New(),
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Selector = "claude:Repository:portable-review",
                InputJson = "{\"target\":\"repository\"}",
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
                HostBudget = new SkillBudget(),
            });

        // Assert
        Assert.Equal(SkillInvocationStatus.Completed, result.Status);
        Assert.StartsWith("claude.portable-review", result.Package.SkillId.Value, StringComparison.Ordinal);
        Assert.Equal(64, result.Package.Digest.Value.Length);
        Assert.Contains(runner.Content, item => item.AssetPath == "SKILL.md"
            && item.Content.Contains("Review this repository.", StringComparison.Ordinal));
        Assert.Equal("{\"summary\":\"ok\"}", result.OutputJson);
    }

    /// <summary>A changed Claude source cannot verify or resume under an earlier digest selector.</summary>
    [Fact]
    public async Task ClaudeAdapter_SourceChange_InvalidatesExactSelection()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var claudeRoot = Path.Combine(directory.Path, "claude");
        var skillRoot = CreateSkill(claudeRoot, "portable-review", string.Empty);
        var nativeRoot = Path.Combine(directory.Path, "native");
        Directory.CreateDirectory(nativeRoot);
        var catalog = new CompatibleSkillCatalog(
            new SkillCatalog([new SkillCatalogSource(SkillScope.User, nativeRoot, "user:test")]),
            CreateClaudeCatalog(claudeRoot));
        await catalog.RefreshAsync();
        var exact = await catalog.ResolveAsync(
            "claude:Repository:portable-review");
        await File.AppendAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "Changed.\n");
        var selector = $"Repository:{exact.Metadata.SkillId.Value}"
            + $"@{exact.Metadata.Version}+{exact.Identity.Digest.Value}";

        // Act/Assert
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ResolveAsync(selector));
    }

    /// <summary>Verifies repository selection overrides the user default and exact reasoning is restored.</summary>
    [Fact]
    public async Task ActiveModels_RepositorySelection_OverridesCatalogDefault()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        ModelProfileId firstId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ModelProfileId secondId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var catalog = CreateModelCatalog(firstId, secondId);
        var configurationPath = Path.Combine(directory.Path, ".threadsmith", "config.json");
        var configurationDirectory = Path.GetDirectoryName(configurationPath)
            ?? throw new InvalidOperationException("Test configuration path has no parent.");
        Directory.CreateDirectory(configurationDirectory);
        await File.WriteAllTextAsync(
            configurationPath,
            $$"""
            {
              "unrelated": true,
              "model": {
                "providerId": "provider-b",
                "profileId": "{{secondId.Value:D}}",
                "reasoningLevel": "medium"
              }
            }
            """);
        var preferences = new SessionModelPreferences(firstId, ReasoningLevel.Low);

        // Act
        var service = new ActiveModelSelectionService(catalog, preferences, configurationPath);

        // Assert
        Assert.Equal(secondId, service.Current.Profile.Id);
        Assert.Equal(ReasoningLevel.Medium, service.Current.ReasoningLevel);
        Assert.Equal(ActiveModelSelectionSource.Repository, service.Current.Source);
    }

    /// <summary>Verifies switching resets unsupported reasoning and atomically preserves unrelated settings.</summary>
    [Fact]
    public async Task ActiveModels_Select_ResetsUnsupportedReasoningAndPersistsCompleteSelection()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        ModelProfileId firstId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ModelProfileId secondId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var catalog = CreateModelCatalog(firstId, secondId);
        var configurationPath = Path.Combine(directory.Path, ".threadsmith", "config.json");
        var configurationDirectory = Path.GetDirectoryName(configurationPath)
            ?? throw new InvalidOperationException("Test configuration path has no parent.");
        Directory.CreateDirectory(configurationDirectory);
        await File.WriteAllTextAsync(configurationPath, "{ \"unrelated\": 42 }");
        var preferences = new SessionModelPreferences(firstId, ReasoningLevel.High);
        var service = new ActiveModelSelectionService(catalog, preferences, configurationPath);

        // Act
        var result = await service.SelectAsync(secondId);
        var saved = await File.ReadAllTextAsync(configurationPath);

        // Assert
        Assert.False(result.ReasoningPreserved);
        Assert.Equal(ReasoningLevel.None, result.Selection.ReasoningLevel);
        Assert.True(result.Persisted);
        Assert.Contains("\"unrelated\": 42", saved, StringComparison.Ordinal);
        Assert.Contains("\"providerId\": \"provider-b\"", saved, StringComparison.Ordinal);
        Assert.Contains($"\"profileId\": \"{secondId.Value:D}\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"reasoningLevel\": \"none\"", saved, StringComparison.Ordinal);
    }

    /// <summary>Repository model persistence rejects a linked settings directory without touching its target.</summary>
    [Fact]
    public async Task ActiveModels_LinkedSettingsDirectory_IsRejected()
    {
        // Arrange
        using var repository = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        var sentinel = Path.Combine(external.Path, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        try
        {
            _ = Directory.CreateSymbolicLink(Path.Combine(repository.Path, ".threadsmith"), external.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        ModelProfileId firstId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ModelProfileId secondId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        // Act and assert
        _ = Assert.Throws<UnauthorizedAccessException>(() => new ActiveModelSelectionService(
            CreateModelCatalog(firstId, secondId),
            new SessionModelPreferences(firstId, ReasoningLevel.Low),
            Path.Combine(repository.Path, ".threadsmith", "config.json")));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
    }

    /// <summary>An immutable request generation cannot revert a newer live model selection.</summary>
    [Fact]
    public void SessionModelPreferenceSnapshot_LateResolution_DoesNotMutateNewerSelection()
    {
        // Arrange
        ModelProfileId firstId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ModelProfileId secondId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var preferences = new SessionModelPreferences(firstId, ReasoningLevel.Low);
        var request = preferences.Capture();
        preferences.SetReasoning(secondId, ReasoningLevel.Medium);

        // Act
        var requestReasoning = request.ResolveFor(firstId);
        var current = preferences.Capture();

        // Assert
        Assert.Equal(ReasoningLevel.Low, requestReasoning);
        Assert.Equal(secondId, current.ProfileId);
        Assert.Equal(ReasoningLevel.Medium, current.Reasoning);
        Assert.True(current.Generation > request.Generation);
    }

    /// <summary>Shared model commands invalidate both run inspection cache and session projection state.</summary>
    [Fact]
    public async Task ActiveModels_SharedSelection_InvalidatesContextInspection()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        ModelProfileId firstId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ModelProfileId secondId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var selection = new ActiveModelSelectionService(
            CreateModelCatalog(firstId, secondId),
            new SessionModelPreferences(firstId, ReasoningLevel.Low),
            Path.Combine(directory.Path, ".threadsmith", "config.json"));
        var assembler = new TrackingContextAssembler();
        var projections = new InMemoryProjectionStore();
        var sessionId = SessionId.New();
        var runId = RunId.New();
        await projections.ApplyAsync(new SessionCreated(sessionId, DateTimeOffset.UtcNow, "test"));
        await projections.ApplyAsync(new ContextAssembled(
            sessionId,
            DateTimeOffset.UtcNow,
            new ContextInspectionProjection { RunId = runId, ModelProfileId = firstId }));
        var application = new ActiveModelSelectionApplication(selection, assembler, projections);

        // Act
        _ = await application.HandleAsync(new SelectActiveModelCommand(secondId));
        var projection = await projections.GetAsync<SessionProjection>(
            new ProjectionKey("session", sessionId.Value.ToString("D")));

        // Assert
        Assert.True(assembler.WasInvalidated);
        Assert.NotNull(projection);
        Assert.Null(projection.ContextInspection);
    }

    /// <summary>Repository model selection accepts the same trailing-comma syntax as normal configuration.</summary>
    [Fact]
    public async Task ActiveModels_RepositorySelection_AcceptsTrailingCommas()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        ModelProfileId firstId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ModelProfileId secondId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var configurationPath = Path.Combine(directory.Path, ".threadsmith", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)
            ?? throw new InvalidOperationException("Test configuration path has no parent."));
        await File.WriteAllTextAsync(
            configurationPath,
            $$"""
            {
              // Existing repository syntax remains accepted.
              "model": {
                "providerId": "provider-b",
                "profileId": "{{secondId.Value:D}}",
                "reasoningLevel": "medium",
              },
            }
            """);

        // Act
        var service = new ActiveModelSelectionService(
            CreateModelCatalog(firstId, secondId),
            new SessionModelPreferences(firstId, ReasoningLevel.Low),
            configurationPath);
        var result = await service.SetReasoningAsync(ReasoningLevel.None);

        // Assert
        Assert.Equal(secondId, result.Selection.Profile.Id);
        Assert.True(result.Persisted);
    }

    /// <summary>Opening another repository reloads selection and redirects subsequent persistence.</summary>
    [Fact]
    public async Task ActiveModels_BindRepository_ReloadsSelectionAndPersistenceTarget()
    {
        // Arrange
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        ModelProfileId firstId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ModelProfileId secondId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var firstConfiguration = Path.Combine(first.Path, ".threadsmith", "config.json");
        var secondConfiguration = Path.Combine(second.Path, ".threadsmith", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(firstConfiguration)
            ?? throw new InvalidOperationException("First test configuration path has no parent."));
        Directory.CreateDirectory(Path.GetDirectoryName(secondConfiguration)
            ?? throw new InvalidOperationException("Second test configuration path has no parent."));
        await File.WriteAllTextAsync(firstConfiguration, "{ \"repository\": \"first\" }");
        await File.WriteAllTextAsync(
            secondConfiguration,
            $$"""
            {
              "repository": "second",
              "model": {
                "providerId": "provider-b",
                "profileId": "{{secondId.Value:D}}",
                "reasoningLevel": "medium"
              }
            }
            """);
        var service = new ActiveModelSelectionService(
            CreateModelCatalog(firstId, secondId),
            new SessionModelPreferences(firstId, ReasoningLevel.Low),
            firstConfiguration);

        // Act
        await service.BindRepositoryAsync(second.Path);
        var result = await service.SelectAsync(firstId);
        var firstSaved = await File.ReadAllTextAsync(firstConfiguration);
        var secondSaved = await File.ReadAllTextAsync(secondConfiguration);

        // Assert
        Assert.Equal(firstId, result.Selection.Profile.Id);
        Assert.Contains("\"repository\": \"first\"", firstSaved, StringComparison.Ordinal);
        Assert.DoesNotContain("providerId", firstSaved, StringComparison.Ordinal);
        Assert.Contains("\"repository\": \"second\"", secondSaved, StringComparison.Ordinal);
        Assert.Contains($"\"profileId\": \"{firstId.Value:D}\"", secondSaved, StringComparison.Ordinal);
    }

    /// <summary>Model list, current, and selection operations are available through the shared dispatcher.</summary>
    [Fact]
    public async Task ActiveModels_SharedCommands_UseOneApplicationBoundary()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        ModelProfileId firstId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ModelProfileId secondId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var service = new ActiveModelSelectionService(
            CreateModelCatalog(firstId, secondId),
            new SessionModelPreferences(firstId, ReasoningLevel.Low),
            Path.Combine(directory.Path, ".threadsmith", "config.json"));
        var dispatcher = new CommandDispatcher([service]);

        // Act
        var models = await dispatcher.DispatchAsync(
            new ListActiveModelsCommand());
        var changed = await dispatcher.DispatchAsync(
            new SelectActiveModelCommand(secondId));
        var current = await dispatcher.DispatchAsync(
            new GetActiveModelSelectionCommand());

        // Assert
        Assert.Equal(2, models.Count);
        Assert.Equal(secondId, changed.Selection.Profile.Id);
        Assert.Equal(secondId, current.Profile.Id);
    }

    private static ClaudeSkillCompatibilityCatalog CreateClaudeCatalog(string root)
    {
        return new ClaudeSkillCompatibilityCatalog(
            [new ClaudeSkillRoot(SkillScope.Repository, root, "repository:.claude/skills", true)]);
    }

    private static string CreateSkill(string root, string name, string extraMetadata)
    {
        var skillRoot = Path.Combine(root, name);
        Directory.CreateDirectory(skillRoot);
        File.WriteAllText(
            Path.Combine(skillRoot, "SKILL.md"),
            $"---\nname: {name}\ndescription: Portable repository review\n{extraMetadata}\n---\nReview this repository.\n");
        return skillRoot;
    }

    private static EffectiveModelProviderCatalog CreateModelCatalog(
        ModelProfileId firstId,
        ModelProfileId secondId)
    {
        var registration = new TestProviderRegistration();
        return new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration
            {
                DefaultProviderId = "provider-a",
                DefaultModelId = firstId,
                Providers =
                [
                    CreateProvider("provider-a", "Provider A", firstId, [ReasoningLevel.None, ReasoningLevel.Low, ReasoningLevel.High]),
                    CreateProvider("provider-b", "Provider B", secondId, [ReasoningLevel.None, ReasoningLevel.Medium]),
                ],
            },
            new ModelProviderRegistry([registration]));
    }

    private static TestProviderConfiguration CreateProvider(
        string id,
        string name,
        ModelProfileId profileId,
        IReadOnlyList<ReasoningLevel> levels)
    {
        return new TestProviderConfiguration
        {
            Id = id,
            Name = name,
            Models =
            [
                new TestModelConfiguration
                {
                    Id = profileId,
                    Name = $"Model {name}",
                    ContextWindow = 32_000,
                    MaximumOutputTokens = 4_000,
                    Capabilities = new ModelCapabilitySet { Streaming = true },
                    SupportedReasoningLevels = levels,
                    DefaultReasoningLevel = levels[0],
                },
            ],
        };
    }

    private sealed class AlwaysCompatibleEvaluator : ISkillCompatibilityEvaluator
    {
        public SkillCompatibilityResult Evaluate(
            SkillCatalogCandidate candidate,
            SkillInvocationRequest request)
        {
            return new SkillCompatibilityResult
            {
                IsCompatible = candidate.Verification != SkillVerificationState.Invalid,
                AvailableRequiredTools = candidate.Metadata.Requirements.RequiredTools,
            };
        }
    }

    private sealed class CapturingProcedureRunner : ISkillProcedureRunner
    {
        public IReadOnlyList<SkillContextSegment> Content { get; private set; } = [];

        public Task<SkillProcedureResult> RunAsync(
            SkillInvocationPlan plan,
            SkillWorkflowStep step,
            int iteration,
            IReadOnlyList<SkillContextSegment> content,
            string inputJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Content = content;
            return Task.FromResult(new SkillProcedureResult("{\"summary\":\"ok\"}", 1, 0));
        }
    }

    private sealed class TestSkillStateStore : ISkillStateStore
    {
        private readonly Dictionary<SkillInvocationId, SkillWorkflowCheckpoint> _checkpoints = [];

        public Task SaveVerificationAsync(
            SkillVerificationRecord verification,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<SkillVerificationRecord?> GetVerificationAsync(
            SkillDigest digest,
            SkillScope scope,
            string source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<SkillVerificationRecord?>(null);
        }

        public Task SaveCheckpointAsync(
            SkillWorkflowCheckpoint checkpoint,
            SkillCheckpointVersion? expectedVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _checkpoints[checkpoint.InvocationId] = checkpoint;
            return Task.CompletedTask;
        }

        public Task<SkillWorkflowCheckpoint?> GetCheckpointAsync(
            SkillInvocationId invocationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _checkpoints.TryGetValue(invocationId, out var checkpoint);
            return Task.FromResult(checkpoint);
        }

        public Task SavePinAsync(
            SkillId skillId,
            SkillPackageIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<SkillPackageIdentity?> GetPinAsync(
            SkillId skillId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<SkillPackageIdentity?>(null);
        }

        public Task<bool> HasActivePackageReferenceAsync(
            SkillPackageIdentity package,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }

    private sealed class TrackingContextAssembler : IContextAssembler
    {
        public bool WasInvalidated { get; private set; }

        public Task<ContextAssemblyResult> AssembleAsync(
            ContextAssemblyRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ContextInspectionProjection? GetInspection(RunId runId)
        {
            return null;
        }

        public void InvalidateInspections()
        {
            WasInvalidated = true;
        }
    }

    private sealed record TestProviderConfiguration : ModelProviderConfiguration;

    private sealed record TestModelConfiguration : ModelConfiguration;

    private sealed class TestProviderRegistration : IModelProviderRegistration
    {
        public string TypeDiscriminator => "test";

        public Type ProviderConfigurationType => typeof(TestProviderConfiguration);

        public Type ModelConfigurationType => typeof(TestModelConfiguration);

        public IModelProvider CreateProvider(ModelProviderActivationContext context)
        {
            return new FakeModelProvider(new ScriptedSession());
        }

        public IReadOnlyList<ModelProfile> CreateProfiles(ModelProviderConfiguration provider)
        {
            return provider.Models.Select(model => new ModelProfile
            {
                Id = model.Id,
                Name = model.Name,
                Provider = provider.Name,
                Endpoint = new Uri("https://models.example.invalid"),
                ModelId = model.Id.Value.ToString("D"),
                ContextWindow = model.ContextWindow,
                MaximumOutputTokens = model.MaximumOutputTokens,
                Capabilities = model.Capabilities,
                SupportedReasoningLevels = model.SupportedReasoningLevels,
                DefaultReasoningLevel = model.DefaultReasoningLevel,
            }).ToArray();
        }

        public void Validate(ModelProviderConfiguration provider)
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"threadsmith-m17-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
