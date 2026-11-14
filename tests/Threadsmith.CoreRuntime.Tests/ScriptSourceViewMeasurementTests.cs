namespace Threadsmith.CoreRuntime.Tests;

using System.Diagnostics;
using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Opt-in local measurements, not wall-clock correctness gates.</summary>
[Collection("TUIKit terminal")]
public sealed class ScriptSourceViewMeasurementTests
{
    /// <summary>Records warm modal activation without imposing a runner-speed requirement.</summary>
    [Theory(Explicit = true)]
    [Trait("Category", "Performance")]
    [InlineData("newline-only")]
    [InlineData("token-dense")]
    [InlineData("short-nonempty-lines")]
    public void MeasureMaximumSourceModalActivation(string shape)
    {
        var source = shape switch
        {
            "token-dense" => "var values = new[]{" + string.Concat(Enumerable.Repeat("1,", 32740)) + "0};",
            "short-nonempty-lines" => string.Concat(Enumerable.Repeat("1\n", ToolSourceDetail.MaximumCharacters / 2)),
            _ => new string('\n', ToolSourceDetail.MaximumCharacters),
        };
        var detail = Detail(source);
        var surface = new BufferSurface(new CellBuffer(110, 40));
        var warm = new ChoiceModal("script", [], sourceDetail: detail);
        warm.Render(surface);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var modal = new ChoiceModal("script", [], sourceDetail: detail);
        modal.Render(surface);
        var elapsed = Stopwatch.GetElapsedTime(start);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        TestContext.Current.TestOutputHelper?.WriteLine($"Modal activation ({shape}, {source.Length} chars): {bytes} allocated bytes; {elapsed.TotalMilliseconds:F3} ms.");
        string? copied = null;
        modal.CopyRequested = text =>
        {
            copied = text;
            return true;
        };
        modal.HandleKey(KeyEvent.Special(KeyCode.F4));
        Assert.Equal(source, copied);
    }

    /// <summary>Records resize and disclosure work near the retained transcript line limit.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Performance")]
    public void MeasureManyInvocationResizeAndDisclosure()
    {
        var view = new TranscriptView();
        var items = Enumerable.Range(0, 200).Select(index => new PresentationTextItem([new("Result\n", PresentationTextRole.Success)])
        {
            SourceDetail = Detail($"// source {index}"),
        }).ToArray();
        view.Present(new(items));
        var wide = new BufferSurface(new CellBuffer(110, 40));
        var narrow = new BufferSurface(new CellBuffer(70, 40));
        view.Render(wide);
        view.Render(narrow);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        view.Render(wide);
        var elapsed = Stopwatch.GetElapsedTime(start);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.TestOutputHelper?.WriteLine($"Resize (200 retained invocations): {bytes} allocated bytes; {elapsed.TotalMilliseconds:F3} ms.");

        view.HandleKey(KeyEvent.Special(KeyCode.Home));
        view.Render(wide);
        view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl));
        before = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        view.HandleKey(KeyEvent.Special(KeyCode.Enter));
        view.Render(wide);
        elapsed = Stopwatch.GetElapsedTime(start);
        bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.TestOutputHelper?.WriteLine($"Disclosure (200 retained invocations): {bytes} allocated bytes; {elapsed.TotalMilliseconds:F3} ms.");
        Assert.Contains("// source 0", view.Lines);
        Assert.Equal(200, view.Lines.Count(line => line == "View full script…"));
    }

    private static ToolSourceDetail Detail(string source) => new()
    {
        ToolInvocationId = ToolInvocationId.New(),
        Language = "csharp",
        Source = source,
        Availability = ToolSourceAvailability.Available,
    };
}
