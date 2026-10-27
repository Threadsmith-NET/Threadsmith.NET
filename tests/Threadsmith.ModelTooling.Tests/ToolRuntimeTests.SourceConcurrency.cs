namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class ToolRuntimeTests
{
    /// <summary>A process in one caller cannot consume another caller's file-read allowance.</summary>
    [Fact]
    public static async Task RunningProcessDoesNotBlockIndependentFileRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "sample.txt"), "available while process runs", ct);
            await using var events = new DomainEventStream();
            var manager = new SourceConcurrencyProcessManager();
            var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
            var pipeline = CreatePipeline(
                events,
                [new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: shell), new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer())],
                runtimeOptions: new() { Defaults = new() { MaximumSourceConcurrency = 1 } });
            var context = CreateContext(repository) with { TrustLevel = RepositoryTrustLevel.TrustedBuild, AllowedExecutables = [shell] };
            var process = pipeline.InvokeAsync(CreateBatchRequest(0, "process", "run_process", context, "{\"command\":\"test barrier\"}").Invocation, ct);
            try
            {
                await Task.WhenAny(manager.Entered.Task, process).WaitAsync(ct);
                Assert.True(manager.Entered.Task.IsCompletedSuccessfully, process.IsCompleted ? (await process).Error : "Process did not enter.");
                var read = await pipeline.InvokeAsync(CreateBatchRequest(0, "read", "read_file", context, "{\"path\":\"sample.txt\"}").Invocation, ct);
                Assert.True(read.Succeeded, read.Error);
                Assert.Contains("available while process runs", read.ResultJson, StringComparison.Ordinal);
                Assert.False(process.IsCompleted);
            }
            finally
            {
                manager.Release.TrySetResult();
                await process;
            }
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>External tools still share a provider cap; built-in registrations retain their own caps.</summary>
    [Theory]
    [InlineData(ToolActivitySourceKind.BuiltIn)]
    [InlineData(ToolActivitySourceKind.Mcp)]
    [InlineData(ToolActivitySourceKind.Extension)]
    public static async Task ConcurrencyScopesRetainCapsAndReleaseCancelledWaiters(ToolActivitySourceKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var limiter = new ToolSourceConcurrencyLimiter();
        var source = new ToolActivitySource(kind, "provider");
        var firstScope = ToolConcurrencyScope.Create(source, "first");
        using var first = await limiter.AcquireAsync(firstScope, 1, ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var same = limiter.AcquireAsync(ToolConcurrencyScope.Create(source, "FIRST"), 1, cancellation.Token).AsTask();
        Assert.False(same.IsCompleted);
        var other = limiter.AcquireAsync(ToolConcurrencyScope.Create(source, "other"), 1, cancellation.Token).AsTask();
        if (kind == ToolActivitySourceKind.BuiltIn)
        {
            Assert.True(other.IsCompletedSuccessfully);
            using var lease = await other.WaitAsync(ct);
        }
        else
        {
            Assert.False(other.IsCompleted);
        }

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await same.WaitAsync(ct));
        if (kind != ToolActivitySourceKind.BuiltIn)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await other.WaitAsync(ct));
        }

        first.Dispose();
        using var recovered = await limiter.AcquireAsync(firstScope, 1, ct);
    }

    private sealed class SourceConcurrencyProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return CreateProcessResult("done");
        }
    }
}
