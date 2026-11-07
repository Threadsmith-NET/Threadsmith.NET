namespace Threadsmith.Architecture.Tests;

using Threadsmith.App;
using TUIKit.Terminal;
using Xunit;

/// <summary>Checks failure diagnostics after restoring the process-owned console.</summary>
[Collection("Startup error console")]
public static class StartupFailureReportingTests
{
    /// <summary>Both failure paths preserve diagnostics beyond a constrained alternate screen.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task AcknowledgedFailurePrintsDiagnosticAfterTerminalRestoration(bool reportedFailure)
    {
        const string cause = "Failed to convert tools:config:memories:Recall:MaximumResults to System.Int32: many is not a valid integer.";
        const string context = "Invalid configuration in C:/Users/developer/.threadsmith/config.json.";
        using var backend = new HeadlessBackend(40, 12);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var error = new RestoredTerminalWriter(backend);
        var originalError = Console.Error;
        Console.SetError(error);
        try
        {
            var run = InteractiveFrontendRunner.RunStartupAsync(
                async (surface, _, token) =>
            {
                if (reportedFailure)
                {
                    await surface.ReportStartupDiagnosticAsync(context + " " + cause, token);
                    return 2;
                }

                throw new InvalidOperationException(context, new FormatException(cause));
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

                Assert.Contains("Full error shown on exit.", output, StringComparison.Ordinal);
                Assert.Equal(string.Empty, error.ToString());
                backend.FeedInput("\r");
                Assert.Equal(reportedFailure ? 2 : 1, await run);
                Assert.Contains(context, error.ToString(), StringComparison.Ordinal);
                Assert.Contains(cause, error.ToString(), StringComparison.Ordinal);
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
        finally
        {
            Console.SetError(originalError);
        }
    }

    private sealed class RestoredTerminalWriter : StringWriter
    {
        private readonly HeadlessBackend _backend;

        internal RestoredTerminalWriter(HeadlessBackend backend)
        {
            _backend = backend;
        }

        public override void WriteLine(string? value)
        {
            Assert.True(_backend.IsStopped);
            base.WriteLine(value);
        }
    }
}

/// <summary>Isolates the process-wide standard-error writer during terminal restoration checks.</summary>
[CollectionDefinition("Startup error console", DisableParallelization = true)]
public sealed class StartupErrorConsoleCollection;
