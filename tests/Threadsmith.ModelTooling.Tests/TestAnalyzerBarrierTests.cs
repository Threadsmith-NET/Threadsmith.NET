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
}
