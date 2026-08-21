namespace Threadsmith.Milestone12.Tests;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Skills;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies governed skill discovery, trust, schemas, loading, workflow, restoration, and persistence.</summary>
public sealed class SkillSubsystemTests
{
    /// <summary>Verifies startup discovery reads metadata while a declared body is exclusively locked.</summary>
    [Fact]
    public async Task CatalogRefresh_DoesNotOpenDeclaredBodies()
    {
        // Arrange
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var body = Path.Combine(package.PackageRoot, "instructions", "analyze.md");
        await using var locked = new FileStream(body, FileMode.Open, FileAccess.Read, FileShare.None);
        var catalog = package.CreateCatalog(SkillScope.Repository);

        // Act
        var snapshot = await catalog.RefreshAsync();

        // Assert
        var candidate = Assert.Single(snapshot.Candidates);
        Assert.Equal(SkillVerificationState.Unverified, candidate.Verification);
        Assert.False(candidate.Enabled);
    }

    /// <summary>Verifies all three maintained packages discover and integrity-verify.</summary>
    [Fact]
    public async Task MaintainedCatalog_ContainsThreeVerifiedWorkflows()
    {
        // Arrange
        var root = MaintainedRoot();
        var catalog = new SkillCatalog(
            [new SkillCatalogSource(SkillScope.Maintained, root, "maintained", IsMaintained: true)]);
        var snapshot = await catalog.RefreshAsync();
        var verifier = new SkillPackageVerifier(new SkillTrustPolicySnapshot());

        // Act
        SkillCatalogCandidate[] verified =
        [
            .. await Task.WhenAll(snapshot.Candidates.Select(item => verifier.VerifyAsync(item))),
        ];

        // Assert
        Assert.Equal(3, verified.Length);
        Assert.All(verified, item => Assert.Equal(SkillVerificationState.Maintained, item.Verification));
        Assert.All(verified, item => Assert.True(item.Enabled));
        Assert.Contains(verified, item => item.Metadata.SkillId.Value == "fix-analyzer-warnings");
        Assert.Contains(verified, item => item.Metadata.SkillId.Value == "upgrade-package");
        Assert.Contains(verified, item => item.Metadata.SkillId.Value == "review-pr");
    }

    /// <summary>Verifies a body changed after metadata discovery fails integrity verification.</summary>
    [Fact]
    public async Task Verifier_TamperedBody_FailsClosed()
    {
        // Arrange
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var catalog = package.CreateCatalog(SkillScope.User);
        var candidate = Assert.Single((await catalog.RefreshAsync()).Candidates);
        await File.AppendAllTextAsync(
            Path.Combine(package.PackageRoot, "instructions", "analyze.md"),
            "tampered");

        // Act
        var verified = await new SkillPackageVerifier(
            new SkillTrustPolicySnapshot()).VerifyAsync(candidate);

        // Assert
        Assert.Equal(SkillVerificationState.Invalid, verified.Verification);
        Assert.False(verified.Enabled);
    }

    /// <summary>Verifies a valid detached signature over the exact digest establishes trust.</summary>
    [Fact]
    public async Task Verifier_TrustedEcdsaSignature_EnablesExactPackage()
    {
        // Arrange
        using var package = TemporaryPackage.CopyMaintained("fix-analyzer-warnings");
        var unsignedCatalog = package.CreateCatalog(SkillScope.User);
        var unsigned = Assert.Single((await unsignedCatalog.RefreshAsync()).Candidates);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = key.SignHash(
            Convert.FromHexString(unsigned.Identity.Digest.Value),
            DSASignatureFormat.Rfc3279DerSequence);
        AddSignature(
            Path.Combine(package.PackageRoot, "skill.json"),
            "test-signer",
            Convert.ToBase64String(signature));
        var catalog = package.CreateCatalog(SkillScope.User);
        var candidate = Assert.Single((await catalog.RefreshAsync()).Candidates);
        var policy = new SkillTrustPolicySnapshot
        {
            TrustedSignerPublicKeys = new Dictionary<string, string>
            {
                ["test-signer"] = key.ExportSubjectPublicKeyInfoPem(),
            },
        };
        var selector = $"User:{candidate.Metadata.SkillId.Value}@{candidate.Metadata.Version}"
            + $"+{candidate.Identity.Digest.Value}";

        // Act
        var verified = await new SkillPackageVerifier(policy).VerifyAsync(candidate);
        var enabled = await new SkillPackageVerifier(
            policy with { EnabledSelectors = new HashSet<string> { selector } }).VerifyAsync(candidate);

        // Assert
        Assert.Equal(unsigned.Identity.Digest.Value, candidate.Identity.Digest.Value);
        Assert.Equal(SkillVerificationState.SignedTrusted, verified.Verification);
        Assert.False(verified.Enabled);
        Assert.True(enabled.Enabled);
    }

