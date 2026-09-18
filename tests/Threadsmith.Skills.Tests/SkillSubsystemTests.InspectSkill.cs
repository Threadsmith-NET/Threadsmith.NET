namespace Threadsmith.Skills.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Discovery reuses restored state without rereading bodies and returns usable inspection selectors.</summary>
    [Theory]
    [InlineData(null, 2)]
    [InlineData("REVIEW", 2)]
    [InlineData("redwood", 1)]
    [InlineData("absent-name", 0)]
    public async Task InspectSkillTool_DiscoveryUsesOnlyCatalogMetadata(string? query, int expectedCount)
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var claudeRoot = Path.Combine(package.Root, "claude", "portable-review");
        Directory.CreateDirectory(claudeRoot);
        var claudeFile = Path.Combine(claudeRoot, "SKILL.md");
        await File.WriteAllTextAsync(claudeFile, "---\nname: portable-review\ndescription: Audits redwood branches\n---\nPrivate instruction body.\n");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, Path.GetDirectoryName(claudeRoot)!, "user:claude", false)]));
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        await application.HandleAsync(new VerifySkillCommand("review"));
        await application.HandleAsync(new SetSkillEnabledCommand("claude:User:portable-review", true));
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());
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
        InspectSkillOutput discovery;
        await using (var lockedSchema = new FileStream(Path.Combine(package.PackageRoot, "schemas", "input.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        await using (var lockedBody = new FileStream(claudeFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(), RunId = RunId.New(), ToolId = "inspect_skill",
                ArgumentsJson = query is null ? "{}" : JsonSerializer.Serialize(new { query }),
                Context = new ToolInvocationContext
                {
                    RepositoryPath = package.Root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model",
                },
            });
            Assert.True(result.Succeeded, result.ResultJson);
            Assert.False(result.IsTruncated);
            Assert.DoesNotContain("inputSchema", result.ResultJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Private instruction body", result.ResultJson, StringComparison.Ordinal);
            discovery = JsonSerializer.Deserialize<InspectSkillOutput>(result.ResultJson!)!;
        }

        Assert.Equal(expectedCount, discovery.Skills.Count);
        Assert.All(discovery.Skills, entry =>
        {
            Assert.Equal("Enabled", entry.Availability);
            Assert.Null(entry.InputSchema);
        });
        Assert.All(catalog.Snapshot.Candidates, candidate => Assert.True(candidate.Enabled));
        Assert.Single(observed.OfType<ToolInvocationStarted>());
        Assert.True(Assert.Single(observed.OfType<ToolInvocationCompleted>()).Succeeded);
        Assert.Empty(observed.OfType<SkillWorkflowCheckpointWritten>());

        // Every discovery selector must also work for detailed inspection.
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext());
        foreach (var entry in discovery.Skills)
        {
            var inspection = await tool.ExecuteAsync(new InspectSkillInput { Selector = entry.Selector }, context);
            var selected = Assert.Single(inspection.Value.Skills);
            Assert.Equal(entry.Name, selected.Name);
            Assert.Equal("Enabled", selected.Availability);
            Assert.NotNull(selected.InputSchema);
        }
    }

    /// <summary>Listing all skills uses an explicit bounded subset and tells the model to narrow broad queries.</summary>
    [Fact]
    public async Task InspectSkillTool_ListsBeyondDefaultCatalogLimit()
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var claudeRoot = Path.Combine(package.Root, "claude");
        for (var index = 0; index < 105; index++)
        {
            var directory = Path.Combine(claudeRoot, $"skill-{index}");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), $"---\nname: skill-{index}\ndescription: Audit changes\n---\nInstructions.\n");
        }

        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, claudeRoot, "user:claude", false)]));
        await catalog.RefreshAsync();
        foreach (var candidate in catalog.Snapshot.Candidates)
        {
            catalog.UpdateCandidate(candidate with { Enabled = true, Verification = SkillVerificationState.DigestAllowlisted });
        }

        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());
        var result = await tool.ExecuteAsync(new InspectSkillInput(), new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext()));
        Assert.Equal(32, result.Value.Skills.Count);
        Assert.Contains("narrower query", result.Value.Guidance, StringComparison.Ordinal);
        Assert.All(result.Value.Skills, entry => Assert.Null(entry.InputSchema));
    }

    /// <summary>Unavailable skills in the first catalog window do not hide later enabled skills.</summary>
    [Fact]
    public async Task InspectSkillTool_FiltersAvailabilityBeforeOutputLimit()
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var claudeRoot = Path.Combine(package.Root, "claude");
        for (var index = 0; index < 150; index++)
        {
            var directory = Path.Combine(claudeRoot, $"aaa-disabled-{index:D3}");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), $"---\nname: aaa-disabled-{index:D3}\ndescription: Audit changes\n---\nInstructions.\n");
        }

        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, claudeRoot, "user:claude", false)]));
        await catalog.RefreshAsync();
        foreach (var candidate in catalog.Snapshot.Candidates)
        {
            var availableReview = candidate.Metadata.SkillId.Value == "review";
            catalog.UpdateCandidate(candidate with
            {
                Enabled = availableReview,
                Verification = availableReview
                    ? SkillVerificationState.Maintained
                    : SkillVerificationState.Unverified,
            });
        }

        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());

        var result = await tool.ExecuteAsync(new InspectSkillInput(), new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext()));

        var entry = Assert.Single(result.Value.Skills);
        Assert.Equal("review", entry.Name);
        Assert.Null(entry.InputSchema);
    }

    /// <summary>Both discovery variations hide unavailable native and Claude skills without changing their state.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("review")]
    public async Task InspectSkillTool_DiscoveryRequiresEnabledAndVerified(string? query)
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var claudeRoot = Path.Combine(package.Root, "claude", "portable-review");
        Directory.CreateDirectory(claudeRoot);
        await File.WriteAllTextAsync(Path.Combine(claudeRoot, "SKILL.md"), "---\nname: portable-review\ndescription: Review changes\n---\nInstructions.\n");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.User),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, Path.GetDirectoryName(claudeRoot)!, "user:claude", false)]));
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext());
        foreach (var verification in Enum.GetValues<SkillVerificationState>())
        {
            foreach (var enabled in new[] { false, true })
            {
                foreach (var candidate in catalog.Snapshot.Candidates)
                {
                    catalog.UpdateCandidate(candidate with { Enabled = enabled, Verification = verification });
                }

                var result = await tool.ExecuteAsync(new InspectSkillInput { Query = query }, context);
                var expected = enabled && verification is (SkillVerificationState.Maintained
                    or SkillVerificationState.SignedTrusted or SkillVerificationState.DigestAllowlisted) ? 2 : 0;
                Assert.Equal(expected, result.Value.Skills.Count);
                Assert.All(result.Value.Skills, entry => Assert.Equal("Enabled", entry.Availability));
                Assert.All(catalog.Snapshot.Candidates, candidate =>
                {
                    Assert.Equal(enabled, candidate.Enabled);
                    Assert.Equal(verification, candidate.Verification);
                });
            }
        }
    }

    /// <summary>Model discovery restores the same startup availability as /skills before filtering.</summary>
    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, true, false)]
    [InlineData(null, false, true)]
    [InlineData(null, false, false)]
    [InlineData("review", true, true)]
    [InlineData("review", true, false)]
    [InlineData("review", false, true)]
    [InlineData("review", false, false)]
    public async Task InspectSkillTool_FreshCatalogMatchesSkillsList(string? query, bool nativeEnabled, bool claudeEnabled)
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var claudeRoot = Path.Combine(package.Root, "claude");
        foreach (var name in new[] { "portable-review", "untrusted-review" })
        {
            var directory = Path.Combine(claudeRoot, name);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: Review changes\n---\nPrivate instructions.\n");
        }

        var policyPath = Path.Combine(package.Root, "policy.json");
        var policy = new FileSkillTrustPolicyProvider(policyPath, new SkillTrustPolicySnapshot());
        var catalog = CreateCatalog();
        await catalog.RefreshAsync();
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        if (!nativeEnabled)
        {
            await application.HandleAsync(new SetSkillEnabledCommand("review", false));
        }

        await application.HandleAsync(new SetSkillEnabledCommand("claude:User:portable-review", claudeEnabled));

        // Reconstruct startup state. Do not open /skills or inspect any selector first.
        policy = new FileSkillTrustPolicyProvider(policyPath, new SkillTrustPolicySnapshot());
        catalog = CreateCatalog();
        await catalog.RefreshAsync();
        application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            new ToolRegistry([tool]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance);

        // A subsequent catalog refresh must preserve the same behavior as a fresh process.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.All(catalog.Snapshot.Candidates, candidate => Assert.Equal(SkillVerificationState.Unverified, candidate.Verification));
            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(), RunId = RunId.New(), ToolId = "inspect_skill",
                ArgumentsJson = query is null ? "{}" : JsonSerializer.Serialize(new { query }),
                Context = new ToolInvocationContext
                {
                    RepositoryPath = package.Root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model",
                },
            });
            Assert.True(result.Succeeded, result.ResultJson);
            var discovery = JsonSerializer.Deserialize<InspectSkillOutput>(result.ResultJson!)!;
            Assert.Equal((nativeEnabled ? 1 : 0) + (claudeEnabled ? 1 : 0), discovery.Skills.Count);
            Assert.Equal(nativeEnabled, discovery.Skills.Any(entry => entry.Scope == "Maintained"));
            Assert.Equal(claudeEnabled, discovery.Skills.Any(entry => entry.Name == "portable-review"));
            Assert.DoesNotContain("Private instructions", result.ResultJson, StringComparison.Ordinal);
            var listed = await application.HandleAsync(new ListSkillsCommand(new SkillCatalogQuery { Text = query }));
            Assert.Equal(
                listed.Where(candidate => candidate.Enabled).Select(candidate => candidate.Metadata.DisplayName),
                discovery.Skills.Select(entry => entry.Name));
            var untrusted = Assert.Single(listed, candidate => candidate.Metadata.DisplayName == "untrusted-review");
            Assert.Equal(SkillVerificationState.Unverified, untrusted.Verification);
            Assert.False(untrusted.Enabled);
            await catalog.RefreshAsync();
        }

        CompatibleSkillCatalog CreateCatalog() => new(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, claudeRoot, "user:claude", false)]));
    }

    /// <summary>Both skill formats expose an actionable contract through ordinary tool events.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectSkillTool_ReturnsVerifiedContractThroughToolPipeline(bool claude)
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var claudeRoot = Path.Combine(package.Root, "claude", "portable-review");
        Directory.CreateDirectory(claudeRoot);
        await File.WriteAllTextAsync(
            Path.Combine(claudeRoot, "SKILL.md"),
            "---\nname: portable-review\ndescription: Review repository\n---\nPrivate instruction body.\n");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog(
                [new ClaudeSkillRoot(SkillScope.User, Path.GetDirectoryName(claudeRoot)!, "user:claude", false)]));
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var selector = claude ? "claude:User:portable-review" : "review";
        if (claude)
        {
            await application.HandleAsync(new SetSkillEnabledCommand(selector, true));
        }

        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var pipeline = new ToolInvocationPipeline(
            new ToolRegistry([new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator())]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance);

        var result = await pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = SessionId.New(), RunId = RunId.New(), ToolId = "inspect_skill",
            ArgumentsJson = JsonSerializer.Serialize(new { selector }),
            Context = new ToolInvocationContext
            {
                RepositoryPath = package.Root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model",
            },
        });

        Assert.True(result.Succeeded, result.ResultJson);
        Assert.False(result.IsTruncated);
        using var output = JsonDocument.Parse(result.ResultJson!);
        var root = output.RootElement;
        var skill = Assert.Single(root.GetProperty("skills").EnumerateArray());
        var exact = skill.GetProperty("selector").GetString()!;
        Assert.True((await application.HandleAsync(new VerifySkillCommand(exact))).Enabled);
        Assert.Contains("Ask the user for any missing, required information", root.GetProperty("guidance").GetString(), StringComparison.Ordinal);
        Assert.Contains("native JSON value", root.GetProperty("guidance").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Private instruction body", result.ResultJson, StringComparison.Ordinal);
        var schema = skill.GetProperty("inputSchema");
        if (claude)
        {
            Assert.Empty(schema.EnumerateObject());
            Assert.Contains("JSON string", root.GetProperty("guidance").GetString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("must be an object", root.GetProperty("guidance").GetString(), StringComparison.Ordinal);
            Assert.Contains("not a quoted or JSON-encoded object string", root.GetProperty("guidance").GetString(), StringComparison.Ordinal);
            using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package.PackageRoot, "schemas", "input.json")));
            Assert.True(JsonElement.DeepEquals(expected.RootElement, schema));
            var validator = new BoundedJsonSchemaValidator();
            var compiled = validator.Compile(schema.GetRawText());
            const string task = "Review https://example.test/pull-requests/729";
            var prepared = JsonSerializer.Serialize(new { mode = "specialInstructions", instructions = task });
            using var validated = JsonDocument.Parse(validator.Validate(compiled, prepared));
            Assert.Equal(task, validated.RootElement.GetProperty("instructions").GetString());
            Assert.Throws<InvalidDataException>(() => validator.Validate(compiled, "{\"pullRequestUrl\":\"https://example.test/pr/729\"}"));
        }

        Assert.Single(observed.OfType<ToolInvocationStarted>());
        Assert.True(Assert.Single(observed.OfType<ToolInvocationCompleted>()).Succeeded);
        Assert.Empty(observed.OfType<SkillWorkflowCheckpointWritten>());
    }

    /// <summary>Inspection rechecks current enablement and package integrity.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectSkillTool_RejectsDisabledOrTamperedPackage(bool tampered)
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, Path.Combine(package.Root, "claude"), "user:claude", false)]));
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());
        var input = new InspectSkillInput { Selector = "review" };
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext());
        await tool.ExecuteAsync(input, context);
        if (tampered)
        {
            await File.AppendAllTextAsync(Path.Combine(package.PackageRoot, "schemas", "input.json"), " ");
        }
        else
        {
            await application.HandleAsync(new SetSkillEnabledCommand("review", false));
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(input, context));
        var discovery = await tool.ExecuteAsync(new InspectSkillInput(), context);
        Assert.Empty(discovery.Value.Skills);
    }

    /// <summary>Selection and search are bounded, exclusive, and cancellation reaches both paths.</summary>
    [Fact]
    public async Task InspectSkillTool_ValidatesSelectorAndPropagatesCancellation()
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, Path.Combine(package.Root, "claude"), "user:claude", false)]));
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());
        foreach (var invalid in new[]
        {
            "{\"selector\":\"\"}", "{\"query\":\" \"}", "{\"selector\":\"review\",\"query\":\"review\"}",
            "{\"selector\":\"review\",\"input\":{}}", JsonSerializer.Serialize(new { selector = new string('x', 1025) }),
            JsonSerializer.Serialize(new { query = new string('x', 1025) }),
        })
        {
            Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(invalid));
        }

        var input = Assert.IsType<InspectSkillInput>(tool.DeserializeInput("{\"selector\":\"review\"}"));
        Assert.Equal("inspect review", tool.GetActivityDetail(input));
        Assert.Equal("search review", tool.GetActivityDetail(new InspectSkillInput { Query = "review" }));
        Assert.Equal("list enabled verified skills", tool.GetActivityDetail(new InspectSkillInput()));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(input, context, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(new InspectSkillInput(), context, cancellation.Token));
    }

    /// <summary>The shared schema reader also checks changes after initial verification.</summary>
    [Fact]
    public async Task SkillSchemaAssets_RejectsChangesAfterVerification()
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var catalog = package.CreateCatalog(SkillScope.Maintained);
        await catalog.RefreshAsync();
        var verified = await new SkillPackageVerifier(new SkillTrustPolicySnapshot()).VerifyAsync(catalog.Resolve("review"));
        await File.AppendAllTextAsync(Path.Combine(package.PackageRoot, "schemas", "input.json"), " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => SkillSchemaAssets.ReadAsync(verified, "schemas/input.json", CancellationToken.None));
    }
}
