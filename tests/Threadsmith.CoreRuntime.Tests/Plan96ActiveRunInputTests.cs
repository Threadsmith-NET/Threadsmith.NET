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

    private static ConsoleKeyInfo CreateKey(char character, ConsoleKey key)
    {
        return new ConsoleKeyInfo(character, key, shift: false, alt: false, control: false);
    }

    private sealed class QueueConsole(IEnumerable<ConsoleKeyInfo> keys) : IConsole
    {
        private readonly Lock _gate = new();
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

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
