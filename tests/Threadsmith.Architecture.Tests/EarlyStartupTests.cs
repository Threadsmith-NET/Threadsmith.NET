namespace Threadsmith.Architecture.Tests;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Core;
using TUIKit.Terminal;
using Xunit;

/// <summary>Exercises host composition under the production early terminal runner.</summary>
[Collection("TUIKit terminal")]
public static class EarlyStartupTests
{
    /// <summary>Unexpected failures after startup retain the existing process error path.</summary>
    [Fact]
    public static async Task FailuresAfterReadinessDoNotWaitForStartupAcknowledgment()
    {
        using var backend = new HeadlessBackend(100, 30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = InteractiveFrontendRunner.RunStartupAsync(
            (_, progress, _) =>
        {
            progress.MarkReady();
            throw new InvalidOperationException("Failure after readiness");
        },
            cancellation,
            backend);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await run.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(backend.IsStopped);
    }

    /// <summary>Optional timeouts recover, but process cancellation keeps its distinct frozen state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CredentialRecoveryDistinguishesProcessCancellation(bool cancelProcess)
    {
        using var process = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(process.Token);
        var progress = new SemanticStartupProgress();
        using var observation = progress.ObserveProcess();
        using (var operation = progress.Begin("credentials", "Credentials", cancellationToken: process.Token))
        {
            await attempt.CancelAsync();
            if (cancelProcess)
            {
                await process.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelComposition.GetOptionalCodexAccessTokenAsync(
                    Task.FromCanceled<string?>, NullLogger.Instance, operation, attempt.Token, process.Token));
            }
            else
            {
                Assert.Null(await ModelComposition.GetOptionalCodexAccessTokenAsync(
                    Task.FromCanceled<string?>, NullLogger.Instance, operation, attempt.Token, process.Token));
            }
        }

        Assert.Equal(cancelProcess ? SemanticStartupPhaseState.Cancelled : SemanticStartupPhaseState.Failed, Assert.Single(observation.Snapshot).State);
        Assert.Equal(cancelProcess, observation.Snapshot[0].Detail is null);
    }

    /// <summary>Configuration cannot begin before an emitted startup frame.</summary>
    [Fact]
    public static async Task HostInitializationStartsOnlyAfterEmittedStartupFrame()
    {
        using var backend = new HeadlessBackend(100, 30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = InteractiveFrontendRunner.RunStartupAsync(
            async (surface, progress, token) =>
        {
            var first = backend.TakeOutput();
            Assert.Contains("Total startup", first, StringComparison.Ordinal);
            Assert.DoesNotContain("Starting Threadsmith", first, StringComparison.Ordinal);
            Assert.Contains("Forge better code, not slop.", first, StringComparison.Ordinal);
            Assert.Contains(Ansi.EndSynchronizedUpdate, first, StringComparison.Ordinal);
            var workspaceFields = surface.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>)).ToArray();
            Assert.Equal(8, workspaceFields.Length);
            foreach (var field in workspaceFields)
            {
                var workspace = field.GetValue(surface)!;
                Assert.False((bool)workspace.GetType().GetProperty("IsValueCreated")!.GetValue(workspace)!);
            }

            using var operation = progress.Begin("configuration", "Blocked configuration", cancellationToken: token);
            entered.SetResult();
            await release.Task.WaitAsync(token);
            await surface.ConfigureStartupAsync(new ConfigurationBuilder().Build(), token);
            foreach (var field in workspaceFields)
            {
                var workspace = field.GetValue(surface)!;
                Assert.True((bool)workspace.GetType().GetProperty("IsValueCreated")!.GetValue(workspace)!);
                var value = workspace.GetType().GetProperty("Value")!;
                Assert.Same(value.GetValue(workspace), value.GetValue(workspace));
            }

            await surface.AttachStartupServicesAsync(null, null, token);
            operation?.Complete();
            return 0;
        },
            cancellation,
            backend);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        release.SetResult();
        Assert.Equal(0, await run);
    }

    /// <summary>A repeated attachment is rejected and the terminal is restored.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task RepeatedAttachmentFailsAndRestoresTerminal(bool configure)
    {
        using var backend = new HeadlessBackend(100, 30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = InteractiveFrontendRunner.RunStartupAsync(
            async (surface, _, token) =>
        {
            for (var index = 0; index < 2; index++)
            {
                if (configure)
                {
                    await surface.ConfigureStartupAsync(new ConfigurationBuilder().Build(), token);
                }
                else
                {
                    await surface.AttachStartupServicesAsync(null, null, token);
                }
            }

            return 0;
        },
            cancellation,
            backend);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await run.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(backend.IsStopped);
    }

    /// <summary>Fatal startup keeps its original result and releases acknowledgment on cancellation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task FatalOutcomeWaitsForAcknowledgmentOrCancellation(bool cancel)
    {
        using var backend = new HeadlessBackend(100, 30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = InteractiveFrontendRunner.RunStartupAsync(
            (_, progress, token) =>
        {
            using var operation = progress.Begin("database", "Database initialization", cancellationToken: token);
            throw new InvalidDataException("Host services failed", new ArgumentException("Configuration error: SemanticMinimum (65) must be between -1 and 1."));
        },
            cancellation,
            backend);
        try
        {
            var output = string.Empty;
            while (!output.Contains("press Enter to exit", StringComparison.Ordinal))
            {
                output += backend.TakeOutput();
                Assert.False(run.IsCompleted);
                await Task.Yield();
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            }

            Assert.Contains("Database initialization Failed", output, StringComparison.Ordinal);
            Assert.Contains("Host services failed", output, StringComparison.Ordinal);
            Assert.Contains("Configuration error: SemanticMinimum (65)", output, StringComparison.Ordinal);
            Assert.Contains("between", output, StringComparison.Ordinal);
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run.WaitAsync(TestContext.Current.CancellationToken));
            }
            else
            {
                backend.FeedInput("\r");
                Assert.Equal(1, await run);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
