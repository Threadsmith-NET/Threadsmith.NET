namespace Threadsmith.Tui.TuiKit;

using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

// Explicit user paste only. Never query the clipboard during rendering/startup.

/// <summary>Reads the platform clipboard only on explicit user request with bounded size and time.</summary>
internal static class ClipboardReader
{
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    /// <summary>Reads clipboard text, preserving newlines, or returns null when unavailable.</summary>
    internal static async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var commands = OperatingSystem.IsWindows()
            ? new[]
            {
                (Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); [Console]::Write([string](Get-Clipboard -Raw))",
            }),
            }
            : OperatingSystem.IsMacOS()
                ? [("/usr/bin/pbpaste", Array.Empty<string>())]
                : ResolveLinuxCommands();
        try
        {
            foreach (var (executable, arguments) in commands)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var result = await ReadProcessAsync(executable, arguments, timeout.Token);
                if (result is not null)
                {
                    return result;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    private static (string Executable, string[] Arguments)[] ResolveLinuxCommands()
    {
        var candidates = new[]
        {
            (Name: "wl-paste", Arguments: new[] { "--no-newline" }),
            (Name: "xclip", Arguments: new[] { "-selection", "clipboard", "-o" }),
            (Name: "xsel", Arguments: new[] { "--clipboard", "--output" }),
        };
        var absoluteDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return candidates
            .Select(candidate => (
                Path: absoluteDirectories.Select(directory => Path.Combine(directory, candidate.Name)).FirstOrDefault(File.Exists),
                candidate.Arguments))
            .Where(candidate => candidate.Path is not null)
            .Select(candidate => (candidate.Path ?? throw new InvalidOperationException("Resolved clipboard helper path was null."), candidate.Arguments))
            .ToArray();
    }

    /// <summary>Decodes at most one MiB of strict UTF-8 clipboard data.</summary>
    internal static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var content = new MemoryStream(4096);
        var bytes = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(0, 4096), cancellationToken);
                if (count == 0)
                {
                    return _strictUtf8.GetString(content.GetBuffer(), 0, (int)content.Length);
                }

                if (content.Length + count > ComposerBuffer.MaximumDraftBytes)
                {
                    throw new InvalidDataException("Clipboard exceeds the 1 MiB input limit.");
                }

                await content.WriteAsync(bytes.AsMemory(0, count), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static async Task<string?> ReadProcessAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Win32Exception)
        {
            return null; // Missing clipboard utility: try the next supported backend.
        }

        // Drain stderr without retaining it; a noisy helper cannot fill its pipe or our heap.
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        try
        {
            var result = await ReadBoundedAsync(process.StandardOutput.BaseStream, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await errors;
            return process.ExitCode == 0 ? result : null;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    { /* The helper exited between the check and kill. */
                    }
                }
            }
            finally
            {
                // Closing pipes releases outstanding I/O, including when process termination fails.
                process.StandardOutput.Close();
                process.StandardError.Close();
                try
                {
                    await errors;
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // Expected after cancellation closes the pipe; the original failure propagates.
                }
            }
        }
    }
}
