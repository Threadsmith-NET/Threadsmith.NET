namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks direct artifact writes through the real tool policy and conversation loop.</summary>
public static class WriteFileTests
{
    /// <summary>Default writes preserve exact UTF-8 bytes and create missing output subfolders.</summary>
    [Fact]
    public static async Task DefaultFolder_PreservesContentAndReportsActivity()
    {
        using var fixture = new Fixture();
        await using var events = new DomainEventStream();
        var observed = new ConcurrentBag<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var tool = fixture.Tool();
        var pipeline = CreatePipeline(events, tool);
        const string content = "# Report\r\n\r\nExact 界 e\u0301 text\n";

        var result = await InvokeAsync(pipeline, fixture.Context, new WriteFileInput(".inbox/nested/report.md", content));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Encoding.UTF8.GetBytes(content), await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, ".inbox/nested/report.md")));
        Assert.Contains(observed.OfType<ToolInvocationStarted>(), item => item.ActivityDetail == ".inbox/nested/report.md");
        Assert.DoesNotContain("Exact", result.ResultJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Repository, ".inbox/nested"), "*.tmp"));
    }

    /// <summary>Traversal and similarly prefixed siblings cannot escape the configured folder.</summary>
    [Theory]
    [InlineData("report.md")]
    [InlineData(".inbox-other/report.md")]
    [InlineData(".inbox/../report.md")]
    [InlineData("../outside/report.md")]
    public static async Task OutsideFolder_IsDeniedBeforeWriting(string path)
    {
        using var fixture = new Fixture();
        await using var events = new DomainEventStream();

        var result = await InvokeAsync(CreatePipeline(events, fixture.Tool()), fixture.Context, new WriteFileInput(path, "denied"));

        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    /// <summary>A configured absolute output folder is usable without expanding ordinary read access.</summary>
    [Fact]
    public static async Task AbsoluteAllowlist_IsSeparateFromReadRoots()
    {
        using var fixture = new Fixture();
        var folder = Path.Combine(fixture.Root, "external");
        var tool = fixture.Tool(JsonConfig(JsonSerializer.Serialize(new { tools = new { writeFile = new { allowedFolders = new[] { folder } } } })));
        var destination = Path.Combine(folder, "data.json");
        await using var events = new DomainEventStream();

        var result = await InvokeAsync(CreatePipeline(events, tool), fixture.Context, new WriteFileInput(destination, "{\"value\":42}"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("{\"value\":42}", await File.ReadAllTextAsync(destination));
        Assert.False(new DefaultPolicyEngine().Evaluate(new ReadFileTool(TestPromptLoader.Instance, new Threadsmith.Telemetry.SecretOutputSanitizer()), new ReadFileInput { Path = destination }, fixture.Context.Invocation).IsAllowed);
    }

    /// <summary>An active scratchpad grants only its bounded destination at the lowest trust level.</summary>
    [Fact]
    public static async Task Scratchpad_WriteIsApprovalFreeAtUntrustedInspection()
    {
        using var fixture = new Fixture();
        var scratchpad = Directory.CreateDirectory(Path.Combine(fixture.Root, "scratchpad")).FullName;
        var invocation = fixture.Context.Invocation with
        {
            TrustLevel = RepositoryTrustLevel.UntrustedInspection,
            Scratchpad = new ScratchpadSessionCapability
            {
                IsActive = true,
                DisabledReason = ScratchpadDisabledReason.None,
                RootPath = scratchpad,
                ModelPath = scratchpad,
            },
        };
        var context = fixture.Context with { Invocation = invocation };
        await using var events = new DomainEventStream();

        var allowed = await InvokeAsync(
            CreatePipeline(events, fixture.Tool()),
            context,
            new WriteFileInput(Path.Combine(scratchpad, "result.log"), "iteration"));
        var ordinary = await InvokeAsync(
            CreatePipeline(events, fixture.Tool()),
            context,
            new WriteFileInput(".inbox/report.md", "denied"));

        Assert.True(allowed.Succeeded, allowed.Error);
        Assert.Equal("iteration", await File.ReadAllTextAsync(Path.Combine(scratchpad, "result.log")));
        Assert.Equal(ToolErrorClassification.PolicyDenied, ordinary.ErrorClassification);
    }

    /// <summary>Scratchpad authority does not permit sibling paths with the same textual prefix.</summary>
    [Fact]
    public static async Task Scratchpad_PrefixSiblingIsDenied()
    {
        using var fixture = new Fixture();
        var scratchpad = Directory.CreateDirectory(Path.Combine(fixture.Root, "scratchpad")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(fixture.Root, "scratchpad-other")).FullName;
        var context = fixture.Context with
        {
            Invocation = fixture.Context.Invocation with
            {
                TrustLevel = RepositoryTrustLevel.UntrustedInspection,
                Scratchpad = new ScratchpadSessionCapability
                {
                    IsActive = true,
                    DisabledReason = ScratchpadDisabledReason.None,
                    RootPath = scratchpad,
                    ModelPath = scratchpad,
                },
            },
        };
        await using var events = new DomainEventStream();

        var result = await InvokeAsync(
            CreatePipeline(events, fixture.Tool()),
            context,
            new WriteFileInput(Path.Combine(sibling, "result.log"), "denied"));

        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Empty(Directory.GetFiles(sibling));
    }

    /// <summary>The built-in readers share only the active scratchpad root outside the repository.</summary>
    [Fact]
    public static async Task Scratchpad_ReadAndSearchUseExternalRoot()
    {
        using var fixture = new Fixture();
        var scratchpad = Directory.CreateDirectory(Path.Combine(fixture.Root, "scratchpad")).FullName;
        var file = Path.Combine(scratchpad, "iteration.log");
        await File.WriteAllTextAsync(file, "needle in transient output");
        var invocation = fixture.Context.Invocation with
        {
            TrustLevel = RepositoryTrustLevel.UntrustedInspection,
            Scratchpad = new ScratchpadSessionCapability
            {
                IsActive = true,
                DisabledReason = ScratchpadDisabledReason.None,
                RootPath = scratchpad,
                ModelPath = scratchpad,
            },
        };
        var execution = fixture.Context with { Invocation = invocation };
        var read = new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer());
        var search = new SearchTextTool(TestPromptLoader.Instance);
        var policy = new DefaultPolicyEngine();

        Assert.True(policy.Evaluate(read, new ReadFileInput { Path = file }, invocation).IsAllowed);
        Assert.True(policy.Evaluate(search, new SearchTextInput { Query = "needle", Path = scratchpad }, invocation).IsAllowed);
        var readResult = await read.ExecuteAsync(new ReadFileInput { Path = file }, execution);
        var searchResult = await search.ExecuteAsync(new SearchTextInput { Query = "needle", Path = scratchpad }, execution);

        Assert.Contains("needle", readResult.Value.Lines.Single(), StringComparison.Ordinal);
        Assert.Single(searchResult.Value.Matches);
        Assert.Equal("iteration.log", searchResult.Value.Matches[0].Path);
    }

    /// <summary>Repository listing neither targets nor reveals an active in-repository scratchpad.</summary>
    [Fact]
    public static async Task Scratchpad_ListFilesCannotAccessOrRevealContents()
    {
        using var fixture = new Fixture();
        var scratchpad = Directory.CreateDirectory(Path.Combine(fixture.Repository, ".scratch")).FullName;
        await File.WriteAllTextAsync(Path.Combine(scratchpad, "private.log"), "transient");
        var invocation = fixture.Context.Invocation with
        {
            Scratchpad = new ScratchpadSessionCapability
            {
                IsActive = true,
                DisabledReason = ScratchpadDisabledReason.None,
                RootPath = scratchpad,
                ModelPath = ".scratch",
            },
        };
        var execution = fixture.Context with { Invocation = invocation };
        var list = new ListFilesTool(TestPromptLoader.Instance);
        var policy = new DefaultPolicyEngine();

        Assert.False(policy.Evaluate(list, new ListFilesInput { Path = ".scratch" }, invocation).IsAllowed);
        var repositoryListing = await list.ExecuteAsync(new ListFilesInput { Path = "." }, execution);

        Assert.DoesNotContain(
            repositoryListing.Value.Files,
            file => file.Path.StartsWith(".scratch/", StringComparison.Ordinal));
    }

    /// <summary>Replacing an active scratchpad root with a link revokes both built-in read paths.</summary>
    [Fact]
    public static async Task Scratchpad_ReplacedRootIsRejectedByReadAndSearch()
    {
        using var fixture = new Fixture();
        var scratchpad = Directory.CreateDirectory(Path.Combine(fixture.Repository, ".scratch")).FullName;
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "external-read")).FullName;
        await File.WriteAllTextAsync(Path.Combine(external, "private.log"), "needle");
        Directory.Delete(scratchpad);
        try
        {
            Directory.CreateSymbolicLink(scratchpad, external);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic-link creation is unavailable: {exception.GetType().Name}.");
            return;
        }

        var invocation = fixture.Context.Invocation with
        {
            Scratchpad = new ScratchpadSessionCapability
            {
                IsActive = true,
                DisabledReason = ScratchpadDisabledReason.None,
                RootPath = scratchpad,
                ModelPath = ".scratch",
            },
        };
        var execution = fixture.Context with { Invocation = invocation };
        var read = new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer());
        var search = new SearchTextTool(TestPromptLoader.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            read.ExecuteAsync(new ReadFileInput { Path = Path.Combine(scratchpad, "private.log") }, execution));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            search.ExecuteAsync(new SearchTextInput { Query = "needle", Path = scratchpad }, execution));
    }

    /// <summary>Managed search ignores repository Git inventory for an ignored in-repository scratchpad.</summary>
    [Fact]
    public static async Task Scratchpad_ManagedSearchEnumeratesIgnoredInRepositoryFiles()
    {
        using var fixture = new Fixture();
        var scratchpad = Directory.CreateDirectory(Path.Combine(fixture.Repository, ".scratch")).FullName;
        await File.WriteAllTextAsync(Path.Combine(scratchpad, "iteration.log"), "needle in ignored output");
        var invocation = fixture.Context.Invocation with
        {
            Scratchpad = new ScratchpadSessionCapability
            {
                IsActive = true,
                DisabledReason = ScratchpadDisabledReason.None,
                RootPath = scratchpad,
                ModelPath = ".scratch",
            },
        };
        var search = new SearchTextTool(
            TestPromptLoader.Instance,
            processManager: new MissingRipgrepEmptyGitProcessManager());

        var result = await search.ExecuteAsync(
            new SearchTextInput { Query = "needle", Path = ".scratch" },
            fixture.Context with { Invocation = invocation });

        Assert.Single(result.Value.Matches);
        Assert.Equal("iteration.log", result.Value.Matches[0].Path);
    }

    /// <summary>Installed ripgrep searches an explicitly selected scratchpad despite repository ignore rules.</summary>
    [Fact]
    public static async Task Scratchpad_RipgrepSearchFindsIgnoredInRepositoryFiles()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("rg", "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (probe is null)
            {
                Assert.Skip("ripgrep is not available on PATH.");
                return;
            }

            await probe.WaitForExitAsync(TestContext.Current.CancellationToken);
            if (probe.ExitCode != 0)
            {
                Assert.Skip("ripgrep is not available on PATH.");
                return;
            }
        }
        catch (Win32Exception)
        {
            Assert.Skip("ripgrep is not available on PATH.");
            return;
        }

        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Repository, ".git"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Repository, ".gitignore"),
            ".scratch/\n",
            TestContext.Current.CancellationToken);
        var scratchpad = Directory.CreateDirectory(Path.Combine(fixture.Repository, ".scratch")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(scratchpad, "iteration.log"),
            "needle in ignored output",
            TestContext.Current.CancellationToken);
        var invocation = fixture.Context.Invocation with
        {
            Scratchpad = new ScratchpadSessionCapability
            {
                IsActive = true,
                DisabledReason = ScratchpadDisabledReason.None,
                RootPath = scratchpad,
                ModelPath = ".scratch",
            },
        };
        var processManager = new ProcessManager(
            new SecretOutputSanitizer(),
            NullLogger<ProcessManager>.Instance);
        var search = new SearchTextTool(TestPromptLoader.Instance, processManager: processManager);

        var result = await search.ExecuteAsync(
            new SearchTextInput { Query = "needle", Path = ".scratch" },
            fixture.Context with { Invocation = invocation },
            TestContext.Current.CancellationToken);

        Assert.Null(result.Value.Warning);
        var match = Assert.Single(result.Value.Matches);
        Assert.Equal("iteration.log", match.Path);
    }

    /// <summary>Missing settings default to .inbox; each higher-priority array replaces the entire earlier list.</summary>
    [Theory]
    [InlineData("{}", ".inbox")]
    [InlineData("{\"tools\":{\"writeFile\":{\"allowedFolders\":[\"reports\"]}}}", "reports")]
    [InlineData("{\"tools\":{\"writeFile\":{\"allowedFolders\":[]}}}", null)]
    [InlineData("{\"tools\":{\"writeFile\":{\"allowedFolders\":null}}}", null)]
    public static void Configuration_ReplacesListsAndHonorsEmpty(string upperJson, string? expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(upperJson));
        var builder = new ConfigurationBuilder();
        if (upperJson != "{}")
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WriteFileConfiguration.AllowedFoldersKey + ":0"] = "machine-one",
                [WriteFileConfiguration.AllowedFoldersKey + ":1"] = "machine-two",
            });
        }

        var configuration = builder.AddJsonStream(stream).Build();

        Assert.Equal(expected is null ? [] : [expected], WriteFileConfiguration.LoadAllowedFolders(configuration));
    }

    /// <summary>Malformed configuration cannot silently broaden authority or restore default access.</summary>
    [Theory]
    [InlineData("\"*\"")]
    [InlineData("[\"\"]")]
    [InlineData("[\"../outside\"]")]
    [InlineData("[\".inbox/*\"]")]
    [InlineData("{\"folder\":\".inbox\"}")]
    public static void Configuration_RejectsInvalidFolders(string foldersJson)
    {
        var configuration = JsonConfig("{\"tools\":{\"writeFile\":{\"allowedFolders\":" + foldersJson + "}}}");

        Assert.Throws<InvalidOperationException>(() => WriteFileConfiguration.LoadAllowedFolders(configuration));
    }

    /// <summary>Switching repositories removes the old repository's absolute grant and restores the fallback.</summary>
    [Fact]
    public static async Task RepositoryBinding_DoesNotLeakOverrides()
    {
        using var fixture = new Fixture();
        var external = Path.Combine(fixture.Root, "external");
        var repositoryConfig = JsonConfig(JsonSerializer.Serialize(new { tools = new { writeFile = new { allowedFolders = new[] { external } } } }));
        var configuration = new WriteFileConfiguration(repositoryConfig, JsonConfig("{}"), fixture.Repository);
        var tool = new WriteFileTool(configuration, new ConversationStore(), TestPromptLoader.Instance);
        var manager = new ToolStateManager([tool.Definition], repositoryConfig, Path.Combine(fixture.Repository, ".threadsmith/config.json"), writeFileConfiguration: configuration);
        var nextRepository = Directory.CreateDirectory(Path.Combine(fixture.Root, "next")).FullName;
        await using var events = new DomainEventStream();
        var pipeline = CreatePipeline(events, tool);

        await manager.BindRepositoryAsync(nextRepository);
        var nextContext = fixture.Context with { Invocation = fixture.Context.Invocation with { RepositoryPath = nextRepository } };
        var denied = await InvokeAsync(pipeline, nextContext, new WriteFileInput(Path.Combine(external, "report.md"), "denied"));
        var stale = await InvokeAsync(pipeline, fixture.Context, new WriteFileInput(Path.Combine(external, "stale.md"), "denied"));
        var allowed = await InvokeAsync(pipeline, nextContext, new WriteFileInput(".inbox/report.md", "next repository"));

        Assert.Equal(ToolErrorClassification.PolicyDenied, denied.ErrorClassification);
        Assert.Equal(ToolErrorClassification.PolicyDenied, stale.ErrorClassification);
        Assert.True(allowed.Succeeded, allowed.Error);
        Assert.False(Directory.Exists(external));
    }

    /// <summary>Existing files are preserved unless the call explicitly requests replacement.</summary>
    [Fact]
    public static async Task Overwrite_IsExplicitAndCancelledCallsPreserveExistingContent()
    {
        using var fixture = new Fixture();
        var tool = fixture.Tool();
        await tool.ExecuteAsync(new WriteFileInput(".inbox/report.txt", "first"), fixture.Context);

        await Assert.ThrowsAsync<IOException>(() => tool.ExecuteAsync(new WriteFileInput(".inbox/report.txt", "second"), fixture.Context));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(new WriteFileInput(".inbox/report.txt", "cancelled", Overwrite: true), fixture.Context, new CancellationToken(true)));
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, ".inbox/report.txt")));
        await tool.ExecuteAsync(new WriteFileInput(".inbox/report.txt", "second", Overwrite: true), fixture.Context);

        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, ".inbox/report.txt")));
    }

    /// <summary>Source files, metadata, and instruction files cannot use the report writer.</summary>
    [Theory]
    [InlineData(".inbox/Program.cs", ToolErrorClassification.InvalidArguments)]
    [InlineData(".inbox/AGENTS.md", ToolErrorClassification.PolicyDenied)]
    [InlineData(".inbox/.git/config.json", ToolErrorClassification.PolicyDenied)]
    [InlineData(".inbox/.threadsmith/config.json", ToolErrorClassification.PolicyDenied)]
    [InlineData(".inbox/private/report.md", ToolErrorClassification.PolicyDenied)]
    public static async Task ProtectedTargets_AreDenied(string path, ToolErrorClassification expected)
    {
        using var fixture = new Fixture();
        await using var events = new DomainEventStream();
        var context = fixture.Context with { Invocation = fixture.Context.Invocation with { ProhibitedPaths = [".inbox/private/**"] } };

        var result = await InvokeAsync(CreatePipeline(events, fixture.Tool()), context, new WriteFileInput(path, "denied"));

        Assert.Equal(expected, result.ErrorClassification);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    /// <summary>Junctions and symlinks cannot redirect writes outside the allowlist.</summary>
    [Fact]
    public static async Task LinkedOutputFolder_IsDenied()
    {
        using var fixture = new Fixture();
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "external")).FullName;
        var link = Path.Combine(fixture.Repository, ".inbox");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, external })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create test junction.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, external);
        }

        try
        {
            await using var events = new DomainEventStream();
            var result = await InvokeAsync(CreatePipeline(events, fixture.Tool()), fixture.Context, new WriteFileInput(".inbox/report.md", "denied"));
            Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
            Assert.Empty(Directory.GetFiles(external));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>The host copies only the latest prior assistant answer from this session, without rewriting it.</summary>
    [Fact]
    public static async Task LastResponse_PreservesExactBodyAndSessionScope()
    {
        using var fixture = new Fixture();
        const string report = "# Existing report\r\n\r\nKeep **every** word. 界\n";
        var prior = Message(fixture.Context.SessionId, 2, report);
        var store = new ConversationStore
        {
            Messages = [Message(fixture.Context.SessionId, 1, "older"), prior, Message(SessionId.New(), 99, "another session"), Message(fixture.Context.SessionId, 3, "current commentary") with { RunId = fixture.Context.RunId }],
        };

        var result = await fixture.Tool(store: store).ExecuteAsync(new WriteFileInput(".inbox/report.md", UseLastResponse: true), fixture.Context);

        Assert.Equal(report, await File.ReadAllTextAsync(result.Value.Path));
        Assert.Equal(prior.Id, result.Value.SourceMessageId);
        Assert.Equal(fixture.Context.SessionId, store.RequestedSession);
    }

    /// <summary>A missing latest body fails visibly instead of saving an older answer or an empty file.</summary>
    [Fact]
    public static async Task LastResponse_MissingBodyDoesNotFallBack()
    {
        using var fixture = new Fixture();
        var store = new ConversationStore { Messages = [Message(fixture.Context.SessionId, 1, "older"), Message(fixture.Context.SessionId, 2, null)] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Tool(store: store).ExecuteAsync(new WriteFileInput(".inbox/report.md", UseLastResponse: true), fixture.Context));

        Assert.False(Directory.Exists(Path.Combine(fixture.Repository, ".inbox")));
    }

    /// <summary>A normal conversation can save a report without entering any plan or mutation phase.</summary>
    [Fact]
    public static async Task Conversation_WritesWithoutMutationWorkflow()
    {
        using var fixture = new Fixture();
        await using var events = new DomainEventStream();
        var observed = new ConcurrentBag<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var pipeline = CreatePipeline(events, fixture.Tool());
        var model = new FakeModelProvider(new ScriptedSession
        {
            Turns = [new ScriptedTurn { ToolName = "write_file", ArgumentsJson = "{\"path\":\".inbox/report.md\",\"content\":\"Saved report\"}" }, new ScriptedTurn { Text = "Saved." }],
        });
        var application = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(fixture.Context.Invocation),
            toolRegistry: pipeline.Registry,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        // Bound the conversation itself; fixture setup is not part of a latency assertion.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var session = await application.HandleAsync(new CreateSessionCommand("report"), timeout.Token);

        var run = await application.HandleAsync(new SubmitRequestCommand(session, "Save a report to .inbox"), timeout.Token);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(run), timeout.Token));

        Assert.Equal("Saved report", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, ".inbox/report.md")));
        Assert.DoesNotContain(observed, item => item is PlanProposed or MutationSetProposed);
        Assert.Contains(observed.OfType<ToolInvocationCompleted>(), item => item.Succeeded);
    }

    private static IConfigurationRoot JsonConfig(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    private static ToolInvocationPipeline CreatePipeline(DomainEventStream events, ITool tool) => new(
        new ToolRegistry([tool]), new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);

    private static Task<ToolInvocationResult> InvokeAsync(ToolInvocationPipeline pipeline, ToolExecutionContext context, WriteFileInput input) => pipeline.InvokeAsync(new ToolInvocationRequest
    {
        SessionId = context.SessionId,
        RunId = context.RunId,
        ToolId = "write_file",
        ArgumentsJson = JsonSerializer.Serialize(input),
        Context = context.Invocation,
    });

    private static ConversationMessage Message(SessionId session, long sequence, string? text) => new()
    {
        Id = ConversationMessageId.New(), SessionId = session, RunId = RunId.New(), Sequence = sequence,
        Role = ConversationRole.Assistant, Content = text, ContentHash = "test", EstimatedTokens = 1, OccurredAt = DateTimeOffset.UtcNow,
    };

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(PhysicalTemporaryPath(), "threadsmith-write-tests-" + Guid.NewGuid().ToString("N"))).FullName;
            Repository = Directory.CreateDirectory(Path.Combine(Root, "repo")).FullName;
            Context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), new ToolInvocationContext
            {
                RepositoryPath = Repository, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model",
            });
        }

        public string Root { get; }

        public string Repository { get; }

        public ToolExecutionContext Context { get; }

        public WriteFileTool Tool(IConfiguration? configuration = null, ConversationStore? store = null) => new(
            new WriteFileConfiguration(configuration ?? JsonConfig("{}"), JsonConfig("{}"), Repository), store ?? new ConversationStore(), TestPromptLoader.Instance);

        public void Dispose()
        {
            if (!Path.GetFileName(Root).StartsWith("threadsmith-write-tests-", StringComparison.Ordinal)
                || !string.Equals(Path.GetDirectoryName(Root), PhysicalTemporaryPath(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected fixture cleanup root.");
            }

            Directory.Delete(Root, recursive: true);
        }

        private static string PhysicalTemporaryPath()
        {
            var path = Path.GetFullPath(Path.GetTempPath());
            var resolved = Path.GetPathRoot(path)!;
            foreach (var segment in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = new DirectoryInfo(Path.Combine(resolved, segment));
                resolved = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
            }

            return Path.TrimEndingDirectorySeparator(resolved);
        }
    }

    private sealed class MissingRipgrepEmptyGitProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FileName.Equals("rg", StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException("ripgrep unavailable");
            }

            return Task.FromResult(new ProcessExecutionResult(
                1,
                0,
                string.Empty,
                string.Empty,
                false,
                false,
                false,
                TimeSpan.Zero));
        }
    }

    private sealed class ConversationStore : IConversationStore
    {
        public IReadOnlyList<ConversationMessage> Messages { get; init; } = [];

        public SessionId? RequestedSession { get; private set; }

        public Task<ConversationStateSnapshot> GetSnapshotAsync(SessionId sessionId, bool includeBodies = true, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedSession = sessionId;
            return Task.FromResult(new ConversationStateSnapshot { SessionId = sessionId, Messages = Messages });
        }

        public Task<ConversationMessage> ArchiveMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetModeAsync(SessionId sessionId, ConversationContextMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReplaceSummaryAsync(SessionId sessionId, IReadOnlyList<ConversationMemoryItem> items, ConversationSummarySnapshot snapshot, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateMemoryAsync(ConversationMemoryItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> RemoveMessageBodiesOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
