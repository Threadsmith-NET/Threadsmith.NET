namespace Threadsmith.ModelTooling.Tests;

using System.IO.Pipes;
using Xunit;

/// <summary>Fixture readiness observes load failures without depending on runner speed.</summary>
public static class TestAnalyzerBarrierTests
{
    /// <summary>A failed load surfaces its original failure instead of waiting for a constructor that cannot run.</summary>
    [Fact]
    public static async Task FailedOperationEndsReadinessWait()
    {
        await using var pipe = new NamedPipeServerStream(Guid.NewGuid().ToString("N")[..16], PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var failure = new InvalidDataException("Synthetic loader failure");

        var observed = await Assert.ThrowsAsync<InvalidDataException>(() => TestAnalyzerBarrier.WaitForConnectionAsync(pipe, Task.FromException(failure), TestContext.Current.CancellationToken));

        Assert.Same(failure, observed);
    }

    /// <summary>A degraded load that never constructs the fixture is an immediate setup failure.</summary>
    [Fact]
    public static async Task CompletedOperationWithoutConnectionEndsReadinessWait()
    {
        await using var pipe = new NamedPipeServerStream(Guid.NewGuid().ToString("N")[..16], PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        await Assert.ThrowsAsync<InvalidOperationException>(() => TestAnalyzerBarrier.WaitForConnectionAsync(pipe, Task.CompletedTask, TestContext.Current.CancellationToken));
    }

    /// <summary>The constructor can signal readiness while its owning operation remains blocked.</summary>
    [Fact]
    public static async Task ConnectedFixtureCanBeReleasedBeforeOperationCompletes()
    {
        var token = TestContext.Current.CancellationToken;
        var name = Guid.NewGuid().ToString("N")[..16];
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = TestAnalyzerBarrier.WaitForConnectionAsync(pipe, operation.Task, token);
        await client.ConnectAsync(token);
        await ready;

        var release = TestAnalyzerBarrier.ReleaseAsync(pipe);
        var signal = new byte[1];
        await client.ReadExactlyAsync(signal, token);
        await client.WriteAsync(signal, token);
        await release;

        Assert.Equal(1, signal[0]);
        Assert.False(operation.Task.IsCompleted);
    }
}
