namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Tools;
using Threadsmith.Tui;
using Xunit;

/// <summary>Verifies the plan-08 tool runtime, policy, persistence, UI, and process lifecycle.</summary>
public static class ToolRuntimeTests
{
    /// <summary>A model-requested tool produces attributable durable output and visible activity.</summary>
    [Fact]
    public static async Task ModelToolRequest_IsTypedPersistedAndVisible()
    {
        var repository = CreateTemporaryDirectory();
        var databasePath = Path.Combine(repository, "events.db");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "sample.txt"), "sample");
            await using var events = new DomainEventStream();
            var store = new SqliteEventStore($"Data Source={databasePath};Pooling=False");
            await store.InitializeAsync();
            var projections = new InMemoryProjectionStore();
            await using var persistenceSubscription = events.Subscribe(store.AppendAsync);
            await using var projectionSubscription = events.Subscribe(projections.ApplyAsync);
            var pipeline = CreatePipeline(events, [new ListFilesTool()]);
            var model = new FakeModelProvider(new ScriptedSession
            {
                Turns =
                [
                    new ScriptedTurn
                    {
                        ToolName = "list_files",
                        ArgumentsJson = "{\"path\":\".\",\"maximumEntries\":10}",
                    },
                ],
            });
            var application = new SessionApplication(
                events,
                model,
                CreateBudget(),
                new TestSanitizer(),
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(CreateContext(repository)));
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await application.HandleAsync(new CreateSessionCommand("tools"));
            var runId = await application.HandleAsync(
                new SubmitRequestCommand(sessionId, "inspect"));

            Assert.True(await application.HandleAsync(new WaitForRunCommand(runId)));
            var stored = await store.ReadAsync(sessionId);
            var started = Assert.IsType<ToolInvocationStarted>(
                Assert.Single(stored, item => item is ToolInvocationStarted));
            var completed = Assert.IsType<ToolInvocationCompleted>(
                Assert.Single(stored, item => item is ToolInvocationCompleted));
            Assert.Equal("model", started.RequestedBy);
            Assert.True(completed.Succeeded);
            Assert.Contains("sample.txt", completed.ResultJson, StringComparison.Ordinal);

            var snapshot = await new TuiPresenter(dispatcher, projections).RenderAsync(sessionId);
            Assert.Contains("Tool list_files (model): succeeded", snapshot.Workspace, StringComparison.Ordinal);
            Assert.Contains("sample.txt", snapshot.Workspace, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Invalid input and escaped paths are rejected before a tool executes.</summary>
    [Fact]
    public static async Task Pipeline_InvalidArgumentsAndEscapedPaths_DoNotExecute()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var secretDirectory = Path.Combine(repository, ".threadsmith", "secrets");
            Directory.CreateDirectory(secretDirectory);
            await File.WriteAllTextAsync(Path.Combine(secretDirectory, "key.txt"), "hidden-marker");
            await File.WriteAllTextAsync(Path.Combine(repository, ".env"), "hidden-marker");
            await File.WriteAllTextAsync(Path.Combine(repository, "visible.txt"), "visible-marker");
            await using var events = new DomainEventStream();
            var countingTool = new CountingTool();
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                1,
                0,
                string.Empty,
                string.Empty,
                false,
                false,
                false,
                TimeSpan.Zero));
            ITool[] tools =
            [
                countingTool,
                new ReadFileTool(),
                new ListFilesTool(),
                new SearchTextTool(),
                new RunProcessTool(processManager),
            ];
            var pipeline = CreatePipeline(events, tools);
            var invalid = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "counting",
                ArgumentsJson = "{\"value\":\"\"}",
                Context = CreateContext(repository),
            });
            var escaped = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "read_file",
                ArgumentsJson = "{\"path\":\"../outside.txt\"}",
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                },
            });
            var approvalDenied = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "counting",
                ArgumentsJson = "{\"value\":\"valid\"}",
                Context = CreateContext(repository) with
                {
                    RequireApprovalToolIds = ["counting"],
                },
            });
            var recursiveContext = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
                ProhibitedPaths = [".threadsmith/secrets/", "**/*.env"],
            };
            var listed = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "list_files",
                ArgumentsJson = "{\"path\":\".\"}",
                Context = recursiveContext,
            });
            var searched = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "search",
                ArgumentsJson = "{\"query\":\"hidden-marker\"}",
                Context = recursiveContext,
            });
            var qualifiedExecutable = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "run_process",
                ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput
                {
                    Command = "dotnet --version",
                }),
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    AllowedExecutables = ["dotnet"],
                },
            });

            Assert.Equal(ToolErrorClassification.InvalidArguments, invalid.ErrorClassification);
            Assert.Equal(0, countingTool.ExecutionCount);
            Assert.Equal(ToolErrorClassification.PolicyDenied, escaped.ErrorClassification);
            Assert.Equal(ToolErrorClassification.ApprovalDenied, approvalDenied.ErrorClassification);
            Assert.DoesNotContain("key.txt", listed.ResultJson, StringComparison.Ordinal);
            Assert.DoesNotContain(".env", listed.ResultJson, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden-marker", searched.ResultJson, StringComparison.Ordinal);
            Assert.Equal(
                ToolErrorClassification.PolicyDenied,
                qualifiedExecutable.ErrorClassification);
            Assert.Equal(0, processManager.ExecutionCount);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Unix path containment does not ignore case.</summary>
    [Fact]
    public static async Task Policy_LinuxPathContainment_IsCaseSensitive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var parent = CreateTemporaryDirectory();
        var repository = Path.Combine(parent, "Repo");
        var outside = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(outside);
        try
        {
            var outsideFile = Path.Combine(outside, "outside.txt");
            await File.WriteAllTextAsync(outsideFile, "outside");
            await using var events = new DomainEventStream();
            var pipeline = CreatePipeline(events, [new ReadFileTool()]);

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "read_file",
                ArgumentsJson = JsonSerializer.Serialize(new ReadFileInput { Path = outsideFile }),
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                },
            });

            Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    /// <summary>A process-manager timeout becomes a failed tool invocation.</summary>
    [Fact]
    public static async Task ProcessTool_ManagerTimeout_IsNormalizedAsTimeout()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var manager = new StubProcessManager(new ProcessExecutionResult(
                1,
                null,
                string.Empty,
                string.Empty,
                false,
                false,
                true,
                TimeSpan.FromSeconds(1)));
            var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
            var pipeline = new ToolInvocationPipeline(
                new ToolRegistry([new RunProcessTool(
                    manager,
                    allowedExecutables: [shell],
                    shellExecutable: shell)]),
                new DefaultPolicyEngine(),
                new AllowApprovalPolicy(),
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget());

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "run_process",
                ArgumentsJson = "{\"command\":\"dotnet --version\",\"timeoutSeconds\":1}",
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    AllowedExecutables = [shell],
                },
            });

            Assert.False(result.Succeeded);
            Assert.Equal(ToolErrorClassification.Timeout, result.ErrorClassification);
            Assert.Equal(1, manager.ExecutionCount);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Exhausted budgets reject before approval without consuming additional usage.</summary>
    [Fact]
    public static async Task Pipeline_ExhaustedBudget_DoesNotRequestApproval()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var approval = new CountingApprovalPolicy();
            var tool = new CountingTool();
            var pipeline = new ToolInvocationPipeline(
                new ToolRegistry([tool]),
                new DefaultPolicyEngine(),
                approval,
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                new ExecutionBudget(new BudgetDimensions(100, 0, TimeSpan.FromMinutes(1))));

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "counting",
                ArgumentsJson = "{\"value\":\"valid\"}",
                Context = CreateContext(repository) with
                {
                    RequireApprovalToolIds = ["counting"],
                },
            });

            Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
            Assert.Equal(0, approval.RequestCount);
            Assert.Equal(0, tool.ExecutionCount);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Approval cancellation closes both the approval and invocation event lifecycles.</summary>
    [Fact]
    public static async Task Pipeline_ApprovalCancellation_PublishesDenialAndCompletion()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var observed = new List<IDomainEvent>();
            await using var subscription = events.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });
            var pipeline = new ToolInvocationPipeline(
                new ToolRegistry([new CountingTool()]),
                new DefaultPolicyEngine(),
                new CancellingApprovalPolicy(),
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.InvokeAsync(
                new ToolInvocationRequest
                {
                    SessionId = SessionId.New(),
                    RunId = RunId.New(),
                    ToolId = "counting",
                    ArgumentsJson = "{\"value\":\"valid\"}",
                    Context = CreateContext(repository) with
                    {
                        RequireApprovalToolIds = ["counting"],
                    },
                }));

            Assert.Contains(observed, item => item is ToolInvocationStarted);
            Assert.Contains(observed, item => item is ApprovalRequested requested
                && requested.Kind == ApprovalRequestKind.ToolInvocation
                && requested.SchemaVersion == 2);
            Assert.Contains(observed, item => item is ApprovalDenied);
            Assert.Contains(observed, item => item is ToolInvocationCompleted);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Logical secret references resolve only through the final secrets scope.</summary>
    [Fact]
    public static async Task SecretStore_ResolvesLogicalReferencesOnly()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["secrets:models:test"] = "credential",
            })
            .Build();
        var store = new ConfigurationSecretStore(configuration);

        Assert.Equal("credential", await store.GetAsync("secrets:models:test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetAsync("model:profiles:0:apiKey"));
    }

    /// <summary>File, search, and Git inspection tools return bounded results with provenance.</summary>
    [Fact]
    public static async Task BuiltInInspectionTools_ReturnAttributableResults()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repository, "visible.txt"),
                "first line\nneedle line");
            await using var events = new DomainEventStream();
            var manager = new ProcessManager(
                new TestSanitizer(),
                NullLogger<ProcessManager>.Instance);
            var pipeline = CreatePipeline(
                events,
                [new ReadFileTool(), new SearchTextTool(), new GitStatusTool(manager)]);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };
            var read = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "read_file",
                ArgumentsJson = "{\"path\":\"visible.txt\",\"maximumLines\":2}",
                Context = context,
            });
            var search = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "search",
                ArgumentsJson = "{\"query\":\"needle\",\"glob\":\"*.txt\"}",
                Context = context,
            });
            var sourceRepository = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var git = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "git_status",
                ArgumentsJson = "{}",
                Context = CreateContext(sourceRepository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                    AllowedExecutables = ["git"],
                },
            });

            Assert.True(read.Succeeded);
            Assert.Contains("needle line", read.ResultJson, StringComparison.Ordinal);
            Assert.Contains(read.Sources, source => source.Kind == "file");
            Assert.True(search.Succeeded);
            Assert.Contains(search.Sources, source => source.Range == "L2");
            Assert.True(git.Succeeded, git.Error);
            Assert.Contains(git.Sources, source => source.Kind == "git");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Literal repository search uses bounded ripgrep output when the host executable is available.</summary>
    [Fact]
    public static async Task SearchTextTool_LiteralSearch_UsesRipgrepFastPath()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                42,
                0,
                "./.hidden/endpoint.cs\0" + "12:8:route needle\n./second.cs\0" + "4:2:needle\n",
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(10)));
            var bundledRipgrep = Path.Combine(repository, "bundled tools", "rg.exe");
            var tool = new SearchTextTool(
                processManager: processManager,
                ripgrepExecutable: bundledRipgrep);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle", MaximumMatches = 1 },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal(".hidden/endpoint.cs", match.Path);
            Assert.Equal(12, match.Line);
            Assert.Equal(8, match.Column);
            Assert.True(result.Value.IsTruncated);
            Assert.Null(result.Value.Warning);
            var request = Assert.IsType<ProcessExecutionRequest>(processManager.LastRequest);
            Assert.Equal(bundledRipgrep, request.FileName);
            Assert.Equal(ProcessRequestOrigin.Host, request.Origin);
            Assert.Contains("--fixed-strings", request.Arguments);
            Assert.Contains("--ignore-case", request.Arguments);
            Assert.Contains("--max-filesize=1048576", request.Arguments);
            Assert.Contains("--iglob=!**/*.db", request.Arguments);
            Assert.Contains("--iglob=!**/*.sqlite", request.Arguments);
            Assert.Contains("--iglob=!**/*.sqlite3", request.Arguments);
            string[] excludedDirectoryGlobs =
            [
                "--iglob=!**/.codegraph/**",
                "--iglob=!**/.git/**",
                "--iglob=!**/.idea/**",
                "--iglob=!**/.vs/**",
                "--iglob=!**/artifacts/**",
                "--iglob=!**/bin/**",
                "--iglob=!**/node_modules/**",
                "--iglob=!**/obj/**",
                "--iglob=!**/TestResults/**",
            ];
            Assert.All(
                excludedDirectoryGlobs,
                glob => Assert.Contains(glob, request.Arguments));
            Assert.DoesNotContain("--glob=!**/*.sqlite", request.Arguments);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Regex and narrowed-glob searches use ripgrep without literal-mode projection.</summary>
    [Fact]
    public static async Task SearchTextTool_RegexAndGlob_UseRipgrepFastPath()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                42,
                0,
                "src/Retriever.cs\0" + "7:14:internal sealed class Retriever : IRetriever\n",
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(10)));
            var tool = new SearchTextTool(processManager: processManager);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput
                {
                    Query = @"class\s+\w+\s*:\s*IRetriever",
                    UseRegularExpression = true,
                    Glob = "*.cs",
                },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("src/Retriever.cs", match.Path);
            Assert.Null(result.Value.Warning);
            var request = Assert.IsType<ProcessExecutionRequest>(processManager.LastRequest);
            Assert.DoesNotContain("--fixed-strings", request.Arguments);
            Assert.DoesNotContain("--ignore-case", request.Arguments);
            Assert.Contains("--iglob=*.cs", request.Arguments);
            Assert.Contains("--regexp", request.Arguments);
            Assert.Contains(@"class\s+\w+\s*:\s*IRetriever", request.Arguments);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The managed regular-expression fallback preserves case-sensitive matching.</summary>
    [Fact]
    public static async Task SearchTextTool_ManagedRegexSearch_IsCaseSensitive()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repository, "markers.txt"),
                "TODO\ntodo\n");
            var tool = new SearchTextTool();
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput
                {
                    Query = "TODO",
                    UseRegularExpression = true,
                    Glob = "*.txt",
                },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("TODO", match.Text);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Failed ripgrep uses Git's ignore-aware file inventory and reports the fallback.</summary>
    [Fact]
    public static async Task SearchTextTool_RipgrepFailure_UsesGitIgnoreAwareFallbackAndWarns()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(repository, "src")).FullName;
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "visible.txt"), "needle");
            var ignoredDirectory = Directory.CreateDirectory(Path.Combine(repository, "ignored")).FullName;
            await File.WriteAllTextAsync(Path.Combine(ignoredDirectory, "hidden.txt"), "needle");
            await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), "ignored/\n");
            var processManager = new RoutingProcessManager(request =>
                request.FileName.Equals("git", StringComparison.OrdinalIgnoreCase)
                    ? CreateProcessResult("src/visible.txt\0")
                    : CreateProcessResult(string.Empty, exitCode: 2));
            var tool = new SearchTextTool(processManager: processManager);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("src/visible.txt", match.Path);
            Assert.Contains("Ripgrep could not execute", result.Value.Warning, StringComparison.Ordinal);
            var gitRequest = Assert.Single(
                processManager.Requests,
                request => request.FileName.Equals("git", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                ["ls-files", "--cached", "--others", "--exclude-standard", "-z"],
                gitRequest.Arguments.TakeLast(5));
            Assert.Contains("core.fsmonitor=false", gitRequest.Arguments);
            Assert.Equal("0", gitRequest.EnvironmentVariables["GIT_OPTIONAL_LOCKS"]);
            Assert.Equal("0", gitRequest.EnvironmentVariables["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("1", gitRequest.EnvironmentVariables["GIT_CONFIG_NOSYSTEM"]);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>A Git-inventoried symbolic-link leaf cannot expose content outside the repository.</summary>
    [Fact]
    public static async Task SearchTextTool_GitInventorySymlink_IsNotRead()
    {
        var repository = CreateTemporaryDirectory();
        var outsideDirectory = CreateTemporaryDirectory();
        try
        {
            var outsidePath = Path.Combine(outsideDirectory, "outside.txt");
            await File.WriteAllTextAsync(outsidePath, "outside needle");
            var linkPath = Path.Combine(repository, "linked.txt");
            try
            {
                File.CreateSymbolicLink(linkPath, outsidePath);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException
                    or PlatformNotSupportedException
                    or IOException)
            {
                Assert.Skip($"Symbolic-link creation is unavailable: {exception.GetType().Name}.");
            }

            var processManager = new RoutingProcessManager(request =>
                request.FileName.Equals("git", StringComparison.OrdinalIgnoreCase)
                    ? CreateProcessResult("linked.txt\0")
                    : CreateProcessResult(string.Empty, exitCode: 2));
            var tool = new SearchTextTool(processManager: processManager);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "outside needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            Assert.Empty(result.Value.Matches);
            Assert.DoesNotContain(
                result.Sources,
                source => source.Identifier.Equals("linked.txt", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    /// <summary>A native missing-executable start failure uses the confined managed scanner.</summary>
    [Fact]
    public static async Task SearchTextTool_MissingRipgrepProcessStart_UsesManagedFallback()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "visible.txt"), "needle");
            var processManager = new RoutingProcessManager(request =>
            {
                if (request.FileName.Equals("git", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateProcessResult("visible.txt\0");
                }

                throw new Win32Exception(2);
            });
            var tool = new SearchTextTool(
                processManager: processManager,
                ripgrepExecutable: $"missing-ripgrep-{Guid.NewGuid():N}");
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("visible.txt", match.Path);
            Assert.Contains("Ripgrep was not found", result.Value.Warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Text search prunes generated and Git metadata subtrees before reading files.</summary>
    [Fact]
    public static async Task SearchTextTool_GeneratedAndGitSubtrees_ArePruned()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "visible.txt"), "needle");
            string[] excludedDirectories =
            [
                ".codegraph",
                ".git",
                ".idea",
                ".vs",
                "artifacts",
                "bin",
                "node_modules",
                "obj",
                "TestResults",
            ];
            foreach (var excludedDirectory in excludedDirectories)
            {
                var directory = Directory.CreateDirectory(Path.Combine(repository, excludedDirectory)).FullName;
                await File.WriteAllTextAsync(Path.Combine(directory, "excluded.txt"), "needle");
            }

            var hostStateDirectory = Directory.CreateDirectory(Path.Combine(repository, ".threadsmith")).FullName;
            await File.WriteAllTextAsync(Path.Combine(hostStateDirectory, "threadsmith.db"), "needle");

            var tool = new SearchTextTool();
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("visible.txt", match.Path);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Model-facing tool descriptions enforce semantic-first selection.</summary>
    [Fact]
    public static void SemanticToolDescriptions_RequireSemanticFirstSelection()
    {
        var resolver = new DescriptionSemanticResolver();
        string[] descriptions =
        [
            new FindSymbolTool(resolver).Definition.Description,
            new FindReferencesTool(resolver).Definition.Description,
            new FindImplementationsTool(resolver).Definition.Description,
        ];

        Assert.All(
            descriptions,
            description => Assert.Contains("MUST use before search", description, StringComparison.Ordinal));
        Assert.Contains(
            "interface implementations",
            new FindImplementationsTool(resolver).Definition.Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "MUST NOT replace an advertised semantic tool",
            new SearchTextTool().Definition.Description,
            StringComparison.Ordinal);
    }

    /// <summary>A locked file does not prevent text search from returning matches in readable files.</summary>
    [Fact]
    public static async Task SearchTextTool_LockedFile_IsSkipped()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "visible.txt"), "needle");
            var lockedPath = Path.Combine(repository, "locked.txt");
            await File.WriteAllTextAsync(lockedPath, "other content");
            using var lockedFile = new FileStream(
                lockedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var tool = new SearchTextTool();
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var result = await tool.ExecuteAsync(
                new SearchTextInput { Query = "needle" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);

            var match = Assert.Single(result.Value.Matches);
            Assert.Equal("visible.txt", match.Path);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Windows reserved device-name entries are skipped without aborting recursive inspection.</summary>
    [Fact]
    public static async Task BuiltInInspectionTools_WindowsReservedDeviceEntry_IsSkipped()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repository = CreateTemporaryDirectory();
        string[] reservedNames = ["nul", "COM¹", "COM².log", "COM³", "LPT¹", "LPT².log", "LPT³"];
        string[] extendedReservedPaths =
        [
            .. reservedNames.Select(name => @"\\?\" + Path.Combine(repository, name)),
        ];
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "visible.txt"), "needle");
            foreach (var extendedReservedPath in extendedReservedPaths)
            {
                await File.WriteAllTextAsync(extendedReservedPath, "needle");
            }

            await using var events = new DomainEventStream();
            var pipeline = CreatePipeline(
                events,
                [new ListFilesTool(), new SearchTextTool()]);
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };

            var listed = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "list_files",
                ArgumentsJson = "{}",
                Context = context,
            });
            var searched = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "search",
                ArgumentsJson = "{\"query\":\"needle\"}",
                Context = context,
            });

            Assert.True(listed.Succeeded, listed.Error);
            Assert.Contains("visible.txt", listed.ResultJson, StringComparison.Ordinal);
            Assert.True(searched.Succeeded, searched.Error);
            Assert.Contains("visible.txt", searched.ResultJson, StringComparison.Ordinal);
            foreach (var reservedName in reservedNames)
            {
                Assert.DoesNotContain(reservedName, listed.ResultJson, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(reservedName, searched.ResultJson, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            foreach (var extendedReservedPath in extendedReservedPaths)
            {
                if (File.Exists(extendedReservedPath))
                {
                    File.Delete(extendedReservedPath);
                }
            }

            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>An allow-listed process requires approval and returns bounded, attributable output.</summary>
    [Fact]
    public static async Task ProcessTool_RequiresApprovalAndReturnsStructuredResult()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var observed = new List<IDomainEvent>();
            await using var subscription = events.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });
            var manager = new ProcessManager(
                new TestSanitizer(),
                NullLogger<ProcessManager>.Instance);
            var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
            var pipeline = new ToolInvocationPipeline(
                new ToolRegistry([new RunProcessTool(
                    manager,
                    allowedExecutables: [shell],
                    shellExecutable: shell)]),
                new DefaultPolicyEngine(),
                new AllowApprovalPolicy(),
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget());

            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "run_process",
                ArgumentsJson = "{\"command\":\"dotnet --version\"}",
                Context = CreateContext(repository) with
                {
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    AllowedExecutables = [shell],
                },
            });

            Assert.True(result.Succeeded);
            Assert.Contains(result.Sources, source => source.Kind == "process");
            Assert.Contains(observed, item => item is ApprovalRequested requested
                && requested.Kind == ApprovalRequestKind.ToolInvocation
                && requested.SchemaVersion == 2);
            Assert.Contains(observed, item => item is ApprovalGranted);
            Assert.Empty(manager.ActiveProcesses);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Run-process exposes its schema but is withheld when the host cannot obtain required approval.</summary>
    [Fact]
    public static void RunProcess_ApprovalRequired_IsNotConversationAvailable()
    {
        var tool = new RunProcessTool(
            new DirectProcessStartManager(),
            allowedExecutables: ["pwsh"],
            shellExecutable: "pwsh");

        using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
        var properties = schema.RootElement.GetProperty("properties");

        Assert.True(properties.TryGetProperty("command", out _));
        Assert.True(properties.TryGetProperty("timeoutSeconds", out _));
        Assert.Contains("PowerShell", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Equal(2, properties.EnumerateObject().Count());
        Assert.False(tool.Definition.ConversationAvailable);
        Assert.Equal(ApprovalLevel.User, tool.Definition.RequiredApproval);
    }

    /// <summary>Shell executables with a normal platform extension use the policy-normalized basename.</summary>
    [Fact]
    public static void RunProcess_ExtendedShellName_UsesNormalizedAllowlistBasename()
    {
        var tool = new RunProcessTool(
            new DirectProcessStartManager(),
            allowedExecutables: ["powershell"],
            requireApproval: false,
            shellExecutable: "powershell.exe");

        Assert.True(tool.Definition.ConversationAvailable);
        Assert.Contains("PowerShell", tool.Definition.Description, StringComparison.Ordinal);
    }

    /// <summary>Unsupported or non-bare shells are never advertised to the model.</summary>
    [Theory]
    [InlineData("fish", "fish")]
    [InlineData("C:\\Tools\\powershell.exe", "powershell")]
    [InlineData("/usr/bin/bash", "bash")]
    public static void RunProcess_InvalidShellConfiguration_IsNotConversationAvailable(
        string shellExecutable,
        string allowedExecutable)
    {
        var tool = new RunProcessTool(
            new DirectProcessStartManager(),
            allowedExecutables: [allowedExecutable],
            requireApproval: false,
            shellExecutable: shellExecutable);

        Assert.False(tool.Definition.ConversationAvailable);
    }

    /// <summary>Trusted composition can remove per-call approval without weakening executable restrictions.</summary>
    [Fact]
    public static void RunProcess_TrustedApprovalOption_RemovesPromptRequirement()
    {
        var tool = new RunProcessTool(
            new DirectProcessStartManager(),
            allowedExecutables: ["pwsh"],
            requireApproval: false,
            shellExecutable: "pwsh");

        Assert.True(tool.Definition.ConversationAvailable);
        Assert.Equal(ApprovalLevel.None, tool.Definition.RequiredApproval);
        Assert.Contains("command", tool.Definition.InputSchema.JsonSchema, StringComparison.Ordinal);
    }

    /// <summary>Unix shells use non-login command mode and preserve the repository working directory.</summary>
    [Fact]
    public static async Task RunProcess_UnixShell_UsesNonLoginCommandModeAtRepositoryRoot()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                1,
                0,
                string.Empty,
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(1)));
            var tool = new RunProcessTool(
                processManager,
                allowedExecutables: ["bash"],
                requireApproval: false,
                shellExecutable: "bash");
            var context = new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                CreateContext(repository));
            var input = tool.DeserializeInput("{\"command\":\"pwd\"}");

            await tool.ExecuteAsync(input, context);

            var request = Assert.IsType<ProcessExecutionRequest>(processManager.LastRequest);
            Assert.Equal(repository, request.WorkingDirectory);
            Assert.Equal(["-c", "pwd"], request.Arguments);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Cancelling a process request terminates its child process tree.</summary>
    [Fact]
    public static async Task ProcessManager_Cancellation_KillsChildTree()
    {
        var repository = CreateTemporaryDirectory();
        var processIdPath = Path.Combine(repository, "child.pid");
        using var cancellation = new CancellationTokenSource();
        Process? child = null;
        try
        {
            var manager = new ProcessManager(
                new TestSanitizer(),
                NullLogger<ProcessManager>.Instance);
            var request = CreateTreeProcessRequest(repository, processIdPath);
            var running = manager.RunAsync(request, cancellation.Token);
            await WaitForFileAsync(processIdPath, TimeSpan.FromSeconds(10));
            var processIdText = await File.ReadAllTextAsync(processIdPath);
            var processId = int.Parse(processIdText, System.Globalization.CultureInfo.InvariantCulture);
            child = Process.GetProcessById(processId);

            await cancellation.CancelAsync();
            var cancellationObserved = false;
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }

            await WaitForTerminationAsync(child, TimeSpan.FromSeconds(10));

            Assert.True(cancellationObserved);
            Assert.True(HasTerminated(child));
            Assert.Empty(manager.ActiveProcesses);
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
            }

            child?.Dispose();
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Injected tool limits override the compiled defaults for read_file and list_files.</summary>
    [Fact]
    public static async Task ConfiguredToolLimits_OverrideCompiledDefaults()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var bigFile = Path.Combine(repository, "big.txt");
            await File.WriteAllLinesAsync(bigFile, Enumerable.Repeat(new string('x', 256), 8));
            var smallFile = Path.Combine(repository, "small.txt");
            await File.WriteAllLinesAsync(smallFile, ["a", "b", "c", "d"]);

            var tightLimits = new ToolLimits
            {
                ReadFileMaximumBytes = 1024,
                ReadFileDefaultLines = 2,
                ListFilesMaxEntries = 5,
            };
            var readTool = new ReadFileTool(tightLimits);
            var listTool = new ListFilesTool(tightLimits);

            // Custom ReadFileMaximumBytes rejects a file that exceeds the configured bound.
            var context = CreateContext(repository) with
            {
                TrustLevel = RepositoryTrustLevel.TrustedRead,
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                readTool.ExecuteAsync(
                    new ReadFileInput { Path = "big.txt" },
                    new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                    CancellationToken.None));

            // Custom ReadFileDefaultLines is applied when the model omits maximumLines (sentinel 0).
            var readResult = await readTool.ExecuteAsync(
                new ReadFileInput { Path = "small.txt" },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);
            Assert.Equal(2, readResult.Value.Lines.Count);

            // Custom ListFilesMaxEntries rejects an explicit value over the configured cap.
            Assert.Throws<ToolArgumentValidationException>(() =>
                listTool.DeserializeInput("{\"path\":\".\",\"maximumEntries\":6}"));

            // Default tool limits (no injection) still apply the compiled 1 MiB read bound, so big.txt reads fine.
            var defaultReadTool = new ReadFileTool();
            var defaultResult = await defaultReadTool.ExecuteAsync(
                new ReadFileInput { Path = "big.txt", MaximumLines = 4 },
                new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context),
                CancellationToken.None);
            Assert.Equal(4, defaultResult.Value.Lines.Count);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The datetime built-in returns consistent UTC, local, timezone, and offset values.</summary>
    [Fact]
    public static async Task DateTimeTool_ReturnsCurrentHostClockInformation()
    {
        var before = DateTimeOffset.UtcNow;
        var tool = new DateTimeTool();
        var result = await tool.ExecuteAsync(
            new DateTimeInput(),
            new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), CreateContext(Environment.CurrentDirectory)),
            CancellationToken.None);
        var utc = DateTimeOffset.Parse(result.Value.UtcNow, System.Globalization.CultureInfo.InvariantCulture);
        var local = DateTimeOffset.Parse(result.Value.LocalNow, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(utc, before, DateTimeOffset.UtcNow);
        Assert.Equal(TimeZoneInfo.Local.Id, result.Value.TimeZoneId);
        Assert.Equal(utc.UtcDateTime, local.UtcDateTime);
        Assert.Equal(local.Offset.ToString("c"), result.Value.OffsetFromUtc);
        Assert.Equal("Date/Time", tool.Definition.DisplayName);
    }

    /// <summary>Tool-level configuration binds typed scalar values and exposes one tool's scalar map.</summary>
    [Fact]
    public static void ToolConfig_ReadsTypedAndCompleteToolValues()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tools:config:csharp_script:timeout_ms"] = "1234",
                ["tools:config:csharp_script:max_output_bytes"] = "2048",
            })
            .Build();
        var config = new ToolConfig(configuration);
        Assert.Equal(1234, config.Get("csharp_script", "timeout_ms", 5000));
        Assert.Equal(99, config.Get("csharp_script", "missing", 99));
        var all = config.GetAll("csharp_script");
        Assert.Equal("2048", all["max_output_bytes"]);
        Assert.Equal(2, all.Count);
    }

    /// <summary>The optional C# tool executes, truncates, rejects forbidden APIs, and kills a timed-out worker.</summary>
    [Fact]
    public static async Task CSharpScriptEngine_EnforcesExecutionBoundaries()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["tools:config:csharp_script:timeout_ms"] = "5000",
                    ["tools:config:csharp_script:max_output_bytes"] = "256",
                    ["tools:config:csharp_script:allowed_assemblies"] = "System.Linq,System.Collections,System.Collections.Generic",
                })
                .Build();
            var processManager = new ProcessManager(new TestSanitizer(), NullLogger<ProcessManager>.Instance);
            var engine = new CSharpScriptEngine(
                processManager,
                new ToolConfig(configuration),
                Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"));
            var context = new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                CreateContext(repository) with { TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation });
            var expression = await engine.ExecuteAsync(
                "Enumerable.Range(1, 4).Sum()",
                ScriptKind.Expression,
                context);
            Assert.True(expression.Success, expression.Error);
            Assert.Equal("10", expression.Output);
            var statement = await engine.ExecuteAsync(
                "var value = 6 * 7; return value;",
                ScriptKind.Statement,
                context);
            Assert.True(statement.Success, statement.Error);
            Assert.Equal("42", statement.Output);
            var invalid = await engine.ExecuteAsync(
                "var value = ;",
                ScriptKind.Statement,
                context);
            Assert.False(invalid.Success);
            Assert.NotNull(invalid.Error);
            Assert.NotEmpty(invalid.Error);
            var oversized = await engine.ExecuteAsync(
                "new string('x', 1000)",
                ScriptKind.Expression,
                context);
            Assert.True(oversized.Success, oversized.Error);
            Assert.True(oversized.IsTruncated);
            Assert.Equal(256, System.Text.Encoding.UTF8.GetByteCount(oversized.Output ?? string.Empty));
            var forbidden = await engine.ExecuteAsync(
                "System.IO.File.Exists(\"anything\")",
                ScriptKind.Expression,
                context);
            Assert.False(forbidden.Success);
            Assert.Contains("prohibited", forbidden.Error, StringComparison.OrdinalIgnoreCase);
            var escapedForbidden = await engine.ExecuteAsync(
                "System.\\u0049O.\\u0046ile.ReadAllText(\"anything\")",
                ScriptKind.Expression,
                context);
            Assert.False(escapedForbidden.Success);
            Assert.Contains("prohibited", escapedForbidden.Error, StringComparison.OrdinalIgnoreCase);
            var disallowedAssembly = await engine.ExecuteAsync(
                "new System.Text.StringBuilder().Append(42).ToString()",
                ScriptKind.Expression,
                context);
            Assert.False(disallowedAssembly.Success);
            Assert.Contains("allowed_assemblies", disallowedAssembly.Error, StringComparison.OrdinalIgnoreCase);

            IConfiguration timeoutConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["tools:config:csharp_script:timeout_ms"] = "500",
                    ["tools:config:csharp_script:max_output_bytes"] = "256",
                })
                .Build();
            var timeoutEngine = new CSharpScriptEngine(
                processManager,
                new ToolConfig(timeoutConfiguration),
                Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"));
            var timeout = await timeoutEngine.ExecuteAsync(
                "while (true) { }",
                ScriptKind.Statement,
                context);
            Assert.False(timeout.Success);
            Assert.Contains("terminated", timeout.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(processManager.ActiveProcesses);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>A staged self-contained worker apphost is launched directly without the dotnet muxer.</summary>
    [Fact]
    public static async Task CSharpScriptEngine_LaunchesWorkerAppHostDirectly()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var workerPath = Path.Combine(
                repository,
                OperatingSystem.IsWindows() ? "Threadsmith.Scripting.Worker.exe" : "Threadsmith.Scripting.Worker");
            await File.WriteAllTextAsync(workerPath, string.Empty);
            var processManager = new StubProcessManager(new ProcessExecutionResult(
                1,
                0,
                "{\"success\":true,\"output\":\"42\",\"executionMs\":1,\"isTruncated\":false}",
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(1)));
            var engine = new CSharpScriptEngine(
                processManager,
                new ToolConfig(new ConfigurationBuilder().Build()),
                workerPath);
            var context = new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                CreateContext(repository) with { TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation });

            var result = await engine.ExecuteAsync("6 * 7", ScriptKind.Expression, context);

            Assert.True(result.Success);
            Assert.Equal("42", result.Output);
            var request = Assert.IsType<ProcessExecutionRequest>(processManager.LastRequest);
            Assert.Equal(workerPath, request.FileName);
            Assert.Empty(request.Arguments);
            Assert.Equal(ProcessRequestOrigin.Host, request.Origin);

            var actualWorkerPath = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(workerPath));
            Assert.True(File.Exists(actualWorkerPath), $"Worker apphost was not copied to '{actualWorkerPath}'.");
            var actualEngine = new CSharpScriptEngine(
                new ProcessManager(new TestSanitizer(), NullLogger<ProcessManager>.Instance),
                new ToolConfig(new ConfigurationBuilder().Build()),
                actualWorkerPath);
            var actualResult = await actualEngine.ExecuteAsync(
                "6 * 7",
                ScriptKind.Expression,
                context);
            Assert.True(actualResult.Success, actualResult.Error);
            Assert.Equal("42", actualResult.Output);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The script tool is registered but unavailable until explicitly enabled.</summary>
    [Fact]
    public static async Task CSharpScriptTool_IsDisabledByDefaultAndCanBeEnabled()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(repository, ".threadsmith", "config.json");
            IConfiguration configuration = new ConfigurationBuilder().Build();
            var engine = new StubCSharpScriptEngine();
            ITool[] tools = [new DateTimeTool(), new CSharpScriptTool(engine)];
            var state = new ToolStateManager(tools.Select(tool => tool.Definition), configuration, configPath);
            var registry = new ToolRegistry(tools, state);
            Assert.Contains(registry.Definitions, definition => definition.Id == "datetime");
            Assert.DoesNotContain(registry.Definitions, definition => definition.Id == "csharp_script");
            Assert.Throws<KeyNotFoundException>(() => registry.Get("csharp_script"));
            await state.EnableAsync("csharp_script");
            var registeredScript = registry.Get("csharp_script");
            Assert.Equal("csharp_script", registeredScript.Definition.Id);
            var execution = await registeredScript.ExecuteAsync(
                registeredScript.DeserializeInput("{\"code\":\"6 * 7\",\"kind\":\"expression\"}"),
                new ToolExecutionContext(
                    ToolInvocationId.New(),
                    SessionId.New(),
                    RunId.New(),
                    CreateContext(repository) with { TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation }),
                CancellationToken.None);
            Assert.Equal("stub", Assert.IsType<CSharpScriptOutput>(execution.Value).Output);
            var restored = new ToolStateManager(
                tools.Select(tool => tool.Definition),
                new ConfigurationBuilder().AddJsonFile(configPath).Build(),
                configPath);
            Assert.True(restored.IsEnabled("csharp_script"));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Independent reviewed tools enter their bodies concurrently and retain model ordinal order.</summary>
    [Fact]
    public static async Task Batch_IndependentParallelSafeTools_OverlapAndJoinInOrdinalOrder()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var gate = new ParallelToolGate(2);
            var pipeline = CreatePipeline(
                events,
                [new BarrierReadTool("parallel_a", gate), new BarrierReadTool("parallel_b", gate)]);
            var context = CreateContext(repository);
            ToolBatchRequest[] requests =
            [
                CreateBatchRequest(2, "call-b", "parallel_b", context),
                CreateBatchRequest(1, "call-a", "parallel_a", context),
            ];

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = pipeline.InvokeBatchAsync(requests, timeout.Token);
            await gate.AllEntered.Task.WaitAsync(timeout.Token);
            Assert.Equal(2, gate.PeakActive);
            gate.Release.TrySetResult();
            var results = await execution;

            Assert.Equal([1, 2], results.Select(result => result.Ordinal));
            Assert.All(results, result => Assert.True(result.Result.Succeeded));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>An invalid sibling becomes an ordinary result without preventing valid work.</summary>
    [Fact]
    public static async Task Batch_InvalidSibling_ReturnsFailureAndRunsValidSibling()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var executionOrder = new ConcurrentQueue<string>();
            var pipeline = CreatePipeline(
                events,
                [new OrderedReadTool("valid_read", ToolConcurrencyMode.ParallelSafe, executionOrder)]);
            var context = CreateContext(repository);
            ToolBatchRequest[] requests =
            [
                CreateBatchRequest(1, "call-missing", "missing_tool", context),
                CreateBatchRequest(2, "call-malformed", "valid_read", context, string.Empty),
                CreateBatchRequest(3, "call-valid", "valid_read", context),
            ];

            var results = await pipeline.InvokeBatchAsync(requests);

            Assert.Equal([1, 2, 3], results.Select(result => result.Ordinal));
            Assert.Equal(ToolErrorClassification.InvalidArguments, results[0].Result.ErrorClassification);
            Assert.Equal(ToolErrorClassification.InvalidArguments, results[1].Result.ErrorClassification);
            Assert.True(results[2].Result.Succeeded);
            Assert.Equal(["valid_read"], executionOrder);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Fail-fast drains and returns the cancelled outcome of every started sibling.</summary>
    [Fact]
    public static async Task Batch_FailFastCancellation_PreservesStartedResults()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var gate = new CoordinatedFailureGate();
            var options = new ToolParallelOptions
            {
                FailureMode = ToolBatchFailureMode.CancelBatchOnFailure,
            };
            var pipeline = CreatePipeline(
                events,
                [
                    new CoordinatedFailureTool("fails", gate, fails: true),
                    new CoordinatedFailureTool("cancelled", gate, fails: false),
                ],
                options);
            var context = CreateContext(repository);

            var results = await pipeline.InvokeBatchAsync(
                [
                    CreateBatchRequest(1, "call-fails", "fails", context),
                    CreateBatchRequest(2, "call-cancelled", "cancelled", context),
                ]);

            Assert.Equal(2, results.Count);
            Assert.Equal(ToolErrorClassification.ExecutionFailure, results[0].Result.ErrorClassification);
            Assert.Equal(ToolErrorClassification.Cancelled, results[1].Result.ErrorClassification);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Fail-fast returns cancelled terminal results for siblings that were never admitted.</summary>
    [Fact]
    public static async Task Batch_FailFastCancellation_ReturnsSkippedResultsForLaterWaves()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var executionOrder = new ConcurrentQueue<string>();
            var options = new ToolParallelOptions
            {
                FailureMode = ToolBatchFailureMode.CancelBatchOnFailure,
            };
            var pipeline = CreatePipeline(
                events,
                [
                    new OrderedReadTool("later", ToolConcurrencyMode.SerializedPerRegistration, executionOrder),
                ],
                options);
            var context = CreateContext(repository);

            var results = await pipeline.InvokeBatchAsync(
                [
                    CreateBatchRequest(1, "call-fails", "missing", context),
                    CreateBatchRequest(2, "call-later", "later", context),
                ]);

            Assert.Equal([1, 2], results.Select(result => result.Ordinal));
            Assert.Equal(ToolErrorClassification.InvalidArguments, results[0].Result.ErrorClassification);
            Assert.Equal(ToolErrorClassification.Cancelled, results[1].Result.ErrorClassification);
            Assert.Contains("Skipped", results[1].Result.Error, StringComparison.Ordinal);
            Assert.Empty(executionOrder);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>A configured batch cap cannot broaden a source's compiled concurrency cap.</summary>
    [Fact]
    public static async Task Batch_SourceConcurrencyCap_NarrowsConfiguredMaximum()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var gate = new ParallelToolGate(4);
            ITool[] tools = [.. Enumerable.Range(1, 5)
                .Select(index => new BarrierReadTool($"parallel_{index}", gate))];
            var pipeline = CreatePipeline(
                events,
                tools,
                new ToolParallelOptions { MaximumConcurrency = 8 });
            var context = CreateContext(repository);
            ToolBatchRequest[] requests = [.. Enumerable.Range(1, 5)
                .Select(index => CreateBatchRequest(index, $"call-{index}", $"parallel_{index}", context))];

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = pipeline.InvokeBatchAsync(requests, timeout.Token);
            await gate.AllEntered.Task.WaitAsync(timeout.Token);
            Assert.Equal(4, gate.PeakActive);
            gate.Release.TrySetResult();
            var results = await execution;

            Assert.Equal(5, results.Count);
            Assert.Equal(4, gate.PeakActive);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>The source cap is shared by sibling batches using the same pipeline.</summary>
    [Fact]
    public static async Task Batch_SourceConcurrencyCap_AppliesAcrossConcurrentBatches()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var gate = new ParallelToolGate(4);
            ITool[] tools = [.. Enumerable.Range(1, 8)
                .Select(index => new BarrierReadTool($"parallel_{index}", gate))];
            var pipeline = CreatePipeline(
                events,
                tools,
                new ToolParallelOptions { MaximumConcurrency = 8 });
            var context = CreateContext(repository);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = pipeline.InvokeBatchAsync(
                [.. Enumerable.Range(1, 4)
                    .Select(index => CreateBatchRequest(index, $"first-{index}", $"parallel_{index}", context))],
                timeout.Token);
            var second = pipeline.InvokeBatchAsync(
                [.. Enumerable.Range(5, 4)
                    .Select(index => CreateBatchRequest(index, $"second-{index}", $"parallel_{index}", context))],
                timeout.Token);

            await gate.AllEntered.Task.WaitAsync(timeout.Token);
            Assert.Equal(4, gate.PeakActive);
            gate.Release.TrySetResult();
            await Task.WhenAll(first, second);
            Assert.Equal(4, gate.PeakActive);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Caller cancellation during the final wave remains observable at the batch boundary.</summary>
    [Fact]
    public static async Task Batch_CallerCancellationDuringFinalWave_IsPropagated()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var gate = new ParallelToolGate(1);
            var pipeline = CreatePipeline(
                events,
                [new BarrierReadTool("parallel", gate)]);
            using var cancellation = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var execution = pipeline.InvokeBatchAsync(
                [CreateBatchRequest(1, "call", "parallel", CreateContext(repository))],
                cancellation.Token);

            await gate.AllEntered.Task.WaitAsync(timeout.Token);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await execution.WaitAsync(timeout.Token));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Text search stays serialized because an invocation may launch ripgrep.</summary>
    [Fact]
    public static void Search_SchedulingDescriptor_SerializesPotentialProcessExecution()
    {
        var scheduling = new SearchTextTool().Definition.Scheduling;

        Assert.Equal(ToolConcurrencyMode.SerializedPerRegistration, scheduling.ConcurrencyMode);
        Assert.Equal(1, scheduling.MaximumSourceConcurrency);
    }

    /// <summary>Later calls never backfill ahead of an earlier invocation they conflict with.</summary>
    [Fact]
    public static async Task Batch_ConflictAcrossWaves_PreservesDependencyOrder()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await using var events = new DomainEventStream();
            var executionOrder = new ConcurrentQueue<string>();
            var pipeline = CreatePipeline(
                events,
                [
                    new OrderedReadTool("first", ToolConcurrencyMode.ParallelSafe, executionOrder),
                    new OrderedReadTool("serialized", ToolConcurrencyMode.SerializedPerRegistration, executionOrder),
                    new OrderedReadTool("third", ToolConcurrencyMode.ParallelSafe, executionOrder),
                ]);
            var context = CreateContext(repository);

            var results = await pipeline.InvokeBatchAsync(
                [
                    CreateBatchRequest(1, "call-first", "first", context),
                    CreateBatchRequest(2, "call-serialized", "serialized", context),
                    CreateBatchRequest(3, "call-third", "third", context),
                ]);

            Assert.Equal([1, 2, 3], results.Select(result => result.Ordinal));
            Assert.Equal(["first", "serialized", "third"], executionOrder);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    private static ToolBatchRequest CreateBatchRequest(
        int ordinal,
        string correlationId,
        string toolId,
        ToolInvocationContext context,
        string argumentsJson = "{}")
    {
        return new ToolBatchRequest(
            ordinal,
            correlationId,
            new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = toolId,
                ArgumentsJson = argumentsJson,
                Context = context,
            });
    }

    private static ProcessExecutionResult CreateProcessResult(
        string standardOutput,
        int exitCode = 0)
    {
        return new ProcessExecutionResult(
            42,
            exitCode,
            standardOutput,
            string.Empty,
            false,
            false,
            false,
            TimeSpan.FromMilliseconds(1));
    }

    private static ToolInvocationPipeline CreatePipeline(
        IDomainEventStream events,
        IEnumerable<ITool> tools,
        ToolParallelOptions? parallelOptions = null)
    {
        return new(
                new ToolRegistry(tools),
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                new TestSanitizer(),
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget(),
                parallelOptions: parallelOptions);
    }

    private static ExecutionBudget CreateBudget()
    {
        return new(
        new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(5)));
    }

    private static ToolInvocationContext CreateContext(string repository)
    {
        return new()
        {
            RepositoryPath = repository,
            TrustLevel = RepositoryTrustLevel.UntrustedInspection,
            ApprovedRoots = ["."],
            RequestedBy = "model",
        };
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-m3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static ProcessExecutionRequest CreateTreeProcessRequest(
        string repository,
        string processIdPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var escapedPath = processIdPath.Replace("'", "''", StringComparison.Ordinal);
            var script = "$child = Start-Process -FilePath powershell.exe "
                + "-ArgumentList @('-NoProfile','-NonInteractive','-Command',"
                + "'Start-Sleep -Seconds 60') -PassThru; "
                + $"[IO.File]::WriteAllText('{escapedPath}', $child.Id.ToString()); "
                + "Wait-Process -Id $child.Id";
            return new ProcessExecutionRequest
            {
                ToolInvocationId = ToolInvocationId.New(),
                RunId = RunId.New(),
                FileName = "powershell.exe",
                Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
                WorkingDirectory = repository,
                Timeout = TimeSpan.FromMinutes(1),
                Origin = ProcessRequestOrigin.Host,
            };
        }

        var shellScript = $"sleep 60 & child=$!; echo $child > '{processIdPath}'; wait $child";
        return new ProcessExecutionRequest
        {
            ToolInvocationId = ToolInvocationId.New(),
            RunId = RunId.New(),
            FileName = "sh",
            Arguments = ["-c", shellScript],
            WorkingDirectory = repository,
            Timeout = TimeSpan.FromMinutes(1),
            Origin = ProcessRequestOrigin.Host,
        };
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path) && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(25);
        }

        Assert.True(File.Exists(path), $"Timed out waiting for process marker '{path}'.");
    }

    private static async Task WaitForTerminationAsync(Process process, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!HasTerminated(process) && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(25);
            process.Refresh();
        }

        Assert.True(HasTerminated(process), $"Timed out waiting for process {process.Id} to terminate.");
    }

    private static bool HasTerminated(Process process)
    {
        if (process.HasExited)
        {
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var statusPath = $"/proc/{process.Id}/stat";
        if (!File.Exists(statusPath))
        {
            return true;
        }

        var status = File.ReadAllText(statusPath);
        var commandEnd = status.LastIndexOf(')');
        return commandEnd >= 0
            && status.Length > commandEnd + 2
            && status[commandEnd + 2] == 'Z';
    }

    private sealed class StubCSharpScriptEngine : ICSharpScriptEngine
    {
        public Task<CSharpScriptOutput> ExecuteAsync(
            string code,
            ScriptKind kind,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CSharpScriptOutput(true, "stub", null, 0, false));
        }
    }

    private sealed class TestSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            return value;
        }
    }

    private sealed class CountingApprovalPolicy : IApprovalPolicy
    {
        public int RequestCount { get; private set; }

        public Task<bool> IsApprovedAsync(
            string action,
            ApprovalLevel requiredLevel,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class CancellingApprovalPolicy : IApprovalPolicy
    {
        public Task<bool> IsApprovedAsync(
            string action,
            ApprovalLevel requiredLevel,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<bool>(new OperationCanceledException(cancellationToken));
        }
    }

    private sealed class DescriptionSemanticResolver : ISemanticEngineResolver
    {
        public SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId)
        {
            return SemanticConfidenceLevel.FullSemantic;
        }

        public Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(
            WorkspaceId workspaceId,
            string query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SymbolResult>>([]);
        }

        public Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
            WorkspaceId workspaceId,
            string symbolId,
            bool allowTextFallback = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ReferenceResult>>([]);
        }

        public Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
            WorkspaceId workspaceId,
            string symbolId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ImplementationResult>>([]);
        }

        public Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> projectPaths,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Diagnostic>>([]);
        }
    }

    private sealed class RoutingProcessManager : IProcessManager
    {
        private readonly Func<ProcessExecutionRequest, ProcessExecutionResult> _handler;

        public RoutingProcessManager(Func<ProcessExecutionRequest, ProcessExecutionResult> handler)
        {
            _handler = handler;
        }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class StubProcessManager : IProcessManager
    {
        private readonly ProcessExecutionResult _result;

        public StubProcessManager(ProcessExecutionResult result)
        {
            _result = result;
        }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public int ExecutionCount { get; private set; }

        public ProcessExecutionRequest? LastRequest { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class DirectProcessStartManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = request.FileName,
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("The test process unexpectedly failed to return a process handle.");
            throw new InvalidOperationException("The deliberately missing test executable unexpectedly started.");
        }
    }

    private sealed record CountingInput
    {
        public required string Value { get; init; }
    }

    private sealed record CountingOutput(string Value);

    private sealed class ParallelToolGate
    {
        private readonly int _expected;
        private int _active;
        private int _entered;
        private int _peakActive;

        public ParallelToolGate(int expected)
        {
            _expected = expected;
        }

        public TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PeakActive => Volatile.Read(ref _peakActive);

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            int observedPeak;
            do
            {
                observedPeak = Volatile.Read(ref _peakActive);
            }
            while (active > observedPeak
                && Interlocked.CompareExchange(ref _peakActive, active, observedPeak) != observedPeak);
            if (Interlocked.Increment(ref _entered) == _expected)
            {
                AllEntered.TrySetResult();
            }

            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed record BarrierReadInput;

    private sealed record BarrierReadOutput(string ToolId);

    private sealed class CoordinatedFailureGate
    {
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public async Task EnterAsync(bool fails, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entered) == 2)
            {
                _bothEntered.TrySetResult();
            }

            await _bothEntered.Task.WaitAsync(cancellationToken);
            if (fails)
            {
                throw new ToolExecutionException("Expected test failure.");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CoordinatedFailureTool : Tool<BarrierReadInput, BarrierReadOutput>
    {
        private readonly ToolDefinition _definition;
        private readonly bool _fails;
        private readonly CoordinatedFailureGate _gate;

        public CoordinatedFailureTool(string id, CoordinatedFailureGate gate, bool fails)
        {
            _gate = gate;
            _fails = fails;
            _definition = CreateReadDefinition(id, ToolConcurrencyMode.ParallelSafe, 4);
        }

        public override ToolDefinition Definition => _definition;

        public override async Task<ToolExecution<BarrierReadOutput>> ExecuteAsync(
            BarrierReadInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            await _gate.EnterAsync(_fails, cancellationToken);
            return new ToolExecution<BarrierReadOutput>(new BarrierReadOutput(Definition.Id), []);
        }

        protected override void ValidateInput(BarrierReadInput input)
        {
        }
    }

    private sealed class OrderedReadTool : Tool<BarrierReadInput, BarrierReadOutput>
    {
        private readonly ToolDefinition _definition;
        private readonly ConcurrentQueue<string> _executionOrder;

        public OrderedReadTool(
            string id,
            ToolConcurrencyMode concurrencyMode,
            ConcurrentQueue<string> executionOrder)
        {
            _executionOrder = executionOrder;
            _definition = CreateReadDefinition(
                id,
                concurrencyMode,
                concurrencyMode == ToolConcurrencyMode.ParallelSafe ? 4 : 1);
        }

        public override ToolDefinition Definition => _definition;

        public override Task<ToolExecution<BarrierReadOutput>> ExecuteAsync(
            BarrierReadInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _executionOrder.Enqueue(Definition.Id);
            return Task.FromResult(new ToolExecution<BarrierReadOutput>(
                new BarrierReadOutput(Definition.Id),
                []));
        }

        protected override void ValidateInput(BarrierReadInput input)
        {
        }
    }

    private sealed class BarrierReadTool : Tool<BarrierReadInput, BarrierReadOutput>
    {
        private readonly ToolDefinition _definition;
        private readonly ParallelToolGate _gate;

        public BarrierReadTool(string id, ParallelToolGate gate)
        {
            _gate = gate;
            _definition = CreateReadDefinition(id, ToolConcurrencyMode.ParallelSafe, 4);
        }

        public override ToolDefinition Definition => _definition;

        public override async Task<ToolExecution<BarrierReadOutput>> ExecuteAsync(
            BarrierReadInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            await _gate.EnterAsync(cancellationToken);
            return new ToolExecution<BarrierReadOutput>(new BarrierReadOutput(Definition.Id), []);
        }

        protected override void ValidateInput(BarrierReadInput input)
        {
        }
    }

    private static ToolDefinition CreateReadDefinition(
        string id,
        ToolConcurrencyMode concurrencyMode,
        int maximumSourceConcurrency)
    {
        return new ToolDefinition
        {
            Id = id,
            Version = "1.0",
            Description = "Test-only scheduled read.",
            Category = ToolCategory.FileRead,
            InputSchema = new ToolSchema(nameof(BarrierReadInput), 1, "{\"type\":\"object\"}"),
            OutputSchema = new ToolSchema(nameof(BarrierReadOutput), 1, "{\"type\":\"object\"}"),
            RequiredTrust = RepositoryTrustLevel.UntrustedInspection,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOutputBytes = 1024,
            Scheduling = new ToolSchedulingDescriptor
            {
                ConcurrencyMode = concurrencyMode,
                ClaimResolverId = "test-scheduled-read-v1",
                MaximumSourceConcurrency = maximumSourceConcurrency,
            },
        };
    }

    private sealed class CountingTool : Tool<CountingInput, CountingOutput>
    {
        private static readonly ToolDefinition _definition = new()
        {
            Id = "counting",
            Version = "1.0",
            Description = "Counts successful executions.",
            Category = ToolCategory.RepositoryInspection,
            InputSchema = new ToolSchema(nameof(CountingInput), 1, "{\"type\":\"object\"}"),
            OutputSchema = new ToolSchema(nameof(CountingOutput), 1, "{\"type\":\"object\"}"),
            Timeout = TimeSpan.FromSeconds(1),
            MaximumOutputBytes = 1024,
            SupportsCancellation = true,
        };

        public int ExecutionCount { get; private set; }

        public override ToolDefinition Definition => _definition;

        public override Task<ToolExecution<CountingOutput>> ExecuteAsync(
            CountingInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(new ToolExecution<CountingOutput>(
                new CountingOutput(input.Value),
                []));
        }

        protected override void ValidateInput(CountingInput input)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Value);
        }
    }
}
