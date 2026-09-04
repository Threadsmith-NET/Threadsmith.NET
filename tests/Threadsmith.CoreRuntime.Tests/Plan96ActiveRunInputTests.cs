namespace Threadsmith.CoreRuntime.Tests;

using System.Text;
using PrettyPrompt.Consoles;
using Threadsmith.Tui;
using Xunit;

/// <summary>Verifies the Plan 96 serialized PrettyPrompt input lease.</summary>
public static class Plan96ActiveRunInputTests
{
    /// <summary>Repeated buffered Enter keys collapse to one idempotent steering signal.</summary>
    [Fact]
    public static async Task ActiveRunInput_RepeatedEnter_ProducesOneSignal()
    {
        var inner = new QueueConsole([
            CreateKey('\r', ConsoleKey.Enter),
            CreateKey('\r', ConsoleKey.Enter),
            CreateKey('\r', ConsoleKey.Enter),
        ]);
        var console = new BufferedPromptConsole(inner);
        await using var session = Assert.IsAssignableFrom<IActiveRunInputSession>(
            console.TryBeginActiveRunInput(TimeProvider.System));

        var signal = await session.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ActiveRunInputSignal.SteeringRequested, signal);
        Assert.False(inner.KeyAvailable);
    }

    /// <summary>Two buffered Escape keys produce one cancellation request.</summary>
    [Fact]
    public static async Task ActiveRunInput_DoubleEscape_ProducesCancellationSignal()
    {
        var inner = new QueueConsole([
            CreateKey('\u001b', ConsoleKey.Escape),
            CreateKey('\u001b', ConsoleKey.Escape),
        ]);
        var console = new BufferedPromptConsole(inner);
        await using var session = Assert.IsAssignableFrom<IActiveRunInputSession>(
            console.TryBeginActiveRunInput(TimeProvider.System));

        var signal = await session.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ActiveRunInputSignal.CancellationRequested, signal);
    }

    /// <summary>A multiline typed or pasted burst is replayed exactly to the next PrettyPrompt read.</summary>
    [Fact]
    public static async Task ActiveRunInput_MultikeyBurst_ReplaysWithoutHotKeyInterpretation()
    {
        ConsoleKeyInfo[] expected =
        [
            CreateKey('a', ConsoleKey.A),
            CreateKey('b', ConsoleKey.B),
            CreateKey('\r', ConsoleKey.Enter),
        ];
        var inner = new QueueConsole(expected);
        var console = new BufferedPromptConsole(inner);
        var session = Assert.IsAssignableFrom<IActiveRunInputSession>(
            console.TryBeginActiveRunInput(TimeProvider.System));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var pendingSignal = session.ReadAsync(cancellation.Token);

#pragma warning disable VSTHRD003 // The test started and owns the active-input read.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingSignal);
#pragma warning restore VSTHRD003
        await session.DisposeAsync();

        Assert.Equal(expected, expected.Select(_ => console.ReadKey(intercept: true)).ToArray());
    }

    /// <summary>Enter after slowly typed input is replayed as submission rather than stolen for steering.</summary>
    [Fact]
    public static async Task ActiveRunInput_TypedPrefixThenEnter_ReplaysCompleteSubmission()
    {
        ConsoleKeyInfo[] expected =
        [
            CreateKey('a', ConsoleKey.A),
            CreateKey('\r', ConsoleKey.Enter),
        ];
        var inner = new QueueConsole([expected[0]]);
        var console = new BufferedPromptConsole(inner);
        var session = Assert.IsAssignableFrom<IActiveRunInputSession>(
            console.TryBeginActiveRunInput(TimeProvider.System));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var pendingSignal = session.ReadAsync(cancellation.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(75), TestContext.Current.CancellationToken);
        inner.Enqueue(expected[1]);

#pragma warning disable VSTHRD003 // The test started and owns the active-input read.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingSignal);
#pragma warning restore VSTHRD003
        await session.DisposeAsync();

        Assert.Equal(expected, expected.Select(_ => console.ReadKey(intercept: true)).ToArray());
    }

    /// <summary>Disposal waits for a consumed batch to be classified and replayed.</summary>
    [Fact]
    public static async Task ActiveRunInput_DisposeDuringRead_PreservesConsumedKeys()
    {
        var expected = CreateKey('a', ConsoleKey.A);
        var inner = new QueueConsole([expected])
        {
            ReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            AllowReadToReturn = new ManualResetEventSlim(initialState: false),
        };
        var console = new BufferedPromptConsole(inner);
        var session = Assert.IsAssignableFrom<IActiveRunInputSession>(
            console.TryBeginActiveRunInput(TimeProvider.System));
        var pendingSignal = session.ReadAsync();
        await inner.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = session.DisposeAsync().AsTask();
        inner.AllowReadToReturn.Set();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));

