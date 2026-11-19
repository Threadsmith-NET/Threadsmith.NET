namespace Threadsmith.CoreRuntime.Tests;

using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Terminal;
using Xunit;

/// <summary>Exercises startup handoffs through the terminal loop and emitted frames.</summary>
[Collection("TUIKit terminal")]
public static class StartupModalLifecycleTests
{
    /// <summary>Cancellation releases an event-based readiness wait without requiring that event.</summary>
    [Fact]
    public static async Task CancellationReleasesUncompletedReadinessObservation()
    {
        using var backend = new ObservedBackend(100, 30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pendingReadiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], cancellation.Cancel, backend);
        var run = surface.RunAsync(
            async token =>
        {
            var startup = surface.ShowStartupAsync("Startup logo", "Semantic Loading ...", pendingReadiness.Task, token);
            await surface.SetStartupDetailsAsync([], token);
            await cancellation.CancelAsync();
            await startup;
        },
            cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run.WaitAsync(TestContext.Current.CancellationToken));
        Assert.False(pendingReadiness.Task.IsCompleted);
        Assert.True(backend.IsStopped);
    }

    /// <summary>Initial theme application leaves warning publication to the coordinator; runtime changes still warn.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public static async Task InitialThemeDoesNotDuplicateStartupWarnings(int expectedWarnings)
    {
        var capabilities = new TerminalCapabilities(TerminalColorDepth.None, false, false, false, false, false, false, false, false);
        using var backend = new ObservedBackend(100, 30, capabilities);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend, deferWorkspace: true);
        await surface.RunAsync(
            async token =>
        {
            var progress = new SemanticStartupProgress();
            using var observation = progress.ObserveProcess();
            await surface.BeginProcessStartupAsync(progress, () => observation.Snapshot, token);
            await surface.ConfigureStartupAsync(new ConfigurationBuilder().Build(), token);
            var warning = Assert.Single(surface.ThemeDiagnostics);

            if (expectedWarnings >= 1)
            {
                // The existing coordinator publishes each startup warning once.
                await surface.PresentAsync(new PresentationBatch([new PresentationTextItem([new(warning + "\n", PresentationTextRole.Warning)])]), token);
            }

            if (expectedWarnings >= 2)
            {
                await surface.SetThemeAsync(BuiltInThemes.Create()[0], token);
            }
        },
            TestContext.Current.CancellationToken);

        // RunAsync joins the terminal loop. Rendering the retained transcript is now exclusive:
        // Render changes its layout state and must never race the terminal owner's rendering.
        Assert.True(backend.IsStopped);
        var agents = Assert.IsType<Lazy<AgentViews>>(typeof(TuiKitSurface).GetField("_agentWorkspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(surface)).Value;
        var cells = new CellBuffer(300, 10);
        agents.Main.Transcript.Render(new BufferSurface(cells));
        var diagnostic = Assert.Single(surface.ThemeDiagnostics);
        Assert.Equal(expectedWarnings, TUIKit.Testing.Snapshot.ToText(cells).Split(diagnostic, StringSplitOptions.None).Length - 1);
    }
}
