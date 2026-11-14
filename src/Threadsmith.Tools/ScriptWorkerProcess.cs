namespace Threadsmith.Tools;

using System.Diagnostics;

/// <summary>Owns an unused process until the ordinary process manager adopts it.</summary>
internal sealed class ScriptWorkerProcess : IAsyncDisposable
{
    private Process? _process;

    private ScriptWorkerProcess(Process process)
    {
        _process = process;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromMilliseconds(new Threadsmith.Core.ProcessResourceLimits().DrainTimeoutMilliseconds));
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Transfers sole process ownership to tracked execution.</summary>
    internal Process TakeProcess()
    {
        return Interlocked.Exchange(ref _process, null)
            ?? throw new InvalidOperationException("The scripting worker has already been consumed.");
    }

    /// <summary>Starts and waits for a bounded compiler-readiness handshake.</summary>
    internal static async Task<ScriptWorkerProcess> CreateAsync(ProcessExecutionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var worker = new ScriptWorkerProcess(ProcessManager.StartProcess(request));
        try
        {
            var process = worker._process ?? throw new InvalidOperationException("The scripting worker is unavailable.");

            // Fixed-size handshake: no unbounded read from a failing worker's output.
            const string marker = "THREADSMITH_SCRIPT_READY\n";
            var buffer = new char[marker.Length];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await process.StandardOutput.ReadAsync(buffer.AsMemory(offset), cancellationToken);
                if (read == 0)
                {
                    throw new InvalidOperationException("The scripting worker exited before becoming ready.");
                }

                offset += read;
            }

            if (!buffer.AsSpan().SequenceEqual(marker))
            {
                throw new InvalidOperationException("The scripting worker returned an invalid readiness marker.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return worker;
        }
        catch
        {
            await worker.DisposeAsync();
            throw;
        }
    }
}
