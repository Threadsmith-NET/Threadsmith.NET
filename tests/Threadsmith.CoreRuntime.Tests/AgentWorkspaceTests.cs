namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Themes;
using Threadsmith.Models;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using TUIKit.Terminal;
using Xunit;

/// <summary>Regression checks for agent workspace tests.</summary>
[Collection("TUIKit terminal")]
public static partial class AgentWorkspaceTests
{
    /// <summary>Verifies names are normalized unique and stable across roles and exhaustion.</summary>
    [Fact]
    public static void NamesAreNormalizedUniqueAndStableAcrossRolesAndExhaustion()
    {
        var catalog = new AgentNameCatalog([" Avery ", "AVERY", "Avery_1", "\u001bunsafe", "界e\u0301"]);
        Assert.Equal(["Avery", "Avery_1", "界é"], catalog.GetNames(AgentRole.Explorer));
        var allocator = new AgentNameAllocator(new AgentNameCatalog(["Avery"]), new Random(1));
        var session = SessionId.New();
        var targets = Enumerable.Range(0, 5).Select(_ => Target(session)).ToArray();
        var names = targets.Select((target, index) => allocator.Allocate(target, (AgentRole)(index % 2))).ToArray();
        Assert.Equal(["Avery", "Avery_1", "Avery_2", "Avery_3", "Avery_4"], names);
        allocator.Release(targets[1]);
        Assert.Equal("Avery_1", allocator.Allocate(Target(session), AgentRole.Explorer));
        Assert.Equal("Avery_3", allocator.Allocate(targets[3], AgentRole.Implementer));
    }

