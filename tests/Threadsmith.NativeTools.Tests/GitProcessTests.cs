namespace Threadsmith.NativeTools.Tests;

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies shared Git capture, cleanup and caller policy contracts.</summary>
public sealed class GitProcessTests
{
    /// <summary>Both pipes drain past retention limits before process exit.</summary>
    [Fact]
    public async Task ExecuteAsync_LargeOutputAndError_DrainsBothWithBoundedRetention()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-git-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var init = GitProcess.CreateProcess(root, ["init"]);
            Assert.Equal(0, (await ExecuteAsync(init, 64, ct)).ExitCode);
            using var process = GitProcess.CreateProcess(root, ["rev-parse", new string('a', 12000)]);

            var result = await ExecuteAsync(process, 64, ct);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal(64, result.Output.Text.Length);
            Assert.Equal(64, result.Error.Text.Length);
            Assert.True(result.Output.IsTruncated);
            Assert.True(result.Error.IsTruncated);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.HasExited);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Cancellation propagates after cleanup.</summary>
    [Fact]
    public async Task ExecuteAsync_CallerCancellation_TerminatesOwnedProcess()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var process = CreateWaitingGit();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<GitProcess.BoundedText>? drain = null;
        var execution = GitProcess.ExecuteAsync(
            process,
            async (owned, _) =>
            {
                Assert.Equal("child-ready", await owned.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
                // Do not cancel this read with the operation: EOF proves descendants released
                // the inherited output pipe, rather than merely cancelling our own reader.
                drain = GitProcess.ReadBoundedAsync(owned.StandardOutput, 64, TestContext.Current.CancellationToken);
                started.SetResult();
                return await drain;
            },
            64,
            cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TestContext.Current.CancellationToken));
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.HasExited);
        Assert.NotNull(drain);
        await drain.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(drain.IsCompletedSuccessfully);
    }

    /// <summary>A failed reader cannot strand Git.</summary>
    [Fact]
    public async Task ExecuteAsync_ReaderFailure_TerminatesProcessAndPreservesFailure()
    {
        using var process = CreateWaitingGit();
        var failure = new InvalidDataException("reader failed");

        var actual = await Assert.ThrowsAsync<InvalidDataException>(() => GitProcess.ExecuteAsync<GitProcess.BoundedText>(
            process,
            async (owned, token) =>
            {
                Assert.Equal("child-ready", await owned.StandardOutput.ReadLineAsync(token));
                throw failure;
            },
            64,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(failure, actual);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.HasExited);
    }

    /// <summary>Start failures remain available for caller-specific mapping.</summary>
    [Fact]
    public async Task ExecuteAsync_MissingExecutable_PreservesStartFailure()
    {
        using var process = GitProcess.CreateProcess(Path.GetTempPath(), ["--version"]);
        process.StartInfo.FileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-git");

        await Assert.ThrowsAsync<Win32Exception>(() => ExecuteAsync(process, 64, TestContext.Current.CancellationToken));
    }

    /// <summary>Baseline decoding retains its replacement fallback.</summary>
    [Fact]
    public async Task ReadBoundedAsync_BaselineDecodingRemainsNonThrowing()
    {
        using var baseline = GitProcess.CreateProcess(Path.GetTempPath(), [], preserveOutputFormat: true);
        using var query = GitProcess.CreateProcess(Path.GetTempPath(), []);
        Assert.Null(baseline.StartInfo.StandardOutputEncoding);
        Assert.Null(baseline.StartInfo.StandardErrorEncoding);
        Assert.IsType<DecoderExceptionFallback>(query.StartInfo.StandardOutputEncoding!.DecoderFallback);
        await using var bytes = new MemoryStream([0xFF, 0x61]);
        using var reader = new StreamReader(bytes, baseline.StartInfo.StandardOutputEncoding ?? Console.OutputEncoding);

        var result = await GitProcess.ReadBoundedAsync(reader, 64, TestContext.Current.CancellationToken);

        Assert.EndsWith("a", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    /// <summary>Only non-caller cancellation maps to timeout.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryCancellation_DistinguishesCallerFromDeadline(bool callerCancelled)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new GitQueryService(new GitResourceLimits { TimeoutMilliseconds = 100 }, timeProvider: clock);
        Func<StreamReader, CancellationToken, Task<GitProcess.BoundedText>> reader = async (_, token) =>
        {
            started.TrySetResult();

            await Task.Delay(Timeout.Infinite, token);
            return new GitProcess.BoundedText(string.Empty, false);
        };
        var method = typeof(GitQueryService).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        string[] arguments = ["--version"];
        var execution = (Task)method.Invoke(service, [Path.GetTempPath(), arguments, cancellation.Token, reader])!;

        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (callerCancelled)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<TimeoutException>(() => execution.WaitAsync(TestContext.Current.CancellationToken));
        }
    }

    /// <summary>Mutation commands do not inherit query policy.</summary>
    [Fact]
    public void CreateProcess_ReadOnlyAndMutationPoliciesRemainSeparate()
    {
        using var query = GitProcess.CreateProcess(Path.GetTempPath(), ["status"]);
        using var mutation = GitProcess.CreateProcess(Path.GetTempPath(), ["worktree", "list"], readOnly: false);

        Assert.False(query.StartInfo.UseShellExecute);
        Assert.Contains("core.fsmonitor=false", query.StartInfo.ArgumentList);
        Assert.Contains("diff.external=", query.StartInfo.ArgumentList);
        Assert.Equal("0", query.StartInfo.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal<string>(["worktree", "list"], mutation.StartInfo.ArgumentList);
        Assert.Null(mutation.StartInfo.StandardOutputEncoding);
    }

    /// <summary>Failed termination or an inherited pipe cannot make cleanup wait forever.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitForCleanupAsync_IncompleteExitOrReader_AbandonsOnCleanupCancellation(bool processExited)
    {
        // Model termination failing to complete exit, or a descendant retaining a pipe.
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (processExited)
        {
            exit.SetResult();
        }

        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = GitProcess.WaitForCleanupAsync(exit.Task, null, error.Task, cleanup.Token);
        Assert.False(waiting.IsCompleted);

        await cleanup.CancelAsync();
        await waiting.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(processExited, exit.Task.IsCompleted);
        Assert.False(error.Task.IsCompleted);
    }

    private static Process CreateWaitingGit()
    {
        // Git itself starts this harmless test-only shell alias as a descendant. Production
        // query entry points never accept aliases, and the runner still invokes Git directly.
        return GitProcess.CreateProcess(
            Path.GetTempPath(),
            ["-c", "alias.cleanup-probe=!echo child-ready; while :; do sleep 1; done", "cleanup-probe"],
            readOnly: false);
    }

    private static Task<GitProcess.ExecutionResult<GitProcess.BoundedText>> ExecuteAsync(Process process, int maximumCharacters, CancellationToken cancellationToken)
    {
        return GitProcess.ExecuteAsync(
            process,
            (owned, token) => GitProcess.ReadBoundedAsync(owned.StandardOutput, maximumCharacters, token),
            maximumCharacters,
            cancellationToken: cancellationToken);
    }
}
