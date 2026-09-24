namespace Threadsmith.CoreRuntime.Tests;

using System.Diagnostics;
using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Interaction.Themes;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using TUIKit.Terminal;
using Xunit;

/// <summary>Checks additive accounting, shared query ownership and native presentation input.</summary>
[Collection("TUIKit terminal")]
public static class ContextUsageTests
{
    /// <summary>Flat source rows preserve prepared order, omit empty entries and keep category totals on the right.</summary>
    [Fact]
    public static void InstructionSourcesAreVisibleWhenModalOpens()
    {
        var instruction = new ContextUsageComponent("instructions", "Repository instructions", "Repository bundle", "Messages", 100)
        {
            Children =
            [
                new("agents", "Repository instructions", "AGENTS.md", "Messages", 60),
                new("append", "Appended prompts", "Append bundle", "Messages", 40)
                {
                    Children = [new("a", "Appended prompts", "first-append.md", "Messages", 30), new("b", "Appended prompts", "second-append.md", "Messages", 10)],
                },
                new("empty", "Memories", "Empty memory", "Messages", 0),
            ],
        };
        var snapshot = Snapshot(new ModelWireEstimate
        {
            Components =
            [
                new("system", "System prompt", "Host policy", "Messages", 10),
                instruction,
                new("empty-tools", "Tools", "Empty tool", "Native tools", 0),
                new("tool-b", "Tools", "tool-b", "Native tools", 10),
                new("tool-a", "Tools", "tool-a", "Native tools", 20),
            ],
            WireInputTokens = 140,
            StablePrefixComponentCount = 5,
        });
        var contributions = ContextUsageFormatter.OrderedContributions(snapshot).ToArray();
        Assert.Equal(["Host policy", "AGENTS.md", "first-append.md", "second-append.md", "tool-b", "tool-a"], contributions.Select(item => item.Label));
        Assert.Equal(snapshot.InputTokens, contributions.Sum(item => item.Tokens));
        Assert.Equal(140, snapshot.StablePrefixTokens);
        Assert.Equal(40, ContextUsageFormatter.Categories(snapshot).Single(item => item.Category == "Appended prompts").Tokens);
        Assert.Equal(30, ContextUsageFormatter.Categories(snapshot).Single(item => item.Category == "Tools").Tokens);

        var modal = new ContextUsageModal(snapshot, _ => CellStyle.Default, () => { }, () => { });
        var buffer = new CellBuffer(160, 50);
        modal.Render(new BufferSurface(buffer));
        var rendered = Read(buffer);
        var formatted = ContextUsageFormatter.Format(snapshot);
        var renderedLines = rendered.Split('\n');
        var firstStableLabelRow = Array.FindIndex(renderedLines, line => line.Contains("[Messages] Host policy", StringComparison.Ordinal));
        var finalStableLabelRow = Array.FindIndex(renderedLines, line => line.Contains("[Native tools] tool-a", StringComparison.Ordinal));
        var stableMarkerColumn = renderedLines[firstStableLabelRow].IndexOf("[Messages] Host policy", StringComparison.Ordinal) - 1;
        Assert.Equal('│', renderedLines[firstStableLabelRow][stableMarkerColumn]);
        Assert.Equal('│', renderedLines[firstStableLabelRow + 1][stableMarkerColumn]);
        Assert.Equal(stableMarkerColumn, renderedLines[finalStableLabelRow].IndexOf("[Native tools] tool-a", StringComparison.Ordinal) - 1);
        Assert.Equal('└', renderedLines[finalStableLabelRow][stableMarkerColumn]);
        Assert.Equal(stableMarkerColumn + 2, Array.FindIndex(renderedLines[firstStableLabelRow + 1].ToCharArray(), character => character is >= '\u2580' and <= '\u258f'));
        foreach (var text in new[] { rendered, formatted })
        {
            Assert.DoesNotContain("bundle", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Empty", text, StringComparison.Ordinal);
            Assert.Contains("cache eligible", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains('└', text);
            var previous = -1;
            foreach (var item in contributions)
            {
                var position = text.IndexOf($"[{item.Container}] {item.Label}", StringComparison.Ordinal);
                Assert.True(position > previous);
                previous = position;
            }
        }

        Assert.Contains("Appended prompts 28.6%", rendered, StringComparison.Ordinal);
        Assert.Contains("Tools 21.4%", rendered, StringComparison.Ordinal);
        modal.HandleKey(new KeyEvent(KeyCode.Enter, 0, KeyModifiers.None));
        modal.Render(new BufferSurface(buffer));
        Assert.Equal(rendered, Read(buffer));
    }

    /// <summary>Eviction releases detailed metadata while preserving numeric status and accounting ownership.</summary>
    [Fact]
    public static void RequestDetailRetentionIsBoundedWithoutErasingUsageStatus()
    {
        var usage = new SessionUsageProjection(maximumContextSnapshots: 2);
        var sessions = Enumerable.Range(0, 3).Select(_ => SessionId.New()).ToArray();
        foreach (var session in sessions)
        {
            var run = RunId.New();
            usage.ObservePreparedRequest(session, new ModelRequestUsageId(run, "conversation", 0, Guid.NewGuid()), Request(run), 10000);
        }

        Assert.Null(usage.GetRequestStatus(sessions[0])?.ContextUsage);
        Assert.NotNull(usage.GetRequestStatus(sessions[0])?.ContextTokens);
        Assert.All(sessions.Skip(1), session => Assert.NotNull(usage.GetRequestStatus(session)?.ContextUsage));
    }

    /// <summary>Occurrence and source attribution cannot change existing estimates or message serialization.</summary>
    [Fact]
    public static void EstimatesPreserveRepeatedOccurrencesAndAdditiveSourcesWithoutSerializingProvenance()
    {
        var message = new ModelMessage
        {
            Role = ModelMessageRole.Developer,
            SectionId = "repository-instructions",
            Content = [new ModelContentPart { Content = "aaaaabbbbbbb" }],
            Sources = [new("Repository instructions", "AGENTS.md", 0, 5), new("Appended prompts", "append.md", 5, 7)],
        };
        var tools = new[]
        {
            new ModelToolDefinition { Name = "small", Description = "small", ArgumentsJsonSchema = "{}" },
            new ModelToolDefinition { Name = "large", Description = new string('x', 1000), ArgumentsJsonSchema = "{}" },
        };
        var estimate = ModelWireEstimator.Estimate([message, message], tools, ToolTransportMode.Native, 1, 100);
        var plain = ModelWireEstimator.Estimate([message with { Sources = [] }, message with { Sources = [] }], tools, ToolTransportMode.Native, 1, 100);
        Assert.Equal(plain.WireInputTokens, estimate.WireInputTokens);
        Assert.Equal(JsonSerializer.Serialize(message with { Sources = [] }), JsonSerializer.Serialize(message));
        Assert.Equal(estimate.WireInputTokens, estimate.Components.Sum(item => item.Tokens));
        var messageComponents = estimate.Components.Where(item => item.Container == "Messages").ToArray();
        Assert.NotEqual(messageComponents[0].Id, messageComponents[1].Id);
        Assert.All(messageComponents, item => Assert.Equal(item.Tokens, item.Children.Sum(child => child.Tokens)));
        Assert.All(estimate.Components.TakeWhile(item => item.Container != "Messages"), item => Assert.Equal("Native tools", item.Container));
        Assert.True(estimate.Components.Single(item => item.Label == "large").Tokens > estimate.Components.Single(item => item.Label == "small").Tokens);
        var categories = ContextUsageFormatter.Categories(Snapshot(estimate));
        Assert.Equal(estimate.WireInputTokens, categories.Sum(item => item.Tokens));
        Assert.Equal(2, categories.Single(item => item.Category == "Appended prompts").Tokens);
        Assert.Equal("<0.1%", ContextUsageFormatter.Percent(1, long.MaxValue));
        Assert.Equal("100.0%", ContextUsageFormatter.Percent(long.MaxValue, long.MaxValue));
        Assert.Equal("?%", ContextUsageFormatter.Percent(0, 0));
    }

    /// <summary>Both user entry points use the same query and cannot assemble new context.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task TypedAndGestureCommandsUseSameReadOnlySnapshotQuery(bool gesture)
    {
        var usage = new SessionUsageProjection();
        var assembler = new InspectionOnlyAssembler();
        var dispatcher = new InspectionDispatcher(new ConversationContextApplication(assembler, usage));
        var run = RunId.New();
        var request = Request(run);
        var identity = new ModelRequestUsageId(run, "conversation", 2, Guid.NewGuid());
        var observed = usage.ObservePreparedRequest(dispatcher.SessionId, identity, request, 16000);
        var prepared = usage.GetRequestStatus(dispatcher.SessionId)?.ContextUsage;
        Assert.NotNull(prepared);
        Assert.False(prepared.DispatchStarted);
        observed.SubmissionObserver?.Invoke();
        var submitted = usage.GetRequestStatus(dispatcher.SessionId)?.ContextUsage;
        Assert.NotNull(submitted);
        Assert.True(submitted.DispatchStarted);
        Assert.False(prepared.DispatchStarted);
        var child = RunId.New();
        usage.RegisterChild(dispatcher.SessionId, child);
        usage.ObservePreparedRequest(dispatcher.SessionId, identity with { RunId = child, InvocationId = Guid.NewGuid() }, Request(child), 8000);
        Assert.Same(submitted, usage.GetRequestStatus(dispatcher.SessionId)?.ContextUsage);

        var surface = new RecordingSurface(gesture);
        await using var events = new DomainEventStream();
        var coordinator = new InteractionCoordinator(new InteractionPresenter(dispatcher, new InMemoryProjectionStore()), events, surface, sessionUsage: usage);
        await coordinator.RunAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        Assert.Same(submitted, Assert.Single(surface.Snapshots));
        Assert.Equal(1, dispatcher.Queries);
        Assert.Equal(run, Assert.Single(assembler.Reads));
        Assert.Equal(submitted.InputTokens, usage.GetRequestStatus(dispatcher.SessionId)?.ContextTokens);
        usage.Restore(dispatcher.SessionId, new SessionDurableUsage(0, 0, false, false, false));
        Assert.Null(usage.GetRequestStatus(dispatcher.SessionId));
    }

    /// <summary>A representative flat metadata inventory renders safely across terminal sizes.</summary>
    [Theory]
    [InlineData(160, 50)]
    [InlineData(120, 35)]
    [InlineData(80, 24)]
    [InlineData(40, 12)]
    [InlineData(20, 6)]
    public static void NativeModalHandlesInventoryResizeAndSafeLabels(int width, int height)
    {
        var text = RenderInventory(width, height, messageCount: 16, toolCount: 4);

        Assert.Contains("Categories", text, StringComparison.Ordinal);
        Assert.Contains("Estimated", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
        Assert.Contains(text, character => character is >= '\u2580' and <= '\u258f');
    }

    /// <summary>Large conversations retain final-row navigation and bounded rendering after resize.</summary>
    [Fact]
    public static void NativeModalHandlesLargeInventoryWithoutFullListRendering()
    {
        var components = Enumerable.Range(0, 1000)
            .Select(index => new ContextUsageComponent($"message:{index}", "Conversation", $"User {index} 中文 é\u001b[2J", "Messages", 10L + index))
            .Concat(Enumerable.Range(0, 200)
                .Select(index => new ContextUsageComponent($"tool:{index}", "Tools", $"tool:{index}", "Native tools", 20L + index)))
            .ToArray();
        var snapshot = Snapshot(new ModelWireEstimate
        {
            WireInputTokens = (int)components.Sum(item => item.Tokens),
            Components = components,
        });
        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var modal = new ContextUsageModal(snapshot, _ => CellStyle.Default, () => { }, () => { });
        var buffer = new CellBuffer(80, 24);
        modal.Render(new BufferSurface(buffer));
        modal.HandleKey(new KeyEvent(KeyCode.End, 0, KeyModifiers.None));
        modal.Render(new BufferSurface(buffer));
        Assert.Contains("tool:199 · 219 tokens", Read(buffer), StringComparison.Ordinal);
        buffer.Resize(160, 50);
        modal.Render(new BufferSurface(buffer));
        var rendered = Read(buffer);
        Assert.Contains("tool:199 · 219 tokens", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', rendered);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.TestOutputHelper?.WriteLine($"1,200 entries: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms, {allocated:N0} allocated bytes.");
        Assert.True(allocated < 10_000_000, $"Unexpected full-inventory rendering allocation: {allocated:N0}");
    }

    /// <summary>Native click synthesis reaches host input actions without consuming drafts or requesting steering.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task RealMouseRouteRequestsMapAndPreservesDraft(bool active)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new HeadlessBackend(120, 35);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("MAIN > "), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("draft");
            await surface.PresentAsync(new PresentationBatch([]), token);
            IActiveRunInputLease? lease = null;
            if (active)
            {
                backend.FeedInput("\r");
                Assert.Equal("draft", (await read).Text);
                lease = surface.BeginActiveRunInput(TimeProvider.System);
            }

            // SGR left presses and releases at the actual output header's right edge (screen row 4).
            backend.FeedInput("\u001b[<0;118;4M\u001b[<0;118;4m\u001b[<0;118;4M\u001b[<0;118;4m");
            if (lease is not null)
            {
                Assert.Equal(ActiveRunInputSignal.ContextMap, await lease.ReadAsync(token));
                await lease.DisposeAsync();
            }
            else
            {
                Assert.Equal(InteractionInputKind.ContextMap, (await read).Kind);
            }

            var modal = surface.ShowContextUsageAsync(Snapshot(Request(RunId.New()).WireEstimate!), token);
            await surface.PresentAsync(new PresentationBatch([new PresentationTextItem([new("execution still flows\n", PresentationTextRole.Default)])]), token);
            backend.FeedInput("\u001b[27u");
            await modal;
            if (!active)
            {
                var draft = surface.ReadComposerAsync(new ComposerRequest("MAIN > "), token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.FeedInput("\r");
                Assert.Equal("draft", (await draft).Text);
            }
        },
            timeout.Token);
    }

    /// <summary>F12 handoff and too-small/startup guards keep context scrolling with the terminal.</summary>
    [Fact]
    public static void ContextModalWheelRequiresApplicationMouseCapture()
    {
        var components = Enumerable.Range(0, 6)
            .Select(index => new ContextUsageComponent($"message:{index}", "Conversation", $"User {index}", "Messages", 10L + index))
            .ToArray();
        var snapshot = Snapshot(new ModelWireEstimate { WireInputTokens = 75, Components = components });
        var canHandleMouse = false;
        var modal = new ContextUsageModal(snapshot, _ => CellStyle.Default, () => { }, () => { }, () => canHandleMouse);
        var cells = new CellBuffer(120, 35);
        var wheel = new MouseEvent(MouseEventKind.Wheel, MouseButton.WheelDown, 20, 10, KeyModifiers.None, 0);

        Assert.True(modal.HandleMouse(wheel));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("User 0 · 10 tokens", Read(cells), StringComparison.Ordinal);

        canHandleMouse = true;
        Assert.True(modal.HandleMouse(wheel));
        cells.Clear(CellStyle.Default);
        modal.Render(new BufferSurface(cells));
        Assert.Contains("User 3 · 13 tokens", Read(cells), StringComparison.Ordinal);
    }

    /// <summary>The real input pump preserves the modal selection and draft across capture and size handoffs.</summary>
    [Fact]
    public static async Task ModalWheelFollowsCaptureAndResizeGuardsThroughApplicationPump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new HeadlessBackend(120, 35);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
            {
                var read = surface.ReadComposerAsync(new ComposerRequest("MAIN > "), token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.FeedInput("draft");
                var components = Enumerable.Range(0, 6)
                    .Select(index => new ContextUsageComponent($"message:{index}", "Conversation", $"User {index}", "Messages", 10L + index))
                    .ToArray();
                var modal = surface.ShowContextUsageAsync(Snapshot(new ModelWireEstimate { WireInputTokens = 75, Components = components }), token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                _ = backend.TakeOutput();

                backend.FeedInput("\u001b[24~");
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.FeedInput("\u001b[<65;20;10M");
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.Resize(121, 35);
                await surface.PresentAsync(new PresentationBatch([]), token);
                await AssertOutputEventuallyContainsAsync(backend, "User 0 · 10 tokens", token);

                backend.FeedInput("\u001b[24~");
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.FeedInput("\u001b[<65;20;10M");
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.Resize(122, 35);
                await surface.PresentAsync(new PresentationBatch([]), token);
                await AssertOutputEventuallyContainsAsync(backend, "User 3 · 13 tokens", token);

                backend.Resize(20, 6);
                backend.FeedInput("\u001b[<65;20;10M");
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.Resize(123, 35);
                await surface.PresentAsync(new PresentationBatch([]), token);
                await AssertOutputEventuallyContainsAsync(backend, "User 3 · 13 tokens", token);
                Assert.False(read.IsCompleted);

                backend.FeedInput("\u001b[27u");
                await modal;
                backend.FeedInput("\r");
                Assert.Equal("draft", (await read).Text);
            },
            timeout.Token);
    }

    /// <summary>A startup overlay cannot make an underlying context modal consume wheel input.</summary>
    [Fact]
    public static async Task ModalWheelIsBlockedDuringStartupThroughApplicationPump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new HeadlessBackend(120, 35);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
            {
                var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var startup = surface.ShowStartupAsync("Threadsmith.NET", "Loading solution ...", operation.Task, token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                var components = Enumerable.Range(0, 6)
                    .Select(index => new ContextUsageComponent($"message:{index}", "Conversation", $"User {index}", "Messages", 10L + index))
                    .ToArray();
                var modal = surface.ShowContextUsageAsync(Snapshot(new ModelWireEstimate { WireInputTokens = 75, Components = components }), token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                _ = backend.TakeOutput();
                backend.FeedInput("\u001b[<65;20;10M");
                backend.Resize(121, 35);
                await surface.PresentAsync(new PresentationBatch([]), token);
                Assert.Contains("User 0 · 10 tokens", backend.TakeOutput(), StringComparison.Ordinal);
                backend.FeedInput("\u001b[27u");
                await modal;
                operation.SetResult();
                await startup;
            },
            timeout.Token);
    }

    /// <summary>Non-activating mouse input leaves the composer submission path and its draft intact.</summary>
    [Theory]
    [InlineData("\u001b[<0;118;4M\u001b[<0;118;4m", true)]
    [InlineData("\u001b[<2;118;4M\u001b[<2;118;4m\u001b[<2;118;4M\u001b[<2;118;4m", true)]
    [InlineData("\u001b[<32;118;4M\u001b[<32;118;4M", false)]
    [InlineData("\u001b[<0;2;4M\u001b[<0;2;4m\u001b[<0;2;4M\u001b[<0;2;4m", true)]
    [InlineData("\u001b[24~\u001b[<0;118;4M\u001b[<0;118;4m\u001b[<0;118;4M\u001b[<0;118;4m", false)]
    public static async Task NonActivatingMouseInputPreservesComposer(string mouseInput, bool outputFocused)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new HeadlessBackend(120, 35);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("MAIN > "), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("draft" + mouseInput);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput(outputFocused ? "\u001b[18~\r" : "\r");
            var input = await read;
            Assert.Equal(InteractionInputKind.Submission, input.Kind);
            Assert.Equal("draft", input.Text);
        },
            timeout.Token);
    }

    private static ModelStreamRequest Request(RunId run)
    {
        var messages = new[] { new ModelMessage { Role = ModelMessageRole.User, SectionId = "current-user", Content = [new ModelContentPart { Content = "hello" }] } };
        return new ModelStreamRequest { RunId = run, Input = "hello", Messages = messages, WireEstimate = ModelWireEstimator.Estimate(messages, [], ToolTransportMode.Native, 0, 100) };
    }

    private static ContextUsageSnapshot Snapshot(ModelWireEstimate estimate) => new()
    {
        RunId = RunId.New(), InvocationId = Guid.NewGuid(), Stage = "conversation", CapturedAt = DateTimeOffset.UtcNow,
        InputTokens = estimate.WireInputTokens, ContextWindow = 1_000_000, OutputReserve = 100,
        StablePrefixTokens = estimate.Components.Take(estimate.StablePrefixComponentCount).Sum(item => item.Tokens),
        StablePrefixComponentCount = estimate.StablePrefixComponentCount,
        Components = estimate.Components, EstimationBasis = estimate.EstimationBasis,
    };

    private static string RenderInventory(
        int width,
        int height,
        int messageCount,
        int toolCount)
    {
        var components = Enumerable.Range(0, messageCount)
            .Select(index => new ContextUsageComponent(
                $"message:{index}",
                "Conversation",
                $"User {index} 中文 é\u001b[2J",
                "Messages",
                10L + index))
            .Concat(Enumerable.Range(0, toolCount)
                .Select(index => new ContextUsageComponent(
                    $"tool:{index}",
                    "Tools",
                    $"tool:{index}",
                    "Native tools",
                    20L + index)))
            .ToArray();
        var snapshot = Snapshot(new ModelWireEstimate
        {
            WireInputTokens = (int)components.Sum(item => item.Tokens),
            Components = components,
        });
        var modal = new ContextUsageModal(snapshot, _ => CellStyle.Default, () => { }, () => { });
        var buffer = new CellBuffer(width, height);
        modal.Render(new BufferSurface(buffer));
        modal.HandleKey(new KeyEvent(KeyCode.End, 0, KeyModifiers.None));
        modal.Render(new BufferSurface(buffer));
        modal.HandleKey(new KeyEvent(KeyCode.Tab, 0, KeyModifiers.None));
        buffer.Resize(160, 50);
        modal.Render(new BufferSurface(buffer));
        return Read(buffer);
    }

    private static string Read(CellBuffer buffer) => string.Join('\n', Enumerable.Range(0, buffer.Height).Select(y => string.Concat(Enumerable.Range(0, buffer.Width).Select(x => buffer.Get(x, y).Grapheme))));

    private static async Task AssertOutputEventuallyContainsAsync(HeadlessBackend backend, string expected, CancellationToken cancellationToken)
    {
        var output = string.Empty;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            output += backend.TakeOutput();
            if (output.Contains(expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        Assert.Contains(expected, output, StringComparison.Ordinal);
    }

    private sealed class InspectionOnlyAssembler : IContextAssembler
    {
        public List<RunId> Reads { get; } = [];

        public Task<ContextAssemblyResult> AssembleAsync(ContextAssemblyRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Inspection must never assemble context.");

        public void InvalidateInspections() => throw new InvalidOperationException("Inspection must not mutate context.");

        public ContextInspectionProjection? GetInspection(RunId runId)
        {
            Reads.Add(runId);
            return null;
        }
    }

    private sealed class InspectionDispatcher : ICommandDispatcher
    {
        private readonly ConversationContextApplication _application;

        public InspectionDispatcher(ConversationContextApplication application) => _application = application;

        public SessionId SessionId { get; } = SessionId.New();

        public int Queries { get; private set; }

        public async Task<TResponse> DispatchAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            if (command is GetContextInspectionCommand query)
            {
                Queries++;
                return (TResponse)(object)(await _application.HandleAsync(query, cancellationToken))!;
            }

            Assert.IsType<CreateSessionCommand>(command);
            return (TResponse)(object)SessionId;
        }
    }

    private sealed class RecordingSurface : IInteractionSurface, IContextUsageSurface
    {
        private readonly bool _gesture;

        public RecordingSurface(bool gesture) => _gesture = gesture;

        private int _reads;

        public List<ContextUsageSnapshot?> Snapshots { get; } = [];

        public InteractionSurfaceCapabilities Capabilities { get; } = new();

        public Task<InteractionInput> ReadComposerAsync(ComposerRequest request, CancellationToken cancellationToken = default) => Task.FromResult(_reads++ > 0
            ? new InteractionInput(true, "/quit", cancellationToken)
            : new InteractionInput(true, _gesture ? string.Empty : "/context map", cancellationToken, _gesture ? InteractionInputKind.ContextMap : InteractionInputKind.Submission));

        public Task ShowContextUsageAsync(ContextUsageSnapshot? snapshot, CancellationToken cancellationToken = default)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<InteractionSelectionResult> SelectAsync(InteractionSelectionRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No selector expected.");

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default) => operation.WaitAsync(cancellationToken);
    }
}
