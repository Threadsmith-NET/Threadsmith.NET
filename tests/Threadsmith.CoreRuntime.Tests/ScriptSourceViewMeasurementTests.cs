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
    private static ToolSourceDetail Detail(string source) => new()
    {
        ToolInvocationId = ToolInvocationId.New(),
        Language = "csharp",
        Source = source,
        Availability = ToolSourceAvailability.Available,
    };
}
