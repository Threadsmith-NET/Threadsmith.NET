namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

public abstract partial class SourceEditAnalysisTests
{
    private protected static async Task<SourceEditAnalysis> AnalyzeCompletedAsync(SemanticEngine engine, DomainEventStream events, ApplySourceEditCommand command, string root, MutationEffectSnapshot snapshot, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<SourceEditAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { CheckName: "advisory edit analysis", Analysis: { } analysis } && analysis.EffectId == command.EffectId)
            {
                completion.TrySetResult(analysis);
            }

            return Task.CompletedTask;
        });
        Assert.NotNull(await engine.AnalyzeCandidateAsync(command, root, snapshot, TimeSpan.Zero, cancellationToken));
        return await completion.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>Checks completion identity independently of worker scheduling.</summary>
public sealed class SourceEditAnalysisTestsCompilerCompletionIdentity : SourceEditAnalysisTests
{
    /// <summary>Delivers analyzer completion while compiler work is held behind a barrier.</summary>
    [Fact]
    public static async Task AnalyzerCompletionCannotReleaseCompilerWait()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        var compilerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompiler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider())
        {
            EditSyntaxAnalysisObserver = async (_, token) =>
            {
                compilerEntered.TrySetResult();
                await releaseCompiler.Task.WaitAsync(token);
            },
        };
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        var command = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        const string relative = "Contracts/Services.cs";
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative), ct);
        var pending = AnalyzeCompletedAsync(engine, events, command, fixture.Root, Snapshot(relative, original, original + "\npublic class Broken { public MissingType Value; }"), ct);
        try
        {
            await compilerEntered.Task.WaitAsync(ct);
            // Publish the competing producer's completion at an explicit compiler barrier.
            var competingCompletion = new SemanticCheckCompleted(command.SessionId, DateTimeOffset.UtcNow, command.RunId, SemanticCheckId.New(), SemanticCheckPhase.PreMutation, "advisory edit analyzers", SemanticCheckOutcome.Completed)
            {
                Analysis = new SourceEditAnalysis { EffectId = command.EffectId, Pending = true },
            };
            await events.PublishAsync(competingCompletion, ct);
            // Enqueue order on the helper subscription guarantees this event precedes compiler completion.
            // The final assertions below detect selecting it even if callback processing is delayed.
        }
        finally
        {
            releaseCompiler.TrySetResult();
        }

        var analysis = await pending;
        Assert.False(analysis.Pending);
        Assert.Contains(analysis.Diagnostics, item => item.Code == "CS0246");
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class SourceEditAnalysisTestsWarningRepairDoesNotResolveErrorsAndNewWarningsPrecedeInitialWarnings : SourceEditAnalysisTests
{
    /// <summary>Warning changes preserve error deltas and initial warning origins.</summary>
    [Fact]
    public static async Task WarningRepairDoesNotResolveErrorsAndNewWarningsPrecedeInitialWarnings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        var path = Path.Combine(fixture.Root, "Contracts", "Services.cs");
        var original = await File.ReadAllTextAsync(path, ct) + "\n#nullable enable\npublic class InitialWarning { public int M(string? value) => value.Length; }";
        await File.WriteAllTextAsync(path, original, ct);
        var props = Path.Combine(fixture.Root, "Directory.Build.props");
        var properties = await File.ReadAllTextAsync(props, ct);
        await File.WriteAllTextAsync(props, properties.Replace("</PropertyGroup>", "<Nullable>enable</Nullable><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>", StringComparison.Ordinal), ct);
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { CompilerWarningIds = ["CS8602", "CS8604"], MaximumReportedDiagnostics = 2 } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        var changed = original + "\npublic class NewWarning { public void M(string? value) => N(value); public void N(string value) { } }";
        var analysis = await AnalyzeCompletedAsync(engine, events, command, fixture.Root, Snapshot("Contracts/Services.cs", original, changed), ct);
        Assert.Equal(1, analysis.CurrentErrors);
        Assert.Equal(2, analysis.CurrentWarnings);
        Assert.Equal(0, analysis.NewErrors);
        Assert.Equal("CS8805", analysis.Diagnostics[0].Code);
        Assert.Equal("CS8604", analysis.Diagnostics[1].Code);
        Assert.Equal("introduced", analysis.Diagnostics[1].Origin);
        Assert.True(analysis.DiagnosticsTruncated);
        engine.ConfirmEditApplied(command.EffectId);
        await engine.RefreshDocumentsAsync([new(path, changed, Hash(changed))], ct);
        var repaired = changed.Replace("N(value)", "N(value ?? string.Empty)", StringComparison.Ordinal);
        var next = Command(command.SessionId, command.RunId, command.WorkspaceId);
        var repair = await AnalyzeCompletedAsync(engine, events, next, fixture.Root, Snapshot("Contracts/Services.cs", changed, repaired), ct);
        Assert.Equal(1, repair.CurrentErrors);
        Assert.Equal(0, repair.ResolvedErrors);
        Assert.Equal(1, repair.CurrentWarnings);
        Assert.Contains(repair.Diagnostics, item => item.Code == "CS8602" && item.Origin == "initial");
        // Unknown warning totals cannot erase measured projection truncation.
        const string added = "class NewlyAdded { }";
        var mixed = Snapshot("Contracts/Services.cs", changed, changed + "\n// next edit");
        mixed = mixed with
        {
            Endpoints = [..mixed.Endpoints, new("Contracts/New.cs", null, Hash(added))
            {
                FinalBytes = Encoding.UTF8.GetBytes(added),
            }

            ],
        };
        var partial = await AnalyzeCompletedAsync(engine, events, Command(command.SessionId, command.RunId, command.WorkspaceId), fixture.Root, mixed, ct);
        Assert.Null(partial.CurrentWarnings);
        Assert.False(partial.FindingRetentionTruncated);
        Assert.True(partial.FindingProjectionTruncated);
        Assert.True(partial.DiagnosticsTruncated);
        Assert.False(partial.ErrorComparisonAvailable);
        Assert.Equal(2, partial.Diagnostics.Count);
    }
}
