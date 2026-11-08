namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Themes;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

public static partial class AgentWorkspaceTests
{
    /// <summary>Instruction insertion precedes queued text, diagnostics and tool activity.</summary>
    [Fact]
    public static async Task InstructionsPrecedeAlreadyAvailableTextDiagnosticsAndTools()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var target = Target(session);
        var sink = new RecordingSurface();
        var stream = new AgentDisplayStream();
        // Stop the periodic pump; direct awaited operations own every mutable snapshot in this test.
        using var pumpStop = new CancellationTokenSource();
        await pumpStop.CancelAsync();
        await using (var projection = new AgentWorkspaceProjection(sink, stream, null, null, new AgentNameCatalog(), false, pumpStop.Token))
        {
            await projection.AttachAsync(session, ct);
            await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), ct);
            await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, "running"), ct);
            stream.Publish(new AgentDisplayText(session, target.RunId, "early response", false, true));
            stream.Publish(new AgentDisplayText(session, target.RunId, "early diagnostic", false, IsDiagnostic: true));
            await projection.RefreshAsync(ct);
            var start = new ToolInvocationStarted(session, DateTimeOffset.UtcNow, ToolInvocationId.New(), "early-tool", target.RunId);
            await projection.ObserveAsync(start, ct);
            await projection.ObserveAsync(new ToolInvocationCompleted(session, DateTimeOffset.UtcNow, start.ToolInvocationId, true), ct);
            Assert.Empty(sink.Output);
            Assert.Empty(sink.Agents[^1].ToolActivities);
            await projection.ObserveAsync(Instructions(target), ct);
        }

        Assert.All(sink.Output, batch => Assert.Equal(target, batch.Target));
        var first = Assert.IsType<PresentationTextItem>(Assert.Single(sink.Output[0].Items));
        Assert.StartsWith("Instructions\n", string.Concat(first.Segments.Select(segment => segment.Text)), StringComparison.Ordinal);
        Assert.Contains(sink.Output.Skip(1), batch => batch.Items.OfType<PresentationSourceItem>().Any(item => item.SafeSource == "early response"));
        var text = string.Concat(sink.Output.SelectMany(batch => batch.Items.OfType<PresentationTextItem>()).SelectMany(item => item.Segments).Select(segment => segment.Text));
        Assert.Contains("early diagnostic", text, StringComparison.Ordinal);
        Assert.Contains("early-tool", text, StringComparison.Ordinal);
    }

    /// <summary>Literal instructions remain isolated and reject duplicate or stale identities.</summary>
    [Fact]
    public static async Task InstructionsAreLiteralIsolatedAndFencedByFullIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var first = Target(session);
        var second = Target(session);
        var sink = new RecordingSurface();
        using var pumpStop = new CancellationTokenSource();
        await pumpStop.CancelAsync();
        await using (var projection = new AgentWorkspaceProjection(sink, null, null, null, new AgentNameCatalog(), true, true, new TuiResourceLimits { MaximumAgentProgressCharacters = 3 }, pumpStop.Token))
        {
            await projection.AttachAsync(session, ct);
            // An instruction observation cannot create membership.
            await projection.ObserveAsync(Instructions(first), ct);
            Assert.Empty(sink.Agents);
            foreach (var target in new[] { first, second })
            {
                await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), ct);
                await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Queued, 1, string.Empty), ct);
            }

            var observation = Instructions(first) with { Sections = [new("Assignment", "**literal**\n界 e\u0301\n\u001b[2J[link](https://example.test)")] };
            await projection.ObserveAsync(observation with { AssignmentId = AgentAssignmentId.New() }, ct);
            await projection.ObserveAsync(observation with { Generation = 2 }, ct);
            await projection.ObserveAsync(observation with { SessionId = SessionId.New() }, ct);
            Assert.Empty(sink.Output);
            await projection.ObserveAsync(observation, ct);
            await projection.ObserveAsync(observation, ct);
            await projection.ObserveAsync(Instructions(second) with { Source = AgentInstructionSource.AssignmentOnly, Sections = [new("Assignment", "second child")] }, ct);
            await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, first.DelegationId, first.AssignmentId, first.RunId, AgentRole.Explorer, AgentRunStatus.Cancelled, 1, "cancelled", 2), ct);
            await projection.ObserveAsync(observation, ct);
        }

        var childBatches = sink.Output.Where(batch => batch.Target is not null).ToArray();
        Assert.Equal([first, second], childBatches.Select(batch => batch.Target));
        var firstSegments = Assert.IsType<PresentationTextItem>(Assert.Single(childBatches[0].Items)).Segments;
        var text = string.Concat(firstSegments.Select(segment => segment.Text));
        Assert.Contains("> **literal**\n> 界 e\u0301\n> ", text, StringComparison.Ordinal);
        Assert.Contains("[link](https://example.test)", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
        Assert.All(firstSegments, segment => Assert.Null(segment.LinkTarget));
        var secondText = string.Concat(Assert.IsType<PresentationTextItem>(Assert.Single(childBatches[1].Items)).Segments.Select(segment => segment.Text));
        Assert.StartsWith("Assignment instructions\nModel instructions were not prepared.\n", secondText, StringComparison.Ordinal);
        Assert.Contains("second child", secondText, StringComparison.Ordinal);
        Assert.DoesNotContain("second child", text, StringComparison.Ordinal);
    }

    /// <summary>An unselected early-ending child remains inspectable in the bounded existing transcript owner.</summary>
    [Fact]
    public static async Task StoppedChildInstructionsRemainInspectableUntilBoundedEviction()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var target = Target(session);
        var limits = new TuiResourceLimits { MaximumRetainedDelegations = 1 };
        var views = new AgentViews(_ => CellStyle.Default, limits);
        var sink = new RecordingSurface { Views = views };
        using var pumpStop = new CancellationTokenSource();
        await pumpStop.CancelAsync();
        await using (var projection = new AgentWorkspaceProjection(sink, null, null, null, new AgentNameCatalog(), false, true, limits, pumpStop.Token))
        {
            await projection.AttachAsync(session, ct);
            await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), ct);
            var lifecycle = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Queued, 1, string.Empty);
            await projection.ObserveAsync(lifecycle, ct);
            await projection.ObserveAsync(Instructions(target) with { Source = AgentInstructionSource.AssignmentOnly, Sections = [new("Assignment", "unselected task\r\n界 context")] }, ct);
            await projection.ObserveAsync(lifecycle with { Status = AgentRunStatus.Failed, Revision = 2 }, ct);
            await projection.ObserveAsync(lifecycle with { Status = AgentRunStatus.Cancelled, Revision = 3 }, ct);
        }

        Assert.Same(views.Main, views.Selected);
        var stopped = views.Ordered[1];
        Assert.True(stopped.Snapshot!.IsTerminal);
        Assert.Equal(AgentRunStatus.Cancelled, stopped.Snapshot.State);
        Assert.True(views.Select(stopped));
        var originalLines = stopped.Transcript.Lines.ToArray();
        Assert.Contains(originalLines, line => line.Contains("unselected task", StringComparison.Ordinal));
        Assert.Contains(originalLines, line => line.Contains("界 context", StringComparison.Ordinal));
        foreach (var width in new[] { 80, 40, 120 })
        {
            var buffer = new CellBuffer(width, 24);
            stopped.Transcript.Render(new BufferSurface(buffer));
        }

        Assert.Equal(originalLines, stopped.Transcript.Lines);
        var next = Snapshot(target with { RunId = RunId.New(), Generation = 2 }, "Next");
        views.Update(next);
        Assert.Same(stopped, views.Selected);
        views.Present(Text("next instructions") with { Target = next.Target });
        views.Update(next with { State = AgentRunStatus.Cancelled, Revision = 2 });
        Assert.DoesNotContain(stopped, views.Ordered);
        Assert.Equal(2, views.Ordered.Count);
        Assert.Same(views.Main, views.Selected);
        Assert.Contains(views.Main.Transcript.Lines, line => line.Contains("Older stopped child views omitted", StringComparison.Ordinal));
        views.Present(Text("late") with { Target = next.Target });
        Assert.Equal(["next instructions"], views.Ordered[1].Transcript.Lines);
    }

    /// <summary>Sequential children may reuse an agent name, but stopped tabs retain distinct stable labels.</summary>
    [Fact]
    public static async Task RetainedTabsDisambiguateReusedNames()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var views = new AgentViews(_ => CellStyle.Default);
        var sink = new RecordingSurface { Views = views };
        var tabs = new AgentTabStrip(views, view => views.Select(view), _ => CellStyle.Default, view => views.Close(view));
        var buffer = new CellBuffer(80, 1);
        using var pumpStop = new CancellationTokenSource();
        await pumpStop.CancelAsync();
        await using (var projection = new AgentWorkspaceProjection(sink, null, null, null, new AgentNameCatalog(["Avery"]), false, pumpStop.Token))
        {
            await projection.AttachAsync(session, ct);
            for (var index = 0; index < 2; index++)
            {
                var target = Target(session);
                await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), ct);
                var lifecycle = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Queued, 1, string.Empty);
                await projection.ObserveAsync(lifecycle, ct);
                await projection.ObserveAsync(Instructions(target), ct);
                tabs.Render(new BufferSurface(buffer));
                Assert.DoesNotContain($"Exp#{index + 1}", TUIKit.Testing.Snapshot.ToText(buffer), StringComparison.Ordinal);
                await projection.ObserveAsync(lifecycle with { Status = AgentRunStatus.Completed, Revision = 2 }, ct);
                tabs.Render(new BufferSurface(buffer));
                Assert.Contains($"Exp#{index + 1}", TUIKit.Testing.Snapshot.ToText(buffer), StringComparison.Ordinal);
            }
        }

        var stopped = views.Ordered.Skip(1).ToArray();
        Assert.All(stopped, view => Assert.Equal("Avery", view.Snapshot!.Name));
        Assert.Equal(2, stopped.Select(view => AgentTabStrip.Label(view, 25)).Distinct(StringComparer.Ordinal).Count());
        Assert.EndsWith("Exp#2 [stopped] x", AgentTabStrip.Label(stopped[0], 25), StringComparison.Ordinal);
        Assert.EndsWith("Exp#1 [stopped] x", AgentTabStrip.Label(stopped[1], 25), StringComparison.Ordinal);
        foreach (var width in new[] { 8, 9, 12, 18, 25 })
        {
            var label = AgentTabStrip.Label(stopped[0], width);
            Assert.InRange(UnicodeWidth.GetWidth(label), 1, width);
            Assert.EndsWith(" x", label, StringComparison.Ordinal);
            Assert.Contains(width >= 9 ? "stopped" : "done", label, StringComparison.Ordinal);
        }
    }

    /// <summary>The painted close cell dismisses only stopped views, including stale clicks and corrections.</summary>
    [Fact]
    public static void StoppedTabsCloseAtTheirVisibleButtonWithoutClosingLiveViews()
    {
        var session = SessionId.New();
        var views = new AgentViews(_ => CellStyle.Default);
        views.Attach(session);
        var stopped = Snapshot(Target(session), "界界_1");
        var running = Snapshot(Target(session), "Hopper");
        views.Update(stopped);
        views.Update(running);
        var stoppedView = views.Ordered.Single(view => view.Snapshot?.Target == stopped.Target);
        var runningView = views.Ordered.Single(view => view.Snapshot?.Target == running.Target);
        Assert.False(views.Close(views.Main));
        Assert.False(views.Close(runningView));
        views.Update(stopped with { State = AgentRunStatus.Completed, Revision = 2 });
        var tabs = new AgentTabStrip(views, view => views.Select(view), _ => CellStyle.Default, view => views.Close(view));
        var buffer = new CellBuffer(100, 1);
        tabs.Render(new BufferSurface(buffer));
        Assert.Contains("[stopped] x", TUIKit.Testing.Snapshot.ToText(buffer), StringComparison.Ordinal);
        var closeColumn = Enumerable.Range(1, buffer.Width - 1).Single(column =>
            buffer.Get(column, 0).Grapheme == "x" && buffer.Get(column - 1, 0).Grapheme == " ");
        var click = new MouseEvent(MouseEventKind.Press, MouseButton.Left, closeColumn, 0, KeyModifiers.None, 1);

        Assert.True(tabs.HandleMouse(click));
        Assert.Same(views.Main, views.Selected);
        Assert.Equal([views.Main, runningView], views.Ordered);
        Assert.False(views.Select(stoppedView));
        Assert.True(tabs.HandleMouse(click));
        Assert.Equal([views.Main, runningView], views.Ordered);
        views.Update(stopped with { State = AgentRunStatus.Failed, Revision = 3 });
        views.Present(Text("late output") with { Target = stopped.Target });
        Assert.Equal([views.Main, runningView], views.Ordered);
    }

    /// <summary>A new user turn removes only stopped nonfailed views and keeps session ownership and selection.</summary>
    [Fact]
    public static void NewUserTurnClosesOnlyStoppedNonfailedViews()
    {
        var session = SessionId.New();
        var views = new AgentViews(_ => CellStyle.Default);
        views.Attach(session);
        var snapshots = Enum.GetValues<AgentRunStatus>()
            .Select(state => Snapshot(Target(session), state.ToString()) with { State = state })
            .ToArray();
        foreach (var snapshot in snapshots)
        {
            views.Update(snapshot with { State = AgentRunStatus.Queued, Revision = 0 });
            views.Update(snapshot);
        }

        var completed = views.Ordered.Single(view => view.Snapshot?.State == AgentRunStatus.Completed);
        Assert.True(views.Select(completed));
        var revision = views.Revision;
        Assert.False(views.BeginUserTurn(SessionId.New()));
        Assert.Equal(revision, views.Revision);
        Assert.Same(completed, views.Selected);
        Assert.True(views.BeginUserTurn(session));
        Assert.Same(views.Main, views.Selected);
        Assert.Equal([AgentRunStatus.Failed, AgentRunStatus.Running, AgentRunStatus.Queued], views.Ordered.Skip(1).Select(view => view.Snapshot!.State).OrderDescending());
        Assert.False(views.Select(completed));
        views.Update(completed.Snapshot! with { State = AgentRunStatus.Completed, Revision = 2 });
        Assert.Equal(4, views.Ordered.Count);

        var failed = views.Ordered.Single(view => view.Snapshot?.State == AgentRunStatus.Failed);
        Assert.True(views.Select(failed));
        revision = views.Revision;
        Assert.False(views.BeginUserTurn(session));
        Assert.Same(failed, views.Selected);
        Assert.Equal(revision, views.Revision);
    }

    /// <summary>Closing the selected stopped tab by mouse or new-turn cleanup preserves MAIN's draft.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ClosingSelectedStoppedTabReturnsToMainWithoutLosingDraft(bool newTurn)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], lifetime.Cancel, backend);
        await surface.RunAsync(
            async token =>
            {
                var session = SessionId.New();
                var child = Snapshot(Target(session), "Hopper");
                await surface.AttachAgentSessionAsync(session, token);
                await surface.PresentAgentAsync(child, token);
                var read = surface.ReadComposerAsync(new ComposerRequest("MAIN > "), token);
                await backend.WaitForOutputAsync("MAIN >", token);
                backend.FeedInput("\u001b[200~saved\ndraft\u001b[201~\u001b[18~\u001b[1;5C");
                var stopped = child with { State = AgentRunStatus.Completed, Revision = 2 };
                await surface.PresentAgentAsync(stopped, token);
                await backend.WaitForOutputAsync("[stopped]", token);
                if (newTurn)
                {
                    await surface.BeginUserTurnAsync(session, token);
                }
                else
                {
                    var tabs = WorkspaceLayout.Create(backend.Size).FindById("tabs")!.ContentRect(backend.Size);
                    var label = AgentTabStrip.Label(new AgentView(stopped, new TranscriptView(), 1), 25);
                    var closeColumn = tabs.Left + UnicodeWidth.GetWidth("MAIN") + 3 + UnicodeWidth.GetWidth(label) + 1;
                    var row = tabs.Top + 1;
                    backend.FeedInput($"\u001b[<0;{closeColumn};{row}M\u001b[<0;{closeColumn};{row}m");
                }

                backend.FeedInput("\u001b[12~");
                var details = await backend.WaitForOutputAsync("Agent details", token);
                Assert.Contains("MAIN |", details, StringComparison.Ordinal);
                backend.FeedInput("\u001b[27u\u001b[18~\r");
                Assert.Equal("saved\ndraft", (await read).Text);
            },
            lifetime.Token);
        Assert.True(backend.IsStopped);
    }

    /// <summary>A retained prefix is explicitly identified before queued child output is released.</summary>
    [Fact]
    public static async Task TruncatedInstructionsShowOmissionBeforeOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        using var pumpStop = new CancellationTokenSource();
        await pumpStop.CancelAsync();
        var session = SessionId.New();
        var target = Target(session);
        var sink = new RecordingSurface();
        var stream = new AgentDisplayStream();
        await using (var projection = new AgentWorkspaceProjection(sink, stream, null, null, new AgentNameCatalog(), false, pumpStop.Token))
        {
            await projection.AttachAsync(session, ct);
            await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), ct);
            await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty), ct);
            stream.Publish(new AgentDisplayText(session, target.RunId, "child response", false, true));
            await projection.ObserveAsync(Instructions(target) with { IsTruncated = true }, ct);
        }

        var first = Assert.IsType<PresentationTextItem>(Assert.Single(sink.Output[0].Items));
        Assert.Contains(first.Segments, segment => segment.Role == PresentationTextRole.Warning && segment.Text.Contains("Instruction display omitted content", StringComparison.Ordinal));
        Assert.Contains(sink.Output.Skip(1), batch => batch.Items.OfType<PresentationSourceItem>().Any(item => item.SafeSource == "child response"));
    }

    private static AgentRunInstructionsObserved Instructions(AgentPresentationTarget target)
    {
        return new(target.SessionId, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, target.Generation, AgentInstructionSource.PreparedRequest, [new("Assignment", "assigned work")]);
    }
}