    /// <summary>Verifies revocation overrides an otherwise trusted exact digest.</summary>
    [Fact]
    public async Task Verifier_RevokedDigest_OverridesMaintainedTrust()
    {
        // Arrange
        var root = MaintainedRoot();
        var catalog = new SkillCatalog(
            [new SkillCatalogSource(SkillScope.Maintained, root, "maintained", IsMaintained: true)]);
        var candidate = (await catalog.RefreshAsync()).Candidates.Single(item =>
            item.Metadata.SkillId.Value == "fix-analyzer-warnings");
        var policy = new SkillTrustPolicySnapshot
        {
            RevokedDigests = new HashSet<string> { candidate.Identity.Digest.Value },
            DeniedSkillIds = new HashSet<string> { candidate.Metadata.SkillId.Value },
        };

        // Act
        var verified = await new SkillPackageVerifier(policy).VerifyAsync(candidate);

        // Assert
        Assert.Equal(SkillVerificationState.Revoked, verified.Verification);
        Assert.False(verified.Enabled);
    }

    /// <summary>Verifies repository content cannot self-authorize but user policy can allowlist the exact tuple.</summary>
    [Fact]
    public async Task UserPolicy_Enable_WritesExactExternalAllowlist()
    {
        // Arrange
        using var package = TemporaryPackage.CopyMaintained("upgrade-package");
        var catalog = package.CreateCatalog(SkillScope.Repository);
        var candidate = Assert.Single((await catalog.RefreshAsync()).Candidates);
        var policyPath = Path.Combine(package.Root, "outside-repository-policy.json");
        var provider = new FileSkillTrustPolicyProvider(
            policyPath,
            new SkillTrustPolicySnapshot());
        var verifier = new SkillPackageVerifier(provider);
        var before = await verifier.VerifyAsync(candidate);

        // Act
        await provider.SetEnabledAsync(candidate, enabled: true);
        var after = await verifier.VerifyAsync(candidate);

        // Assert
        Assert.Equal(SkillVerificationState.Unverified, before.Verification);
        Assert.False(before.Enabled);
        Assert.Equal(SkillVerificationState.DigestAllowlisted, after.Verification);
        Assert.True(after.Enabled);
        Assert.DoesNotContain(package.PackageRoot, await File.ReadAllTextAsync(policyPath));
    }

    /// <summary>Verifies unsupported references, unknown keywords, extra values, and integer mismatch fail closed.</summary>
    [Fact]
    public void SchemaValidator_RejectsUnsafeOrMismatchedSchemasAndValues()
    {
        // Arrange
        var validator = new BoundedJsonSchemaValidator();
        const string safe = """
            {"type":"object","additionalProperties":false,"required":["count"],
             "properties":{"count":{"type":"integer","minimum":1,"maximum":3}}}
            """;
        var compiled = validator.Compile(safe);

        // Act / Assert
        Assert.Throws<NotSupportedException>(() =>
            validator.Compile("{\"type\":\"object\",\"$ref\":\"https://evil.invalid/schema\"}"));
        Assert.Throws<NotSupportedException>(() =>
            validator.Compile("{\"type\":\"string\",\"pattern\":\".*\"}"));
        Assert.Throws<InvalidDataException>(() =>
            validator.Compile("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}"));
        Assert.Throws<InvalidDataException>(() => validator.Validate(compiled, "{\"count\":2,\"extra\":true}"));
        Assert.Throws<InvalidDataException>(() => validator.Validate(compiled, "{\"count\":4}"));
        Assert.Throws<InvalidDataException>(() => validator.Validate(compiled, "{\"count\":2.5}"));
    }

