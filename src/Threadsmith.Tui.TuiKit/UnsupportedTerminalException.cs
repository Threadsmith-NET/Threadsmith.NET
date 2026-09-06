namespace Threadsmith.Tui.TuiKit;

/// <summary>Identifies an expected terminal capability mismatch during frontend startup.</summary>
internal sealed class UnsupportedTerminalException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="UnsupportedTerminalException"/> class.</summary>
    public UnsupportedTerminalException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="UnsupportedTerminalException"/> class.</summary>
    public UnsupportedTerminalException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="UnsupportedTerminalException"/> class.</summary>
    public UnsupportedTerminalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
