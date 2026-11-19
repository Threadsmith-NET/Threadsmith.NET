namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Build diagnostics remain visible through ordinary tool completion and durable replay.</summary>
public static class BuildDiagnosticPresentationTests
{
    private static ValidationToolResult Result(IReadOnlyList<Diagnostic> diagnostics) => new(
        "invocation", ValidationInvocationKind.Build, ValidationAuthority.Exploratory, true, "Example.csproj", ["build", "Example.csproj"], diagnostics, "raw process output", 0, TimeSpan.Zero, false, false);

    private static Diagnostic Finding(string code, DiagnosticSeverity severity, int line) => new()
    {
        Id = $"{code}-{line}",
        Code = code,
        Severity = severity,
        Project = "Example",
        TargetFramework = "net10.0",
        File = "Example.cs",
        Range = new(line, 47, line, 47),
        Message = "Possible null argument",
        Confidence = SemanticConfidenceLevel.None,
    };

    private static (ToolInvocationStarted Started, ToolInvocationCompleted Completed) Events(ValidationToolResult result)
    {
        var started = new ToolInvocationStarted(SessionId.New(), DateTimeOffset.UtcNow, ToolInvocationId.New(), "dotnet_build", Source: new(ToolActivitySourceKind.BuiltIn, "Built-in"));
        return (started, new(started.SessionId, started.OccurredAt, started.ToolInvocationId, true, JsonSerializer.Serialize(result), Source: started.Source));
    }
}
