namespace Threadsmith.Tui.TuiKit;

/// <summary>Identifies an expected terminal capability mismatch during frontend startup.</summary>
internal sealed class UnsupportedTerminalException(string message) : InvalidOperationException(message);
