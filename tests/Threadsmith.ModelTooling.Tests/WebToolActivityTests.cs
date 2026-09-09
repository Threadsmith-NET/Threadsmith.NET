namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies live search/fetch details through the real invocation and serialization boundaries.</summary>
public sealed class WebToolActivityTests
{
    /// <summary>Search displays the complete query even on failure, without adding it to durable events.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_QueryIsAvailableToLiveDisplay(bool fail)
    {
        var query = "search-display-canary " + new string('q', 450);
        var tool = new WebSearchTool(
            new SearchClient(fail), new WebSearchOptions(), new SecretOutputSanitizer(), TestPromptLoader.Instance);

        var run = await InvokeAsync(tool, JsonSerializer.Serialize(new { query }), CreateContext());

        Assert.Equal(!fail, run.Result.Succeeded);
        Assert.Equal(query, run.Started.TransientActivityDetail);
        Assert.Null(run.Started.ActivityDetail);
        Assert.DoesNotContain("search-display-canary", DomainEventJson.Serialize(run.Started), StringComparison.Ordinal);
        Assert.DoesNotContain("search-display-canary", DomainEventJson.Serialize(run.Completed), StringComparison.Ordinal);
        Assert.DoesNotContain("search-display-canary", JsonSerializer.Serialize(run.Result), StringComparison.Ordinal);
    }

