// Threadsmith.NET repository-configuration binding test (plan-01 task 7 / acceptance criterion).
//
// Proves .threadsmith/config.example loads via Microsoft.Extensions.Configuration without
// error and that every strategy §21.2 key is present. The config file is JSON-with-comments
// (loaded with the Json configuration provider, which supports comments).
namespace Threadsmith.Architecture.Tests;

using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Execution;
using Xunit;

/// <summary>
/// Asserts the Threadsmith.NET repository configuration example loads and contains every
/// strategy §21.2 key.
/// </summary>
public static class RepoConfigTests
{
    /// <summary>The strategy §21.2 repository-configuration keys that must be present.</summary>
    private static readonly string[] _requiredKeys =
    [
        "solution",
        "tui",
        "build",
        "nuget",
        "test",
        "editableRoots",
        "prohibitedPaths",
        "model",
        "context",
        "tools",
        "webSearch",
        "webFetch",
        "mutation",
        "execution",
        "agents",
        "skills",
        "repository",
        "extensions",
        "prompt append files",
        "formatting",
        "validation",
        "persistence",
        "mcp",
        "diagnostics",
    ];

    /// <summary>The .threadsmith/config.example path relative to the repo root.</summary>
    private static string ConfigExamplePath
    {
        get
        {
            var bin = AppContext.BaseDirectory;
            var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
            return Path.Combine(repoRoot, ".threadsmith", "config.example");
        }
    }

    /// <summary>The config example file must exist.</summary>
    [Fact]
    public static void ConfigExampleFileExists()
    {
        Assert.True(File.Exists(ConfigExamplePath), $"Expected config example at {ConfigExamplePath}.");
    }

    /// <summary>
    /// The config example must load via Microsoft.Extensions.Configuration without error.
    /// The file is JSON-with-comments (the Json provider supports comments).
    /// </summary>
    [Fact]
    public static void ConfigExampleLoadsWithoutError()
    {
        var config = LoadConfigExample();
        Assert.NotNull(config);
        // The config must not be empty (proves the JSON-with-comments parsed).
        var children = config.GetChildren().ToList();
        Assert.True(children.Count > 0, "Config example loaded but produced no top-level keys (JSON parse failed?).");
    }

    /// <summary>The documented solution preference uses the nested configuration-provider path.</summary>
    [Fact]
    public static void ConfigExampleContainsNestedSolutionPath()
    {
        var config = LoadConfigExample();
        Assert.Equal("src/Threadsmith.sln", config["solution:path"]);
    }

    /// <summary>Every strategy §21.2 key must be present in the loaded config.</summary>
    [Theory]
    [MemberData(nameof(RequiredKeyData))]
    public static void ConfigExampleContainsRequiredKey(string key)
    {
        var config = LoadConfigExample();
        var section = config.GetSection(key);
        Assert.True(
            section.Exists() || config.GetChildren().Any(c => c.Key == key),
            $"Config example is missing required §21.2 key '{key}'.");
    }

    /// <summary>The prompt-append-files key must resolve to a non-empty list (§21.2).</summary>
    [Fact]
    public static void PromptAppendFilesKeyResolvesToList()
    {
        var config = LoadConfigExample();
        var appendFiles = config.GetSection("prompt append files").Get<string[]>();
        Assert.NotNull(appendFiles);
        Assert.NotEmpty(appendFiles);
        Assert.All(appendFiles, f => Assert.False(string.IsNullOrWhiteSpace(f)));
    }

    /// <summary>Plan-26 composer-adjacent status is enabled in the reference configuration.</summary>
    [Fact]
    public static void TuiFooterSettingBindsToConfiguredValue()
    {
        var config = LoadConfigExample();
        Assert.True(config.GetValue("tui:footer:enabled", false));
    }

    /// <summary>Plan-63 semantic Markdown rendering is enabled in the reference configuration.</summary>
    [Fact]
    public static void TuiMarkdownRenderingSettingBindsToConfiguredValue()
    {
        var config = LoadConfigExample();
        Assert.True(config.GetValue("tui:renderMarkdown", false));
    }

    /// <summary>Plan-49 operation-duration display is enabled in the reference configuration.</summary>
    [Fact]
    public static void TuiOperationDurationSettingBindsToConfiguredValue()
    {
        var config = LoadConfigExample();
        Assert.True(config.GetValue("tui:showOperationDurations", false));
    }

    /// <summary>The optional active-turn compaction profile is documented as a trusted null fallback.</summary>
    [Fact]
    public static void ActiveTurnCompactionProfileDefaultsToActiveModelFallback()
    {
        var config = LoadConfigExample();

        Assert.Null(config["context:activeTurnCompaction:profileId"]);
        Assert.True(config.GetSection("context:activeTurnCompaction").Exists());
    }