    /// <summary>Verifies simultaneous allocations reserve names atomically.</summary>
    [Fact]
    public static async Task SimultaneousAllocationsReserveNamesAtomically()
    {
        var allocator = new AgentNameAllocator(new AgentNameCatalog(["Shackleton"]), new Random(2));
        var session = SessionId.New();
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() => allocator.Allocate(Target(session), AgentRole.Explorer)));
        var names = await Task.WhenAll(tasks);
        Assert.Equal(16, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Shackleton_15", names);
    }

    /// <summary>Verifies tabs preserve inactive transcripts selection and unicode hit mapping.</summary>
    [Fact]
    public static void TabsPreserveInactiveTranscriptsSelectionAndUnicodeHitMapping()
    {
        var session = SessionId.New();
        var views = new AgentViews(_ => CellStyle.Default);
        views.Attach(session);
        var first = Snapshot(Target(session), "界界_1");
        var second = Snapshot(Target(session), "Hopper");
        views.Update(first);
        views.Update(second);
        var selectedStyle = CellStyle.Default.WithForeground(Color.FromPalette(15)).WithBackground(Color.FromPalette(8));
        var inactiveStyle = CellStyle.Default.WithBackground(Color.FromPalette(4));
        var strip = new AgentTabStrip(
            views,
            view => views.Select(view),
            role => role switch
            {
                PresentationTextRole.AgentSelectedTabRole => selectedStyle,
                PresentationTextRole.AgentNotSelectedTabRole => inactiveStyle,
                _ => CellStyle.Default,
            },
            view => views.Close(view));
        var cells = new CellBuffer(80, 1);
        strip.Render(new BufferSurface(cells));
        Assert.StartsWith(" MAIN", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Equal(selectedStyle, cells.Get(1, 0).Style);
        Assert.Equal(CellStyle.Default, cells.Get(6, 0).Style);
        var gap = UnicodeWidth.GetWidth(AgentTabStrip.Label(views.Main, 25)) + 2;
        Assert.Equal(CellStyle.Default, cells.Get(gap, 0).Style);
        Assert.Equal(inactiveStyle, cells.Get(gap + 1, 0).Style);
        strip.HandleMouse(new MouseEvent(MouseEventKind.Press, MouseButton.Left, gap, 0, KeyModifiers.None, 1));
        Assert.Same(views.Main, views.Selected);
        var selected = views.Ordered.Single(view => view.Snapshot?.Target == first.Target);
        views.Select(selected);
        views.Present(Text("MAIN output"));
        views.Present(Text("Child one") with { Target = first.Target });
        views.Present(Text("Child two") with { Target = second.Target });
        views.Update(second with { State = AgentRunStatus.Completed, Revision = 2 });
        Assert.Same(selected, views.Selected);
        Assert.Equal(["MAIN output"], views.Main.Transcript.Lines);
        Assert.Equal(["Child one"], selected.Transcript.Lines);
        strip.Render(new BufferSurface(cells));
        var childColumn = Enumerable.Range(0, cells.Width).First(column => cells.Get(column, 0).Grapheme == "界");
        strip.HandleMouse(new MouseEvent(MouseEventKind.Press, MouseButton.Left, childColumn, 0, KeyModifiers.None, 1));
        Assert.Same(selected, views.Selected);
        views.Update(first with { State = AgentRunStatus.Cancelled, Revision = 3 });
        views.Present(Text("late") with { Target = first.Target });
        Assert.Same(selected, views.Selected);
        Assert.Equal(3, views.Ordered.Count);
        Assert.Equal(["Child one"], selected.Transcript.Lines);
        Assert.Equal(["MAIN output"], views.Main.Transcript.Lines);
        views.Select(views.Main);
        strip.Render(new BufferSurface(cells));
        Assert.StartsWith(" MAIN", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Equal(selectedStyle, cells.Get(1, 0).Style);
        Assert.Equal(CellStyle.Default, cells.Get(6, 0).Style);
    }

    /// <summary>New children appear beside MAIN, pushing older tabs into reachable right-side overflow.</summary>
    [Fact]
    public static void NewestChildrenAppearBesideMainAndOlderTabsRemainReachable()
    {
        var session = SessionId.New();
        var views = new AgentViews(_ => CellStyle.Default);
        views.Attach(session);
        for (var index = 0; index < 20; index++)
        {
            views.Update(Snapshot(Target(session), "Shackleton_" + index));
            Assert.Same(views.Main, views.Selected);
            Assert.Equal("Shackleton_" + index, views.Ordered[1].Snapshot?.Name);
        }

        var strip = new AgentTabStrip(views, view => views.Select(view), _ => CellStyle.Default, view => views.Close(view));
        var cells = new CellBuffer(40, 1);
        strip.Render(new BufferSurface(cells));
        Assert.Contains("Shackleton_19", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.DoesNotContain("Shackleton_0", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Equal("›", cells.Get(39, 0).Grapheme);
        var paintedNext = views.Ordered[2];
        views.Update(Snapshot(Target(session), "Shackleton_20"));
        strip.HandleMouse(new MouseEvent(MouseEventKind.Press, MouseButton.Left, 39, 0, KeyModifiers.None, 1));
        Assert.Same(paintedNext, views.Selected);
        views.Select(views.Main);
        for (var index = 0; index < views.Ordered.Count; index++)
        {
            strip.Render(new BufferSurface(cells));
            Assert.Contains(index == 0 ? "MAIN" : "Shackleton_" + (views.Ordered.Count - index - 1), TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
            views.Cycle(1);
        }

        Assert.Same(views.Main, views.Selected);
    }

    /// <summary>Verifies child selection blocks every composer ingress and preserves draft.</summary>
    [Fact]
    public static async Task ChildSelectionBlocksEveryComposerIngressAndPreservesDraft()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var session = SessionId.New();
            await surface.AttachAgentSessionAsync(session, token);
            await surface.PresentAgentAsync(Snapshot(Target(session), "Hopper"), token);
            var read = surface.ReadComposerAsync(new ComposerRequest("Threadsmith > "), token);
            await surface.PresentAsync(Text(string.Empty), token);
            backend.FeedInput("\u001b[200~saved\ndraft\u001b[201~\u001b[18~\u001b[1;5C");
            await surface.PresentAsync(Text(string.Empty), token);
            backend.FeedInput("bad\r\u001b[200~paste\n\r\u001b[201~\u001b[13~\u001b[18~");
            await surface.PresentAsync(Text(string.Empty), token);
            Assert.False(read.IsCompleted);
            backend.FeedInput("\u001b[1;5D\u001b[18~\r");
            Assert.Equal("saved\ndraft", (await read).Text);
        },
            timeout.Token);
        Assert.True(backend.IsStopped);
    }

    /// <summary>Verifies immediate toggles reconcile host denial and do not touch locked items.</summary>
    [Fact]
    public static async Task ImmediateTogglesReconcileHostDenialAndDoNotTouchLockedItems()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new List<string>();
            var toggles = surface.SelectTogglesAsync(
                new InteractionToggleRequest("Tools", [
                new("essential", "Essential", "Tools", true, true),
                new("allowed", "Allowed", "Tools", false),
                new("denied", "Denied", "Tools", false),
            ]),
                (id, enabled, _) =>
            {
                calls.Add(id);
                if (id == "denied")
                {
                    applied.TrySetResult();
                }

                return Task.FromResult(new InteractionToggleResult(id == "allowed" && enabled, id == "denied" ? "Denied by host" : null));
            },
                token);
            await surface.PresentAsync(Text(string.Empty), token);
            backend.FeedInput(" ");
            await applied.Task.WaitAsync(token);
            await surface.PresentAsync(Text(string.Empty), token);
            Assert.False(toggles.IsCompleted);
            backend.FeedInput("\u001b[27u");
            await toggles;
            Assert.Equal(["allowed", "denied"], calls);
        },
            timeout.Token);
    }

    /// <summary>The native action menu dispatches only the selected leaf and Esc cancels without closing its parent list.</summary>
    [Fact]
    public static async Task AuthenticationActionCanBeCancelledAndListRemainsUsable()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(100, 28);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var toggles = surface.SelectActionTogglesAsync(
                new InteractionToggleRequest("Connections", [new("oauth", "OAuth profile", "Connections", false) { Actions = [new("authenticate", "Sign in / Authenticate")] }]),
                (_, enabled, _) => Task.FromResult(new InteractionToggleResult(enabled)),
                async (id, action, operationToken) =>
            {
                Assert.Equal("oauth", id);
                Assert.Equal("authenticate", action);
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, operationToken);
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                    cancelled.SetResult();
                }

                return new InteractionToggleResult(false, "Authentication cancelled.");
            },
                token);
            await surface.PresentAsync(Text(string.Empty), token);
            backend.TakeOutput();
            backend.FeedInput("\u001b[B\u001bOR");
            var output = string.Empty;
            while (!output.Contains("Sign in / Authenticate", StringComparison.Ordinal))
            {
                output += await backend.ReadOutputAsync(token);
            }

            backend.FeedInput("\r");
            await started.Task.WaitAsync(token);
            backend.FeedInput("\u001b[27u");
            await cancelled.Task.WaitAsync(token);
            output = string.Empty;
            while (!output.Contains("Authentication cancelled.", StringComparison.Ordinal))
            {
                output += await backend.ReadOutputAsync(token);
            }

            Assert.False(toggles.IsCompleted);
            backend.FeedInput("\u001b[27u");
            await toggles;
        },
            timeout.Token);
    }

    /// <summary>The progress omission marker is included in the configured character budget.</summary>
    [Theory]
    [InlineData(1, "…")]
    [InlineData(3, "ab…")]
    [InlineData(6, "abcdef")]
    public static async Task ProgressSummaryIncludesMarkerWithinLimit(int limit, string expected)
    {
        var session = SessionId.New();
        var target = Target(session);
        var sink = new RecordingSurface();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var projection = new AgentWorkspaceProjection(sink, null, null, null, new AgentNameCatalog(["Avery"]), false, true, new TuiResourceLimits { MaximumAgentProgressCharacters = limit }, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
        await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Completed, 1, "abcdef"), timeout.Token);
        var text = string.Concat(Assert.Single(sink.Output).Items.OfType<PresentationTextItem>().SelectMany(item => item.Segments).Select(segment => segment.Text));
        Assert.EndsWith(" — " + expected + "\n", text, StringComparison.Ordinal);
    }

    /// <summary>Child build completions honor the same captured limits as MAIN.</summary>
    [Fact]
    public static async Task ChildBuildDiagnosticsHonorCapturedDisplayLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var target = Target(session);
        var sink = new RecordingSurface();
        var limits = new TuiResourceLimits { MaximumBuildDiagnostics = 1 };
        await using var projection = new AgentWorkspaceProjection(sink, null, null, null, new AgentNameCatalog(["Avery"]), false, false, limits, ct);
        await projection.AttachAsync(session, ct);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), ct);
        await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty), ct);
        await projection.ObserveAsync(Instructions(target), ct);
        sink.Output.Clear();
        var started = new ToolInvocationStarted(session, DateTimeOffset.UtcNow, ToolInvocationId.New(), "dotnet_build", target.RunId);
        var warning = new Diagnostic { Id = "1", Code = "CS8604", Severity = DiagnosticSeverity.Warning, Project = "Example", TargetFramework = "net10.0", Message = "Possible null", Confidence = SemanticConfidenceLevel.None };
        var result = new ValidationToolResult("build", ValidationInvocationKind.Build, ValidationAuthority.Exploratory, false, "Example.csproj", [], [warning, warning with { Id = "2", Code = "CS0246", Severity = DiagnosticSeverity.Error }], string.Empty, 1, TimeSpan.Zero, false, false);
        var completed = new ToolInvocationCompleted(session, started.OccurredAt, started.ToolInvocationId, true, JsonSerializer.Serialize(result));
        await projection.ObserveAsync(started, ct);
        await projection.ObserveAsync(completed, ct);
        var text = string.Concat(sink.Output.Where(batch => batch.Target == target).SelectMany(batch => batch.Items.OfType<PresentationTextItem>()).SelectMany(item => item.Segments).Select(segment => segment.Text));
        Assert.Contains("Error CS0246", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Warning CS8604", text, StringComparison.Ordinal);
        Assert.Contains("1 additional diagnostics omitted", text, StringComparison.Ordinal);
        var main = new ConversationTranscript(string.Empty, limits: limits);
        main.Apply(started);
        main.Apply(completed);
        Assert.Equal(main.Text, text);
    }

    /// <summary>Child tool starts publish immediately and one completion leaves the other tool visible.</summary>
    [Fact]
    public static async Task ConcurrentChildToolsPublishIndependentActivityImmediately()
    {
        var session = SessionId.New();
        var target = Target(session);
        var sink = new RecordingSurface();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var projection = new AgentWorkspaceProjection(sink, null, null, null, new AgentNameCatalog(["Avery"]), false, false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
        var lifecycle = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty);
        await projection.ObserveAsync(lifecycle, timeout.Token);
        await projection.ObserveAsync(Instructions(target), timeout.Token);
        sink.Output.Clear();
        var first = new ToolInvocationStarted(session, DateTimeOffset.UtcNow, ToolInvocationId.New(), "first", target.RunId, Source: new ToolActivitySource(ToolActivitySourceKind.Mcp, "Server"));
        var second = first with { ToolInvocationId = ToolInvocationId.New(), ToolName = "second" };
        await projection.ObserveAsync(first, timeout.Token);
        Assert.Single(sink.Agents[^1].ToolActivities);
        await projection.ObserveAsync(second, timeout.Token);
        var activities = sink.Agents[^1].ToolActivities;
        Assert.Equal(2, activities.Count);
        Assert.All(activities, activity => Assert.False(activity.ShowDuration));
        var firstCompleted = new ToolInvocationCompleted(session, DateTimeOffset.UtcNow, first.ToolInvocationId, true);
        await projection.ObserveAsync(firstCompleted, timeout.Token);
        Assert.Same(activities[1], Assert.Single(sink.Agents[^1].ToolActivities));
        var secondCompleted = new ToolInvocationCompleted(session, DateTimeOffset.UtcNow, second.ToolInvocationId, true);
        await projection.ObserveAsync(secondCompleted, timeout.Token);
        Assert.Empty(sink.Agents[^1].ToolActivities);

        var text = string.Concat(sink.Output.Where(batch => batch.Target == target)
            .SelectMany(batch => batch.Items.OfType<PresentationTextItem>())
            .SelectMany(item => item.Segments)
            .Select(segment => segment.Text));
        Assert.Contains("   └ mcp Server" + Environment.NewLine + Environment.NewLine + " MCP:", text, StringComparison.Ordinal);
        var main = new ConversationTranscript(string.Empty, false);
        main.Apply(first);
        main.Apply(second);
        main.Apply(firstCompleted);
        main.Apply(secondCompleted);
        Assert.Equal(main.Text, text);

        await projection.ObserveAsync(lifecycle with { Status = AgentRunStatus.Cancelled, Revision = 2 }, timeout.Token);
        Assert.Empty(sink.Agents[^1].ToolActivities);
    }

    /// <summary>Verifies child output before lifecycle is retained and terminal outcome precedes retirement.</summary>
    [Fact]
    public static async Task ChildOutputBeforeLifecycleIsRetainedAndTerminalOutcomePrecedesRetirement()
    {
        var session = SessionId.New();
        var target = Target(session);
        var sink = new RecordingSurface();
        var stream = new AgentDisplayStream();
        var usage = new SessionUsageProjection();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var projection = new AgentWorkspaceProjection(sink, stream, usage, null, new AgentNameCatalog(["Avery"]), false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        stream.Publish(new AgentDisplayText(session, target.RunId, "child text", false, true));
        await projection.RefreshAsync(timeout.Token);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
        var lifecycle = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty, 1);
        await projection.ObserveAsync(lifecycle, timeout.Token);
        await projection.ObserveAsync(Instructions(target), timeout.Token);
        await projection.RefreshAsync(timeout.Token);
        Assert.Contains(sink.Output, batch => batch.Target == target && batch.Items.OfType<PresentationSourceItem>().Any(item => item.SafeSource == "child text"));
        await projection.ObserveAsync(lifecycle with { Status = AgentRunStatus.Failed, Reason = "bounded failure", Revision = 2 }, timeout.Token);
        var retired = sink.Order.LastIndexOf("retire");
        Assert.Equal("main", sink.Order[retired - 1]);
        var count = sink.Output.Count;
        var snapshots = sink.Agents.Count;
        stream.Publish(new AgentDisplayText(session, target.RunId, "late", false));
        await projection.RefreshAsync(timeout.Token);
        await projection.ObserveAsync(lifecycle, timeout.Token);
        Assert.Equal(count, sink.Output.Count);
        Assert.Equal(snapshots, sink.Agents.Count);
        Assert.Single(sink.Agents.Select(item => item.Target).Distinct());
    }

    /// <summary>A new accepted generation retires its predecessor and rejects stale lifecycle updates.</summary>
    [Fact]
    public static async Task RetryRetiresOldGenerationWithoutResurrection()
    {
        var session = SessionId.New();
        var target = Target(session);
        var sink = new RecordingSurface();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var projection = new AgentWorkspaceProjection(sink, null, new SessionUsageProjection(), null, new AgentNameCatalog(["Avery"]), false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        var checkpoint = new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run");
        await projection.ObserveAsync(checkpoint, timeout.Token);
        var lifecycle = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty);
        await projection.ObserveAsync(lifecycle, timeout.Token);
        await projection.ObserveAsync(Instructions(target), timeout.Token);
        await projection.ObserveAsync(checkpoint with { Generation = 2 }, timeout.Token);
        Assert.Contains(sink.Agents, snapshot => snapshot.Target == target && snapshot.IsTerminal);
        var next = lifecycle with { ChildRunId = RunId.New(), Generation = 2 };
        await projection.ObserveAsync(next, timeout.Token);
        var nextTarget = target with { RunId = next.ChildRunId, Generation = 2 };
        await projection.ObserveAsync(Instructions(nextTarget), timeout.Token);
        await projection.ObserveAsync(Instructions(target), timeout.Token);
        Assert.Equal([target, nextTarget], sink.Output.Where(batch => batch.Target is not null).Select(batch => batch.Target));
        var count = sink.Agents.Count;
        await projection.ObserveAsync(lifecycle with { Revision = 100 }, timeout.Token);
        await projection.ObserveAsync(lifecycle with { Generation = 3, ChildRunId = RunId.New() }, timeout.Token);
        Assert.Equal(count, sink.Agents.Count);
        Assert.Equal("Avery", sink.Agents[^1].Name);
    }

    /// <summary>Concurrent tool completions correlate by invocation and never enter MAIN.</summary>
    [Fact]
    public static async Task ConcurrentChildToolsRouteToTheirExactOwners()
    {
        var session = SessionId.New();
        var targets = new[] { Target(session), Target(session) };
        var sink = new RecordingSurface();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var projection = new AgentWorkspaceProjection(sink, null, new SessionUsageProjection(), null, new AgentNameCatalog(), false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        var invocations = new[] { ToolInvocationId.New(), ToolInvocationId.New() };
        for (var index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
            await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty), timeout.Token);
            await projection.ObserveAsync(Instructions(target), timeout.Token);
            Assert.True(await projection.ObserveAsync(new ToolInvocationStarted(session, DateTimeOffset.UtcNow, invocations[index], "tool-" + index, target.RunId), timeout.Token));
        }

        sink.Output.Clear();
        for (var index = 1; index >= 0; index--)
        {
            Assert.True(await projection.ObserveAsync(new ToolInvocationCompleted(session, DateTimeOffset.UtcNow, invocations[index], true, "{}") { RunId = targets[index].RunId }, timeout.Token));
        }

        Assert.Equal([targets[1], targets[0]], sink.Output.Select(batch => batch.Target));
        Assert.Contains("tool-1", string.Concat(sink.Output[0].Items.OfType<PresentationTextItem>().SelectMany(item => item.Segments).Select(segment => segment.Text)), StringComparison.Ordinal);
    }

    /// <summary>Active and stopped child text shares the same aggregate budget until session detach.</summary>
    [Fact]
    public static void ChildRetentionStaysWithinAggregateBudget()
    {
        var session = SessionId.New();
        var views = new AgentViews(_ => CellStyle.Default);
        views.Attach(session);
        var snapshots = Enumerable.Range(0, 5).Select(index => Snapshot(Target(session), "Avery_" + index)).ToArray();
        foreach (var snapshot in snapshots)
        {
            views.Update(snapshot);
        }

        foreach (var snapshot in snapshots)
        {
            views.Present(Text(new string('x', TranscriptView.ByteLimit + 1)) with { Target = snapshot.Target });
        }

        Assert.InRange(views.Ordered.Skip(1).Sum(view => view.Transcript.RetainedBytes), 0, AgentViews.ChildTextBudget / 2);
        foreach (var snapshot in snapshots)
        {
            views.Update(snapshot with { State = AgentRunStatus.Completed, Revision = 2 });
        }

        Assert.Equal(6, views.Ordered.Count);
        Assert.InRange(views.Ordered.Skip(1).Sum(view => view.Transcript.RetainedBytes), 0, AgentViews.ChildTextBudget / 2);
        views.Attach(SessionId.New());
        Assert.Single(views.Ordered);
        Assert.Empty(views.Main.Transcript.Lines);
    }

    /// <summary>Modifier chords cannot change either locked or eligible checkbox state.</summary>
    [Theory]
    [InlineData(KeyModifiers.Shift, 0)]
    [InlineData(KeyModifiers.Ctrl, 1)]
    [InlineData(KeyModifiers.Alt, 2)]
    public static void ModifiedSpaceCannotToggleCheckTree(KeyModifiers modifier, int down)
    {
        var modal = new ToggleModal(
            new InteractionToggleRequest("Tools", [new("locked", "Essential", "Tools", true, true), new("allowed", "Optional", "Tools", false)]),
            _ => CellStyle.Default,
            () => { },
            () => { });
        for (var index = 0; index < down; index++)
        {
            modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        }

        var cells = new CellBuffer(80, 24);
        modal.Render(new BufferSurface(cells));
        var before = TUIKit.Testing.Snapshot.ToText(cells);
        modal.HandleKey(KeyEvent.Char(' ', modifier));
        modal.Render(new BufferSurface(cells));
        Assert.Equal(before, TUIKit.Testing.Snapshot.ToText(cells));
        Assert.False(modal.Changes.TryRead(out _));
    }

    /// <summary>Valid long underscores and wide labels cannot erase the role or exceed the tab budget.</summary>
    [Theory]
    [InlineData("A_123456789012345678901234567890")]
    [InlineData("Long_name_with_many_letters")]
    [InlineData("界界界界界界界界界界界界界界_10")]
    public static void CompactTabLabelsStayWithinTheirMeasuredBudget(string name)
    {
        var view = new AgentView(Snapshot(Target(SessionId.New()), name), new TranscriptView());
        var label = AgentTabStrip.Label(view, 25);
        Assert.InRange(UnicodeWidth.GetWidth(label), 1, 25);
        Assert.EndsWith("Exp", label, StringComparison.Ordinal);
    }

    /// <summary>A terminal-first failure and a corrected terminal outcome stay visible without reopening a child.</summary>
    [Fact]
    public static async Task TerminalCorrectionsReachMainWithoutReopening()
    {
        var session = SessionId.New();
        var target = Target(session);
        var views = new AgentViews(_ => CellStyle.Default);
        var sink = new RecordingSurface { Views = views };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var projection = new AgentWorkspaceProjection(sink, null, new SessionUsageProjection(), null, new AgentNameCatalog(["Avery"]), false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
        var terminal = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Completed, 1, string.Empty);
        await projection.ObserveAsync(terminal, timeout.Token);
        await projection.ObserveAsync(terminal with { Status = AgentRunStatus.Failed, Revision = 2, Reason = "Join failed" }, timeout.Token);
        Assert.True(Assert.Single(sink.Agents).IsTerminal);
        Assert.Single(views.Ordered);
        Assert.Equal(2, sink.Output.Count);
        Assert.All(sink.Output, batch => Assert.Null(batch.Target));
        Assert.Equal(
            [PresentationTextRole.Success, PresentationTextRole.Error],
            sink.Output.SelectMany(batch => batch.Items.OfType<PresentationTextItem>())
                .SelectMany(item => item.Segments)
                .Select(segment => segment.Role));
        Assert.Contains("Updated outcome: Avery", string.Concat(sink.Output[1].Items.OfType<PresentationTextItem>().SelectMany(item => item.Segments).Select(segment => segment.Text)), StringComparison.Ordinal);
    }

    /// <summary>A delayed OS paste is discarded after leaving and returning to MAIN.</summary>
    [Fact]
    public static async Task DelayedPasteCannotCrossAnAgentSelectionEpoch()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(80, 24);
        var clipboard = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var surface = new TuiKitSurface(
            BuiltInThemes.Create()[0],
            timeout.Cancel,
            backend,
            token =>
            {
                requested.TrySetResult();
                return clipboard.Task.WaitAsync(token);
            });
        await surface.RunAsync(
            async token =>
            {
                var session = SessionId.New();
                await surface.AttachAgentSessionAsync(session, token);
                await surface.PresentAgentAsync(Snapshot(Target(session), "Hopper"), token);
                var read = surface.ReadComposerAsync(new ComposerRequest("MAIN > "), token);
                await surface.PresentAsync(Text(string.Empty), token);
                backend.FeedInput("draft\u0016");
                await requested.Task.WaitAsync(token);
                backend.FeedInput("\u001b[18~\u001b[1;5C");
                await surface.PresentAsync(Text(string.Empty), token);
                clipboard.SetResult("stale paste");
                await surface.PresentAsync(Text(string.Empty), token);
                backend.FeedInput("\u001b[1;5D\u001b[18~\r");
                Assert.Equal("draft", (await read).Text);
            },
            timeout.Token);
    }

    /// <summary>Resize recovery drains work below minimum and routes an immediate click using the painted geometry.</summary>
    [Fact]
    public static async Task ResizeAndModalMouseInputKeepTheComposerDestination()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
            {
                var session = SessionId.New();
                await surface.AttachAgentSessionAsync(session, token);
                await surface.PresentAgentAsync(Snapshot(Target(session), "Hopper"), token);
                var read = surface.ReadComposerAsync(new ComposerRequest("MAIN > "), token);
                await surface.PresentAsync(Text(string.Empty), token);
                var selector = surface.SelectAsync(new InteractionSelectionRequest("Modal", [new("one", "Choice")]), token);
                await surface.PresentAsync(Text(string.Empty), token);
                backend.FeedInput("\u001b[<0;15;2M\u001b[27u");
                Assert.True((await selector).IsCancelled);
                backend.Resize(2, 2);
                await surface.PresentAsync(Text("retained during small terminal"), token);
                backend.Resize(40, 12);
                backend.FeedInput("\u001b[<0;2;10Mtyped\r");
                Assert.Equal("typed", (await read).Text);
            },
            timeout.Token);
        Assert.True(backend.IsStopped);
    }

    /// <summary>Normal child-finished phases and retries keep only bounded recent identity metadata.</summary>
    [Theory]
    [InlineData(DelegationCheckpointPhase.ResearchJoined)]
    [InlineData(DelegationCheckpointPhase.ReviewsJoined)]
    [InlineData(DelegationCheckpointPhase.WorkersFrozen)]
    public static async Task SuccessfulChurnAndRetryPreserveBoundedTerminalCorrections(DelegationCheckpointPhase phase)
    {
        var session = SessionId.New();
        var targets = Enumerable.Range(0, 65).Select(_ => Target(session)).ToArray();
        var sink = new RecordingSurface();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var projection = new AgentWorkspaceProjection(sink, null, new SessionUsageProjection(), null, new AgentNameCatalog(["Avery"]), false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        foreach (var target in targets.Take(64))
        {
            await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
            await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Completed, 1, string.Empty), timeout.Token);
            await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), phase, 1, "joined"), timeout.Token);
        }

        var retry = targets[0] with { RunId = RunId.New(), Generation = 2 };
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, retry.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 2, "retry"), timeout.Token);
        var completed = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, retry.DelegationId, retry.AssignmentId, retry.RunId, AgentRole.Explorer, AgentRunStatus.Completed, 2, string.Empty);
        await projection.ObserveAsync(completed, timeout.Token);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, retry.DelegationId, RunId.New(), phase, 2, "joined"), timeout.Token);
        var count = sink.Output.Count;
        await projection.ObserveAsync(completed with { Status = AgentRunStatus.Failed, Revision = 2, Reason = "Join failure" }, timeout.Token);
        Assert.Equal(count + 1, sink.Output.Count);

        var last = targets[^1];
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, last.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
        await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, last.DelegationId, last.AssignmentId, last.RunId, AgentRole.Explorer, AgentRunStatus.Completed, 1, string.Empty), timeout.Token);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, last.DelegationId, RunId.New(), phase, 1, "joined"), timeout.Token);
        Assert.Equal(64, projection.RetainedDelegationCount);
        Assert.All(sink.Agents, snapshot => Assert.True(snapshot.IsTerminal));
    }

    /// <summary>Real interleaved child completions carry their own live source into the shared viewer.</summary>
    [Fact]
    public static async Task ChildCompletionProjectionPreservesExactSourceIdentity()
    {
        var token = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        var opened = new List<ToolSourceDetail>();
        var views = new AgentViews(_ => CellStyle.Default, sourceRequested: opened.Add);
        var sink = new RecordingSurface { Views = views };
        await using var projection = new AgentWorkspaceProjection(sink, null, null, null, new AgentNameCatalog(["Avery", "Robin"]), false, token);
        await projection.AttachAsync(session, token);
        var targets = new[] { Target(session), Target(session) };
        var sources = targets.Select((_, index) => new ToolSourceDetail
        {
            ToolInvocationId = ToolInvocationId.New(),
            Language = "csharp",
            Availability = ToolSourceAvailability.Available,
            Source = $"// child {index}\r\n\t42",
        }).ToArray();
        for (var index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            await projection.ObserveAsync(new DelegationCheckpointWritten(session, now, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "accepted"), token);
            await projection.ObserveAsync(new AgentRunLifecycleObserved(session, now, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, "running"), token);
            await projection.ObserveAsync(new AgentRunInstructionsObserved(session, now, target.DelegationId, target.AssignmentId, target.RunId, 1, AgentInstructionSource.AssignmentOnly, [new("Task", "Run a script")]), token);
            Assert.True(await projection.ObserveAsync(new ToolInvocationStarted(session, now, sources[index].ToolInvocationId, "csharp_script", target.RunId), token));
        }

        for (var index = targets.Length - 1; index >= 0; index--)
        {
            var target = targets[index];
            var completion = new ToolInvocationCompleted(session, now, sources[index].ToolInvocationId, index == 0, ResultJson: "{}")
            {
                RunId = target.RunId,
                SourceDetail = sources[index],
            };
            Assert.True(await projection.ObserveAsync(completion, token));
            var batch = Assert.Single(sink.Output, batch => batch.Items.OfType<PresentationTextItem>().Any(item => item.SourceDetail == sources[index]));
            Assert.Equal(target, batch.Target);
            Assert.Same(sources[index], Assert.Single(batch.Items.OfType<PresentationTextItem>()).SourceDetail);
            var view = views.Ordered.Single(view => view.Snapshot?.Target == target).Transcript;
            var buffer = new CellBuffer(110, 40);
            view.Render(new BufferSurface(buffer));
            var rows = TUIKit.Testing.Snapshot.ToText(buffer).Split('\n');
            var row = Array.FindIndex(rows, line => line.Contains("View full script", StringComparison.Ordinal));
            Assert.True(row >= 2);
            Assert.StartsWith(" TOOLS: csharp_script - ", rows[row - 2], StringComparison.Ordinal);
            Assert.StartsWith("   │ [+] C# script execution", rows[row - 1], StringComparison.Ordinal);
            Assert.StartsWith("   └ View full script…", rows[row], StringComparison.Ordinal);
            var linkColumn = rows[row].IndexOf("View full script", StringComparison.Ordinal);
            Assert.True(view.HandleMouse(new(MouseEventKind.Press, MouseButton.Left, linkColumn, row, KeyModifiers.None, 1)));
            Assert.Same(sources[index], opened[^1]);
        }

        Assert.Equal(2, opened.Count);
        Assert.Empty(views.Main.Transcript.Lines);
    }

    private static AgentPresentationTarget Target(SessionId session)
    {
        return new(session, DelegationId.New(), AgentAssignmentId.New(), RunId.New(), 1);
    }

    private static AgentPresentationSnapshot Snapshot(AgentPresentationTarget target, string name)
    {
        return new(target, name, AgentRole.Explorer, AgentRunStatus.Running, 1);
    }

    private static PresentationBatch Text(string text)
    {
        return new([new PresentationTextItem([new(text, PresentationTextRole.Default)])]);
    }

    private sealed class RecordingSurface : IInteractionSurface, IAgentWorkspaceSurface
    {
        public InteractionSurfaceCapabilities Capabilities { get; } = new();

        internal AgentViews? Views { get; init; }

        internal List<PresentationBatch> Output { get; } = [];

        internal List<AgentPresentationSnapshot> Agents { get; } = [];

        internal List<string> Order { get; } = [];

        public Task AttachAgentSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        {
            Views?.Attach(sessionId);
            return Task.CompletedTask;
        }

        public Task BeginUserTurnAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        {
            Views?.BeginUserTurn(sessionId);
            return Task.CompletedTask;
        }

        public Task PresentAgentAsync(AgentPresentationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Views?.Update(snapshot);
            Agents.Add(snapshot);
            Order.Add(snapshot.IsTerminal ? "retire" : "agent");
            return Task.CompletedTask;
        }

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default)
        {
            Views?.Present(batch);
            Output.Add(batch);
            Order.Add(batch.Target is null ? "main" : "child");
            return Task.CompletedTask;
        }

        public Task<InteractionInput> ReadComposerAsync(ComposerRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InteractionSelectionResult> SelectAsync(InteractionSelectionRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task PresentSessionStatusAsync(Threadsmith.Interaction.Sessions.SessionStatusSnapshot status, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
