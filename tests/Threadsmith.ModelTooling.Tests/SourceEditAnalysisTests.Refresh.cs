namespace Threadsmith.ModelTooling.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

public static partial class SourceEditAnalysisTests
{
    /// <summary>Graph refresh resumes exact applied edits across encodings without accepting text-equivalent replacements.</summary>
    [Theory]
    [InlineData("utf8", false)]
    [InlineData("utf8-bom", false)]
    [InlineData("utf16", false)]
    [InlineData("utf8-bom", true)]
    public static async Task FullRefreshContinuesOnlyExactAppliedBytes(string encodingName, bool replaceEncodingAfterApply)
    {
        using var fixture = new TestSemanticSolution();
        var ct = TestContext.Current.CancellationToken;
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance);
        await using var coordinator = new SemanticRefreshCoordinator(
            new RegistrySemanticRefreshBackend(registry), events, NullLogger<SemanticRefreshCoordinator>.Instance);
        await registry.LoadAsync(request, ct);
        await coordinator.BindAsync(request, ct);
        var engine = registry.GetEngine(request.WorkspaceId);
        var command = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        var completed = new TaskCompletionSource<SemanticCheckCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { Phase: SemanticCheckPhase.PostMutation, CheckName: "advisory edit analysis" } check
                && check.RunId == command.RunId && check.Analysis?.EffectId == command.EffectId)
            {
                completed.TrySetResult(check);
            }

            return Task.CompletedTask;
        });

        var encoding = encodingName switch
        {
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf16" => Encoding.Unicode,
            _ => Encoding.UTF8,
        };
        var solutionText = await File.ReadAllTextAsync(request.SolutionPath, ct) + "\n";
        const string sourceText = "public class AddedByEdit { MissingType Value; }";
        var preamble = encodingName == "utf8" ? [] : encoding.GetPreamble();
        byte[] solutionBytes = [.. preamble, .. encoding.GetBytes(solutionText)];
        byte[] sourceBytes = [.. preamble, .. encoding.GetBytes(sourceText)];
        const string sourceRelativePath = "Contracts/AddedByEdit.cs";
        var sourcePath = Path.Combine(fixture.Root, sourceRelativePath);
        var snapshot = new MutationEffectSnapshot(
            MutationSetId.New(),
            [],
            [
                new("SmallDotNetSolution.sln", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.SolutionPath, ct))), Convert.ToHexString(SHA256.HashData(solutionBytes))) { FinalBytes = solutionBytes },
                new(sourceRelativePath, null, Convert.ToHexString(SHA256.HashData(sourceBytes))) { FinalBytes = sourceBytes },
            ]);
        Assert.NotNull(await engine.AnalyzeCandidateAsync(command, fixture.Root, snapshot, TimeSpan.Zero, ct));
        await File.WriteAllBytesAsync(request.SolutionPath, solutionBytes, ct);
        await File.WriteAllBytesAsync(sourcePath, sourceBytes, ct);
        engine.ConfirmEditApplied(command.EffectId);

        if (replaceEncodingAfterApply)
        {
            // Identical decoded text is insufficient proof that the applied bytes survived.
            await File.WriteAllTextAsync(request.SolutionPath, solutionText, new UTF8Encoding(false), ct);
        }

        var refresh = await coordinator.ForceRefreshAsync(request.SessionId, ct);
        Assert.Equal(SemanticRefreshMode.Full, refresh.Mode);
        Assert.True(coordinator.IsCurrent(request.SessionId));
        var analysis = engine.GetLatestEditAnalysis(command.SessionId, command.RunId, command.EffectId);
        Assert.NotNull(analysis);
        if (replaceEncodingAfterApply)
        {
            Assert.True(analysis.Obsolete);
            Assert.Null(analysis.CommittedGeneration);
            Assert.False(completed.Task.IsCompleted);
            return;
        }

        // Publication must attach the edit before returning, even while diagnostics are still running.
        Assert.NotNull(analysis.CommittedGeneration);
        var check = await completed.Task.WaitAsync(ct);
        Assert.Equal(SemanticCheckOutcome.Completed, check.Outcome);
        analysis = engine.GetLatestEditAnalysis(command.SessionId, command.RunId, command.EffectId);
        Assert.NotNull(analysis);
        Assert.False(analysis.Pending);
        Assert.False(analysis.Obsolete);
        Assert.True(analysis.ProjectsAnalyzed > 0);
        Assert.Contains(analysis.Diagnostics, item => item.Code == "CS0246" && item.File == sourceRelativePath);
    }
}
