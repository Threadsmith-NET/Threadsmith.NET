namespace Threadsmith.Tui.TuiKit;

using TUIKit;
using TUIKit.Terminal;

/// <summary>Preserves RGB colors when a modern Windows console has no terminal environment markers.</summary>
internal sealed class TuiKitConsoleBackend : ITerminalBackend
{
    private readonly ConsoleBackend _console = new();

    /// <summary>Initializes a new instance of the <see cref="TuiKitConsoleBackend"/> class.</summary>
    internal TuiKitConsoleBackend()
    {
        var detected = _console.Capabilities;

        // Default-terminal activation occurs after the application process is created, so
        // Windows Terminal cannot supply WT_SESSION/COLORTERM to that process. Modern native
        // Windows consoles support RGB too; missing Unix markers do not imply 16-color output.
        var term = Environment.GetEnvironmentVariable("TERM");
        var nativeRgb = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063)
            && string.IsNullOrEmpty(term)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COLORTERM"));
        var directRgb = term?.EndsWith("-direct", StringComparison.OrdinalIgnoreCase) == true;
        var useRgb = _console.IsInteractive
            && detected.ColorDepth is TerminalColorDepth.Ansi16 or TerminalColorDepth.Palette256
            && (nativeRgb || directRgb);
        Capabilities = useRgb
            ? new TerminalCapabilities(
                TerminalColorDepth.TrueColor,
                detected.EnhancedKeyboard,
                detected.SgrMouse,
                detected.Hyperlinks,
                detected.ClipboardOsc52,
                detected.BracketedPaste,
                detected.AnyMotionMouse,
                detected.FocusReporting)
            : detected;
    }

    /// <inheritdoc />
    public TerminalCapabilities Capabilities { get; }

    /// <inheritdoc />
    public Size Size => _console.Size;

    /// <inheritdoc />
    public bool IsInteractive => _console.IsInteractive;

    /// <inheritdoc />
    public void Start() => _console.Start();

    /// <inheritdoc />
    public void Write(string data) => _console.Write(data);

    /// <inheritdoc />
    public void Flush() => _console.Flush();

    /// <inheritdoc />
    public int ReadInput(byte[] buffer, int offset, int count) => _console.ReadInput(buffer, offset, count);

    /// <inheritdoc />
    public void Stop() => _console.Stop();

    /// <inheritdoc />
    public void Dispose() => _console.Dispose();
}
