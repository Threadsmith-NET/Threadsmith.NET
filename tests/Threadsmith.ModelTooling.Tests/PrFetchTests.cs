namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Tools.PullRequests;
using Xunit;

/// <summary>Exercises provider scope, shared acquisition, lifecycle and ordinary policy integration.</summary>
public sealed class PrFetchTests
{
    /// <summary>URL routing uses enabled accounts for the matching compiled host only.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UrlOnlySelectsMatchingEnabledAccount(bool bitbucket)
    {
        using var handler = new PrHandler(bitbucket);
        using var http = new HttpClient(handler);
        var options = Options(bitbucket);
        options = options with
        {
            Providers = new Dictionary<string, PullRequestProviderOptions>
            {
                ["account"] = options.Providers["account"],
                ["disabled"] = options.Providers["account"] with { Enabled = false },
                ["other-host"] = Options(!bitbucket).Providers["account"],
            },
        };
        var secrets = new TestSecrets();
        var tool = new PrFetchTool([new GitHubPullRequestProvider(http, secrets, options), new BitbucketCloudPullRequestProvider(http, secrets, options)], options, TestPromptLoader.Instance);
        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(JsonSerializer.Serialize(new { url = Input(bitbucket).Url })));
        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(JsonSerializer.Serialize(new { url = Input(bitbucket).Url, kind = "patch" })));
        var input = (PrFetchInput)tool.DeserializeInput(JsonSerializer.Serialize(new { url = Input(bitbucket).Url, kind = "inventory" }));
        Assert.Equal(PrFetchKind.Inventory, input.Kind);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var result = await tool.ExecuteAsync(input, Context(scope));
        Assert.Equal("account", result.Value.Provider);
        Assert.Equal(1, handler.Requests);
        using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
        Assert.True(schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal).SetEquals(["kind", "url"]));
        var kindSchema = schema.RootElement.GetProperty("properties").GetProperty("kind");
        Assert.Equal(["inventory", "diff"], kindSchema.GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("Use kind:\"inventory\"", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("Use kind:\"diff\" only when", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("instead of fetching all diff pages before delegation", tool.Definition.Description, StringComparison.Ordinal);
        Assert.False(tool.Definition.AllowDuplicateInvocations);
        using var outputSchema = JsonDocument.Parse(tool.Definition.OutputSchema.JsonSchema);
        var outputProperties = outputSchema.RootElement.GetProperty("properties");
        Assert.Equal(["inventory", "diff"], outputProperties.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("boolean", outputProperties.GetProperty("isContinuation").GetProperty("type").GetString());
    }

    /// <summary>Ambiguity is actionable without probing credentials or selecting an arbitrary account.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UrlRoutingReportsAmbiguousOrMissingAccounts(bool disabled)
    {
        using var handler = new PrHandler(false);
        using var http = new HttpClient(handler);
        var secrets = new TestSecrets();
        var options = Options(false);
        options = options with
        {
            Providers = new Dictionary<string, PullRequestProviderOptions>
            {
                ["account"] = options.Providers["account"] with { Enabled = !disabled },
                ["second"] = options.Providers["account"] with { Enabled = !disabled },
            },
        };
        var tool = CreateTool(http, secrets, options, false);
        var error = Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput("{\"url\":\"https://github.com/org/repo/pull/1\",\"kind\":\"inventory\"}"));
        Assert.Contains(disabled ? "No enabled" : "Ask the user", error.Message, StringComparison.Ordinal);
        if (!disabled)
        {
            Assert.Contains("account, second", error.Message, StringComparison.Ordinal);
            Assert.IsType<PrFetchInput>(tool.DeserializeInput("{\"url\":\"https://github.com/org/repo/pull/1\",\"kind\":\"inventory\",\"provider\":\"second\"}"));
        }

        Assert.Equal(0, secrets.Calls);
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>Configured patterns route organizations to accounts using canonical PR URLs.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredPatternsSelectAccountAndPreserveExplicitOverride(bool bitbucket)
    {
        using var handler = new PrHandler(bitbucket);
        using var http = new HttpClient(handler);
        var options = Options(bitbucket);
        var host = bitbucket ? "bitbucket.org" : "github.com";
        var route = bitbucket ? "pull-requests" : "pull";
        options = options with
        {
            Providers = new Dictionary<string, PullRequestProviderOptions>
            {
                ["account"] = options.Providers["account"] with { UrlPatterns = [$"https://{host}/unused/*", $"https://{host}/org/*/{route}/*"] },
                ["other"] = options.Providers["account"] with { UrlPatterns = [$"https://{host}/other/*"] },
            },
        };
        var tool = CreateTool(http, new TestSecrets(), options, bitbucket);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var input = Input(bitbucket) with { Provider = null, Url = Input(bitbucket).Url + "/files#diff" };
        var result = await tool.ExecuteAsync(input, Context(scope));
        Assert.Equal("account", result.Value.Provider);
        Assert.Equal(Input(bitbucket).Url, result.Value.Metadata.Url);
        Assert.DoesNotContain("/unused/", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(JsonSerializer.Serialize(new { url = Input(bitbucket).Url.Replace("/org/", "/unknown/", StringComparison.Ordinal), kind = "inventory" })));
        var explicitResult = await tool.ExecuteAsync(Input(bitbucket) with { Provider = "other" }, Context(scope));
        Assert.Equal("other", explicitResult.Value.Provider);
    }

    /// <summary>Routing configuration cannot redefine an adapter's credential destination.</summary>
    [Theory]
    [InlineData("https://evil.example/*")]
    [InlineData("http://github.com/*")]
    [InlineData("https://*.github.com/*")]
    [InlineData("https://github.com/*?token=abc")]
    [InlineData("https://user@github.com/*")]
    [InlineData("")]
    public void InvalidRoutingPatternsAreRejected(string pattern)
    {
        using var handler = new PrHandler(false);
        using var http = new HttpClient(handler);
        var options = Options(false);
        options = options with
        {
            Providers = new Dictionary<string, PullRequestProviderOptions>
            {
                ["account"] = options.Providers["account"] with { UrlPatterns = [pattern] },
            },
        };
        Assert.Throws<InvalidOperationException>(() => CreateTool(http, new TestSecrets(), options, false));
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>The generic outbound consent gate also governs the configured PR tool.</summary>
    [Fact]
    public void RepositoryEnableOverrideCannotManufactureOutboundConsent()
    {
        using var handler = new PrHandler(false);
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(false), false);
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-pr-consent-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:defaultEnabledOverrides:0"] = "pr_fetch",
        }).Build();
        var state = new ToolStateManager([tool.Definition], configuration, Path.Combine(root, ".threadsmith", "config.json"), Path.Combine(root, "user", "consent.json"));
        Assert.False(state.IsEnabled("pr_fetch"));
        Assert.True(Assert.Single(state.GetAllStates()).ConsentRequired);
        Assert.Empty(new ToolRegistry([tool], state).Definitions);
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>File-inventory mode completes consistently without retrieving diff content.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileInventoryModeCompletesWithoutDiff_AndHasSeparateCacheIdentity(bool bitbucket)
    {
        using var handler = new PrHandler(bitbucket) { MultipleFilePages = true };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(bitbucket), bitbucket);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var context = Context(scope);
        var input = Input(bitbucket) with { Provider = null, Kind = PrFetchKind.Inventory };

        var pages = await ReadAllAsync(tool, input, context);

        Assert.Equal(bitbucket ? 2 : 21, pages.Sum(page => page.Page.Files.Count));
        Assert.DoesNotContain(pages, page => page.Page.Kind == "diff");
        Assert.All(pages, page => Assert.Empty(page.Page.Diff));
        Assert.True(pages[^1].AcquisitionComplete);
        Assert.True(pages[^1].DeliveryComplete);
        Assert.Null(pages[^1].Cursor);
        Assert.Contains("Diff content was not requested", Assert.Single(pages[^1].Page.Limitations), StringComparison.Ordinal);
        Assert.Equal(0, handler.DiffRequests);
        Assert.Equal(2, handler.MetadataRequests);

        var full = await tool.ExecuteAsync(input with { Kind = PrFetchKind.Diff }, context);
        Assert.False(full.Value.CacheHit);
        Assert.NotEqual(pages[0].SnapshotId, full.Value.SnapshotId);
        Assert.Equal(3, handler.MetadataRequests);
    }

    /// <summary>Large single-file diffs are delivered losslessly in bounded chunks.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeDiffAndInventoryPaginationRemainBounded(bool bitbucket)
    {
        var diff = "diff --git a/src/changed.cs b/src/changed.cs\n" + string.Concat(Enumerable.Repeat("+Unicode 😀 changed line\n", 1000));
        using var handler = new PrHandler(bitbucket) { DiffText = diff, MultipleFilePages = true };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(bitbucket), bitbucket);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var context = Context(scope);
        var first = (await tool.ExecuteAsync(Input(bitbucket), context)).Value;
        Assert.Equal(1, handler.Requests);
        Assert.False(first.AcquisitionComplete);
        Assert.False(first.DeliveryComplete);
        var pages = await ReadAllAsync(tool, Input(bitbucket), context);
        Assert.Equal(diff, string.Concat(pages.Select(page => page.Page.Diff)));
        Assert.All(pages, page => Assert.True(page.Page.Diff.Length <= 257));
        Assert.Equal(bitbucket ? 2 : 21, pages.Sum(page => page.Page.Files.Count));
        var cached = (await tool.ExecuteAsync(Input(bitbucket), context)).Value;
        Assert.True(cached.AcquisitionComplete);
        Assert.False(cached.DeliveryComplete);
        Assert.True(pages[^1].DeliveryComplete);
    }

    /// <summary>Visible activity detail distinguishes PR fetch pagination without exposing cursor values.</summary>
    [Fact]
    public async Task ActivityDetailShowsKindAndPaginationState()
    {
        using var handler = new PrHandler(false) { MultipleFilePages = true };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(false), false);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var context = Context(scope);
        var input = Input(false) with { Kind = PrFetchKind.Inventory };

        var startedDetail = tool.GetActivityDetail(input);
        Assert.Contains("account · inventory · first page", startedDetail, StringComparison.Ordinal);

        var first = await tool.ExecuteAsync(input, context);
        Assert.Contains("account · inventory · metadata · first page", first.TransientActivityDetail, StringComparison.Ordinal);
        Assert.Contains("cursor returned", first.TransientActivityDetail, StringComparison.Ordinal);
        Assert.Contains("acquisition pending", first.TransientActivityDetail, StringComparison.Ordinal);

        var nextInput = input with { Cursor = first.Value.Cursor };
        Assert.Contains("account · inventory · continuation", tool.GetActivityDetail(nextInput), StringComparison.Ordinal);

        var next = await tool.ExecuteAsync(nextInput, context);
        Assert.Contains("continuation", next.TransientActivityDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Value.Cursor ?? string.Empty, next.TransientActivityDetail, StringComparison.Ordinal);
    }

    /// <summary>Redirects must be validated before credentials can reach a different authority.</summary>
    [Fact]
    public async Task CrossOriginRedirectFailsBeforeSecondRequest()
    {
        using var handler = new PrHandler(false) { MetadataRedirect = "https://other.example/collect" };
        using var http = new HttpClient(handler);
        var secrets = new TestSecrets();
        var tool = CreateTool(http, secrets, Options(false), false);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(Input(false), Context(scope)));
        Assert.Contains("untrusted API destination", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, secrets.Calls);
    }

    /// <summary>Bitbucket's same-repository PR diff redirect works with API-token basic authentication.</summary>
    [Fact]
    public async Task BitbucketBasicAuthenticationAndDiffRedirectUseSharedTransport()
    {
        var options = Options(true);
        options = options with
        {
            Providers = new Dictionary<string, PullRequestProviderOptions>
            {
                ["account"] = options.Providers["account"] with
                {
                    Authentication = new() { Mode = "basic", Username = "user@example.org", SecretReference = "secrets:pr:test" },
                },
            },
        };
        using var handler = new PrHandler(true) { RedirectDiff = true, ExpectedCredential = Convert.ToBase64String(Encoding.UTF8.GetBytes("user@example.org:test-credential")) };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), options, true);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var pages = await ReadAllAsync(tool, Input(true), Context(scope));
        Assert.True(pages[^1].DeliveryComplete);
        Assert.Contains("+new", string.Concat(pages.Select(page => page.Page.Diff)), StringComparison.Ordinal);
    }

    /// <summary>Diff limits fail explicitly instead of declaring truncated evidence complete.</summary>
    [Fact]
    public async Task OversizedDiffFailsWithoutCompletePage()
    {
        using var handler = new PrHandler(false) { DiffText = new string('x', 3000) };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(false) with { MaximumResponseBytes = 1500 }, false);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAllAsync(tool, Input(false), Context(scope)));
        Assert.Contains("maximumResponseBytes", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Cached evidence never bypasses a later tool or network denial.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedCallsStillPassCurrentPipelinePolicy(bool denyTool)
    {
        using var handler = new PrHandler(false);
        using var http = new HttpClient(handler);
        var secrets = new TestSecrets();
        var tool = CreateTool(http, secrets, Options(false), false);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var context = Context(scope);
        await ReadAllAsync(tool, Input(false), context);
        var requests = handler.Requests;
        var resolutions = secrets.Calls;
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var request = new ToolInvocationRequest
        {
            SessionId = context.SessionId,
            RunId = context.RunId,
            ToolId = "pr_fetch",
            ArgumentsJson = "{\"url\":\"https://github.com/org/repo/pull/1\",\"kind\":\"diff\"}",
            Context = context.Invocation,
        };
        Assert.True((await pipeline.InvokeAsync(request)).Succeeded);
        var denied = await pipeline.InvokeAsync(request with
        {
            Context = denyTool ? context.Invocation with { DeniedToolIds = ["pr_fetch"] }
                : context.Invocation with { AllowedNetworkHosts = [] },
        });
        Assert.False(denied.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, denied.ErrorClassification);
        Assert.Equal(requests, handler.Requests);
        Assert.Equal(resolutions, secrets.Calls);
    }

    /// <summary>Provider scope excludes base-only files and is shared only across matching authorization scopes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderScopePagingAndCache_AreSharedByParentAndChild(bool bitbucket)
    {
        using var handler = new PrHandler(bitbucket);
        using var http = new HttpClient(handler);
        var secrets = new TestSecrets();
        var options = Options(bitbucket);
        var tool = CreateTool(http, secrets, options, bitbucket);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var parent = Context(scope);
        var input = Input(bitbucket) with { Provider = null };

        var pages = await ReadAllAsync(tool, input, parent);
        var first = pages[0];
        Assert.Equal("account", first.Provider);
        Assert.True(pages[^1].AcquisitionComplete);
        Assert.Null(pages[^1].Cursor);
        Assert.All(pages, page => Assert.Equal(first.SnapshotId, page.SnapshotId));
        var file = Assert.Single(pages.SelectMany(page => page.Page.Files));
        Assert.Equal("src/changed.cs", file.Path);
        Assert.DoesNotContain(pages.SelectMany(page => page.Page.Files), item => item.Path.Contains("extra.tf", StringComparison.Ordinal));
        Assert.Contains("+new", string.Concat(pages.Select(page => page.Page.Diff)), StringComparison.Ordinal);
        var requests = handler.Requests;
        var resolutions = secrets.Calls;

        var sameScopeChild = parent with
        {
            RunId = RunId.New(),
            ToolInvocationId = ToolInvocationId.New(),
        };
        var repeated = await tool.ExecuteAsync(Input(bitbucket), sameScopeChild);
        Assert.True(repeated.Value.CacheHit);
        Assert.Equal(first.SnapshotId, repeated.Value.SnapshotId);
        Assert.Equal(requests, handler.Requests);
        Assert.Equal(resolutions, secrets.Calls);

        var restrictedChild = parent with
        {
            RunId = RunId.New(),
            ToolInvocationId = ToolInvocationId.New(),
            Invocation = parent.Invocation with { ApprovedRoots = ["src"], ProhibitedPaths = ["local-only.secret"] },
        };
        var restrictedPages = await ReadAllAsync(tool, input, restrictedChild);
        Assert.NotEqual(first.SnapshotId, restrictedPages[0].SnapshotId);
        Assert.Contains("+new", string.Concat(restrictedPages.Select(page => page.Page.Diff)), StringComparison.Ordinal);
        requests = handler.Requests;
        resolutions = secrets.Calls;

        var disjointChild = restrictedChild with
        {
            Invocation = restrictedChild.Invocation with { ApprovedRoots = ["docs"], ProhibitedPaths = [] },
        };
        var scopedInventory = await ReadAllAsync(
            tool,
            input with { Kind = PrFetchKind.Inventory },
            disjointChild);
        Assert.Empty(scopedInventory.SelectMany(page => page.Page.Files));
        Assert.Contains(
            scopedInventory.SelectMany(page => page.Page.Limitations),
            limitation => limitation.Contains("outside the caller's approved repository path scope", StringComparison.Ordinal));
        Assert.Equal(SecretProviderTrust.UserOwned, secrets.MinimumTrust);
        Assert.DoesNotContain("test-credential", tool.Definition.Description, StringComparison.Ordinal);

        var refreshed = await tool.ExecuteAsync(input with { Refresh = true }, parent);
        Assert.NotEqual(first.SnapshotId, refreshed.Value.SnapshotId);
        await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync(input with { Cursor = first.Cursor }, restrictedChild));
        await ReadAllAsync(tool, input, parent);
        Assert.True(handler.Requests > requests);
    }

    /// <summary>Raw diff is denied only when the provider inventory identifies a prohibited changed file.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiffWithScopedPolicy_DeniesOnlyAffectedPullRequestPaths(bool bitbucket)
    {
        using var handler = new PrHandler(bitbucket)
        {
            ChangedPath = ".env",
            DiffText = "diff --git a/.env b/.env\n--- a/.env\n+++ b/.env\n@@ -1 +1 @@\n-old\n+new\n",
        };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(bitbucket), bitbucket);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var baseContext = Context(scope);
        var context = baseContext with
        {
            Invocation = baseContext.Invocation with { ProhibitedPaths = [".env"] },
        };

        var first = await tool.ExecuteAsync(Input(bitbucket), context);
        var inventory = await tool.ExecuteAsync(Input(bitbucket) with { Cursor = first.Value.Cursor }, context);
        Assert.Empty(inventory.Value.Page.Files);
        Assert.Contains(
            inventory.Value.Page.Limitations,
            limitation => limitation.Contains("outside the caller's approved repository path scope", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tool.ExecuteAsync(Input(bitbucket) with { Cursor = inventory.Value.Cursor }, context));
        Assert.Contains("outside the caller's approved repository path scope", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A cancelled waiter cannot cancel another reader's acquisition.</summary>
    [Fact]
    public async Task ConcurrentReadersShareFetch_OneWaiterCancellationDoesNotCancelOwner()
    {
        using var handler = new PrHandler(false) { HoldMetadata = true };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(false), false);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var context = Context(scope);
        using var waiter = new CancellationTokenSource();
        var first = tool.ExecuteAsync(Input(false), context, waiter.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = tool.ExecuteAsync(Input(false), context with { RunId = RunId.New() });

        await waiter.CancelAsync();
#pragma warning disable VSTHRD003 // Observe the caller task started above after cancelling its wait.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
#pragma warning restore VSTHRD003
        handler.Release.TrySetResult();
        var result = await second;
        Assert.True(result.Value.CacheHit);
        await ReadAllAsync(tool, Input(false), context);
        Assert.Equal(2, handler.MetadataRequests);
    }

    /// <summary>Refresh generations are fenced and concurrent replacement calls coalesce.</summary>
    [Fact]
    public async Task RefreshReplacesInflightGeneration_AndConcurrentRefreshJoins()
    {
        using var handler = new PrHandler(false) { HoldMetadata = true };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(false), false);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var context = Context(scope);
        var old = tool.ExecuteAsync(Input(false), context);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var refresh = tool.ExecuteAsync(Input(false) with { Refresh = true }, context);
        var joined = tool.ExecuteAsync(Input(false) with { Refresh = true }, context);
        handler.Release.TrySetResult();

#pragma warning disable VSTHRD003 // Observe the superseded acquisition started above.
        await Assert.ThrowsAsync<InvalidOperationException>(() => old);
#pragma warning restore VSTHRD003
        var results = await Task.WhenAll(refresh, joined);
        Assert.Equal(results[0].Value.SnapshotId, results[1].Value.SnapshotId);
        Assert.True(results[1].Value.CacheHit);
    }

    /// <summary>Changed metadata rejects mixed evidence without permanently poisoning the cache.</summary>
    [Fact]
    public async Task ProviderMovementFailsAcquisition_AndLaterCallCanRetry()
    {
        using var handler = new PrHandler(false) { ChangeRevision = true };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(false), false);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var context = Context(scope);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAllAsync(tool, Input(false), context));
        Assert.Contains("changed during acquisition", failure.Message, StringComparison.Ordinal);
        handler.ChangeRevision = false;
        var pages = await ReadAllAsync(tool, Input(false), context);
        Assert.True(pages[^1].AcquisitionComplete);
    }

    /// <summary>Owner exit cancels and joins provider work.</summary>
    [Fact]
    public async Task OwnerCancellationAndDisposalTerminateInflightHttp()
    {
        using var handler = new PrHandler(false) { HoldMetadata = true };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(false), false);
        var scope = new ToolOperationScope(CancellationToken.None);
        var pending = tool.ExecuteAsync(Input(false), Context(scope));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await scope.DisposeAsync();
