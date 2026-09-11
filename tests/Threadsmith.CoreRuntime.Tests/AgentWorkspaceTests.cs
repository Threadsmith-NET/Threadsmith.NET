namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Contracts;
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
public static class AgentWorkspaceTests
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

    /// <summary>Verifies layout retains minimum usable rows.</summary>
    [Theory]
    [InlineData(40, 12, 1)]
    [InlineData(40, 14, 2)]
    [InlineData(40, 16, 4)]
    [InlineData(40, 20, 5)]
    [InlineData(80, 24, 5)]
    [InlineData(120, 35, 5)]
    public static void LayoutRetainsMinimumUsableRows(int width, int height, int composerRows)
    {
        var size = new Size(width, height);
        var layout = WorkspaceLayout.Create(size);
        var output = layout.FindById("transcript")!.ContentRect(size);
        var composer = layout.FindById("composer")!.ContentRect(size);
        var transcript = WorkspaceLayout.OutputContent(new Size(output.Width, output.Height));
        var header = WorkspaceLayout.OutputHeader(new Size(output.Width, output.Height));
        var editor = WorkspaceLayout.ComposerContent(new Size(composer.Width, composer.Height));
        Assert.Equal(new Rect(1, 1, width - 2, 1), header);
        Assert.Equal(header.Bottom + 1, transcript.Top);
        Assert.True(transcript.Height >= 2);
        Assert.Equal(composerRows, editor.Height);
        Assert.Equal(2, transcript.Left);
        Assert.Equal(width - 2, transcript.Right);
        Assert.Equal(2, editor.Left);
        Assert.Equal(width - 2, editor.Right);
        if (height >= 24)
        {
            Assert.Equal(3, transcript.Top);
            Assert.Equal(output.Height - 2, transcript.Bottom);
            Assert.Equal(1, editor.Top);
            Assert.Equal(composer.Height - 2, editor.Bottom);
        }

        Assert.Equal(height - 1, layout.FindById("status")!.ContentRect(size).Top);
        Assert.Equal(output.Bottom, composer.Top);
    }

    /// <summary>Child display metadata comes from its own effective request and configured provider name.</summary>
    [Fact]
    public static async Task ChildHeaderUsesItsEffectiveProviderAndContext()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var session = SessionId.New();
        var target = Target(session);
        var profile = new ModelProfile
        {
            Id = new ModelProfileId(Guid.NewGuid()),
            Name = "Child model",
            Provider = "openai-compatible",
            ProviderName = "Remote inference",
            Endpoint = new Uri("https://models.example/v1/chat/completions"),
            ModelId = "child",
            ContextWindow = 10000,
            MaximumOutputTokens = 1000,
            Capabilities = new ModelCapabilitySet { Streaming = true },
            Cost = new ModelCostMetadata(),
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.High],
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 1, Delay = TimeSpan.Zero },
        };
        var usage = new SessionUsageProjection();
        var sink = new RecordingSurface();
        await using var projection = new AgentWorkspaceProjection(sink, null, usage, new ConfiguredModelCatalog([profile]), new AgentNameCatalog(["Avery"]), false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
        await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty), timeout.Token);
        usage.ObserveRequest(session, target.RunId, new AgentRequestStatus(profile.Id, ReasoningLevel.High, 2500, 10000, 1));

        await projection.RefreshAsync(timeout.Token);

        var observed = sink.Agents.Last();
        Assert.Equal("Remote inference", observed.ProviderName);
        Assert.Equal("Child model", observed.Model);
        Assert.Equal(2500, observed.ContextTokens);
        Assert.Equal(10000, observed.ContextLimit);
        Assert.Equal(ReasoningLevel.High, observed.Reasoning);
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
        var strip = new AgentTabStrip(views, view => views.Select(view), role => role == Threadsmith.Interaction.Presentation.PresentationTextRole.AgentSelectedTabRole ? selectedStyle : CellStyle.Default);
        var cells = new CellBuffer(80, 1);
        strip.Render(new BufferSurface(cells));
        Assert.StartsWith(" MAIN", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Equal(selectedStyle, cells.Get(1, 0).Style);
        var selected = views.Ordered[1];
        views.Select(selected);
        views.Present(Text("MAIN output"));
        views.Present(Text("Child one") with { Target = first.Target });
        views.Present(Text("Child two") with { Target = second.Target });
        views.Update(second with { State = AgentRunStatus.Completed, Revision = 2 });
        Assert.Same(selected, views.Selected);
        Assert.Equal(["MAIN output"], views.Main.Transcript.Lines);
        Assert.Equal(["Child one"], selected.Transcript.Lines);
        strip.Render(new BufferSurface(cells));
        var mainWidth = UnicodeWidth.GetWidth(AgentTabStrip.Label(views.Main, 25)) + 3;
        strip.HandleMouse(new MouseEvent(MouseEventKind.Press, MouseButton.Left, mainWidth + 1, 0, KeyModifiers.None, 1));
        Assert.Same(selected, views.Selected);
        views.Update(first with { State = AgentRunStatus.Cancelled, Revision = 3 });
        views.Present(Text("late") with { Target = first.Target });
        Assert.Same(views.Main, views.Selected);
        Assert.Single(views.Ordered);
        Assert.Equal(["MAIN output"], views.Main.Transcript.Lines);
        strip.Render(new BufferSurface(cells));
        Assert.StartsWith(" MAIN", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Equal(selectedStyle, cells.Get(1, 0).Style);
    }

    /// <summary>Verifies every overflowed tab remains reachable.</summary>
    [Fact]
    public static void EveryOverflowedTabRemainsReachable()
    {
        var session = SessionId.New();
        var views = new AgentViews(_ => CellStyle.Default);
        views.Attach(session);
        for (var index = 0; index < 20; index++)
        {
            views.Update(Snapshot(Target(session), "Shackleton_" + index));
        }

        var strip = new AgentTabStrip(views, view => views.Select(view), _ => CellStyle.Default);
        var cells = new CellBuffer(40, 1);
        for (var index = 0; index < views.Ordered.Count; index++)
        {
            strip.Render(new BufferSurface(cells));
            Assert.Contains(index == 0 ? "MAIN" : "Shackleton_" + (index - 1), TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
            views.Cycle(1);
        }

        Assert.Same(views.Main, views.Selected);
    }

    /// <summary>Verifies child selection blocks every composer ingress and preserves draft.</summary>
    [Fact]
    public static async Task ChildSelectionBlocksEveryComposerIngressAndPreservesDraft()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var backend = new HeadlessBackend(80, 24);
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

    /// <summary>Successful splash phases stay out of MAIN while warnings and unsuccessful outcomes remain inspectable.</summary>
    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "The assertion observes the startup task owned and completed by this test.")]
    public static async Task StartupRetainsOnlyDiagnosticsInMain(string outcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            await surface.ShowStartupAsync("Splash logo", "Loading solution", Task.CompletedTask, token);
            await surface.SetStartupDetailsAsync(["Loading remembered solution: Sample.sln", "  (Use --solution to change)"], token);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startup = surface.ShowStartupAsync("Splash logo", "Semantic loading", loaded.Task, token);
            await surface.PresentAsync(new PresentationBatch([new PresentationTextItem([new("Startup warning\n", PresentationTextRole.Warning)])]), token);
            if (outcome == "Failed")
            {
                loaded.SetException(new InvalidOperationException("Startup failed"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => startup);
            }
            else if (outcome == "Cancelled")
            {
                loaded.SetCanceled();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
            }
            else
            {
                loaded.SetResult();
                await startup;
            }

            var read = surface.ReadComposerAsync(new ComposerRequest("Threadsmith > "), token);
            await surface.PresentAsync(Text(string.Empty), token);
            _ = backend.TakeOutput();
            backend.FeedInput("\u001b[18~\u0001\u0003\u001b[18~done\r");
            Assert.Equal("done", (await read).Text);
            var copy = Assert.Single(backend.TakeOutput().Split("\u001b]52;c;").Skip(1));
            var transcript = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(copy[..copy.IndexOf('\u001b')]));
            Assert.Contains("Startup warning", transcript, StringComparison.Ordinal);
            Assert.DoesNotContain("Splash logo", transcript, StringComparison.Ordinal);
            Assert.DoesNotContain("Loading solution", transcript, StringComparison.Ordinal);
            Assert.DoesNotContain("Completed", transcript, StringComparison.Ordinal);
            Assert.DoesNotContain("Loading remembered solution", transcript, StringComparison.Ordinal);
            Assert.DoesNotContain("Use --solution to change", transcript, StringComparison.Ordinal);
            if (outcome == "Completed")
            {
                Assert.DoesNotContain("Semantic loading", transcript, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains($"Semantic loading: {outcome}", transcript, StringComparison.Ordinal);
            }
        },
            timeout.Token);
    }

    /// <summary>Startup details and progress remain visible with a blank row below the logo's tagline.</summary>
    [Fact]
    public static void StartupModalRendersRememberedSolutionDetails()
    {
        var cells = new CellBuffer(120, 35);
        var modal = new StartupModal(
            "Threadsmith.NET\nForge better code, not slop.",
            "Semantic loading",
            ["Loading solution: Completed"],
            () => { },
            _ => CellStyle.Default,
            ["Loading remembered solution: Sample.sln", "  (Use --solution to change)"]);

        modal.Render(new BufferSurface(cells));

        var text = TUIKit.Testing.Snapshot.ToText(cells);
        var lines = text.Split('\n');
        var taglineRow = Array.FindIndex(lines, line => line.Contains("Forge better code, not slop.", StringComparison.Ordinal));
        Assert.True(taglineRow >= 0);
        Assert.Matches(@"^\s*│\s+│\s*$", lines[taglineRow + 1]);
        Assert.Contains("Loading remembered solution: Sample.sln", lines[taglineRow + 2], StringComparison.Ordinal);
        Assert.Contains("Loading remembered solution: Sample.sln", text, StringComparison.Ordinal);
        Assert.Contains("(Use --solution to change)", text, StringComparison.Ordinal);
        Assert.Contains("Loading solution: Completed", text, StringComparison.Ordinal);
        Assert.Contains("Semantic loading", text, StringComparison.Ordinal);
    }

    /// <summary>Verifies startup discards typeahead and closes before one composer read.</summary>
    [Fact]
    public static async Task StartupDiscardsTypeaheadAndClosesBeforeOneComposerRead()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startup = surface.ShowStartupAsync("Threadsmith.NET", "Semantic loading", loaded.Task, token);
            await surface.PresentAsync(Text(string.Empty), token);
            backend.FeedInput("discard\r\u001b[200~discard paste\u001b[201~");
            await surface.PresentAsync(Text(string.Empty), token);
            loaded.SetResult();
            await startup;
            var read = surface.ReadComposerAsync(new ComposerRequest("Threadsmith > "), token);
            await surface.PresentAsync(Text(string.Empty), token);
            backend.FeedInput("kept\r");
            Assert.Equal("kept", (await read).Text);
        },
            timeout.Token);
    }

    /// <summary>Verifies immediate toggles reconcile host denial and do not touch locked items.</summary>
    [Fact]
    public static async Task ImmediateTogglesReconcileHostDenialAndDoNotTouchLockedItems()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var backend = new HeadlessBackend(80, 24);
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

    /// <summary>Verifies child output before lifecycle is retained and terminal outcome precedes retirement.</summary>
    [Fact]
    public static async Task ChildOutputBeforeLifecycleIsRetainedAndTerminalOutcomePrecedesRetirement()
    {
        var session = SessionId.New();
        var target = Target(session);
        var sink = new RecordingSurface();
        var stream = new AgentDisplayStream();
        var usage = new SessionUsageProjection();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var projection = new AgentWorkspaceProjection(sink, stream, usage, null, new AgentNameCatalog(["Avery"]), false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        stream.Publish(new AgentDisplayText(session, target.RunId, "child text", false, true));
        await projection.RefreshAsync(timeout.Token);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
        var lifecycle = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty, 1);
        await projection.ObserveAsync(lifecycle, timeout.Token);
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var projection = new AgentWorkspaceProjection(sink, null, new SessionUsageProjection(), null, new AgentNameCatalog(["Avery"]), false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        var checkpoint = new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run");
        await projection.ObserveAsync(checkpoint, timeout.Token);
        var lifecycle = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty);
        await projection.ObserveAsync(lifecycle, timeout.Token);
        await projection.ObserveAsync(checkpoint with { Generation = 2 }, timeout.Token);
        Assert.Contains(sink.Agents, snapshot => snapshot.Target == target && snapshot.IsTerminal);
        var next = lifecycle with { ChildRunId = RunId.New(), Generation = 2 };
        await projection.ObserveAsync(next, timeout.Token);
        var count = sink.Agents.Count;
        await projection.ObserveAsync(lifecycle with { Revision = 100 }, timeout.Token);
        await projection.ObserveAsync(lifecycle with { Generation = 3, ChildRunId = RunId.New() }, timeout.Token);
        Assert.Equal(count, sink.Agents.Count);
        Assert.Equal("Avery", sink.Agents.Last().Name);
    }

    /// <summary>Concurrent tool completions correlate by invocation and never enter MAIN.</summary>
    [Fact]
    public static async Task ConcurrentChildToolsRouteToTheirExactOwners()
    {
        var session = SessionId.New();
        var targets = new[] { Target(session), Target(session) };
        var sink = new RecordingSurface();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var projection = new AgentWorkspaceProjection(sink, null, new SessionUsageProjection(), null, new AgentNameCatalog(), false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        var invocations = new[] { ToolInvocationId.New(), ToolInvocationId.New() };
        for (var index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
            await projection.ObserveAsync(new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty), timeout.Token);
            Assert.True(await projection.ObserveAsync(new ToolInvocationStarted(session, DateTimeOffset.UtcNow, invocations[index], "tool-" + index, target.RunId), timeout.Token));
        }

        for (var index = 1; index >= 0; index--)
        {
            Assert.True(await projection.ObserveAsync(new ToolInvocationCompleted(session, DateTimeOffset.UtcNow, invocations[index], true, "{}") { RunId = targets[index].RunId }, timeout.Token));
        }

        Assert.Equal([targets[1], targets[0]], sink.Output.Select(batch => batch.Target));
        Assert.Contains("tool-1", string.Concat(sink.Output[0].Items.OfType<PresentationTextItem>().SelectMany(item => item.Segments).Select(segment => segment.Text)), StringComparison.Ordinal);
    }

    /// <summary>Filtering limits group mutations to visible eligible IDs while details preserve full labels.</summary>
    [Fact]
    public static void ToggleFilteringRetainsStateAndBoundsGroupChanges()
    {
        var modal = new ToggleModal(
            new InteractionToggleRequest("Tools", [new("a", "Visible label", "Tools", true), new("b", "Hidden label", "Tools", false), new("c", "Visible locked", "Tools", true, true)]),
            _ => CellStyle.Default,
            () => { },
            () => { });
        modal.HandlePaste("Visible");
        Assert.Equal(["a"], modal.Members("group:Tools").Select(option => option.Id));
        modal.Reconcile("a", new(false, "Denied by host"));
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        var cells = new CellBuffer(80, 24);
        modal.Render(new BufferSurface(cells));
        Assert.Contains("Visible label", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        Assert.False(modal.Members("group:Tools")[0].Enabled);
    }

    /// <summary>Child text budgets rebalance across active panes and retire all associated retained state.</summary>
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

        Assert.Single(views.Ordered);
        Assert.Empty(views.Main.Transcript.Lines);
    }

    /// <summary>Measures the retained primitives and workspace under the same synthetic eight-stream workload.</summary>
    [Fact]
    public static void RecordSyntheticWorkspaceRenderCost()
    {
        var session = SessionId.New();
        var views = new AgentViews(_ => CellStyle.Default);
        views.Attach(session);
        var snapshots = Enumerable.Range(0, 8).Select(index => Snapshot(Target(session), "Hopper_" + index)).ToArray();
        foreach (var snapshot in snapshots)
        {
            views.Update(snapshot);
        }

        var baseline = new TranscriptView();
        var tabs = new AgentTabStrip(views, view => views.Select(view), _ => CellStyle.Default);
        var cells = new CellBuffer(120, 35);
        var surface = new BufferSurface(cells);
        var samples = new List<double>();
        var baselineSamples = new List<double>();
        const string fragment = "synthetic stream: a bounded line with Unicode 界 and a status result\n";
        for (var iteration = 0; iteration < 240; iteration++)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var snapshot in snapshots)
            {
                baseline.Present(Text(fragment));
            }

            baseline.Render(surface.CreateView(new Rect(1, 4, 118, 22)));
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (iteration >= 40)
            {
                baselineSamples.Add(elapsed);
            }

            start = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var snapshot in snapshots)
            {
                views.Present(Text(fragment) with { Target = snapshot.Target });
            }

            views.Cycle(1);
            tabs.Render(surface.CreateView(new Rect(0, 1, 120, 1)));
            WorkspaceLayout.DrawFrame(surface.CreateView(new Rect(0, 2, 120, 25)), CellStyle.Default);
            views.Selected.Transcript.Render(surface.CreateView(new Rect(1, 4, 118, 21)));
            elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (iteration >= 40)
            {
                samples.Add(elapsed);
            }
        }

        samples.Sort();
        baselineSamples.Sort();
        Console.WriteLine($"Plan 105 synthetic render 120x35, 8 streams, 200 measured frames: primitive p50={baselineSamples[100]:F3}ms p95={baselineSamples[190]:F3}ms; workspace p50={samples[100]:F3}ms p95={samples[190]:F3}ms max={samples[^1]:F3}ms");
        Assert.Equal(200, samples.Count);
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

    /// <summary>The minimum and normal compact footer retain every counter even with long paths and branches.</summary>
    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    public static void RepositoryFooterPreservesCounts(int width)
    {
        var footer = RepositoryFooter.Format("C:/very/long/repository/directory", null, new RepositoryGitStatus("long/界界/feature/branch/name", false, 1, 2, 3, 4, false), width, " | ");
        Assert.InRange(UnicodeWidth.GetWidth(footer), 1, width);
        Assert.Contains("S1 M2 U3 !4", footer, StringComparison.Ordinal);
    }

    /// <summary>A terminal-first failure and a corrected terminal outcome stay visible without reopening a child.</summary>
    [Fact]
    public static async Task TerminalCorrectionsReachMainWithoutReopening()
    {
        var session = SessionId.New();
        var target = Target(session);
        var sink = new RecordingSurface();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var projection = new AgentWorkspaceProjection(sink, null, new SessionUsageProjection(), null, new AgentNameCatalog(["Avery"]), false, timeout.Token);
        await projection.AttachAsync(session, timeout.Token);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), timeout.Token);
        var terminal = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Completed, 1, string.Empty);
        await projection.ObserveAsync(terminal, timeout.Token);
        await projection.ObserveAsync(terminal with { Status = AgentRunStatus.Failed, Revision = 2, Reason = "Join failed" }, timeout.Token);
        Assert.Empty(sink.Agents);
        Assert.Equal(2, sink.Output.Count);
        Assert.All(sink.Output, batch => Assert.Null(batch.Target));
        Assert.Contains("Updated outcome: Avery", string.Concat(sink.Output[1].Items.OfType<PresentationTextItem>().SelectMany(item => item.Segments).Select(segment => segment.Text)), StringComparison.Ordinal);
    }

    /// <summary>A delayed OS paste is discarded after leaving and returning to MAIN.</summary>
    [Fact]
    public static async Task DelayedPasteCannotCrossAnAgentSelectionEpoch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var backend = new HeadlessBackend(80, 24);
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var backend = new HeadlessBackend(80, 24);
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
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
        Assert.Empty(sink.Agents);
    }

    /// <summary>The actual renderer preserves the final conflict digit beside its bottom-right workaround.</summary>
    [Fact]
    public static async Task ComposedNarrowFooterKeepsTheLastCounter()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var backend = new HeadlessBackend(40, 12);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
            {
                await surface.PresentSessionStatusAsync(
                    new Threadsmith.Interaction.Sessions.SessionStatusSnapshot("C:/very/long/repository/directory", "repo", "model", ReasoningLevel.None, null, null, new SessionUsageSnapshot(0, 0, false))
                    {
                        GitStatus = new RepositoryGitStatus("very-long-branch-name", false, 1, 2, 3, 4, false),
                        IsPostResume = true,
                    },
                    token);
                await surface.PresentAsync(Text(string.Empty), token);
                Assert.Contains("S1 M2 U3 !4", backend.TakeOutput(), StringComparison.Ordinal);
            },
            timeout.Token);
    }

    private static AgentPresentationTarget Target(SessionId session) => new(session, DelegationId.New(), AgentAssignmentId.New(), RunId.New(), 1);

    private static AgentPresentationSnapshot Snapshot(AgentPresentationTarget target, string name) => new(target, name, AgentRole.Explorer, AgentRunStatus.Running, 1);

    private static PresentationBatch Text(string text) => new([new PresentationTextItem([new(text, PresentationTextRole.Default)])]);

    private sealed class RecordingSurface : IInteractionSurface, IAgentWorkspaceSurface
    {
        public InteractionSurfaceCapabilities Capabilities { get; } = new();

        internal List<PresentationBatch> Output { get; } = [];

        internal List<AgentPresentationSnapshot> Agents { get; } = [];

        internal List<string> Order { get; } = [];

        public Task AttachAgentSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PresentAgentAsync(AgentPresentationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Agents.Add(snapshot);
            Order.Add(snapshot.IsTerminal ? "retire" : "agent");
            return Task.CompletedTask;
        }

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default)
        {
            Output.Add(batch);
            Order.Add(batch.Target is null ? "main" : "child");
            return Task.CompletedTask;
        }

        public Task<InteractionInput> ReadComposerAsync(ComposerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InteractionSelectionResult> SelectAsync(InteractionSelectionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task PresentSessionStatusAsync(Threadsmith.Interaction.Sessions.SessionStatusSnapshot status, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
