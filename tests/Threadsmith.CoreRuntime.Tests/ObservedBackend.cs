namespace Threadsmith.CoreRuntime.Tests;

using TUIKit;
using TUIKit.Terminal;

/// <summary>Observes completed terminal writes without waiting for wall-clock time.</summary>
internal sealed class ObservedBackend : ITerminalBackend
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _flushed;
    private int _bells;

    public ObservedBackend(int width = 80, int height = 24, TerminalCapabilities? capabilities = null)
    {
        Input = capabilities is null ? new(width, height) : new(width, height, capabilities);
    }

    public HeadlessBackend Input { get; }

    public TerminalCapabilities Capabilities => Input.Capabilities;

    public Size Size => Input.Size;

    public bool IsInteractive => true;

    public bool IsStopped => Input.IsStopped;

    public int Bells => Volatile.Read(ref _bells);

    public void Start() => Input.Start();

    public void Stop() => Input.Stop();

    public void Dispose() => Input.Dispose();

    public int ReadInput(byte[] buffer, int offset, int count) => Input.ReadInput(buffer, offset, count);

    public void FeedInput(string input) => Input.FeedInput(input);

    public void Resize(int width, int height) => Input.Resize(width, height);

    public void Write(string data)
    {
        lock (_gate)
        {
            _flushed = false;
            Interlocked.Add(ref _bells, data.Count(character => character == '\a'));
            Input.Write(data);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            Input.Flush();
            _flushed = true;
            var changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    public string TakeOutput()
    {
        lock (_gate)
        {
            return Input.TakeOutput();
        }
    }

    public string PeekOutput()
    {
        lock (_gate)
        {
            return Input.PeekOutput();
        }
    }

    public Task ExpectOutputAsync(string text)
    {
        lock (_gate)
        {
            Input.TakeOutput();
            return WaitForOutputAsync(text, Xunit.TestContext.Current.CancellationToken, consume: false);
        }
    }

    public Task<string> ReadOutputAsync(CancellationToken cancellationToken)
        => WaitForOutputAsync(string.Empty, cancellationToken);

    public async Task<string> WaitForOutputAsync(string text, CancellationToken cancellationToken, bool consume = true)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var output = Input.PeekOutput();
                if (_flushed && output.Length > 0 && output.Contains(text, StringComparison.Ordinal))
                {
                    return consume ? Input.TakeOutput() : output;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }
}
