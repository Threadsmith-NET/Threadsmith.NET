namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Verifies readable archeology completion projections.</summary>
public static class RepositoryArcheologyPresentationTests
{
    /// <summary>Preserves the ordinary pipeline error presentation.</summary>
    [Fact]
    public static void Pipeline_error_remains_readable()
    {
        var started = new ToolInvocationStarted(SessionId.New(), DateTimeOffset.UnixEpoch, ToolInvocationId.New(), "repository_archeology");
        var completed = new ToolInvocationCompleted(started.SessionId, started.OccurredAt, started.ToolInvocationId, false, Error: "Deadline expired while queued.");
        var display = InteractionPresentationFormatter.FormatToolCompletion(started, completed, false);
        Assert.Contains("Deadline expired while queued.", display, StringComparison.Ordinal);
    }
}