    /// <summary>Verifies compatibility reports stable phase, trust, tool, and model denials before body loading.</summary>
    [Fact]
    public async Task Compatibility_MissingRequirements_ReturnsStableDenials()
    {
        // Arrange
        var root = MaintainedRoot();
        var catalog = new SkillCatalog(
            [new SkillCatalogSource(SkillScope.Maintained, root, "maintained", IsMaintained: true)]);
        var discovered = (await catalog.RefreshAsync()).Candidates.Single(item =>
            item.Metadata.SkillId.Value == "fix-analyzer-warnings");
        var candidate = await new SkillPackageVerifier(
            new SkillTrustPolicySnapshot()).VerifyAsync(discovered);
        var evaluator = new SkillCompatibilityEvaluator(
            new ToolRegistry([]),
            new ConfiguredModelCatalog([]),
            "1.0.0");

        // Act
        var result = evaluator.Evaluate(
            candidate,
            new SkillInvocationRequest
            {
                InvocationId = SkillInvocationId.New(),
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Selector = "fix-analyzer-warnings",
                InputJson = "{}",
                Trust = RepositoryTrustLevel.UntrustedInspection,
                Phase = RunPhase.ChangePlanning,
                HostBudget = new SkillBudget(),
            });

        // Assert
        Assert.False(result.IsCompatible);
        Assert.Contains("phase-ineligible", result.DenialReasons);
        Assert.Contains("insufficient-repository-trust", result.DenialReasons);
        Assert.Contains("no-compatible-model", result.DenialReasons);
        Assert.Contains(result.DenialReasons, item => item.StartsWith(
            "required-tool-unavailable:",
            StringComparison.Ordinal));
    }

    /// <summary>Verifies evidence collection is rejected before checkpointing when no model is configured.</summary>
    [Fact]
    public async Task Compatibility_CollectEvidenceWithoutModel_IsRejected()
    {
        // Arrange
        var root = MaintainedRoot();
        var catalog = new SkillCatalog(
            [new SkillCatalogSource(SkillScope.Maintained, root, "maintained", IsMaintained: true)]);
        var discovered = (await catalog.RefreshAsync()).Candidates.Single(item =>
            item.Metadata.SkillId.Value == "fix-analyzer-warnings");
        var verified = await new SkillPackageVerifier(
            new SkillTrustPolicySnapshot()).VerifyAsync(discovered);
        SkillWorkflowStep[] steps =
        [
            .. verified.Metadata.Workflow.Steps.Select((step, index) => index == 0
                ? step with { Kind = SkillWorkflowStepKind.CollectEvidence }
                : step),
        ];
        var candidate = verified with
        {
            Metadata = verified.Metadata with
            {
                Workflow = verified.Metadata.Workflow with { Steps = steps },
            },
        };
        var evaluator = new SkillCompatibilityEvaluator(
            new ToolRegistry([]),
            new ConfiguredModelCatalog([]),
            "1.0.0");

        // Act
        var result = evaluator.Evaluate(
            candidate,
            new SkillInvocationRequest
            {
                InvocationId = SkillInvocationId.New(),
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Selector = candidate.Metadata.SkillId.Value,
                InputJson = "{}",
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
                HostBudget = new SkillBudget(),
            });

        // Assert
        Assert.Contains("no-compatible-model", result.DenialReasons);
    }

    /// <summary>Verifies invoke_skill preserves the authoritative phase supplied by the tool pipeline.</summary>
    [Fact]
    public async Task InvokeSkillTool_NonEvidenceTurn_PreservesCallerPhase()
    {
        // Arrange
        var workflows = new CapturingWorkflowOrchestrator();
        var tool = new InvokeSkillTool(workflows);
        var context = new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext
            {
                RepositoryPath = Environment.CurrentDirectory,
                TrustLevel = RepositoryTrustLevel.TrustedRead,
                RequestedBy = "model",
            })
        {
            Phase = RunPhase.ChangePlanning,
        };

        // Act
        _ = await tool.ExecuteAsync(
            new InvokeSkillInput { Selector = "test-skill", InputJson = "{}" },
            context);