#pragma warning disable VSTHRD003 // The test started and owns the active-input read.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingSignal);
#pragma warning restore VSTHRD003
        Assert.Equal(expected, console.ReadKey(intercept: true));
        inner.AllowReadToReturn.Dispose();
    }

    /// <summary>An empty prompt yields for output without consuming a simultaneously queued user key.</summary>
    [Fact]
    public static async Task PromptOutput_EmptyComposer_YieldsAndPreservesPhysicalInput()
    {
        var expected = CreateKey('a', ConsoleKey.A);
        var inner = new QueueConsole([expected]);
        var console = new BufferedPromptConsole(inner);
        console.BeginPromptRead();
        var output = console.RegisterPromptOutput();

        var yielded = console.ReadKey(intercept: true);

        Assert.Equal(ConsoleKey.F24, yielded.Key);
        Assert.Equal(
            ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift,
            yielded.Modifiers);
        Assert.False(console.KeyAvailable);
        Assert.True(inner.KeyAvailable);
        console.ObservePromptText(string.Empty);
        Assert.True(console.TryAcknowledgeIdlePromptYield(string.Empty));
        var completion = console.EndPromptRead();
        Assert.True(completion.WasYieldedForIdleOutput);
        Assert.False(completion.OutputDrained.IsCompleted);

        output.Dispose();
        await completion.OutputDrained.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(expected, console.ReadKey(intercept: true));
    }

    /// <summary>Queued output waits for a draft and yields as soon as the draft becomes empty.</summary>
    [Fact]
    public static async Task PromptOutput_ClearedDraft_YieldsWithoutSubmission()
    {
        var typed = CreateKey(' ', ConsoleKey.Spacebar);
        var backspace = CreateKey('\b', ConsoleKey.Backspace);
        var inner = new QueueConsole([typed]);
        var console = new BufferedPromptConsole(inner);
        console.BeginPromptRead();
        Assert.Equal(typed, console.ReadKey(intercept: true));
        console.ObservePromptText(" ");
        var output = console.RegisterPromptOutput();
        inner.Enqueue(backspace);

        Assert.Equal(backspace, console.ReadKey(intercept: true));
        console.ObservePromptText(string.Empty);
        Assert.Equal(ConsoleKey.F24, console.ReadKey(intercept: true).Key);
        Assert.True(console.TryAcknowledgeIdlePromptYield(string.Empty));
        var completion = console.EndPromptRead();

        output.Dispose();
        await completion.OutputDrained.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completion.WasYieldedForIdleOutput);
    }

    private static ConsoleKeyInfo CreateKey(char character, ConsoleKey key)
    {
        return new ConsoleKeyInfo(character, key, shift: false, alt: false, control: false);
    }

    private sealed class QueueConsole(IEnumerable<ConsoleKeyInfo> keys) : IConsole
    {
        private readonly Lock _gate = new();
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);
        private int _readObserved;

        /// <summary>Gets a test barrier that delays the first consumed key.</summary>
        public ManualResetEventSlim? AllowReadToReturn { get; init; }

        /// <summary>Gets a signal completed when the first key has been consumed.</summary>
        public TaskCompletionSource? ReadEntered { get; init; }

        public int CursorTop => 0;

        public int BufferWidth => 120;

        public int WindowHeight => 40;

        public int WindowTop => 0;

        public bool KeyAvailable
        {
            get
            {
                lock (_gate)
                {
                    return _keys.Count > 0;
                }
            }
        }

        public bool IsErrorRedirected => false;

        public bool CaptureControlC { get; set; }

#pragma warning disable CS0067 // Required by PrettyPrompt's console contract.
        public event ConsoleCancelEventHandler? CancelKeyPress;
#pragma warning restore CS0067

        public void Clear()
        {
        }

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            if (Interlocked.Exchange(ref _readObserved, 1) == 0)
            {
                ReadEntered?.TrySetResult();
                AllowReadToReturn?.Wait();
            }

            lock (_gate)
            {
                return _keys.Dequeue();
            }
        }

        /// <summary>Adds one key after the active reader has begun polling.</summary>
        public void Enqueue(ConsoleKeyInfo key)
        {
            lock (_gate)
            {
                _keys.Enqueue(key);
            }
        }

        public void InitVirtualTerminalProcessing()
        {
        }

        public void SetNewlineAutoReturn(bool enabled)
        {
        }

        public void SetModifyOtherKeys(bool enabled)
        {
        }

        public void Write(StringBuilder value, bool hideCursor)
        {
        }

        public void WriteLine(StringBuilder value, bool hideCursor)
        {
        }

        public void WriteError(StringBuilder value, bool hideCursor)
        {
        }

        public void WriteErrorLine(StringBuilder value, bool hideCursor)
        {
        }

        public void Write(string? value)
        {
        }

        public void WriteLine(string? value)
        {
        }

        public void WriteError(string? value)
        {
        }

        public void WriteErrorLine(string? value)
        {
        }

        public void Write(ReadOnlySpan<char> value)
        {
        }

        public void WriteLine(ReadOnlySpan<char> value)
        {
        }

        public void WriteError(ReadOnlySpan<char> value)
        {
        }

        public void WriteErrorLine(ReadOnlySpan<char> value)
        {
        }

        public void ShowCursor()
        {
        }

        public void HideCursor()
        {
        }
    }
}