    /// <summary>Direct, search and current-user routes show the actual final URL including query parameters.</summary>
    [Theory]
    [InlineData("direct", false)]
    [InlineData("search", false)]
    [InlineData("user", false)]
    [InlineData("search", true)]
    public async Task Fetch_FullUrlSurvivesReferenceResolutionAndRedirects(string route, bool redirect)
    {
        var context = CreateContext();
        var options = new WebFetchOptions();
        var authority = new WebFetchAuthorizationAuthority(options);
        var requested = new Uri("https://example.com/" + new string('p', 320) + "?q=request-display-canary&view=full");
        var final = redirect ? new Uri("https://example.com/final?q=final-display-canary&view=full") : requested;
        var reference = IssueReference(authority, context, requested, route);
        var transport = new ContentTransport(final);
        var tool = new WebFetchTool(
            new WebContentFetcher(transport, options, TestPromptLoader.Instance), authority, options, TestPromptLoader.Instance);

        var run = await InvokeAsync(tool, JsonSerializer.Serialize(new { reference }), context);

        Assert.True(run.Result.Succeeded, run.Result.Error);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal(requested.AbsoluteUri, run.Started.TransientActivityDetail);
        Assert.Equal(final.AbsoluteUri, run.Completed.TransientActivityDetail);
        Assert.DoesNotContain("activityUrl", tool.Definition.OutputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        string[] serializedValues = [DomainEventJson.Serialize(run.Started), DomainEventJson.Serialize(run.Completed), JsonSerializer.Serialize(run.Result)];
        foreach (var serialized in serializedValues)
        {
            Assert.DoesNotContain("display-canary", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("TransientActivityDetail", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("ActivityUrl", serialized, StringComparison.Ordinal);
        }
    }

    /// <summary>Showing an opaque reference cannot disclose another run/session/repository's destination or consume it.</summary>
    [Theory]
    [InlineData("run")]
    [InlineData("session")]
    [InlineData("repository")]
    public async Task Fetch_ReferenceDisplayHonorsScopeWithoutConsumingAuthority(string changedScope)
    {
        var context = CreateContext();
        var options = new WebFetchOptions();
        var authority = new WebFetchAuthorizationAuthority(options);
        var url = new Uri("https://example.com/doc?q=scoped-display-canary");
        var reference = IssueReference(authority, context, url, "search");
        var transport = new ContentTransport(url);
        var tool = new WebFetchTool(
            new WebContentFetcher(transport, options, TestPromptLoader.Instance), authority, options, TestPromptLoader.Instance);
        var other = changedScope switch
        {
            "run" => context with { RunId = RunId.New() },
            "session" => context with { SessionId = SessionId.New() },
            _ => context with { Invocation = context.Invocation with { RepositoryPath = Path.Combine(Path.GetTempPath(), "another-repository") } },
        };

        var rejected = await InvokeAsync(tool, JsonSerializer.Serialize(new { reference }), other);

        Assert.False(rejected.Result.Succeeded);
        Assert.Null(rejected.Started.TransientActivityDetail);
        Assert.Equal(0, transport.CallCount);
        var accepted = await InvokeAsync(tool, JsonSerializer.Serialize(new { reference }), context);
        Assert.True(accepted.Result.Succeeded, accepted.Result.Error);
        Assert.Equal(url.AbsoluteUri, accepted.Completed.TransientActivityDetail);
        Assert.Equal(1, transport.CallCount);
    }

    /// <summary>URL details retain ordinary parameters while the existing secret sanitizer masks credentials.</summary>
    [Fact]
    public async Task Fetch_LiveDetailStillSanitizesCredentialValues()
    {
        var context = CreateContext();
        var options = new WebFetchOptions();
        var authority = new WebFetchAuthorizationAuthority(options);
        var url = new Uri("https://example.com/docs?q=public&token=credential-canary");
        var reference = IssueReference(authority, context, url, "direct");
        var tool = new WebFetchTool(
            new WebContentFetcher(new ContentTransport(url), options, TestPromptLoader.Instance), authority, options, TestPromptLoader.Instance);

        var run = await InvokeAsync(tool, JsonSerializer.Serialize(new { reference }), context);

        Assert.True(run.Result.Succeeded, run.Result.Error);
        Assert.Equal(new SecretOutputSanitizer().Sanitize(url.AbsoluteUri), run.Completed.TransientActivityDetail);
        Assert.Contains("q=public", run.Completed.TransientActivityDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-canary", run.Completed.TransientActivityDetail, StringComparison.Ordinal);
    }

    private static ToolExecutionContext CreateContext()
    {
        return new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext
            {
                RepositoryPath = Path.GetTempPath(),
                AllowedNetworkHosts = ["example.com", "api.search.brave.com"],
                RequestedBy = "test",
            });
    }

    private static string IssueReference(WebFetchAuthorizationAuthority authority, ToolExecutionContext context, Uri url, string route)
    {
        if (route == "search")
        {
            return authority.IssueSearchResult(
                context.Invocation.RepositoryPath, url, context.SessionId, context.RunId, ToolInvocationId.New(), "stub", "query-identity", 1);
        }

        if (route == "user")
        {
            return Assert.Single(authority.IssueCurrentUserMessageUrls(
                context.Invocation.RepositoryPath,
                context.SessionId,
                context.RunId,
                ConversationMessageId.New(),
                "Read " + url.AbsoluteUri,
                context.Invocation)).Id;
        }

        authority.GrantDirectUrl(context.Invocation.RepositoryPath, context.SessionId, url.AbsoluteUri);
        return url.AbsoluteUri;
    }

    private static async Task<InvocationRun> InvokeAsync(ITool tool, string arguments, ToolExecutionContext context)
    {
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            if (item is ToolInvocationCompleted)
            {
                completed.TrySetResult();
            }

            return Task.CompletedTask;
        });
        var pipeline = new ToolInvocationPipeline(
            new ToolRegistry([tool]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance);
        var result = await pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = context.SessionId,
            RunId = context.RunId,
            ToolId = tool.Definition.Id,
            ArgumentsJson = arguments,
            Context = context.Invocation,
        });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return new InvocationRun(
            result, Assert.Single(observed.OfType<ToolInvocationStarted>()), Assert.Single(observed.OfType<ToolInvocationCompleted>()));
    }

    private sealed record InvocationRun(ToolInvocationResult Result, ToolInvocationStarted Started, ToolInvocationCompleted Completed);

    private sealed class SearchClient : IWebSearchClient
    {
        private readonly bool _fail;

        public SearchClient(bool fail)
        {
            _fail = fail;
        }

        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_fail)
            {
                throw new InvalidOperationException("Simulated provider failure.");
            }

            return Task.FromResult(new WebSearchResponse { QueryIdentity = "identity", ProviderId = "stub", TrustBoundary = "untrusted" });
        }
    }

    private sealed class ContentTransport : IWebContentTransport
    {
        private readonly Uri _final;

        public ContentTransport(Uri final)
        {
            _final = final;
        }

        public int CallCount { get; private set; }

        public Task<WebFetchTransportResponse> GetAsync(
            Uri uri,
            WebFetchSourceKind sourceKind,
            IReadOnlySet<string> authorizedDirectUrlDigests,
            WebFetchOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var bytes = Encoding.UTF8.GetBytes("content");
            return Task.FromResult(new WebFetchTransportResponse(
                uri,
                _final,
                HttpStatusCode.OK,
                "text/plain",
                "utf-8",
                null,
                bytes,
                bytes.Length,
                uri == _final ? [] : [(uri, _final)]));
        }
    }
}
