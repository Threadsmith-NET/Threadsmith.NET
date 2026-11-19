namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class EditAnalyzerTestsCompilerCompletesWhileAnalyzerIsBlocked : EditAnalyzerTests
{
    /// <summary>Compiler feedback completes independently; cancellation or promotion discards blocked snapshot analysis.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CompilerCompletesWhileAnalyzerIsBlocked(bool promote)
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs");
        const string original = Violations + "\npublic class AnalyzerBarrier {}";
        await File.WriteAllTextAsync(path, original, ct);
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        await using var events = new DomainEventStream();
        var clock = new ObservedTimeProvider();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { CompilationWorkers = 2, InitialPreparationFrontier = 2, EditAnalysis = new() { Analyzers = new() { Enabled = false }, CandidateLifetimeMilliseconds = int.MaxValue } }, timeProvider: clock);
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var document = Assert.Single(source.GetDocumentIdsWithFilePath(path));
        var options = new EditAnalyzerOptions
        {
            DiagnosticIds = ["TS1170"],
            BudgetMilliseconds = int.MaxValue,
        };
        var pipeName = Guid.NewGuid().ToString("N")[..16];
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var marker = ExecutionBarrierPath(fixture.Root);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await File.WriteAllTextAsync(marker, pipeName, ct);
        var pending = engine.AnalyzeAnalyzerCandidateAsync(source, source, [document], options, false, true, cancellation.Token);
        try
        {
            await TestAnalyzerBarrier.WaitForConnectionAsync(pipe, pending, ct);
            var command = new ApplySourceEditCommand(session, RunId.New(), workspace, Guid.NewGuid(), new() { Rationale = "Verify independent compiler completion.", Mutations = [] });
            var completed = new TaskCompletionSource<SemanticCheckCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = events.Subscribe((item, _) =>
            {
                if (item is SemanticCheckCompleted check && check.Analysis?.EffectId == command.EffectId)
                {
                    completed.TrySetResult(check);
                }

                return Task.CompletedTask;
            });
            var snapshot = new MutationEffectSnapshot(MutationSetId.New(), [], [new("Contracts/AnalyzerCases.cs", AnalyzerHash(original), AnalyzerHash(Repairs)) { FinalBytes = Encoding.UTF8.GetBytes(Repairs) }]);
            Assert.NotNull(await engine.AnalyzeCandidateAsync(command, fixture.Root, snapshot, TimeSpan.Zero, ct));
            var compiler = await completed.Task.WaitAsync(ct);
            Assert.Equal(SemanticCheckOutcome.Completed, compiler.Outcome);
            Assert.False(pending.IsCompleted);
            if (promote)
            {
                await File.WriteAllTextAsync(path, Repairs, ct);
                var written = await File.ReadAllTextAsync(path, ct);
                engine.ConfirmEditApplied(command.EffectId);
                var refresh = engine.RefreshDocumentsAsync([new(path, written, AnalyzerHash(written))], ct);
                await clock.WaitForTimerAsync(TimeSpan.FromSeconds(2), ct);
                clock.Advance(TimeSpan.FromSeconds(2));
                await refresh;
                Assert.True(engine.GetLatestEditAnalysis(session, command.RunId, command.EffectId)!.CandidateReused);
            }
            else
            {
                await cancellation.CancelAsync();
            }

#pragma warning disable VSTHRD003 // The test owns and joins this explicitly cancelled or superseded observation.

            if (promote)
            {
                var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
                Assert.Contains("superseded", rejected.Message, StringComparison.Ordinal);
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
#pragma warning restore VSTHRD003
        }
        finally
        {
            File.Delete(marker);
            await cancellation.CancelAsync();
            await TestAnalyzerBarrier.ReleaseAsync(pipe);
        }

        var current = engine.CaptureAdvancedSnapshot().Solution;
        var recovered = await engine.AnalyzeAnalyzerCandidateAsync(current, current.WithDocumentText(document, SourceText.From(Repairs)), [document], options, false, true, ct);
        Assert.False(Assert.Single(recovered.Before).Reused);
    }
}
