namespace Threadsmith.ModelTooling.Tests;

using System.IO.Pipes;

/// <summary>Observes fixture readiness together with its owning operation and bounds cleanup.</summary>
internal static class TestAnalyzerBarrier
{
    internal static async Task WaitForConnectionAsync(NamedPipeServerStream pipe, Task operation, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connected = pipe.WaitForConnectionAsync(waiting.Token);
        try
        {
            await Task.WhenAny(connected, operation).WaitAsync(cancellationToken);
            if (!connected.IsCompleted)
            {
                await operation.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Semantic work completed before the analyzer fixture reached its construction barrier.");
            }

            await connected;
        }
        finally
        {
            await waiting.CancelAsync();
        }
    }

    internal static async Task ReleaseAsync(NamedPipeServerStream pipe)
    {
        if (!pipe.IsConnected)
        {
            return;
        }

        // This is a cleanup backstop, independent of runner cancellation, not a functional timing assertion.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await pipe.WriteAsync(new byte[] { 1 }, cleanup.Token);
            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, cleanup.Token);
            if (acknowledgement[0] != 1)
            {
                throw new InvalidDataException("The analyzer fixture returned an invalid release acknowledgement.");
            }
        }
        finally
        {
            await pipe.DisposeAsync();
        }
    }
}
