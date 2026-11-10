namespace Threadsmith.ModelTooling.Tests;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

public static partial class OpenRouterProviderTests
{
    /// <summary>Opt-in offline allocation evidence; memory figures are observations, never runner-dependent pass criteria.</summary>
    [Theory]
    [InlineData(8388608, 0)]
    [InlineData(10485760, 0)]
    [InlineData(10000, 10000)]
    public static async Task MeasureStreamAllocations(int characters, int deltas)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("THREADSMITH_OPENROUTER_PERF") == "1", "Explicit offline allocation-probe opt-in required.");
        var value = new string('x', characters);
        var frames = new StringBuilder();
        for (var index = 0; index < deltas; index++)
        {
            frames.Append(Frame(JsonSerializer.Serialize(new { type = "response.output_text.delta", output_index = 0, content_index = 0, delta = "x" })));
        }

        frames.Append(Completed(OutputMessage(value)));
        using var handler = new Handler(frames.ToString());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var provider = Provider(client, Model() with { MaximumStreamedBytes = 0 });
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var observedCharacters = 0;

        await foreach (var chunk in provider.StreamAsync(Request(), TestContext.Current.CancellationToken))
        {
            observedCharacters += chunk.Text?.Length ?? 0;
        }

        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        TestContext.Current.TestOutputHelper?.WriteLine($"Characters={characters}; deltas={deltas}; allocated={allocatedBytes} bytes; process-lifetime peak working set={process.PeakWorkingSet64} bytes. Allocation includes transport and provider; prebuilt fixture excluded. Run each case in a fresh process for comparable peaks.");
        Assert.Equal(characters, observedCharacters);
        Assert.Equal(1, handler.Calls);
    }
}
