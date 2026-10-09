namespace Threadsmith.RepositoryIntelligence.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

internal sealed class TestProfileHost : IAsyncDisposable
{
    private readonly string _temporaryRoot;
    private readonly DomainEventStream _events = new();
    private readonly IDomainEventSubscription _subscription;
    private readonly RepositoryIntelligenceFeature _feature;
    private readonly ToolRegistry _registry;

    private TestProfileHost(string temporaryRoot, string repository, int maximumFiles, IOutputSanitizer? sanitizer)
    {
        _temporaryRoot = temporaryRoot;
        Repository = repository;
        Settings = Path.Combine(temporaryRoot, "settings");
        _feature = new RepositoryIntelligenceFeature(Settings, Repository, new(maximumFiles, 0, 0));
        _subscription = _events.Subscribe((item, token) =>
        {
            Events.Enqueue(item);
            return Task.CompletedTask;
        });
        sanitizer ??= new SecretOutputSanitizer();
        Git = new GitQueryService();
        var registry = new ToolRegistry(
        [
            new GitShowTool(Git, TestPromptLoader.Instance),
            new GitDiffTool(Git, TestPromptLoader.Instance),
            new GitLogTool(Git, TestPromptLoader.Instance),
            new ReadFileTool(TestPromptLoader.Instance, sanitizer),
            new ListFilesTool(TestPromptLoader.Instance),
        ]);
        _registry = registry;
        Pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), _events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        Reads = new ObservingPipeline(Pipeline);
        Tool = new RepositoryIntelligenceStatusTool(
            (identity, token) => Task.FromResult<(RepositoryIntelligenceFeature, string?)>((_feature, null)),
            TestPromptLoader.Instance,
            Reads);
        registry.RegisterOrReplace(Tool, new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "repository-intelligence"));
    }

    public string Repository { get; }

    public string Settings { get; }

    public GitQueryService Git { get; }

    public RepositoryIntelligenceStatusTool Tool { get; }

    public ToolInvocationPipeline Pipeline { get; }

    public ObservingPipeline Reads { get; }

    public ConcurrentQueue<IDomainEvent> Events { get; } = new();

    public static async Task<TestProfileHost> CreateAsync(bool initializeGit = true, int maximumFiles = 32, IOutputSanitizer? sanitizer = null)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "threadsmith-t04-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temporaryRoot, "repo");
        Directory.CreateDirectory(repository);
        if (initializeGit)
        {
            await RunGitAsync(repository, "init", "-b", "main");
            await RunGitAsync(repository, "config", "user.email", "tests@example.invalid");
            await RunGitAsync(repository, "config", "user.name", "Threadsmith Tests");
            repository = (await RunGitAsync(repository, "rev-parse", "--show-toplevel")).Trim();
        }

        return new TestProfileHost(temporaryRoot, repository, maximumFiles, sanitizer);
    }

    public async Task WriteAsync(string path, string content)
    {
        var fullPath = Path.Combine(Repository, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, TestContext.Current.CancellationToken);
    }

    public async Task<string> CommitAsync(string subject = "fixture")
    {
        await RunGitAsync(Repository, "add", "--all");
        await RunGitAsync(Repository, "-c", "commit.gpgSign=false", "commit", "-m", subject);
        return (await RunGitAsync(Repository, "rev-parse", "HEAD")).Trim();
    }

    public ToolInvocationContext Context => new()
    {
        RepositoryPath = Repository,
        TrustLevel = RepositoryTrustLevel.TrustedRead,
        AllowedExecutables = ["git"],
        RequestedBy = "user",
    };

    public async Task<ToolInvocationResult> InvokeAsync(RepositoryProfileSelection? selection, ToolInvocationContext? context = null, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, cancellationToken);
        return await Pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Phase = RunPhase.Intake,
                ToolId = "repository_intelligence",
                ArgumentsJson = JsonSerializer.SerializeToElement(new RepositoryIntelligenceStatusInput { Profile = selection }).GetRawText(),
                Context = context ?? Context,
            },
            linked.Token);
    }

    public async Task<RepositoryStructuralProfile> CaptureAsync(RepositoryProfileSelection? selection = null)
    {
        var result = await InvokeAsync(selection ?? new RepositoryProfileSelection());
        Assert.True(result.Succeeded, result.Error);
        var output = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(result.ResultJson!);
        Assert.NotNull(output?.Profile);
        Assert.False(output.Controls.Persistence);
        Assert.False(output.Controls.Recall);
        Assert.False(output.Controls.Maintenance);
        Assert.False(output.AnalysisAvailable);
        return output.Profile;
    }

    public async Task FlushEventsAsync() => await _subscription.DisposeAsync();

    public async Task<T> RunEvidenceAsync<T>(
        Func<ToolExecutionContext, CancellationToken, Task<T>> action,
        ToolInvocationContext? context = null,
        SessionId? sessionId = null)
        where T : class
    {
        T? value = null;
        var tool = new EvidenceOperationTool(async (execution, token) => value = await action(execution, token));
        _registry.RegisterOrReplace(tool, new ToolActivitySource(ToolActivitySourceKind.BuiltIn, tool.Definition.Id));
        var result = await Pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = sessionId ?? SessionId.New(),
                RunId = RunId.New(),
                ToolId = tool.Definition.Id,
                ArgumentsJson = "{}",
                Context = context ?? Context,
            },
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(value);
        return value;
    }

    private sealed record EvidenceOperationInput;

    private sealed class EvidenceOperationTool : Tool<EvidenceOperationInput, string>
    {
        private readonly Func<ToolExecutionContext, CancellationToken, Task> _action;

        public EvidenceOperationTool(Func<ToolExecutionContext, CancellationToken, Task> action)
        {
            _action = action;
            Definition = ToolDefinitionFactory.Create<EvidenceOperationInput, string>(
                "test_evidence_" + Guid.NewGuid().ToString("N"),
                "Test-only governed evidence operation",
                ToolCategory.RepositoryInspection,
                RepositoryTrustLevel.TrustedRead,
                ApprovalLevel.None,
                ToolSideEffect.ReadOnly,
                TimeSpan.FromMinutes(2),
                1024);
        }

        public override ToolDefinition Definition { get; }

        public override async Task<ToolExecution<string>> ExecuteAsync(
            EvidenceOperationInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            await _action(context, cancellationToken);
            return new("Complete", [], false);
        }

        protected override void ValidateInput(EvidenceOperationInput input) => ArgumentNullException.ThrowIfNull(input);
    }

    public static async Task<string> RunGitAsync(string repository, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var token = TestContext.Current.CancellationToken;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var registration = token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    public async ValueTask DisposeAsync()
    {
        await _feature.DisposeAsync();
        await _subscription.DisposeAsync();
        await _events.DisposeAsync();
        var root = Path.GetFullPath(_temporaryRoot);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), root, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("threadsmith-t04-", Path.GetFileName(root), StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    public sealed class ObservingPipeline : IToolInvocationPipeline
    {
        private readonly IToolInvocationPipeline _inner;

        public ObservingPipeline(IToolInvocationPipeline inner) => _inner = inner;

        public List<(string ToolId, string Arguments, ToolInvocationResult Result)> Calls { get; } = [];

        public Func<string, string, ToolInvocationResult, CancellationToken, Task>? AfterReadAsync { get; set; }

        public Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken = default)
            => _inner.InvokeAsync(request, cancellationToken);

        public ToolBatchPreflightResult PreflightBatch(IReadOnlyList<ToolBatchRequest> requests) => _inner.PreflightBatch(requests);

        public async Task<ToolInvocationResult> InvokeNestedReadAsync(ToolExecutionContext parent, string toolId, string argumentsJson, CancellationToken cancellationToken = default)
        {
            var result = await _inner.InvokeNestedReadAsync(parent, toolId, argumentsJson, cancellationToken);
            Calls.Add((toolId, argumentsJson, result));
            if (AfterReadAsync is { } observe)
            {
                await observe(toolId, argumentsJson, result, cancellationToken);
            }

            return result;
        }
    }
}