#pragma warning disable VSTHRD003 // Observe the caller task started above after disposing its owner.
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
#pragma warning restore VSTHRD003
        Assert.True(handler.Cancelled);
    }

    /// <summary>Retention limits fail rather than evicting a captured revision.</summary>
    [Fact]
    public async Task CacheIsBounded_AndDoesNotServeACompleteResultAfterFailure()
    {
        using var handler = new PrHandler(false);
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(false) with { MaximumCacheBytes = 32 }, false);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(Input(false), Context(scope)));
        Assert.Contains("maximumCacheBytes", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Separate primary operations own separate acquisitions.</summary>
    [Fact]
    public async Task DifferentOperationsNeverReuseSnapshots()
    {
        using var handler = new PrHandler(false);
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new TestSecrets(), Options(false), false);
        await using var first = new ToolOperationScope(CancellationToken.None);
        await using var second = new ToolOperationScope(CancellationToken.None);
        var parent = Context(first);
        var one = await tool.ExecuteAsync(Input(false), parent);
        var two = await tool.ExecuteAsync(Input(false), parent with { Invocation = parent.Invocation with { OperationScope = second } });
        Assert.NotEqual(one.Value.SnapshotId, two.Value.SnapshotId);
    }

    /// <summary>Invalid provider URL or input fails before network or Secrets access.</summary>
    [Fact]
    public void ProviderMismatchAndDisabledAccountsFailBeforeSecretsOrHttp()
    {
        using var handler = new PrHandler(false);
        using var http = new HttpClient(handler);
        var secrets = new TestSecrets();
        var options = Options(false);
        var tool = CreateTool(http, secrets, options, false);
        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput("{\"provider\":\"account\",\"url\":\"https://bitbucket.org/a/b/pull-requests/1\",\"kind\":\"diff\"}"));
        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput("{\"provider\":\"account\",\"url\":\"https://github.com/a/b/pull/1\",\"kind\":\"diff\",\"refresh\":true,\"cursor\":\"x\"}"));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(0, secrets.Calls);
    }

    /// <summary>Repository layers cannot rebind trusted accounts or broaden ceilings.</summary>
    [Fact]
    public void RepositoryConfigurationCannotRedirectCredentialsOrBroadenLimits()
    {
        var trusted = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:prFetch:providers:work:type"] = "github",
            ["tools:prFetch:providers:work:enabled"] = "true",
            ["tools:prFetch:providers:work:urlPatterns:0"] = "https://github.com/trusted/*",
            ["tools:prFetch:providers:work:authentication:mode"] = "bearer",
            ["tools:prFetch:providers:work:authentication:secretReference"] = "secrets:trusted",
            ["tools:prFetch:maximumCacheBytes"] = "10000",
        }).Build();
        var effective = new ConfigurationBuilder().AddConfiguration(trusted).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:prFetch:providers:work:type"] = "bitbucketCloud",
            ["tools:prFetch:providers:work:urlPatterns:0"] = "https://github.com/untrusted/*",
            ["tools:prFetch:providers:work:authentication:secretReference"] = "secrets:repository",
            ["tools:prFetch:maximumCacheBytes"] = "0",
        }).Build();
        var result = PrFetchOptions.FromConfiguration(effective, trusted);
        Assert.Equal("github", result.Providers["work"].Type);
        Assert.Equal("https://github.com/trusted/*", Assert.Single(result.Providers["work"].UrlPatterns));
        Assert.Equal("secrets:trusted", result.Providers["work"].Authentication.SecretReference);
        Assert.Equal(10000, result.MaximumCacheBytes);
    }

    /// <summary>The PR tool uses default protection while the shared opt-in remains available.</summary>
    [Fact]
    public void DuplicateMetadataPreservesHistory_WithoutChangingOrdinaryTools()
    {
        using var handler = new PrHandler(false);
        using var http = new HttpClient(handler);
        var definition = CreateTool(http, new TestSecrets(), Options(false), false).Definition;
        var history = new ToolCallHistory();
        Assert.True(history.TryAdd(definition, "{}"));
        Assert.False(history.TryAdd(definition, "{}"));
        Assert.True(history.ContainsTool("pr_fetch"));
        var responseBatch = new ToolCallHistory(history);
        Assert.False(responseBatch.TryAdd(definition, "{}"));
        Assert.True(responseBatch.TryAdd(definition, "{\"cursor\":\"next\"}"));
        Assert.False(responseBatch.TryAdd(definition, "{\"cursor\":\"next\"}"));

        var optedIn = definition with { AllowDuplicateInvocations = true };
        var optedInHistory = new ToolCallHistory();
        Assert.True(optedInHistory.TryAdd(optedIn, "{}"));
        Assert.True(optedInHistory.TryAdd(optedIn, "{}"));
        var optedInBatch = new ToolCallHistory(optedInHistory);
        Assert.True(optedInBatch.TryAdd(optedIn, "{}"));
        Assert.False(optedInBatch.TryAdd(optedIn, "{}"));

        Assert.False(new DateTimeTool(TestPromptLoader.Instance).Definition.AllowDuplicateInvocations);
        var ordinaryHistory = new ToolCallHistory();
        Assert.True(ordinaryHistory.TryAdd("PR_FETCH", "{}"));
        Assert.False(ordinaryHistory.TryAdd(definition with { AllowDuplicateInvocations = false }, "{}"));
        var wrapped = new ToolRegistry([CreateTool(http, new TestSecrets(), Options(false), false)], runtimeOptions: new ToolRuntimeOptions
        {
            Defaults = new ToolRuntimeOverride { MaximumOutputBytes = 65536 },
        });
        Assert.False(wrapped.Get("pr_fetch").Definition.AllowDuplicateInvocations);
    }

    private static PrFetchTool CreateTool(HttpClient http, ISecretResolver secrets, PrFetchOptions options, bool bitbucket)
        => new([bitbucket ? new BitbucketCloudPullRequestProvider(http, secrets, options) : new GitHubPullRequestProvider(http, secrets, options)], options, TestPromptLoader.Instance);

    private static PrFetchOptions Options(bool bitbucket) => new()
    {
        PageCharacters = 256,
        Providers = new Dictionary<string, PullRequestProviderOptions>
        {
            ["account"] = new()
            {
                Type = bitbucket ? "bitbucketCloud" : "github",
                Enabled = true,
                Authentication = new() { Mode = "bearer", SecretReference = "secrets:pr:test" },
            },
        },
    };

    private static PrFetchInput Input(bool bitbucket) => new()
    {
        Provider = "account",
        Url = bitbucket ? "https://bitbucket.org/org/repo/pull-requests/1" : "https://github.com/org/repo/pull/1",
        Kind = PrFetchKind.Diff,
    };

    private static ToolExecutionContext Context(ToolOperationScope scope) => new(ToolInvocationId.New(), SessionId.New(), RunId.New(), new ToolInvocationContext
    {
        RepositoryPath = Path.GetFullPath("."),
        TrustLevel = RepositoryTrustLevel.TrustedRead,
        AllowedNetworkHosts = ["api.github.com", "api.bitbucket.org"],
        OperationScope = scope,
        RequestedBy = "pr-test",
    });

    private static async Task<List<PrFetchOutput>> ReadAllAsync(PrFetchTool tool, PrFetchInput input, ToolExecutionContext context)
    {
        var pages = new List<PrFetchOutput>();
        do
        {
            var page = (await tool.ExecuteAsync(input, context)).Value;
            Assert.Equal(input.Kind, page.Kind);
            Assert.Equal(input.Cursor is not null, page.IsContinuation);
            pages.Add(page);
            input = input with { Cursor = page.Cursor, Refresh = false };
        }
        while (input.Cursor is not null);
        return pages;
    }

    private sealed class TestSecrets : ISecretResolver
    {
        private int _calls;

        public int Calls => _calls;

        public SecretProviderTrust MinimumTrust { get; private set; }

        public Task<SecretResolutionResult> ResolveAsync(SecretResolutionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            MinimumTrust = request.MinimumTrust;
            return Task.FromResult(new SecretResolutionResult { Value = new SecretValue("test-credential") });
        }
    }

    private sealed class PrHandler : HttpMessageHandler
    {
        private readonly bool _bitbucket;
        private int _requests;
        private int _metadataRequests;
        private int _diffRequests;

        public PrHandler(bool bitbucket) => _bitbucket = bitbucket;

        public int Requests => _requests;

        public int MetadataRequests => _metadataRequests;

        public int DiffRequests => _diffRequests;

        public bool HoldMetadata { get; init; }

        public bool ChangeRevision { get; set; }

        public bool Cancelled { get; private set; }

        public bool MultipleFilePages { get; init; }

        public string? MetadataRedirect { get; init; }

        public bool RedirectDiff { get; init; }

        public string ExpectedCredential { get; init; } = "test-credential";

        public string ChangedPath { get; init; } = "src/changed.cs";

        public string DiffText { get; init; } = "diff --git a/src/changed.cs b/src/changed.cs\n--- a/src/changed.cs\n+++ b/src/changed.cs\n@@ -1 +1 @@\n-old\n+new\n";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var uri = request.RequestUri!;
            Assert.Equal(ExpectedCredential, request.Headers.Authorization?.Parameter);
            if (uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                if (MultipleFilePages)
                {
                    var second = uri.Query.Contains("&page=2", StringComparison.Ordinal);
                    return Json(JsonSerializer.Serialize(Enumerable.Range(second ? 20 : 0, second ? 1 : 20).Select(index => new { filename = $"src/{index}.cs", status = "modified", patch = "+new" })));
                }

                return Json(JsonSerializer.Serialize(new[] { new { filename = ChangedPath, status = "modified", patch = "@@ -1 +1 @@\n-old\n+new" } }));
            }

            if (uri.AbsolutePath.EndsWith("/diffstat", StringComparison.Ordinal))
            {
                if (MultipleFilePages)
                {
                    var second = uri.Query.Contains("page=2", StringComparison.Ordinal);
                    return Json(JsonSerializer.Serialize(new { size = 2, values = new[] { new { status = "modified", @new = new { path = second ? "src/second.cs" : "src/first.cs" } } }, next = second ? null : "https://api.bitbucket.org/2.0/repositories/org/repo/pullrequests/1/diffstat?page=2" }));
                }

                return Json(JsonSerializer.Serialize(new
                {
                    size = 1,
                    values = new[] { new { status = "modified", @new = new { path = ChangedPath }, old = new { path = ChangedPath } } },
                }));
            }

            if (RedirectDiff && uri.AbsolutePath.EndsWith("/diff", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _diffRequests);
                return Redirect("https://api.bitbucket.org/2.0/repositories/org/repo/diff/head");
            }

            if (uri.AbsolutePath.Contains("/diff", StringComparison.Ordinal) || request.Headers.Accept.Any(item => item.MediaType == "application/vnd.github.diff"))
            {
                Interlocked.Increment(ref _diffRequests);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(DiffText, Encoding.UTF8, "text/plain") };
            }

            if (MetadataRedirect is not null)
            {
                return Redirect(MetadataRedirect);
            }

            var count = Interlocked.Increment(ref _metadataRequests);
            Started.TrySetResult();
            if (HoldMetadata)
            {
                try
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }

            var revision = ChangeRevision && count > 1 ? "moved" : "head";
            return _bitbucket
                ? Json("""{"title":"PR","description":"desc","state":"OPEN","source":{"commit":{"hash":"HEAD"},"repository":{"full_name":"org/repo"}},"destination":{"commit":{"hash":"base-with-unrelated-terraform"},"repository":{"full_name":"org/repo"}}} """.Replace("HEAD", revision, StringComparison.Ordinal))
                : Json("""{"title":"PR","body":"desc","state":"open","changed_files":1,"head":{"sha":"HEAD","repo":{"full_name":"org/repo"}},"base":{"sha":"base-with-unrelated-terraform","repo":{"full_name":"org/repo"}}} """.Replace("HEAD", revision, StringComparison.Ordinal).Replace("\"changed_files\":1", MultipleFilePages ? "\"changed_files\":21" : "\"changed_files\":1", StringComparison.Ordinal));
        }

        private static HttpResponseMessage Redirect(string url) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(url) } };

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
