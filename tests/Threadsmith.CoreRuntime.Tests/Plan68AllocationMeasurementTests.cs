namespace Threadsmith.CoreRuntime.Tests;

using System.Globalization;
using System.Text;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Telemetry;
using Threadsmith.Tui.TuiKit;
using Xunit;

/// <summary>
/// Plan-68 evidence harness: measures warm steady-state allocations for representative small,
/// typical, and bounded-large inputs across production TUI Markdown layout and secret-output
/// sanitization paths. Results are emitted as test diagnostics for reproducible comparison.
/// This is NOT a wall-clock CI gate; it reports allocation bytes per operation over repeated samples.
/// </summary>
public sealed class Plan68AllocationMeasurementTests
{
    private static void Record(string label, (string Name, long Bytes)[] measurements)
    {
        Console.WriteLine(new string('=', 70));
        Console.WriteLine(label);
        foreach ((var name, var bytes) in measurements)
        {
            Console.WriteLine($"  {name}: {bytes} bytes/op");
        }
    }
}
