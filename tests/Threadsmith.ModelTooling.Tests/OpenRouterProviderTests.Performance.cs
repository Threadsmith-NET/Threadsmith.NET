namespace Threadsmith.ModelTooling.Tests;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

public static partial class OpenRouterProviderTests
{
    /// <summary>Opt-in offline allocation budget; run only this method in an isolated test process.</summary>
    [Theory]
    [InlineData(8388608, 0, 1)]
    [InlineData(10485760, 0, 1)]
    [InlineData(8388608, 0, 4)]
    [InlineData(10485760, 0, 4)]
    [InlineData(10000, 10000, 1)]
    public static async Task MeasureStreamAllocations(int characters, int deltas, int streams)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("THREADSMITH_OPENROUTER_PERF") == "1", "Explicit offline allocation-probe opt-in required.");
        var value = new string('x', characters);
        var frames = new StringBuilder();
        for (var index = 0; index < deltas; index++)
        {
            frames.Append(Frame(JsonSerializer.Serialize(new { type = "response.output_text.delta", output_index = 0, content_index = 0, delta = "x" })));
        }

        frames.Append(Completed(OutputMessage(value)));
        var fixture = frames.ToString();
        var cancellationToken = TestContext.Current.CancellationToken;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var tasks = Enumerable.Range(0, streams).Select(_ => Task.Run(
            async () =>
        {
            using var handler = new Handler(fixture);
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var provider = Provider(client, Model() with { MaximumStreamedBytes = 0 });
            var observedCharacters = 0;
            await foreach (var chunk in provider.StreamAsync(Request(), cancellationToken))
            {
                observedCharacters += chunk.Text?.Length ?? 0;
            }

            Assert.Equal(characters, observedCharacters);
            Assert.Equal(1, handler.Calls);
        },
            cancellationToken)).ToArray();
        await Task.WhenAll(tasks);

        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        // Size-based regression budget, not a runner-speed assertion. Include transport, DOM,
        // decoded text, reconciliation and encoder growth, plus fixed/per-event overhead.
        // Cold concurrent JSON/encoding pools may each rent a 16 MiB bucket for 8 MiB + headers.
        var budget = streams * ((18L * characters) + (8192L * deltas) + (16L * 1024 * 1024));
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        TestContext.Current.TestOutputHelper?.WriteLine($"Characters={characters}; deltas={deltas}; streams={streams}; allocated={allocatedBytes} bytes; budget={budget} bytes; process-lifetime peak working set={process.PeakWorkingSet64} bytes. Allocation includes transport and provider; prebuilt fixture excluded. Peak is observational and comparable only in fresh processes; filter to this method to isolate the allocation budget.");
        Assert.True(allocatedBytes <= budget, $"Allocated {allocatedBytes} bytes; budget {budget} bytes.");
    }
}