    /// <summary>Plan-08 policy and plan-27 availability lists bind to explicit arrays.</summary>
    [Fact]
    public static void ToolPolicyKeysResolveToLists()
    {
        var config = LoadConfigExample();
        var enabled = config.GetSection("tools:enabled").Get<string[]>() ?? [];
        Assert.NotEmpty(enabled);
        Assert.Contains("invoke_skill", enabled);
        Assert.Contains("nuget_health", enabled);
        Assert.Contains("dotnet_build", enabled);
        Assert.Contains("test_run_targeted", enabled);
        Assert.NotNull(config.GetSection("tools:disabled").Get<string[]>());
        Assert.NotEmpty(config.GetSection("tools:allow").Get<string[]>() ?? []);
        Assert.NotEmpty(config.GetSection("tools:deny").Get<string[]>() ?? []);
        var allowedExecutables = config.GetSection("tools:allowedExecutables").Get<string[]>() ?? [];
        Assert.Contains("powershell", allowedExecutables);
        Assert.Contains("bash", allowedExecutables);
        Assert.Null(config["tools:runProcess:shellExecutable"]);
        Assert.DoesNotContain(
            "run_process",
            config.GetSection("tools:requireApproval").Get<string[]>() ?? []);
        Assert.NotNull(config.GetSection("tools:allowedNetworkHosts").Get<string[]>());
    }