        // Assert
        Assert.NotNull(workflows.Request);
        Assert.Equal(RunPhase.ChangePlanning, workflows.Request.Phase);
    }

    /// <summary>Verifies required procedure assets cannot be silently dropped under context pressure.</summary>
    [Fact]
    public async Task ContentLoader_RequiredAssetPressure_FailsClosed()
    {
        // Arrange
        var root = MaintainedRoot();
        var catalog = new SkillCatalog(
            [new SkillCatalogSource(SkillScope.Maintained, root, "maintained", IsMaintained: true)]);
        var discovered = (await catalog.RefreshAsync()).Candidates.Single(item =>
            item.Metadata.SkillId.Value == "fix-analyzer-warnings");
        var candidate = await new SkillPackageVerifier(
            new SkillTrustPolicySnapshot()).VerifyAsync(discovered);
        var step = candidate.Metadata.Workflow.Steps.Single(item => item.StepId == "analyze");
        var loader = new SkillContentLoader(new Threadsmith.Telemetry.SecretOutputSanitizer());

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(
            candidate,
            step,
            maximumTokens: 1));
    }

    /// <summary>Verifies archive traversal is rejected before package verification or installation.</summary>
    [Fact]
    public async Task Installer_ArchiveTraversal_IsRejectedInQuarantine()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-skill-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(root, "bad.zip");
        try
        {
            await using (var archiveStream = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var entry = archive.CreateEntry("../escape.txt");
                await using var stream = await entry.OpenAsync();
                await stream.WriteAsync("bad"u8.ToArray());
            }

            var installer = new SkillPackageInstaller(
                Path.Combine(root, "store"),
                Path.Combine(root, "quarantine"));

            // Act / Assert
            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallArchiveAsync(
                archivePath,
                new SkillPackageVerifier(new SkillTrustPolicySnapshot()),
                SkillScope.User,
                "test"));
            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    /// <summary>Verifies a maintained two-step plan workflow pauses for a host action and then completes.</summary>
    [Fact]
    public async Task Workflow_ProcedureThenHostAction_CheckpointsAndCompletes()
    {
        // Arrange
        var root = MaintainedRoot();
        var catalog = new SkillCatalog(
            [new SkillCatalogSource(SkillScope.Maintained, root, "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var state = new InMemorySkillStateStore();
        await using var events = new DomainEventStream();
        var orchestrator = new SkillWorkflowOrchestrator(
            catalog,
            new SkillPackageVerifier(new SkillTrustPolicySnapshot()),
            new CompatibleEvaluator(),
            new SkillContentLoader(new Threadsmith.Telemetry.SecretOutputSanitizer()),
            new BoundedJsonSchemaValidator(),
            new FixedProcedureRunner(ValidPlanJson()),
            state,
            (_, _) => Task.FromResult(new SkillInvocationHostContext
            {
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
            }),
            events);
        var invocationId = SkillInvocationId.New();

        // Act
        var waiting = await orchestrator.InvokeAsync(
            new SkillInvocationRequest
            {
                InvocationId = invocationId,
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Selector = "fix-analyzer-warnings@1.0.0",
                InputJson = "{\"diagnostics\":[\"CA1822 at A.cs:10\"],\"scope\":[\"A.cs\"]}",
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
                HostBudget = new SkillBudget(),
            });
        var completed = await orchestrator.ContinueAsync(
            invocationId,
            "{\"accepted\":true,\"planId\":\"plan-1\"}");

        // Assert
        Assert.Equal(SkillInvocationStatus.AwaitingHost, waiting.Status);
        Assert.Equal(SkillHostActionKind.ProposePlan, Assert.Single(waiting.HostActions).Kind);
        Assert.Equal(SkillInvocationStatus.Completed, completed.Status);
        Assert.Equal(2, completed.Checkpoint.Steps.Count);
        Assert.Equal(completed.Checkpoint, await state.GetCheckpointAsync(invocationId));
        await orchestrator.DisposeAsync();
    }

    /// <summary>Verifies continuation revalidates current trust instead of checkpointed trust.</summary>
    [Fact]
    public async Task Workflow_ContinueAfterTrustDowngrade_IsRejected()
    {
        // Arrange
        var root = MaintainedRoot();
        var catalog = new SkillCatalog(
            [new SkillCatalogSource(SkillScope.Maintained, root, "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var state = new InMemorySkillStateStore();
        await using var events = new DomainEventStream();
        var workspaceId = WorkspaceId.New();
        SkillInvocationHostContext current = new()
        {
            WorkspaceId = workspaceId,
            Trust = RepositoryTrustLevel.TrustedRead,
            Phase = RunPhase.EvidenceCollection,
        };
        var compatibility = new CurrentFactsEvaluator();
        await using var orchestrator = new SkillWorkflowOrchestrator(
            catalog,
            new SkillPackageVerifier(new SkillTrustPolicySnapshot()),
            compatibility,
            new SkillContentLoader(new Threadsmith.Telemetry.SecretOutputSanitizer()),
            new BoundedJsonSchemaValidator(),
            new FixedProcedureRunner(ValidPlanJson()),
            state,
            (_, _) => Task.FromResult(current),
            events);
        var invocationId = SkillInvocationId.New();
        _ = await orchestrator.InvokeAsync(new SkillInvocationRequest
        {
            InvocationId = invocationId,
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            WorkspaceId = workspaceId,
            Selector = "fix-analyzer-warnings@1.0.0",
            InputJson = "{\"diagnostics\":[\"CA1822 at A.cs:10\"],\"scope\":[\"A.cs\"]}",
            Trust = RepositoryTrustLevel.TrustedRead,
            Phase = RunPhase.EvidenceCollection,
            HostBudget = new SkillBudget(),
        });
        current = current with { Trust = RepositoryTrustLevel.UntrustedInspection };

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ContinueAsync(
            invocationId,
            "{\"accepted\":true,\"planId\":\"plan-1\"}"));
        Assert.Equal(RepositoryTrustLevel.UntrustedInspection, compatibility.LastRequest?.Trust);
    }

    /// <summary>Verifies continuation cannot cross into a different workspace.</summary>
    [Fact]
    public async Task Workflow_ContinueAfterWorkspaceChange_IsRejected()
    {
        // Arrange
        var root = MaintainedRoot();
        var catalog = new SkillCatalog(
            [new SkillCatalogSource(SkillScope.Maintained, root, "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var state = new InMemorySkillStateStore();
        await using var events = new DomainEventStream();
        var workspaceId = WorkspaceId.New();
        SkillInvocationHostContext current = new()
        {
            WorkspaceId = workspaceId,
            Trust = RepositoryTrustLevel.TrustedRead,
            Phase = RunPhase.EvidenceCollection,
        };
        await using var orchestrator = new SkillWorkflowOrchestrator(
            catalog,
            new SkillPackageVerifier(new SkillTrustPolicySnapshot()),
            new CompatibleEvaluator(),
            new SkillContentLoader(new Threadsmith.Telemetry.SecretOutputSanitizer()),
            new BoundedJsonSchemaValidator(),
            new FixedProcedureRunner(ValidPlanJson()),
            state,
            (_, _) => Task.FromResult(current),
            events);
        var invocationId = SkillInvocationId.New();
        _ = await orchestrator.InvokeAsync(new SkillInvocationRequest
        {
            InvocationId = invocationId,
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            WorkspaceId = workspaceId,
            Selector = "fix-analyzer-warnings@1.0.0",
            InputJson = "{\"diagnostics\":[\"CA1822 at A.cs:10\"],\"scope\":[\"A.cs\"]}",
            Trust = RepositoryTrustLevel.TrustedRead,
            Phase = RunPhase.EvidenceCollection,
            HostBudget = new SkillBudget(),
        });
        current = current with { WorkspaceId = WorkspaceId.New() };

        // Act / Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ContinueAsync(
                invocationId,
                "{\"accepted\":true,\"planId\":\"plan-1\"}"));
        Assert.Contains("workspace", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies an unqualified invocation resolves the exact saved package pin.</summary>
    [Fact]
    public async Task Workflow_UnqualifiedSelector_UsesSavedPin()
    {
        // Arrange
        var root = MaintainedRoot();
        var source = new SkillCatalog(
            [new SkillCatalogSource(SkillScope.Maintained, root, "maintained", IsMaintained: true)]);
        var discovered = (await source.RefreshAsync()).Candidates.Single(item =>
            item.Metadata.SkillId.Value == "review-pr");
        var first = discovered with
        {
            Metadata = discovered.Metadata with { Version = "1.0.0" },
            Identity = discovered.Identity with { Version = "1.0.0" },
            Enabled = true,
            Verification = SkillVerificationState.Maintained,
        };
        var pinned = discovered with
        {
            Metadata = discovered.Metadata with { Version = "2.0.0" },
            Identity = discovered.Identity with
            {
                Version = "2.0.0",
                Digest = new SkillDigest("sha256", new string('b', 64)),
            },
            Enabled = true,
            Verification = SkillVerificationState.Maintained,
        };
        var catalog = new FixedCatalog([first, pinned]);
        var state = new InMemorySkillStateStore();
        await state.SavePinAsync(pinned.Metadata.SkillId, pinned.Identity);
        var compatibility = new CapturingIncompatibleEvaluator();
        await using var events = new DomainEventStream();
        await using var orchestrator = new SkillWorkflowOrchestrator(
            catalog,
            new PassThroughVerifier(),
            compatibility,
            new SkillContentLoader(new Threadsmith.Telemetry.SecretOutputSanitizer()),
            new BoundedJsonSchemaValidator(),
            new FixedProcedureRunner("{}"),
            state,
            (_, _) => Task.FromResult(new SkillInvocationHostContext
            {
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
            }),
            events);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.InvokeAsync(
            new SkillInvocationRequest
            {
                InvocationId = SkillInvocationId.New(),
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Selector = "review-pr",
                InputJson = "{}",
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
                HostBudget = new SkillBudget(),
            }));
        Assert.Equal(pinned.Identity, compatibility.Candidate?.Identity);
    }

    /// <summary>Verifies migration 5 and durable pins/checkpoints round-trip.</summary>
    [Fact]
    public async Task Persistence_MigrationFive_RoundTripsPinAndCheckpoint()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-skills-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            var version = await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
            var store = new SqliteSkillStateStore(connectionString);
            var identity = PackageIdentity();
            var invocationId = SkillInvocationId.New();
            var checkpoint = CreateCheckpoint(invocationId, identity);
            var verification = new SkillVerificationRecord
            {
                Package = identity,
                Scope = SkillScope.User,
                Source = "user:test",
                State = SkillVerificationState.DigestAllowlisted,
                Reason = "test exact allowlist",
                VerifiedAt = DateTimeOffset.UtcNow,
            };

            // Act
            await store.SaveVerificationAsync(verification);
            await store.SavePinAsync(identity.SkillId, identity);
            await store.SaveCheckpointAsync(checkpoint, expectedVersion: null);

            // Assert
            Assert.Equal(8, version);
            Assert.Equal(identity, await store.GetPinAsync(identity.SkillId));
            var restored = await store.GetCheckpointAsync(invocationId);
            Assert.NotNull(restored);
            Assert.Equal(checkpoint.InvocationId, restored.InvocationId);
            Assert.Equal(checkpoint.WorkflowId, restored.WorkflowId);
            Assert.Equal(checkpoint.Package, restored.Package);
            Assert.Equal(checkpoint.Status, restored.Status);
            Assert.Equal(checkpoint.InputJson, restored.InputJson);
            Assert.Equal(
                verification,
                await store.GetVerificationAsync(identity.Digest, SkillScope.User, "user:test"));
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM skill_package_provenance;";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync() ?? -1L));
            Assert.True(await store.HasActivePackageReferenceAsync(identity));
            Assert.Equal(
                0,
                await store.DeleteCheckpointsOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.NotNull(await store.GetCheckpointAsync(invocationId));
            var completed = checkpoint with
            {
                Status = SkillInvocationStatus.Completed,
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await store.SaveCheckpointAsync(
                completed,
                new SkillCheckpointVersion(checkpoint.Generation, checkpoint.Status));
            Assert.Equal(
                1,
                await store.DeleteCheckpointsOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.Null(await store.GetCheckpointAsync(invocationId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Verifies conditional checkpoint writes reject a stale generation and status.</summary>
    [Fact]
    public async Task Persistence_StaleCheckpointWrite_IsRejected()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-skills-cas-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            _ = await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
            var store = new SqliteSkillStateStore(connectionString);
            var accepted = CreateCheckpoint(
                SkillInvocationId.New(),
                PackageIdentity()) with
            {
                Status = SkillInvocationStatus.Accepted,
            };
            await store.SaveCheckpointAsync(accepted, expectedVersion: null);
            var running = accepted with
            {
                Status = SkillInvocationStatus.Running,
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await store.SaveCheckpointAsync(
                running,
                new SkillCheckpointVersion(accepted.Generation, accepted.Status));
            var stale = accepted with
            {
                Generation = checked(accepted.Generation + 1),
                RecordedAt = DateTimeOffset.UtcNow,
            };

            // Act / Assert
            await Assert.ThrowsAsync<SkillCheckpointConflictException>(() =>
                store.SaveCheckpointAsync(
                    stale,
                    new SkillCheckpointVersion(accepted.Generation, accepted.Status)));
            var restored = await store.GetCheckpointAsync(accepted.InvocationId);
            Assert.Equal(SkillInvocationStatus.Running, restored?.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string MaintainedRoot()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "Threadsmith.Skills",
            "MaintainedSkills"));
        Assert.True(Directory.Exists(root), root);
        return root;
    }

    private static void AddSignature(string manifestPath, string signerId, string signature)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException("Test skill manifest is invalid.");
        manifest["signature"] = new JsonObject
        {
            ["signerId"] = signerId,
            ["algorithm"] = "ecdsa-p256-sha256",
            ["signature"] = signature,
        };
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    private static string ValidPlanJson()
    {
        return """
            {"schemaVersion":1,"plan":{"schemaVersion":2,"revision":1,
             "summary":"Apply the existing static-member pattern.",
             "steps":[{"stepId":{"value":"11111111-1111-1111-1111-111111111111"},
             "title":"Fix analyzer diagnostic","description":"Inspect and fix A.cs",
             "fileIntents":[{"kind":"Modify","path":"A.cs"}],"expectedOutcome":"CA1822 is resolved",
             "validation":["dotnet test"]}],"risks":[],"outstandingQuestions":[]}}
            """;
    }

    private static SkillPackageIdentity PackageIdentity()
    {
        return new SkillPackageIdentity(
            new SkillId("test-skill"),
            "test.package",
            "1.0.0",
            new SkillDigest("sha256", new string('a', 64)),
            "test");
    }

    private static SkillWorkflowCheckpoint CreateCheckpoint(
        SkillInvocationId invocationId,
        SkillPackageIdentity identity)
    {
        return new SkillWorkflowCheckpoint
        {
            WorkflowId = SkillWorkflowId.New(),
            InvocationId = invocationId,
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Package = identity,
            InputJson = "{}",
            Trust = RepositoryTrustLevel.UntrustedInspection,
            Phase = RunPhase.EvidenceCollection,
            EffectiveBudget = new SkillBudget(),
            Status = SkillInvocationStatus.AwaitingHost,
            NextAction = "test host action",
            RecordedAt = DateTimeOffset.UtcNow,
        };
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class TemporaryPackage : IDisposable
    {
        private TemporaryPackage(string root, string packageRoot)
        {
            Root = root;
            PackageRoot = packageRoot;
        }

        void IDisposable.Dispose()
        {
            DeleteDirectory(Root);
        }

        internal string Root { get; }

        internal string PackageRoot { get; }

        internal static TemporaryPackage CopyMaintained(string package)
        {
            var root = Path.Combine(Path.GetTempPath(), $"threadsmith-skill-{Guid.NewGuid():N}");
            var packageRoot = Path.Combine(root, package);
            CopyDirectory(Path.Combine(MaintainedRoot(), package), packageRoot);
            return new TemporaryPackage(root, packageRoot);
        }

        internal SkillCatalog CreateCatalog(SkillScope scope)
        {
            return new SkillCatalog([new SkillCatalogSource(scope, Root, $"test:{scope}")]);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(
                source,
                "*",
                SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
            }
        }
    }

    private sealed class CompatibleEvaluator : ISkillCompatibilityEvaluator
    {
        private readonly ModelProfileId _profileId = ModelProfileId.New();

        public SkillCompatibilityResult Evaluate(
            SkillCatalogCandidate candidate,
            SkillInvocationRequest request)
        {
            return new SkillCompatibilityResult
            {
                IsCompatible = true,
                CompatibleModels = [_profileId],
            };
        }
    }

    private sealed class CurrentFactsEvaluator : ISkillCompatibilityEvaluator
    {
        private readonly ModelProfileId _profileId = ModelProfileId.New();

        internal SkillInvocationRequest? LastRequest { get; private set; }

        public SkillCompatibilityResult Evaluate(
            SkillCatalogCandidate candidate,
            SkillInvocationRequest request)
        {
            LastRequest = request;
            var compatible = request.Trust >= RepositoryTrustLevel.TrustedRead
                && request.Phase == RunPhase.EvidenceCollection;
            return new SkillCompatibilityResult
            {
                IsCompatible = compatible,
                DenialReasons = compatible ? [] : ["current-host-facts-incompatible"],
                CompatibleModels = compatible ? [_profileId] : [],
            };
        }
    }

    private sealed class CapturingIncompatibleEvaluator : ISkillCompatibilityEvaluator
    {
        internal SkillCatalogCandidate? Candidate { get; private set; }

        public SkillCompatibilityResult Evaluate(
            SkillCatalogCandidate candidate,
            SkillInvocationRequest request)
        {
            Candidate = candidate;
            return new SkillCompatibilityResult
            {
                IsCompatible = false,
                DenialReasons = ["test-stop-after-resolution"],
            };
        }
    }

    private sealed class FixedCatalog : ISkillCatalog
    {
        internal FixedCatalog(IReadOnlyList<SkillCatalogCandidate> candidates)
        {
            Snapshot = new SkillCatalogSnapshot
            {
                Generation = 1,
                Candidates = candidates,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }

        public SkillCatalogSnapshot Snapshot { get; }

        public Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshot);
        }

        public IReadOnlyList<SkillCatalogCandidate> Search(SkillCatalogQuery query)
        {
            return Snapshot.Candidates;
        }

        public SkillCatalogCandidate Resolve(string selector)
        {
            SkillCatalogCandidate[] matches =
            [
                .. Snapshot.Candidates.Where(item => string.Equals(
                    item.Metadata.SkillId.Value,
                    selector,
                    StringComparison.Ordinal)),
            ];
            return matches.Length == 1
                ? matches[0]
                : throw new InvalidOperationException("The test selector is ambiguous.");
        }
    }

    private sealed class PassThroughVerifier : ISkillPackageVerifier
    {
        public Task<SkillCatalogCandidate> VerifyAsync(
            SkillCatalogCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(candidate);
        }
    }

    private sealed class CapturingWorkflowOrchestrator : ISkillWorkflowOrchestrator
    {
        internal SkillInvocationRequest? Request { get; private set; }

        public Task<SkillInvocationResult> InvokeAsync(
            SkillInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            var package = PackageIdentity();
            var checkpoint = new SkillWorkflowCheckpoint
            {
                WorkflowId = SkillWorkflowId.New(),
                InvocationId = request.InvocationId,
                SessionId = request.SessionId,
                RunId = request.RunId,
                WorkspaceId = request.WorkspaceId,
                Package = package,
                InputJson = request.InputJson,
                Trust = request.Trust,
                Phase = request.Phase,
                EffectiveBudget = request.HostBudget,
                Status = SkillInvocationStatus.Completed,
                NextAction = "test complete",
                RecordedAt = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(new SkillInvocationResult
            {
                InvocationId = request.InvocationId,
                Package = package,
                Status = SkillInvocationStatus.Completed,
                Reason = "test complete",
                Checkpoint = checkpoint,
            });
        }

        public Task<SkillInvocationResult> ResumeAsync(
            SkillInvocationId invocationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<SkillInvocationResult>(new NotSupportedException());
        }

        public Task<SkillInvocationResult> ContinueAsync(
            SkillInvocationId invocationId,
            string hostResultJson,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<SkillInvocationResult>(new NotSupportedException());
        }

        public Task<bool> CancelAsync(
            SkillInvocationId invocationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class FixedProcedureRunner : ISkillProcedureRunner
    {
        private readonly string _output;

        internal FixedProcedureRunner(string output)
        {
            _output = output;
        }

        public Task<SkillProcedureResult> RunAsync(
            SkillInvocationPlan plan,
            SkillWorkflowStep step,
            int iteration,
            IReadOnlyList<SkillContextSegment> content,
            string inputJson,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SkillProcedureResult(_output, ModelTurns: 1, ToolCalls: 0));
        }
    }

    private sealed class InMemorySkillStateStore : ISkillStateStore
    {
        private readonly Dictionary<SkillInvocationId, SkillWorkflowCheckpoint> _checkpoints = [];
        private readonly Dictionary<SkillId, SkillPackageIdentity> _pins = [];
        private readonly Dictionary<string, SkillVerificationRecord> _verifications = [];

        public Task SaveVerificationAsync(
            SkillVerificationRecord verification,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _verifications[$"{verification.Package.Digest.Value}|{verification.Scope}|{verification.Source}"] = verification;
            return Task.CompletedTask;
        }

        public Task<SkillVerificationRecord?> GetVerificationAsync(
            SkillDigest digest,
            SkillScope scope,
            string source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _verifications.TryGetValue($"{digest.Value}|{scope}|{source}", out var record);
            return Task.FromResult(record);
        }

        public Task SaveCheckpointAsync(
            SkillWorkflowCheckpoint checkpoint,
            SkillCheckpointVersion? expectedVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = _checkpoints.TryGetValue(
                checkpoint.InvocationId,
                out var current);
            var matches = expectedVersion is null
                ? !exists
                : current is not null
                    && current.Generation == expectedVersion.Generation
                    && current.Status == expectedVersion.Status;
            if (!matches)
            {
                throw new SkillCheckpointConflictException();
            }

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
            _pins[skillId] = identity;
            return Task.CompletedTask;
        }

        public Task<SkillPackageIdentity?> GetPinAsync(
            SkillId skillId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pins.TryGetValue(skillId, out var identity);
            return Task.FromResult(identity);
        }

        public Task<bool> HasActivePackageReferenceAsync(
            SkillPackageIdentity package,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var referenced = _checkpoints.Values.Any(checkpoint =>
                checkpoint.Package == package
                && checkpoint.Status is not (SkillInvocationStatus.Completed
                    or SkillInvocationStatus.Failed
                    or SkillInvocationStatus.Cancelled));
            return Task.FromResult(referenced);
        }
    }
}
