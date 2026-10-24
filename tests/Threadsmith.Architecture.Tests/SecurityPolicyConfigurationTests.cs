namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Exercises source-aware authority resolution using disposable configuration files.</summary>
[Collection("Security policy environment")]
public sealed partial class SecurityPolicyConfigurationTests : IDisposable
{
    private readonly string _root;
    private readonly ConfigurationPaths _paths;

    /// <summary>Initializes a new instance of the <see cref="SecurityPolicyConfigurationTests"/> class.</summary>
    public SecurityPolicyConfigurationTests()
    {
        // macOS temporary paths may traverse /var -> /private/var. Use the physical
        // directory so positive write controls do not trip the production link guard.
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var physical = Path.GetPathRoot(temporary)!;
        foreach (var segment in temporary[physical.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(physical, segment));
            physical = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
        }

        _root = Path.Combine(physical, "threadsmith-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var repository = Path.Combine(_root, "repository");
        Directory.CreateDirectory(Path.Combine(repository, ".threadsmith"));
        _paths = ConfigurationBootstrap.ResolvePaths(repository) with
        {
            MachineConfiguration = Path.Combine(_root, "machine.json"),
            UserConfiguration = Path.Combine(_root, "user.json"),
            UserProviderCatalog = Path.Combine(_root, "providers.json"),
        };
    }

    /// <summary>Ordinary tool preferences cannot broaden the trusted tool allowlist.</summary>
    [Fact]
    public void QuestionToolPreferenceCannotBroadenPolicy()
    {
        File.WriteAllText(_paths.UserConfiguration, """{"tools":{"allow":["datetime"]}}""");
        File.WriteAllText(_paths.RepositoryConfiguration, """{"tools":{"defaultEnabledOverrides":["ask_user"]}}""");
        Assert.DoesNotContain("ask_user", Load().AllowedTools);
        Assert.Contains("datetime", Load().AllowedTools);
    }

    /// <summary>Distinguishes absent authority from explicit denial.</summary>
    [Fact]
    public void MissingAndExplicitEmptyToolListsHaveDifferentAuthority()
    {
        Assert.True(Load().AllowedTools.IsDefault);
        File.WriteAllText(_paths.RepositoryConfiguration, """{"tools":{"allow":[]}}""");
        Assert.False(Load().AllowedTools.IsDefault);
        Assert.Empty(Load().AllowedTools);
        File.WriteAllText(_paths.RepositoryConfiguration, "{}");
        File.WriteAllText(_paths.UserConfiguration, """{"tools":{"allow":[]}}""");
        var denied = Load();
        Assert.Empty(denied.AllowedTools);
        Assert.Contains(denied.Diagnostics, diagnostic => diagnostic.Contains("PolicyMigrationRequired", StringComparison.Ordinal)
            && diagnostic.Contains("ALL model tools", StringComparison.Ordinal)
            && diagnostic.Contains(_paths.UserConfiguration, StringComparison.Ordinal));
        File.WriteAllText(_paths.UserConfiguration, "{}");
        Assert.True(Load().AllowedTools.IsDefault);
    }

    /// <summary>Rejects indexed blending and widening across source boundaries.</summary>
    [Fact]
    public void TrustedArraysReplaceAndRepositoryArraysIntersectIndependently()
    {
        File.WriteAllText(_paths.MachineConfiguration, """{"tools":{"allowedExecutables":["git","dotnet","powershell"]}}""");
        File.WriteAllText(_paths.UserConfiguration, """{"tools":{"allowedExecutables":["git","dotnet"]}}""");
        File.WriteAllText(_paths.RepositoryConfiguration, """{"tools":{"allowedExecutables":["git","curl"]}}""");
        File.WriteAllText(_paths.SessionConfiguration, """{"tools":{"allowedExecutables":["dotnet"]}}""");
        Assert.Empty(Load().AllowedExecutables);
        File.WriteAllText(_paths.SessionConfiguration, "{}");
        var policy = Load();
        Assert.Equal(["git"], policy.AllowedExecutables);
        Assert.Contains(policy.Diagnostics, diagnostic => diagnostic.Contains("PolicyWideningRejected", StringComparison.Ordinal));
        Assert.Contains(policy.Sources, source => source.Kind == "User" && source.Key == "tools:allowedExecutables");
    }

    /// <summary>Malformed restrictions never become defaults.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("{}")]
    public void InvalidRestrictionsFailClosed(string value)
    {
        File.WriteAllText(_paths.RepositoryConfiguration, "{\"tools\":{\"allow\":" + value + "}}");
        Assert.Throws<InvalidDataException>(Load);
    }

    /// <summary>Duplicate keys cannot hide a restriction.</summary>
    [Fact]
    public void DuplicateCaseVariantKeysAreRejected()
    {
        File.WriteAllText(_paths.RepositoryConfiguration, """{"tools":{"allow":[],"Allow":["write_file"]}}""");
        Assert.Throws<InvalidDataException>(Load);
    }

    /// <summary>Mandatory denials and reviews are monotonic.</summary>
    [Fact]
    public void RepositoryCannotRemoveDenialsOrReviewRequirements()
    {
        File.WriteAllText(_paths.UserConfiguration, """{"tools":{"deny":["write_file"],"requireApproval":["git_status"],"runProcess":{"requireApproval":true}}}""");
        File.WriteAllText(_paths.RepositoryConfiguration, """{"tools":{"deny":[],"requireApproval":[],"runProcess":{"requireApproval":false}}}""");
        var policy = Load();
        Assert.Contains("write_file", policy.DeniedTools);
        Assert.Contains("git_status", policy.ReviewTools);
        Assert.True(policy.RequireShellApproval);
    }

    /// <summary>Directory intersection preserves narrower subtrees.</summary>
    [Fact]
    public void PathsIntersectByContainmentWithoutSiblingPrefixGrants()
    {
        File.WriteAllText(_paths.UserConfiguration, """{"tools":{"writeFile":{"allowedFolders":["exports"]}}}""");
        File.WriteAllText(_paths.RepositoryConfiguration, """{"tools":{"writeFile":{"allowedFolders":["exports/reports","exports-sibling"]}}}""");
        Assert.Equal([Path.Combine(_paths.RepositoryRoot, "exports", "reports")], Load().WriteFolders);
    }

    /// <summary>Static preauthorization is separate from consent eligibility.</summary>
    [Fact]
    public void StaticHostAbsencePreservesConsentEligibilityWhileEmptyRestrictionDeniesAll()
    {
        var baseline = Load();
        Assert.Empty(baseline.AllowedNetworkHosts);
        Assert.True(baseline.NetworkHostRestriction.IsDefault);
        File.WriteAllText(_paths.RepositoryConfiguration, """{"tools":{"allowedNetworkHosts":[]}}""");
        var restricted = Load();
        Assert.False(restricted.NetworkHostRestriction.IsDefault);
        Assert.Empty(restricted.NetworkHostRestriction);
    }

    /// <summary>Repository requests cannot supply consent; explicit trusted ceilings remain authoritative.</summary>
    [Fact]
    public void MutationPreferenceCannotRaiseTrustedCeilingOrThreshold()
    {
        File.WriteAllText(_paths.RepositoryConfiguration, """{"mutation":{"approvalPolicy":"alwaysTrustRepo","largeDiffThreshold":10000}}""");
        var policy = Load();
        Assert.Equal(MutationApprovalPolicy.ReviewAll, policy.MutationCeiling);
        Assert.False(policy.HasExplicitMutationCeiling);
        Assert.Equal(500, policy.LargeDiffThreshold);
        var binding = new SessionPolicyBinding();
        binding.Publish(policy);
        Assert.True(new MutationApprovalPolicyService(policyBinding: binding).RequiresApproval(new MutationRiskAssessment()));
        var preference = new ConfigurationBuilder().AddJsonFile(_paths.RepositoryConfiguration).Build();
        var approval = new MutationApprovalPolicyService(preference, policyBinding: binding);
        Assert.True(approval.RequiresApproval(new MutationRiskAssessment()));
        Assert.True(approval.RequiresApproval(new MutationRiskAssessment { HasLargeDiff = true }));
        File.WriteAllText(_paths.UserConfiguration, """{"mutation":{"approvalPolicy":"reviewAll"}}""");
        binding.Publish(Load());
        Assert.True(approval.RequiresApproval(new MutationRiskAssessment()));
        File.WriteAllText(_paths.UserConfiguration, """{"mutation":{"approvalPolicy":"reviewRisky","largeDiffThreshold":200}}""");
        Assert.Equal(MutationApprovalPolicy.ReviewRisky, Load().MutationCeiling);
        Assert.Equal(200, Load().LargeDiffThreshold);
        binding.Publish(Load());
        Assert.False(approval.RequiresApproval(new MutationRiskAssessment()));
        Assert.True(approval.RequiresApproval(new MutationRiskAssessment { HasLargeDiff = true }));
    }

    /// <summary>Captured immutable authority cannot survive a generation change.</summary>
    [Fact]
    public void SnapshotRemainsStableAndGenerationFenceRejectsOldRequests()
    {
        var captured = Load();
        File.WriteAllText(_paths.RepositoryConfiguration, """{"tools":{"allow":[]}}""");
        var next = Load() with { BindingId = captured.BindingId, Generation = captured.Generation + 1 };
        Assert.True(captured.AllowedTools.IsDefault);
        Assert.NotEqual(captured.Fingerprint, next.Fingerprint);
        var binding = new SessionPolicyBinding();
        binding.Publish(captured);
        binding.Validate(captured);
        binding.Publish(next);
        Assert.Throws<UnauthorizedAccessException>(() => binding.Validate(captured));
        binding.Invalidate();
        Assert.Throws<UnauthorizedAccessException>(() => binding.Validate(next));
    }

    /// <summary>Control paths do not accidentally include ordinary state or prefix siblings.</summary>
    [Theory]
    [InlineData(".threadsmith/config.json", true)]
    [InlineData(".threadsmith", true)]
    [InlineData(".threadsmith/config.json.backup", false)]
    [InlineData(".threadsmith/memory.json", false)]
    [InlineData(".inbox/report.md", false)]
    public void ControlGuardProtectsOnlyExactFilesAndTheirAncestors(string path, bool denied)
    {
        if (denied)
        {
            Assert.Throws<UnauthorizedAccessException>(() => AuthorityControlPathGuard.Validate(path, _paths.RepositoryRoot));
        }
        else
        {
            AuthorityControlPathGuard.Validate(path, _paths.RepositoryRoot);
        }
    }

    /// <summary>Boolean launch inputs preserve trusted precedence and repository narrowing.</summary>
    [Theory]
    [InlineData("true", false, true)]
    [InlineData("false", true, false)]
    [InlineData("TRUE", false, true)]
    [InlineData("invalid", true, null)]
    public void BooleanEnvironmentValuesAreTypedAndOverrideTrustedFiles(string value, bool lower, bool? expected)
    {
        foreach (var key in new[] { "parallel__enabled", "runProcess__requireApproval" })
        {
            var variable = "THREADSMITH_tools__" + key;
            var previous = Environment.GetEnvironmentVariable(variable);
            try
            {
                File.WriteAllText(
                    _paths.UserConfiguration,
                    "{\"tools\":{\"parallel\":{\"enabled\":" + lower.ToString().ToLowerInvariant()
                    + "},\"runProcess\":{\"requireApproval\":" + lower.ToString().ToLowerInvariant() + "}}}");
                Environment.SetEnvironmentVariable(variable, value);
                if (expected is null)
                {
                    var error = Assert.Throws<InvalidDataException>(Load);
                    Assert.Contains("PolicyValueInvalid", error.Message);
                    Assert.Contains("Environment", error.Message);
                }
                else
                {
                    var policy = Load();
                    Assert.Equal(expected.Value, key.StartsWith("parallel", StringComparison.Ordinal)
                        ? policy.ParallelToolsEnabled : policy.RequireShellApproval);
                    File.WriteAllText(
                        _paths.RepositoryConfiguration,
                        """{"tools":{"parallel":{"enabled":false},"runProcess":{"requireApproval":true}}}""");
                    File.WriteAllText(
                        _paths.SessionConfiguration,
                        """{"tools":{"parallel":{"enabled":true},"runProcess":{"requireApproval":false}}}""");
                    var narrowed = Load();
                    Assert.False(narrowed.ParallelToolsEnabled);
                    Assert.True(narrowed.RequireShellApproval);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
                File.WriteAllText(_paths.RepositoryConfiguration, "{}");
                File.WriteAllText(_paths.SessionConfiguration, "{}");
            }
        }
    }

    /// <summary>Case-distinct restrictions survive source merging and reach all path validators.</summary>
    [Fact]
    public void CrossSourceCaseDistinctProhibitionsReachReadAndWriteValidators()
    {
        File.WriteAllText(_paths.UserConfiguration, """{"prohibitedPaths":["Private/**"],"tools":{"writeFile":{"allowedFolders":["."]}}}""");
        File.WriteAllText(_paths.RepositoryConfiguration, """{"prohibitedPaths":["private/**"]}""");
        var policy = Load();
        Assert.Equal(["Private/**", "private/**"], policy.ProhibitedPaths);
        AssertPathAdmission(policy, "Private/file.txt", false);
        AssertPathAdmission(policy, "private/file.txt", false);
        AssertPathAdmission(policy, "public/file.txt", true);
    }

    /// <summary>Read and write admission follow the actual filesystem case behavior.</summary>
    [Fact]
    public void CaseAliasesFollowTheActualRepositoryFileSystem()
    {
        Directory.CreateDirectory(Path.Combine(_paths.RepositoryRoot, "private"));
        File.WriteAllText(Path.Combine(_paths.RepositoryRoot, "private", "file.txt"), "fixture");
        var aliasesSameFile = File.Exists(Path.Combine(_paths.RepositoryRoot, "PRIVATE", "file.txt"));
        File.WriteAllText(_paths.UserConfiguration, """{"prohibitedPaths":["private/**"],"tools":{"writeFile":{"allowedFolders":["."]}}}""");
        var policy = Load();
        AssertPathAdmission(policy, "private/file.txt", false);
        AssertPathAdmission(policy, "PRIVATE/file.txt", !aliasesSameFile);
    }

    /// <summary>Cached regular expressions retain independent filesystem case modes.</summary>
    [Fact]
    public void GlobCacheSeparatesFilesystemCaseModes()
    {
        foreach (var ignoreCase in new[] { false, true, false, true })
        {
            var context = new RepositoryPathCaseContext(_paths.RepositoryRoot, _ => !ignoreCase);
            Assert.Equal(ignoreCase, RepositoryPathPolicy.IsProhibited("PRIVATE/file.txt", ["private/**"], _paths.RepositoryRoot, context));
        }
    }

    /// <summary>Repository authority definitions remain inert and visibly diagnosed.</summary>
    [Fact]
    public void RepositoryTrustedOnlySettingsAreIgnoredWithSourceDiagnostics()
    {
        File.WriteAllText(_paths.RepositoryConfiguration, """{"repository":{"configurationBytes":1},"mcp":{"profiles":[]}}""");
        var policy = Load();
        Assert.Contains(policy.Diagnostics, diagnostic => diagnostic.Contains("PolicySourceNotAllowed: repository:configurationBytes", StringComparison.Ordinal));
        Assert.Contains(policy.Diagnostics, diagnostic => diagnostic.Contains("PolicySourceNotAllowed: mcp:profiles", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Existing BOM-prefixed JSON files retain their policy semantics.</summary>
    [Fact]
    public void BomFilesAndTrustedArrayReplacementRemainCompatible()
    {
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(_paths.MachineConfiguration, """{"tools":{"allowedExecutables":["git","dotnet"]}}""", encoding);
        File.WriteAllText(_paths.UserConfiguration, """{"tools":{"allowedExecutables":["git"]}}""", encoding);
        File.WriteAllText(_paths.RepositoryConfiguration, "{}", encoding);
        Assert.Equal(["git"], Load().AllowedExecutables);
        var trusted = SecurityPolicyConfigurationLoader.BuildTrusted(_paths);
        using var lifetime = trusted as IDisposable;
        Assert.Equal("git", trusted["tools:allowedExecutables:0"]);
        Assert.Null(trusted["tools:allowedExecutables:1"]);
    }

    /// <summary>Repository and session restrictions both bound live scheduler and fetch options.</summary>
    [Fact]
    public void SessionResourceRestrictionsCannotBeOverwrittenByRepositoryOrTrustedValues()
    {
        File.WriteAllText(_paths.RepositoryConfiguration, """{"tools":{"parallel":{"enabled":false,"maximumConcurrency":2}},"webFetch":{"maximumRedirects":0,"maximumDecodedBytes":1024}}""");
        File.WriteAllText(_paths.SessionConfiguration, """{"tools":{"parallel":{"enabled":true,"maximumConcurrency":1}},"webFetch":{"maximumDecodedBytes":2048}}""");
        var policy = Load();
        Assert.False(policy.ParallelToolsEnabled);
        Assert.Equal(1, policy.ResourceLimits["tools:parallel:maximumConcurrency"]);
        var options = WebFetchOptions.FromPolicy(policy);
        Assert.Equal(0, options.MaximumRedirects);
        Assert.Equal(1024, options.MaximumDecodedBytes);
    }

    /// <summary>Compound adapter bounds are checked before a policy candidate becomes executable.</summary>
    [Fact]
    public void InvalidFetchCandidateCannotBeResolved()
    {
        File.WriteAllText(_paths.UserConfiguration, """{"webFetch":{"maximumExtractedCharacters":2147483647}}""");
        Assert.Throws<InvalidOperationException>(Load);
    }

    /// <summary>Specialized startup identity changes only when its authority definitions change.</summary>
    [Fact]
    public void SpecializedIdentityIgnoresOrdinaryPreferences()
    {
        var initial = Load();
        File.WriteAllText(_paths.UserConfiguration, """{"theme":"new-theme"}""");
        Assert.Equal(initial.StartupAuthorityFingerprint, Load().StartupAuthorityFingerprint);
        File.WriteAllText(_paths.UserConfiguration, """{"mcp":{"profiles":[]}}""");
        Assert.NotEqual(initial.StartupAuthorityFingerprint, Load().StartupAuthorityFingerprint);
    }

    /// <summary>Ordinary formatting cannot change the specialized hook request identity.</summary>
    [Fact]
    public void HookIdentityUsesSemanticConfiguration()
    {
        File.WriteAllText(_paths.RepositoryConfiguration, """{"hooks":{"repositoryHandlers":[{"id":"fixture","command":"fixture"}]}}""");
        var initial = Load();
        File.WriteAllText(_paths.RepositoryConfiguration, """{ "theme": "new", "hooks": { "repositoryHandlers": [ { "Command": "fixture", "Id": "fixture" } ] } }""");
        Assert.Equal(initial.StartupAuthorityFingerprint, Load().StartupAuthorityFingerprint);
    }

    /// <summary>Indexed launch inputs replace trusted arrays rather than retaining lower-provider suffixes.</summary>
    [Fact]
    public void EnvironmentArrayReplacesUserAndMachineArrays()
    {
        const string prefix = "THREADSMITH_tools__allowedExecutables";
        var saved = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => ((string)entry.Key).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var key in saved.Keys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }

            File.WriteAllText(_paths.MachineConfiguration, """{"tools":{"allowedExecutables":["git","dotnet","curl"]}}""");
            File.WriteAllText(_paths.UserConfiguration, """{"tools":{"allowedExecutables":["git","dotnet"]}}""");
            Environment.SetEnvironmentVariable(prefix + "__0", "git");
            var policy = Load();
            Assert.Equal(["git"], policy.AllowedExecutables);
            Assert.Contains(policy.Sources, source => source.Kind == "Environment" && source.Key == "tools:allowedExecutables");
            var trusted = SecurityPolicyConfigurationLoader.BuildTrusted(_paths);
            Assert.Equal("git", trusted["tools:allowedExecutables:0"]);
            Assert.Null(trusted["tools:allowedExecutables:1"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "__0", null);
            foreach (var (key, value) in saved)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private void AssertPathAdmission(EffectiveSessionPolicy policy, string path, bool allowed)
    {
        var context = new ToolInvocationContext
        {
            RepositoryPath = _paths.RepositoryRoot,
            RequestedBy = "test",
            TrustLevel = RepositoryTrustLevel.TrustedMutation,
            ProhibitedPaths = policy.ProhibitedPaths,
            SecurityPolicy = policy,
        };
        var read = new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer());
        var configuration = new ConfigurationBuilder().Build();
        var write = new WriteFileTool(
            new WriteFileConfiguration(configuration, configuration, _paths.RepositoryRoot),
            new UnusedConversationStore(),
            TestPromptLoader.Instance);
        var engine = new DefaultPolicyEngine();
        var readDecision = engine.Evaluate(read, new ReadFileInput { Path = path }, context);
        var writeDecision = engine.Evaluate(write, new WriteFileInput(path, "fixture"), context);
        Assert.Equal(allowed, readDecision.IsAllowed);
        Assert.Equal(allowed, writeDecision.IsAllowed);
        if (!allowed)
        {
            Assert.Contains("prohibited", readDecision.Reason);
            Assert.Contains("prohibited", writeDecision.Reason);
        }

        var binding = new SessionPolicyBinding();
        binding.Publish(policy);
        var mutations = new MutationApprovalPolicyService(policyBinding: binding);
        if (allowed)
        {
            mutations.ValidatePaths([path], _paths.RepositoryRoot);
        }
        else
        {
            Assert.Throws<MutationPolicyException>(() => mutations.ValidatePaths([path], _paths.RepositoryRoot));
        }
    }

    private sealed class UnusedConversationStore : IConversationStore
    {
        public Task<ConversationMessage> ArchiveMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ConversationStateSnapshot> GetSnapshotAsync(SessionId sessionId, bool includeBodies = true, ConversationHistoryWindow? historyWindow = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> RemoveMessageBodiesOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ReplaceSummaryAsync(SessionId sessionId, IReadOnlyList<ConversationMemoryItem> items, ConversationSummarySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SetModeAsync(SessionId sessionId, ConversationContextMode mode, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task UpdateMemoryAsync(ConversationMemoryItem item, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>A broad export grant cannot create or replace the user-owned secret store.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserSecretStoreIsProtectedUnderBroadWriteGrant(bool exists)
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(
            _paths.UserConfiguration,
            System.Text.Json.JsonSerializer.Serialize(new { tools = new { writeFile = new { allowedFolders = new[] { _root } } } }),
            token);
        var secretPath = _paths.UserSecretsConfiguration;
        const string original = "synthetic-secret-store";
        if (exists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(secretPath)!);
            await File.WriteAllTextAsync(secretPath, original, token);
        }

        var policy = Load();
        Assert.Contains(secretPath, policy.ControlPaths);
        var configuration = new ConfigurationBuilder().Build();
        var write = new WriteFileTool(
            new WriteFileConfiguration(configuration, configuration, _paths.RepositoryRoot),
            new UnusedConversationStore(),
            TestPromptLoader.Instance);
        var invocation = new ToolInvocationContext
        {
            RepositoryPath = _paths.RepositoryRoot,
            RequestedBy = "test",
            TrustLevel = RepositoryTrustLevel.TrustedMutation,
            SecurityPolicy = policy,
        };
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), invocation);
        var input = new WriteFileInput(secretPath, "replacement") { Overwrite = true };
        Assert.False(new DefaultPolicyEngine().Evaluate(write, input, invocation).IsAllowed);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => write.ExecuteAsync(input, context, token));
        Assert.Equal(exists, File.Exists(secretPath));
        if (exists)
        {
            Assert.Equal(original, await File.ReadAllTextAsync(secretPath, token));
        }

        Assert.Throws<UnauthorizedAccessException>(() => AuthorityControlPathGuard.Validate(
            Path.GetDirectoryName(secretPath)!, _paths.RepositoryRoot, policy.ControlPaths));
        var artifact = Path.Combine(_root, "export.txt");
        await write.ExecuteAsync(new WriteFileInput(artifact, "allowed"), context, token);
        Assert.Equal("allowed", await File.ReadAllTextAsync(artifact, token));
    }

    private EffectiveSessionPolicy Load() => SecurityPolicyConfigurationLoader.Load(_paths, Guid.NewGuid(), 1);
}

/// <summary>Serializes process-wide launch environment changes against other architecture tests.</summary>
[CollectionDefinition("Security policy environment", DisableParallelization = true)]
public sealed class SecurityPolicyEnvironmentCollection;
