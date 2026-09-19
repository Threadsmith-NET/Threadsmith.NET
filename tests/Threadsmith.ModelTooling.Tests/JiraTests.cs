namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Tools.Jira;
using Xunit;

/// <summary>Exercises Jira account selection, transport, projection and ordinary pipeline bounds.</summary>
public sealed class JiraTests
{
    /// <summary>Both token endpoint modes send one bounded read with request-local Basic authentication.</summary>
    [Theory]
    [InlineData("site", "https://example.atlassian.net/rest/api/3/issue/APP-123")]
    [InlineData("scopedGateway", "https://api.atlassian.com/ex/jira/11111111-2222-4333-8444-555555555555/rest/api/3/issue/APP-123")]
    public async Task ReadsIssueThroughConfiguredEndpointMode(string endpointMode, string expectedPrefix)
    {
        using var handler = new JiraHandler();
        using var http = new HttpClient(handler);
        var secrets = new RotatingSecrets("token-a");
        var tool = CreateTool(http, secrets, Options(endpointMode));
        var input = (JiraInput)tool.DeserializeInput(
            "{\"kind\":\"read\",\"issue\":\"https://example.atlassian.net/browse/app-123?source=mail#description\"}");

        var result = await tool.ExecuteAsync(input, Context());

        Assert.StartsWith(expectedPrefix, handler.LastUri?.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("fields=summary,description,updated", handler.LastUri?.Query, StringComparison.Ordinal);
        Assert.Contains("updateHistory=false", handler.LastUri?.Query, StringComparison.Ordinal);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("developer@example.org:token-a")), handler.Authorization);
        Assert.Equal("APP-123", result.Value.RequestedIssue);
        Assert.Equal("APP-124", result.Value.Key);
        Assert.Equal("https://example.atlassian.net/browse/APP-124", result.Value.Url);
        Assert.Contains("First paragraph", result.Value.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("source=mail", result.Value.Url, StringComparison.Ordinal);
        Assert.Contains("read · APP-124 (requested APP-123) · work-jira", result.TransientActivityDetail, StringComparison.Ordinal);
        Assert.Equal(1, secrets.Calls);
        Assert.Equal(SecretProviderTrust.UserOwned, secrets.MinimumTrust);
    }

    /// <summary>Trusted aliases select an account without becoming credential destinations.</summary>
    [Fact]
    public async Task BrowseAliasIsInputOnlyAndCanonicalizesToConfiguredSite()
    {
        using var handler = new JiraHandler();
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new RotatingSecrets("token"), Options("site"));
        var input = (JiraInput)tool.DeserializeInput(
            "{\"kind\":\"read\",\"issue\":\"https://issues.example.org/browse/APP-123/\"}");

        var result = await tool.ExecuteAsync(input, Context());

