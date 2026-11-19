namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises advisory candidates against a real evaluated multi-project solution.</summary>
public abstract partial class SourceEditAnalysisTests
{
    private protected static ApplySourceEditCommand Command(SessionId sessionId, RunId runId, WorkspaceId workspaceId)
    {
        return new(sessionId, runId, workspaceId, Guid.NewGuid(), new() { Rationale = "Exercise exact writer receipt analysis.", Mutations = [] });
    }

    private protected static MutationEffectSnapshot Snapshot(string path, string before, string after)
    {
        return new(MutationSetId.New(), [], [new(path, Hash(before), Hash(after)) { FinalBytes = Encoding.UTF8.GetBytes(after) }]);
    }

    private protected static string Hash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class SourceEditAnalysisTestsMixedMembershipCandidateRetainsNewFileSyntaxErrors : SourceEditAnalysisTests
{
    /// <summary>Existing-project aggregation preserves syntax findings from a newly created, not-yet-evaluated file.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task MixedMembershipCandidateRetainsNewFileSyntaxErrors(bool excludeSyntax)
    {
        using var fixture = new TestSemanticSolution();
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { ExcludedDiagnosticIds = excludeSyntax ? ["CS1513"] : [] } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var root = fixture.Root;
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var original = await File.ReadAllTextAsync(Path.Combine(root, "Contracts", "Services.cs"), ct);
        var existing = Snapshot("Contracts/Services.cs", original, original + "\npublic class MixedEdit { }\n");
        const string added = "public class Broken {";
        var snapshot = existing with
        {
            Endpoints = [..existing.Endpoints, new("Contracts/New.cs", null, Hash(added))
            {
                FinalBytes = Encoding.UTF8.GetBytes(added),
            }

            ],
        };
        var completed = new TaskCompletionSource<SemanticCheckCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { CheckName: "advisory edit analysis" } check && check.Analysis?.EffectId == command.EffectId)
            {
                completed.TrySetResult(check);
            }

            return Task.CompletedTask;
        });
        _ = await engine.AnalyzeCandidateAsync(command, root, snapshot, TimeSpan.FromSeconds(20), ct);
        var result = (await completed.Task.WaitAsync(ct)).Analysis;
        Assert.NotNull(result);
        Assert.False(result.Pending);
        Assert.Equal(2, result.SyntaxDocumentsAnalyzed);
        Assert.Equal(!excludeSyntax, result.SyntaxErrors > 0);
        Assert.False(result.ErrorComparisonAvailable);
        Assert.True(result.ProjectsAnalyzed > 0);
        Assert.Equal(!excludeSyntax, result.Diagnostics.Any(item => item.Code == "CS1513" && item.File == "Contracts/New.cs"));
        Assert.Equal(excludeSyntax, result.OutputFiltered);
        Assert.Equal(excludeSyntax ? 1 : 2, result.CurrentErrors);
        Assert.False(result.DiagnosticsTruncated);
    }
}