    /// <summary>Plan-42 advisory sources are explicit HTTPS values in the reference configuration.</summary>
    [Fact]
    public static void NuGetAdvisorySourcesAreHttps()
    {
        var config = LoadConfigExample();
        IConfigurationSection[] sources = [.. config.GetSection("nuget:advisorySources").GetChildren()];
        Assert.NotEmpty(sources);
        Assert.All(sources, source =>
        {
            var name = Assert.IsType<string>(source["name"]);
            var uri = Assert.IsType<string>(source["uri"]);
            Assert.NotEmpty(name);
            Assert.StartsWith("https://", uri, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>The per-tool operational limit keys bind to their documented values (§21.2).</summary>
    [Fact]
    public static void ToolOperationalLimitsBindToConfiguredValues()
    {
        var config = LoadConfigExample();
        Assert.Equal(200, config.GetValue("tools:listFiles:defaultEntries", 0));
        Assert.Equal(2000, config.GetValue("tools:listFiles:maxEntries", 0));
        Assert.Equal(1_048_576L, config.GetValue<long>("tools:readFile:maxBytes", 0));
        Assert.Equal(2000, config.GetValue("tools:readFile:defaultLines", 0));
        Assert.Equal(2000, config.GetValue("tools:readFile:maxLines", 0));
        Assert.Equal(51_200, config.GetValue("tools:readFile:maxContentBytes", 0));
        Assert.Equal(1_048_576L, config.GetValue<long>("tools:search:maxBytes", 0));
        Assert.Equal(100, config.GetValue("tools:search:defaultMatches", 0));
        Assert.Equal(500, config.GetValue("tools:search:maxMatches", 0));
        Assert.Equal(1000, config.GetValue("tools:findSymbol:maxResults", 0));
        Assert.Equal(1000, config.GetValue("tools:findReferences:maxResults", 0));
        Assert.Equal(1000, config.GetValue("tools:findImplementations:maxResults", 0));
        Assert.False(config.GetValue("tools:codeExplore:inspectCodeExploreOutput", true));
        Assert.Equal(30, config.GetValue("tools:runProcess:defaultTimeoutSeconds", 0));
        Assert.Equal(60, config.GetValue("tools:runProcess:maxTimeoutSeconds", 0));
        Assert.Equal(5000, config.GetValue("tools:config:csharp_script:timeout_ms", 0));
        Assert.Equal(65536, config.GetValue("tools:config:csharp_script:max_output_bytes", 0));
        Assert.Equal(
            "System.Linq,System.Collections,System.Collections.Generic",
            config["tools:config:csharp_script:allowed_assemblies"]);
    }

    /// <summary>Plan-30 mutation approval defaults bind to safe review-all behavior.</summary>
    [Fact]
    public static void MutationApprovalPolicyBindsToSafeDefaults()
    {
        var config = LoadConfigExample();
        Assert.Equal("reviewAll", config["mutation:approvalPolicy"]);
        Assert.Equal(500, config.GetValue("mutation:largeDiffThreshold", 0));
    }

    /// <summary>Plan-75 plan approval defaults bind to safe manual-review behavior.</summary>
    [Fact]
    public static void PlanApprovalPolicyBindsToSafeDefaults()
    {
        var config = LoadConfigExample();
        Assert.Equal("reviewAll", config["planning:approvalPolicy"]);
        Assert.Null(config["planning:approvalRepositoryIdentity"]);
    }

    /// <summary>The execution-limit and repository-safety keys bind to their documented values (§21.2).</summary>
    [Fact]
    public static void ExecutionAndRepositoryLimitsBindToConfiguredValues()
    {
        var config = LoadConfigExample();
        Assert.Equal(ExecutionLimits.DefaultMaxModelRounds, config.GetValue("execution:maxModelRounds", 0));
        Assert.Equal(ExecutionLimits.DefaultMaxPlanningToolRounds, config.GetValue("execution:maxPlanningToolRounds", 0));
        Assert.Equal(3, config.GetValue("execution:maxCorrectiveTurns", 0));
        Assert.Equal(32, config.GetValue("agents:queueCapacity", 0));
        Assert.Equal(4, config.GetValue("agents:maxActiveGlobal", 0));
        Assert.Equal(3, config.GetValue("agents:maxActivePerParent", 0));
        Assert.Equal(2, config.GetValue("agents:maxActiveImplementers", 0));
        Assert.Equal(30, config.GetValue("agents:shutdownTimeoutSeconds", 0));
        Assert.True(config.GetValue("skills:repositoryCatalogEnabled", false));
        Assert.False(config.GetValue("hooks:repositoryHandlers:0:enabled", true));
        Assert.Equal("example-advisory-check", config["hooks:repositoryHandlers:0:id"]);
        Assert.Equal(8_388_608, config.GetValue("execution:maxStructuredOutputCharacters", 0));
        Assert.Equal(4096, config.GetValue("execution:toolResultPreviewCharacters", 0));
        var validationStages = config.GetSection("validation:stages").Get<string[]>() ?? [];
        Assert.Equal(["semantic", "compile", "diagnostics", "tests"], validationStages);
        Assert.Equal(1_048_576L, config.GetValue<long>("repository:configurationBytes", 0));
    }

    /// <summary>The production composition root binds the documented corrective-turn limit key.</summary>
    [Fact]
    public static void HostFoundationBindsCorrectiveTurnLimit()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ConfigExamplePath) ?? ".", ".."));
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "Threadsmith.App", "HostFoundation.cs"));
        Assert.Contains("execution:maxCorrectiveTurns", source, StringComparison.Ordinal);
        Assert.Contains("MaxCorrectiveTurns = maximumCorrectiveTurns", source, StringComparison.Ordinal);
        Assert.DoesNotContain("execution:maxPlanProposalRepairAttempts", source, StringComparison.Ordinal);
        Assert.DoesNotContain("execution:maxMutationProposalRepairAttempts", source, StringComparison.Ordinal);
    }

    /// <summary>M8 persistence, retention, MCP, and diagnostic keys bind to their documented values (plan-18/19/20).</summary>
    [Fact]
    public static void Milestone8ConfigurationKeysBindToConfiguredValues()
    {
        var config = LoadConfigExample();
        // Persistence + artifacts (plan-18).
        Assert.Equal(".threadsmith/threadsmith.db", config["persistence:path"]);
        Assert.Equal(".threadsmith/artifacts", config["persistence:artifactDirectory"]);
        // Retention (plan-18, §19.6).
        Assert.True(config.GetValue("persistence:retention:enabled", false));
        Assert.Equal(30, config.GetValue("persistence:retention:sessionAgeDays", 0));
        Assert.False(config.GetValue("persistence:retention:metadataOnly", true));
        // Redaction audit (plan-20, §19.6).
        Assert.True(config.GetValue("persistence:redactionAudit:enabled", false));
        Assert.True(config.GetValue("persistence:redactionAudit:repairArtifacts", false));
        // MCP (plan-19, §20).
        Assert.Equal(10, config.GetValue("mcp:defaultDrainKillTimeoutSeconds", 0));
        Assert.NotEmpty(config.GetSection("mcp:profiles").GetChildren());
        var firstProfile = config.GetSection("mcp:profiles:0");
        Assert.Equal("example-stdio", firstProfile["id"]);
        Assert.Equal("TrustedRead", firstProfile["trust"]);
        Assert.Equal("stdio", firstProfile["transport"]);
        Assert.NotEmpty(firstProfile.GetSection("secretScope").Get<string[]>() ?? []);
        Assert.NotEmpty(firstProfile.GetSection("allowedCapabilities").Get<string[]>() ?? []);
        Assert.Equal(30, firstProfile.GetValue("startupTimeoutSeconds", 0));
        Assert.Equal(60, firstProfile.GetValue("requestTimeoutSeconds", 0));
        Assert.Equal(10, firstProfile.GetValue("drainKillTimeoutSeconds", 0));
        // Real HTTP transport + static-token/OAuth-stub configuration (plan-22).
        var httpProfile = config.GetSection("mcp:profiles:1");
        Assert.Equal("example-http", httpProfile["id"]);
        Assert.Equal("http", httpProfile["transport"]);
        Assert.Equal("secrets:MCP_HTTP_TOKEN", httpProfile["headers:Authorization"]);
        Assert.Equal("example", httpProfile["headers:X-Tenant-Id"]);
        Assert.False(httpProfile.GetValue("oauth:enabled", true));
        Assert.Equal("threadsmith", httpProfile["oauth:clientId"]);
        Assert.Equal("secrets:MCP_OAUTH_CLIENT_SECRET", httpProfile["oauth:clientSecret"]);
        Assert.Null(httpProfile["oauth:clientMetadataDocumentUri"]);
        Assert.Equal(8400, httpProfile.GetValue("oauth:redirectPort", 0));
        Assert.Null(httpProfile["oauth:discoveryUrl"]);
        // Diagnostic bundles (plan-20, §23.4).
        Assert.True(config.GetValue("diagnostics:enabled", false));
        Assert.Equal(".threadsmith/diagnostics", config["diagnostics:directory"]);
        Assert.True(config.GetValue("diagnostics:includeLogs", false));
        Assert.True(config.GetValue("diagnostics:includeEvents", false));
        Assert.True(config.GetValue("diagnostics:includeArtifacts", false));
        Assert.True(config.GetValue("diagnostics:includeConfiguration", false));
        Assert.True(config.GetValue("diagnostics:includeVersionInfo", false));
        Assert.Equal(67_108_864L, config.GetValue<long>("diagnostics:maxBytes", 0));
        Assert.Equal(1000, config.GetValue("diagnostics:recentEventsPerSession", 0));
    }

    /// <summary>The user config is scaffolded from the shipped catalog on first launch and never overwritten.</summary>
    [Fact]
    public static void UserConfigurationIsScaffoldedFromCatalogWhenMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"threadsmith-scaffold-{Guid.NewGuid():N}");
        var userConfig = Path.Combine(tempDir, ".threadsmith", "config.json");
        try
        {
            // First launch: the file does not exist, so it is scaffolded from the shipped catalog.
            Threadsmith.App.Program.ScaffoldUserConfigurationIfMissing(userConfig);
            Assert.True(File.Exists(userConfig), $"Expected scaffolded user config at {userConfig}");
            var scaffolded = new ConfigurationBuilder()
                .AddJsonFile(userConfig, optional: false)
                .Build();
            // The scaffold carries the documented defaults, so the catalog keys bind.
            Assert.Equal(ExecutionLimits.DefaultMaxModelRounds, scaffolded.GetValue("execution:maxModelRounds", 0));
            Assert.Equal(ExecutionLimits.DefaultMaxPlanningToolRounds, scaffolded.GetValue("execution:maxPlanningToolRounds", 0));
            Assert.Equal(200, scaffolded.GetValue("tools:listFiles:defaultEntries", 0));
            Assert.Equal(60, scaffolded.GetValue("tools:runProcess:maxTimeoutSeconds", 0));

            // The scaffolded file is strict JSON: System.Text.Json rejects comments, so a
            // successful parse proves no // or /* */ comments survived the copy. (Glob values
            // such as "**/*.env" legitimately contain "/*" inside string literals.)
            var written = File.ReadAllText(userConfig);
            using var parsed = JsonDocument.Parse(written);
            Assert.NotEmpty(parsed.RootElement.EnumerateObject());

            // A user edit must be preserved: re-scaffolding must not overwrite an existing file.
            File.WriteAllText(userConfig, "{ \"execution\": { \"maxModelRounds\": 3 } }\n");
            Threadsmith.App.Program.ScaffoldUserConfigurationIfMissing(userConfig);
            var preserved = new ConfigurationBuilder()
                .AddJsonFile(userConfig, optional: false)
                .Build();
            Assert.Equal(3, preserved.GetValue("execution:maxModelRounds", 0));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Loads .threadsmith/config.example via a JSON stream so the non-.json file extension
    /// does not affect provider selection. The Json parser supports comments.
    /// </summary>
    private static IConfigurationRoot LoadConfigExample()
    {
        var json = File.ReadAllText(ConfigExamplePath);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream(bytes);
        return new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
    }

    /// <summary>Theory data: every required §21.2 key.</summary>
    // ReSharper disable once MemberCanBePrivate.Global
    public static System.Collections.Generic.IEnumerable<TheoryDataRow<string>> RequiredKeyData =>
        _requiredKeys.Select(static key => new TheoryDataRow<string>(key));
}