        Assert.Equal("example.atlassian.net", handler.LastUri?.Host);
        Assert.Equal("https://example.atlassian.net/browse/APP-124", result.Value.Url);
        Assert.Equal("read · APP-123 · work-jira", tool.GetActivityDetail(input));
    }

    /// <summary>Discarded query and fragment decorations cannot invalidate a valid browse path.</summary>
    [Fact]
    public void BrowseUrlIgnoresEncodedSeparatorsOutsideThePath()
    {
        using var handler = new JiraHandler();
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new RotatingSecrets("token"), Options("site"));

        var input = (JiraInput)tool.DeserializeInput(
            "{\"kind\":\"read\",\"issue\":\"https://example.atlassian.net/browse/APP-123?returnUrl=https%3A%2F%2Fexample.org#x%2Fy\"}");

        Assert.Equal("read · APP-123 · work-jira", tool.GetActivityDetail(input));
    }

    /// <summary>Invalid kinds, routes and ambiguous accounts fail before Secrets or HTTP.</summary>
    [Fact]
    public void InvalidOrAmbiguousInputCannotReachPrivilegedBoundaries()
    {
        using var handler = new JiraHandler();
        using var http = new HttpClient(handler);
        var secrets = new RotatingSecrets("token");
        var options = Options("site") with
        {
            Providers = new Dictionary<string, JiraProviderOptions>
            {
                ["work-jira"] = Options("site").Providers["work-jira"],
                ["second"] = Options("site").Providers["work-jira"],
            },
        };
        var tool = CreateTool(http, secrets, options);

        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
            "{\"kind\":1,\"issue\":\"APP-123\"}"));
        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
            "{\"kind\":\"transition\",\"issue\":\"APP-123\"}"));
        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
            "{\"kind\":\"read\",\"issue\":\"https://example.atlassian.net/issues/?jql=APP-123\"}"));
        var ambiguity = Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
            "{\"kind\":\"read\",\"issue\":\"APP-123\"}"));
        Assert.Contains("Pass provider", ambiguity.Message, StringComparison.Ordinal);
        Assert.IsType<JiraInput>(tool.DeserializeInput(
            "{\"kind\":\"read\",\"issue\":\"APP-123\",\"provider\":\"second\"}"));
        Assert.Equal(0, secrets.Calls);
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>Repository configuration may disable or narrow Jira but cannot rebind trusted accounts.</summary>
    [Fact]
    public void ConfigurationKeepsTrustedBindingsAndNarrowsLimits()
    {
        var trusted = Configuration(new Dictionary<string, string?>
        {
            ["tools:jira:providers:work:type"] = "jiraCloud",
            ["tools:jira:providers:work:enabled"] = "true",
            ["tools:jira:providers:work:siteUrl"] = "https://example.atlassian.net",
            ["tools:jira:providers:work:endpointMode"] = "site",
            ["tools:jira:providers:work:authentication:mode"] = "basic",
            ["tools:jira:providers:work:authentication:username"] = "developer@example.org",
            ["tools:jira:providers:work:authentication:secretReference"] = "secrets:jira:trusted",
            ["tools:jira:maximumResponseBytes"] = "2000000",
            ["tools:jira:maximumBodyBytes"] = "64000",
        });
        var effective = new ConfigurationBuilder().AddConfiguration(trusted).AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["tools:jira:providers:work:siteUrl"] = "https://evil.atlassian.net",
                ["tools:jira:providers:work:authentication:secretReference"] = "secrets:jira:repository",
                ["tools:jira:maximumResponseBytes"] = "1000000",
                ["tools:jira:maximumBodyBytes"] = "32000",
            }).Build();

        var options = JiraOptions.FromConfiguration(effective, trusted);

        Assert.Equal("https://example.atlassian.net", options.Providers["work"].SiteUrl);
        Assert.Equal("secrets:jira:trusted", options.Providers["work"].Authentication.SecretReference);
        Assert.Equal(1000000, options.MaximumResponseBytes);
        Assert.Equal(32000, options.MaximumBodyBytes);
    }

    /// <summary>Authentication bindings require an email-shaped account identifier.</summary>
    [Fact]
    public void ConfigurationRejectsNonEmailUsername()
    {
        var options = Options("site");
        options = options with
        {
            Providers = new Dictionary<string, JiraProviderOptions>
            {
                ["work-jira"] = options.Providers["work-jira"] with
                {
                    Authentication = options.Providers["work-jira"].Authentication with
                    {
                        Username = "not-an-email",
                    },
                },
            },
        };

        Assert.Throws<InvalidOperationException>(() => JiraOptions.Validate(options));
    }

    /// <summary>ADF projection retains supported text and reports unsupported rich content.</summary>
    [Fact]
    public void AdfProjectionIsReadableAndHonestAboutCoverage()
    {
        using var document = JsonDocument.Parse(
            """
            {"type":"doc","version":1,"content":[
              {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Heading"}]},
              {"type":"paragraph","content":[{"type":"text","text":"See","marks":[{"type":"link","attrs":{"href":"https://example.org/path"}}]},{"type":"hardBreak"},{"type":"status","attrs":{"text":"In progress"}}]},
              {"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"item"}]}]}]},
              {"type":"table","content":[{"type":"tableRow","content":[{"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"A"}]}]},{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"B"}]}]}]}]},
              {"type":"inlineCard","attrs":{"url":"https://example.org/card"}},
              {"type":"taskList","content":[{"type":"text","text":"fallback"}]}
            ]}
            """);

        var result = JiraDescriptionReader.Read(document.RootElement, 65536);

        Assert.Contains("Heading", result.Body, StringComparison.Ordinal);
        Assert.Contains("See (https://example.org/path)", result.Body, StringComparison.Ordinal);
        Assert.Contains("In progress", result.Body, StringComparison.Ordinal);
        Assert.Contains("- item", result.Body, StringComparison.Ordinal);
        Assert.Contains("A\tB", result.Body, StringComparison.Ordinal);
        Assert.Contains("card preview not retrieved", result.Body, StringComparison.Ordinal);
        Assert.Contains("[unsupported content] fallback", result.Body, StringComparison.Ordinal);
        Assert.False(result.BodyComplete);
        Assert.False(result.IsTruncated);
        Assert.Contains("card-preview-not-retrieved", result.Limitations);
        Assert.Contains("unsupported-adf-content", result.Limitations);
    }

    /// <summary>Large valid descriptions return a Unicode-safe marked prefix.</summary>
    [Fact]
    public void AdfBodyLimitReturnsPartialPrefix()
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                type = "doc",
                version = 1,
                content = new[]
                {
                    new { type = "paragraph", content = new[] { new { type = "text", text = string.Concat(Enumerable.Repeat("😀", 100)) } } },
                },
            }));

        var result = JiraDescriptionReader.Read(document.RootElement, 21);

        Assert.Equal(20, Encoding.UTF8.GetByteCount(result.Body));
        Assert.False(result.BodyComplete);
        Assert.True(result.IsTruncated);
        Assert.Contains("body-byte-limit", result.Limitations);
    }

    /// <summary>The body ceiling stops traversal before an unconsumed malformed suffix.</summary>
    [Fact]
    public void AdfBodyLimitStopsBeforeMalformedSuffix()
    {
        using var document = JsonDocument.Parse(
            """
            {"type":"doc","version":1,"content":[
              {"type":"paragraph","content":[{"type":"text","text":"01234567890123456789"}]},
              {"type":"paragraph","content":false}
            ]}
            """);

        var result = JiraDescriptionReader.Read(document.RootElement, 10);

        Assert.Equal("0123456789", result.Body);
        Assert.True(result.IsTruncated);
        Assert.Contains("body-byte-limit", result.Limitations);
        Assert.DoesNotContain("projection-work-limit", result.Limitations);
    }

    /// <summary>Projection bounds repeated mark expansion before it can amplify allocations.</summary>
    [Fact]
    public void AdfBodyLimitBoundsRepeatedMarkExpansion()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "doc",
            version = 1,
            content = new[]
            {
                new
                {
                    type = "paragraph",
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = new string('x', 64 * 1024),
                            marks = Enumerable.Range(0, 500).Select(_ => new { type = "strike" }).ToArray(),
                        },
                    },
                },
            },
        }));
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = JiraDescriptionReader.Read(document.RootElement, 10);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("xxxxxxxxxx", result.Body);
        Assert.InRange(allocated, 0, 8 * 1024 * 1024);
    }

    /// <summary>Empty table rows consume the same bounded projection-work budget as other ADF nodes.</summary>
    [Fact]
    public void AdfProjectionBoundsEmptyTableRows()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "doc",
            version = 1,
            content = new[]
            {
                new
                {
                    type = "table",
                    content = Enumerable.Range(0, 50_100)
                        .Select(_ => new { type = "tableRow", content = Array.Empty<object>() })
                        .ToArray(),
                },
            },
        }));

        var result = JiraDescriptionReader.Read(document.RootElement, 2 * 1024 * 1024);

        Assert.True(result.IsTruncated);
        Assert.False(result.BodyComplete);
        Assert.Contains("projection-work-limit", result.Limitations);
    }

    /// <summary>Table delimiters preserve leading empty rows and cells instead of shifting later values.</summary>
    [Fact]
    public void AdfProjectionPreservesLeadingEmptyTablePositions()
    {
        using var document = JsonDocument.Parse(
            """{"type":"doc","version":1,"content":[{"type":"table","content":[{"type":"tableRow","content":[]},{"type":"tableRow","content":[{"type":"tableCell","content":[]},{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"value"}]}]}]}]}]}""");

        var result = JiraDescriptionReader.Read(document.RootElement, 65536);

        Assert.Equal("\n\tvalue", result.Body);
        Assert.True(result.BodyComplete);
    }

    /// <summary>Blockquote prefixes stop at the output bound without materializing an array of lines.</summary>
    [Fact]
    public void AdfProjectionBoundsBlockquotePrefixExpansion()
    {
        const int maximumBodyBytes = 1024 * 1024;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "doc",
            version = 1,
            content = new[]
            {
                new
                {
                    type = "blockquote",
                    content = new[]
                    {
                        new { type = "paragraph", content = new[] { new { type = "text", text = new string('\n', maximumBodyBytes - 1) } } },
                    },
                },
            },
        }));
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = JiraDescriptionReader.Read(document.RootElement, maximumBodyBytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.StartsWith("> \n> ", result.Body, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Body), maximumBodyBytes - 2, maximumBodyBytes);
        Assert.True(result.IsTruncated);
        Assert.Contains("body-byte-limit", result.Limitations);
        Assert.InRange(allocated, 0, 32 * 1024 * 1024);
    }

    /// <summary>Media containers retain their child placeholders without inventing another attachment.</summary>
    [Fact]
    public void AdfProjectionDoesNotDuplicateMediaContainerPlaceholder()
    {
        using var document = JsonDocument.Parse(
            """{"type":"doc","version":1,"content":[{"type":"mediaSingle","content":[{"type":"media","attrs":{"alt":"attachment.png"}}]}]}""");

        var result = JiraDescriptionReader.Read(document.RootElement, 65536);

        Assert.Equal("attachment.png [media not retrieved]", result.Body);
        Assert.Equal(1, result.Body.Split("[media not retrieved]", StringSplitOptions.None).Length - 1);
        Assert.False(result.BodyComplete);
        Assert.Contains("media-not-retrieved", result.Limitations);
    }

    /// <summary>Known ADF containers and label fallbacks preserve honest coverage and table shape.</summary>
    [Fact]
    public void AdfKnownNodesValidateAndPreserveStructure()
    {
        using var malformedList = JsonDocument.Parse(
            """{"type":"doc","version":1,"content":[{"type":"bulletList","content":[{"type":"listItem"}]}]}""");
        Assert.Throws<InvalidDataException>(() =>
            JiraDescriptionReader.Read(malformedList.RootElement, 65536));

        using var emoji = JsonDocument.Parse(
            """{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"emoji","attrs":{"text":"","shortName":":smile:"}}]}]}""");
        var emojiResult = JiraDescriptionReader.Read(emoji.RootElement, 65536);
        Assert.Equal(":smile:", emojiResult.Body);
        Assert.True(emojiResult.BodyComplete);

        using var table = JsonDocument.Parse(
            """{"type":"doc","version":1,"content":[{"type":"table","content":[{"type":"tableRow","content":[{"type":"tableCell","attrs":{"rowspan":2},"content":[{"type":"paragraph","content":[{"type":"text","text":"A"}]}]},{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"B"}]}]}]}]}]}""");
        var tableResult = JiraDescriptionReader.Read(table.RootElement, 65536);
        Assert.Equal("[merged cell] A\tB", tableResult.Body);
        Assert.False(tableResult.BodyComplete);
    }

    /// <summary>ADF projection observes invocation cancellation before doing bounded synchronous work.</summary>
    [Fact]
    public void AdfProjectionObservesCancellation()
    {
        using var document = JsonDocument.Parse("""{"type":"doc","version":1,"content":[]}""");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            JiraDescriptionReader.Read(document.RootElement, 65536, cancellation.Token));
    }

    /// <summary>Secret resolution is request-local so token rotation is observed without rebuilding the tool.</summary>
    [Fact]
    public async Task SeparateReadsObserveSecretRotation()
    {
        using var handler = new JiraHandler();
        using var http = new HttpClient(handler);
        var secrets = new RotatingSecrets("first", "second");
        var tool = CreateTool(http, secrets, Options("site"));
        var input = new JiraInput { Kind = "read", Issue = "APP-123", Provider = "work-jira" };

        await tool.ExecuteAsync(input, Context());
        var first = handler.Authorization;
        await tool.ExecuteAsync(input, Context());
        var second = handler.Authorization;

        Assert.NotEqual(first, second);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("developer@example.org:second")), second);
        Assert.Equal(2, secrets.Calls);
    }

    /// <summary>The configured runtime cap returns partial structured output instead of failing the whole read.</summary>
    [Fact]
    public async Task PipelineUsesEffectiveOutputCapAndKeepsStructuredPartialBody()
    {
        using var handler = new JiraHandler { BodyText = new string('x', 6000) };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new RotatingSecrets("token"), Options("site"));
        var registry = new ToolRegistry([tool], runtimeOptions: new ToolRuntimeOptions
        {
            ByTool = [new ToolRuntimeToolOverride { ToolId = "jira", MaximumOutputBytes = 1400 }],
        });
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance);
        var context = InvocationContext();

        var result = await pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "jira",
            ArgumentsJson = "{\"kind\":\"read\",\"issue\":\"APP-123\",\"provider\":\"work-jira\"}",
            Context = context,
        });

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.IsTruncated);
        Assert.NotNull(result.ResultJson);
        Assert.True(Encoding.UTF8.GetByteCount(result.ResultJson) <= 1400);
        using var output = JsonDocument.Parse(result.ResultJson);
        Assert.False(output.RootElement.GetProperty("BodyComplete").GetBoolean());
        Assert.Contains("tool-output-limit", output.RootElement.GetProperty("Limitations").EnumerateArray().Select(value => value.GetString()));
        Assert.NotEmpty(output.RootElement.GetProperty("Body").GetString() ?? string.Empty);
        var started = Assert.Single(observed.OfType<ToolInvocationStarted>());
        Assert.Contains("read · APP-123 · work-jira", started.ActivityDetail, StringComparison.Ordinal);
        var completed = Assert.Single(observed.OfType<ToolInvocationCompleted>());
        Assert.Contains("read · APP-124 (requested APP-123) · work-jira", completed.TransientActivityDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.BodyText, completed.TransientActivityDetail, StringComparison.Ordinal);
    }

    /// <summary>Sanitizer expansion is re-bounded without restoring pre-sanitized Jira text.</summary>
    [Fact]
    public async Task PipelineReboundsSanitizerExpansion()
    {
        using var handler = new JiraHandler { BodyText = string.Concat(Enumerable.Repeat("MASK", 1000)) };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new RotatingSecrets("token"), Options("site"));
        var registry = new ToolRegistry([tool], runtimeOptions: new ToolRuntimeOptions
        {
            ByTool = [new ToolRuntimeToolOverride { ToolId = "jira", MaximumOutputBytes = 1400 }],
        });
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new ExpandingJiraSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance);

        var result = await pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "jira",
            ArgumentsJson = "{\"kind\":\"read\",\"issue\":\"APP-123\",\"provider\":\"work-jira\"}",
            Context = InvocationContext(),
        });

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.IsTruncated);
        Assert.NotNull(result.ResultJson);
        Assert.True(Encoding.UTF8.GetByteCount(result.ResultJson) <= 1400);
        Assert.DoesNotContain("MASK", result.ResultJson, StringComparison.Ordinal);
        using var output = JsonDocument.Parse(result.ResultJson);
        Assert.Contains("tool-output-limit", output.RootElement.GetProperty("Limitations").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>A scripted ordinary conversation receives the body and can display it in its response.</summary>
    [Fact]
    public async Task ConversationReceivesAndDisplaysRequestedDescription()
    {
        var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var repository = Path.Combine(temporaryParent, $"threadsmith-jira-conversation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repository);
        try
        {
            const string canary = "CANARY ticket description from Jira";
            using var handler = new JiraHandler { BodyText = canary };
            using var http = new HttpClient(handler);
            var tool = CreateTool(http, new RotatingSecrets("token"), Options("site"));
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var assembler = new ContextAssembler(
                evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                TestPromptLoader.Instance);
            var budget = new ExecutionBudget(new BudgetDimensions(100000, 10, TimeSpan.FromMinutes(1)));
            var configuration = new ConfigurationBuilder().Build();
            var state = new ToolStateManager(
                [tool.Definition],
                configuration,
                Path.Combine(repository, ".threadsmith", "config.json"),
                Path.Combine(repository, "user", "consent.json"));
            await state.GrantConsentAndEnableAsync("jira");
            var registry = new ToolRegistry([tool], state);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget);
            var model = new JiraConversationProbe(canary);
            var application = new SessionApplication(
                events,
                model,
                budget,
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(new ToolInvocationContext
                {
                    RepositoryPath = repository,
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                    RequestedBy = "model",
                    AllowedNetworkHosts = ["example.atlassian.net"],
                }),
                assembler,
                evidence,
                registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var session = await dispatcher.DispatchAsync(
                new CreateSessionCommand("Jira conversation probe"),
                timeout.Token);
            var run = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(
                    session,
                    "Show me the ticket description for https://example.atlassian.net/browse/APP-123"),
                timeout.Token);
            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), timeout.Token));

            Assert.Equal(2, model.Requests.Count);
            Assert.Contains(canary, model.ObservedToolResult, StringComparison.Ordinal);
            Assert.Equal($"Ticket description: {canary}", model.FinalResponse);
        }
        finally
        {
            var ownedRoot = Path.GetFullPath(repository);
            if (!string.Equals(Path.GetDirectoryName(ownedRoot), temporaryParent, StringComparison.Ordinal)
                || !Path.GetFileName(ownedRoot).StartsWith("threadsmith-jira-conversation-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing cleanup outside the exact owned Jira test directory.");
            }

            Directory.Delete(ownedRoot, recursive: true);
        }
    }

    /// <summary>Metadata-only output fitting preserves an absent description's completeness.</summary>
    [Fact]
    public async Task MetadataOnlyOutputTruncationPreservesBodyCompleteness()
    {
        using var handler = new JiraHandler
        {
            DescriptionIsNull = true,
            SummaryText = new string('s', 512),
        };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new RotatingSecrets("token"), Options("site"));

        var result = await tool.ExecuteAsync(
            new JiraInput { Kind = "read", Issue = "APP-123", Provider = "work-jira" },
            Context(650));

        Assert.Equal("absent", result.Value.BodyState);
        Assert.True(result.Value.BodyComplete);
        Assert.Empty(result.Value.Body);
        Assert.Empty(result.Value.Summary);
        Assert.Contains("summary-truncated", result.Value.Limitations);
        Assert.True(result.IsTruncated);
    }

    /// <summary>Authentication and response-size failures are bounded and do not expose provider bodies.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication failed")]
    [InlineData(HttpStatusCode.Forbidden, "denied access")]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    public async Task HttpFailuresUseSafeMessages(HttpStatusCode status, string expected)
    {
        using var handler = new JiraHandler { StatusCode = status, FailureBody = "secret-provider-detail" };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new RotatingSecrets("token"), Options("site"));

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            tool.ExecuteAsync(new JiraInput { Kind = "read", Issue = "APP-123", Provider = "work-jira" }, Context()));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-provider-detail", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Date-form retry guidance is parsed without exposing provider response content.</summary>
    [Fact]
    public async Task RateLimitDateReturnsBoundedRetryGuidance()
    {
        using var handler = new JiraHandler
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            RetryAfter = DateTimeOffset.UtcNow.AddMinutes(5),
            FailureBody = "provider-rate-limit-detail",
        };
        using var http = new HttpClient(handler);
        var tool = CreateTool(http, new RotatingSecrets("token"), Options("site"));

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            tool.ExecuteAsync(new JiraInput { Kind = "read", Issue = "APP-123", Provider = "work-jira" }, Context()));

        Assert.Contains("Retry after about", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-rate-limit-detail", error.Message, StringComparison.Ordinal);
    }

    private static JiraTool CreateTool(HttpClient http, ISecretResolver secrets, JiraOptions options)
        => new(new JiraCloudClient(http, secrets, options), options, TestPromptLoader.Instance);

    private static JiraOptions Options(string endpointMode) => new()
    {
        Providers = new Dictionary<string, JiraProviderOptions>
        {
            ["work-jira"] = new()
            {
                Enabled = true,
                SiteUrl = "https://example.atlassian.net",
                BrowseHostAliases = ["issues.example.org"],
                EndpointMode = endpointMode,
                CloudId = endpointMode == "scopedGateway" ? "11111111-2222-4333-8444-555555555555" : null,
                Authentication = new()
                {
                    Username = "developer@example.org",
                    SecretReference = "secrets:jira:test-token",
                },
            },
        },
    };

    private static IConfigurationRoot Configuration(IReadOnlyDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ToolExecutionContext Context(int? maximumOutputBytes = null) => new(
        ToolInvocationId.New(),
        SessionId.New(),
        RunId.New(),
        InvocationContext())
    {
        MaximumOutputBytes = maximumOutputBytes,
    };

    private static ToolInvocationContext InvocationContext() => new()
    {
        RepositoryPath = Path.GetFullPath("."),
        TrustLevel = RepositoryTrustLevel.TrustedRead,
        AllowedNetworkHosts = ["example.atlassian.net", "api.atlassian.com"],
        RequestedBy = "jira-test",
    };

    private sealed class RotatingSecrets : ISecretResolver
    {
        private readonly Queue<string> _values;

        internal RotatingSecrets(params string[] values) => _values = new Queue<string>(values);

        internal int Calls { get; private set; }

        internal SecretProviderTrust MinimumTrust { get; private set; }

        public Task<SecretResolutionResult> ResolveAsync(
            SecretResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            MinimumTrust = request.MinimumTrust;
            var value = _values.Count > 1 ? _values.Dequeue() : _values.Peek();
            return Task.FromResult(new SecretResolutionResult { Value = new SecretValue(value) });
        }
    }

    private sealed class ExpandingJiraSanitizer : IOutputSanitizer
    {
        private static readonly string _replacement = $"[REDACTED:{new string('x', 96)}]";

        public string Sanitize(string value)
            => value.Replace("MASK", _replacement, StringComparison.Ordinal);
    }

    private sealed class JiraConversationProbe(string canary) : IModelProvider
    {
        internal List<ModelStreamRequest> Requests { get; } = [];

        internal string ObservedToolResult { get; private set; } = string.Empty;

        internal string FinalResponse { get; private set; } = string.Empty;

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1)
            {
                Assert.Contains(request.Tools, tool => tool.Name == "jira");
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "jira",
                        "{\"kind\":\"read\",\"issue\":\"https://example.atlassian.net/browse/APP-123\"}"),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            ObservedToolResult = Assert.Single(request.Messages, message =>
                message.Role == ModelMessageRole.Tool && message.ToolName == "jira").GetModelVisibleContent();
            Assert.Contains(canary, ObservedToolResult, StringComparison.Ordinal);
            FinalResponse = $"Ticket description: {canary}";
            yield return new ModelChunk { Text = FinalResponse, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class JiraHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        internal Uri? LastUri { get; private set; }

        internal string? Authorization { get; private set; }

        internal string BodyText { get; init; } = "First paragraph";

        internal string SummaryText { get; init; } = "Issue summary";

        internal bool DescriptionIsNull { get; init; }

        internal HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

        internal string FailureBody { get; init; } = string.Empty;

        internal DateTimeOffset? RetryAfter { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            LastUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.Parameter;
            if (StatusCode != HttpStatusCode.OK)
            {
                var response = new HttpResponseMessage(StatusCode)
                {
                    Content = new StringContent(FailureBody, Encoding.UTF8, "application/json"),
                };
                if (RetryAfter is { } retryAfter)
                {
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
                }

                return Task.FromResult(response);
            }

            object? description = DescriptionIsNull
                ? null
                : new
                {
                    type = "doc",
                    version = 1,
                    content = new[]
                    {
                        new
                        {
                            type = "paragraph",
                            content = new[] { new { type = "text", text = BodyText } },
                        },
                    },
                };
            var body = JsonSerializer.Serialize(new
            {
                id = "10001",
                key = "APP-124",
                fields = new
                {
                    summary = SummaryText,
                    updated = "2026-09-18T12:00:00.000+0000",
                    description,
                },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
