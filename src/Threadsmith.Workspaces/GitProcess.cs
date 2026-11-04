namespace Threadsmith.Workspaces;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

/// <summary>Shared mechanics for owned Git processes; callers retain command and error policy.</summary>
internal static class GitProcess
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Creates a shell-free Git process, optionally applying read-only query settings.</summary>
    internal static Process CreateProcess(string repositoryPath, IReadOnlyList<string> arguments, bool readOnly = true, bool preserveOutputFormat = false)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        if (readOnly)
        {
            if (!preserveOutputFormat)
            {
                process.StartInfo.StandardOutputEncoding = StrictUtf8;
                process.StartInfo.StandardErrorEncoding = StrictUtf8;
            }

            process.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
            process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            string[] fixedArguments =
            [
                "--no-pager", "-c", "color.ui=false", "-c", "core.pager=cat",
                "-c", "core.fsmonitor=false", "-c", "diff.external=", "-c", "diff.trustExitCode=false",
            ];
            foreach (var argument in fixedArguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!preserveOutputFormat)
            {
                process.StartInfo.ArgumentList.Add("-c");
                process.StartInfo.ArgumentList.Add("core.quotepath=false");
            }
        }

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }

    /// <summary>Executes Git with concurrent capture and bounded cleanup on every exit path.</summary>
    internal static async Task<ExecutionResult<T>> ExecuteAsync<T>(
        Process process,
        Func<Process, CancellationToken, Task<T>> readOutput,
        int maximumCharacters,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        process.StartInfo.RedirectStandardInput = standardInput is not null;
        if (!process.Start())
        {
            throw new InvalidOperationException("Git did not start.");
        }

        Task<T>? output = null;
        Task<BoundedText>? error = null;
        try
        {
            await using var termination = RegisterTermination(process, cancellationToken);
            output = readOutput(process, cancellationToken);
            error = ReadBoundedAsync(process.StandardError, maximumCharacters, cancellationToken);
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();
            }

            var exit = process.WaitForExitAsync(cancellationToken);
            var pending = new List<Task> { output, error, exit };
            while (pending.Count > 0)
            {
                // A failed reader must terminate Git even when its pipe would otherwise fill.
                var completed = await Task.WhenAny(pending).WaitAsync(cancellationToken);
                await completed;
                pending.Remove(completed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ExecutionResult<T>(process.ExitCode, await output, await error);
        }
        finally
        {
            await CleanupAsync(process, output, error);
        }
    }

    /// <summary>Terminates the owned process tree when its operation is cancelled.</summary>
    internal static CancellationTokenRegistration RegisterTermination(Process process, CancellationToken cancellationToken)
    {
        return cancellationToken.Register(() => Terminate(process));
    }

    /// <summary>Retains a bounded prefix while draining the entire output stream.</summary>
    internal static async Task<BoundedText> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            truncated |= read > remaining;
        }

        return new BoundedText(builder.ToString(), truncated);
    }

    /// <summary>Terminates an owned process and bounds exit and reader cleanup independently of operation cancellation.</summary>
    internal static async Task CleanupAsync(Process process, Task? output, Task? error)
    {
        Terminate(process);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
#pragma warning disable VSTHRD003 // These are the owned process readers; cleanup joins them with an independent deadline.
        await WaitForCleanupAsync(process.WaitForExitAsync(cleanup.Token), output, error, cleanup.Token);
#pragma warning restore VSTHRD003
    }

    /// <summary>Bounds cleanup even when termination leaves an exit or pipe task incomplete.</summary>
    internal static async Task WaitForCleanupAsync(Task exit, Task? output, Task? error, CancellationToken cancellationToken)
    {
        try
        {
            await exit.WaitAsync(cancellationToken);
            await Task.WhenAll(output ?? Task.CompletedTask, error ?? Task.CompletedTask).WaitAsync(cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested || output?.IsFaulted == true || error?.IsFaulted == true
            || output?.IsCanceled == true || error?.IsCanceled == true)
        {
            // Cleanup is best effort: preserve the operation's result, including arbitrary
            // custom-reader failures. Abandoned reader faults are observed below.
        }
        finally
        {
            ObserveFailure(exit);
            ObserveFailure(output);
            ObserveFailure(error);
        }
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The owned process has already exited.
        }
        catch (Exception exception) when (exception is Win32Exception or AggregateException)
        {
            // Exit races or descendant termination restrictions must not mask cancellation.
        }
    }

    private static void ObserveFailure(Task? task)
    {
        if (task is not null)
        {
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>Retained text and whether any characters were discarded.</summary>
    internal sealed record BoundedText(string Text, bool IsTruncated);

    /// <summary>Raw completion data for caller-specific error handling.</summary>
    internal sealed record ExecutionResult<T>(int ExitCode, T Output, BoundedText Error);
}
