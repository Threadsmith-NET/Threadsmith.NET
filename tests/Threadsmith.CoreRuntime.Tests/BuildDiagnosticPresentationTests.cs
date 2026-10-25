namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Build diagnostics remain visible through ordinary tool completion and durable replay.</summary>
public static class BuildDiagnosticPresentationTests
{
    /// <summary>A successful build exposes all seven warnings in the reported scenario without model prose.</summary>
    [Fact]
    public static void SuccessfulBuildShowsWarningsAndSurvivesReplay()
    {
        var result = Result(Enumerable.Range(1, 7).Select(index => Finding("CS8604", DiagnosticSeverity.Warning, index)).ToArray());
        var (started, completed) = Events(result);
        var transcript = new ConversationTranscript(string.Empty);
        transcript.Apply(started);
        Assert.True(transcript.Apply(completed));
        Assert.Contains("TOOLS: dotnet_build - completed", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Build: succeeded; exit code 0; Example.csproj", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("0 errors; 7 warnings; 0 informational", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Warning CS8604 Example.cs:7:47 [Example; net10.0]: Possible null argument", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("no additional detail", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("raw process output", transcript.Text, StringComparison.Ordinal);

        var replay = new ConversationTranscript(string.Empty);
        replay.Apply(DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(started), 1, DomainEventJson.Serialize(started)));
        replay.Apply(DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(completed), 1, DomainEventJson.Serialize(completed)));
        Assert.Equal(transcript.Text, replay.Text);
    }

    /// <summary>The configured display bound puts errors first and discloses omitted warnings.</summary>
    [Fact]
    public static void ConfiguredLimitShowsErrorsBeforeWarnings()
    {
        var result = Result([Finding("CS8604", DiagnosticSeverity.Warning, 1), Finding("CS0246", DiagnosticSeverity.Error, 2)]) with { Succeeded = false, ExitCode = 1 };
        var (started, completed) = Events(result);
        var transcript = new ConversationTranscript(string.Empty, limits: new TuiResourceLimits { MaximumBuildDiagnostics = 1 });
        transcript.Apply(started);
        transcript.Apply(completed);
        Assert.Contains("Build: failed; exit code 1", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("1 errors; 1 warnings", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Error CS0246", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Warning CS8604", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("1 additional diagnostics omitted from display", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Incomplete capture and a failed build without parsed errors cannot imply validation success.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void FailedOrTimedOutBuildKeepsOutcomeAndCoverage(bool timedOut)
    {
        var result = Result([]) with { Succeeded = false, ExitCode = -1, TimedOut = timedOut, IsTruncated = true };
        var (started, completed) = Events(result);
        var transcript = new ConversationTranscript(string.Empty);
        transcript.Apply(started);
        transcript.Apply(completed);
        Assert.Contains(timedOut ? "Build: timed out" : "Build: failed", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("counts may be incomplete", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("No structured compiler diagnostics were captured", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Build: succeeded", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Malformed or absent historical payloads use the existing failure detail.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{broken")]
    [InlineData("{\"Succeeded\":true,\"Diagnostics\":[],\"Kind\":1}")]
    public static void UnavailableReceiptDoesNotInventCleanBuild(string? json)
    {
        var (started, completed) = Events(Result([]));
        var transcript = new ConversationTranscript(string.Empty);
        transcript.Apply(started);
        transcript.Apply(completed with { Succeeded = false, ResultJson = json, Error = "Build output unavailable" });
        Assert.Contains("Build output unavailable", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("0 errors", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Build: succeeded", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Diagnostics are sanitized and only the built-in tool receives this presentation.</summary>
    [Fact]
    public static void ControlsAreCollapsedAndExternalNamesDoNotSpoofBuilds()
    {
        var result = Result([Finding("CS8604", DiagnosticSeverity.Warning, 1) with { Message = "Possible null\nInjected\u001b[31m", TargetFramework = string.Empty }]);
        var (started, completed) = Events(result);
        var transcript = new ConversationTranscript(string.Empty);
        transcript.Apply(started);
        transcript.Apply(completed);
        Assert.DoesNotContain("\nInjected", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', transcript.Text);
        Assert.Contains("[Example]: Possible null Injected", transcript.Text, StringComparison.Ordinal);
        var external = new ToolActivitySource(ToolActivitySourceKind.Mcp, "Server");
        var other = new ConversationTranscript(string.Empty);
        other.Apply(started with { Source = external });
        other.Apply(completed with { Source = external });
        Assert.DoesNotContain("Captured diagnostics:", other.Text, StringComparison.Ordinal);
    }

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
