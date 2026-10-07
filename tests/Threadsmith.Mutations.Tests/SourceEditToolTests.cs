namespace Threadsmith.Mutations.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies the model-facing edit receipt preserves evidence without claiming unmeasured success.</summary>
public sealed class SourceEditToolTests
{
    /// <summary>User-visible feedback summarizes coverage without rendering serialized state or unknown error totals.</summary>
    [Theory]
    [InlineData(true, false, 0, "pending", false)]
    [InlineData(false, true, 2, "obsolete", false)]
    [InlineData(false, false, 0, "unavailable", false)]
    [InlineData(false, false, 1, "Coverage is incomplete", true)]
    [InlineData(false, false, 2, "2 of 2 projects analyzed", true)]
    public void DisplaySummaryReportsCoverageWithoutJson(bool pending, bool obsolete, int projectsAnalyzed, string expectedStatus, bool reportsErrors)
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            Pending = pending,
            Obsolete = obsolete,
            ProjectsAnalyzed = projectsAnalyzed,
            ProjectsInScope = 2,
            CurrentErrors = 3,
            ErrorComparisonAvailable = true,
        };

        var summary = SourceEditAnalysisProjection.CreateDisplaySummary(analysis);

        Assert.Contains(expectedStatus, summary, StringComparison.Ordinal);
        Assert.Equal(reportsErrors, summary.Contains("3 current errors, 0 new errors", StringComparison.Ordinal));
        Assert.DoesNotContain("{", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(analysis.EffectId.ToString(), summary, StringComparison.Ordinal);
        Assert.Contains("does not establish build or test success", summary, StringComparison.Ordinal);
    }

    /// <summary>Incomplete results omit error totals while completed coverage retains measured errors and origins.</summary>
    [Theory]
    [InlineData(true, false, 0, "pending", false)]
    [InlineData(true, false, 1, "pending", false)]
    [InlineData(false, true, 1, "obsolete", false)]
    [InlineData(false, false, 0, "unavailable", false)]
    [InlineData(false, false, 1, "partial", true)]
    [InlineData(false, false, 2, "complete", true)]
    public async Task ModelReceiptDistinguishesUnmeasuredCountsFromCompletedCoverage(bool pending, bool obsolete, int projectsAnalyzed, string status, bool hasCounts)
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            Pending = pending,
            Obsolete = obsolete,
            ProjectsAnalyzed = projectsAnalyzed,
            ProjectsInScope = 2,
            CurrentErrors = projectsAnalyzed == 0 ? 0 : 3,
            NewErrors = 0,
            ErrorComparisonAvailable = true,
            Diagnostics = [new("CS5001", "Missing entry point", null, null, "Example.Tests", "net10.0", "initial")],
        };
        var receipt = new SourceEditReceipt(analysis.EffectId, MutationSetId.New(), SourceEditStatus.Applied, ["Example.cs"], "Applied") { Analysis = analysis };
        var tool = new SourceEditTool(new ReceiptHandler(receipt), TestPromptLoader.Instance);
        var result = await tool.ExecuteAsync(
            new() { Rationale = "Add a field", Mutations = [] },
            new(ToolInvocationId.New(), SessionId.New(), RunId.New(), new() { RepositoryPath = Path.GetTempPath(), WorkspaceId = WorkspaceId.New(), RequestedBy = "model" }),
            TestContext.Current.CancellationToken);

        Assert.Same(receipt, result.Value);
        using var modelResult = JsonDocument.Parse(Assert.IsType<string>(result.ModelResultContent));
        var projected = modelResult.RootElement.GetProperty("Analysis");
        Assert.Equal(status, projected.GetProperty("Status").GetString());
        Assert.Equal(hasCounts, projected.TryGetProperty("CurrentErrors", out var currentErrors));
        Assert.Equal(hasCounts, projected.TryGetProperty("NewErrors", out _));
        Assert.Equal(hasCounts, projected.TryGetProperty("ResolvedErrors", out _));
        if (hasCounts)
        {
            Assert.Equal(3, currentErrors.GetInt32());
        }

        Assert.Equal("initial", projected.GetProperty("Diagnostics")[0].GetProperty("Origin").GetString());
    }

    /// <summary>The ordinary tool completion explains reuse without claiming obsolete results are current.</summary>
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    public async Task ToolDetailReportsOnlyCurrentCandidateReuse(bool reused, bool pending, bool obsolete, bool reportsReuse)
    {
        var analysis = new SourceEditAnalysis { EffectId = Guid.NewGuid(), CandidateReused = reused, CommittedGeneration = reused ? 1 : null, Pending = pending, Obsolete = obsolete };
        var receipt = new SourceEditReceipt(analysis.EffectId, MutationSetId.New(), SourceEditStatus.Applied, ["Example.cs"], "Applied") { Analysis = analysis };
        var tool = new SourceEditTool(new ReceiptHandler(receipt), TestPromptLoader.Instance);

        var result = await tool.ExecuteAsync(
            new() { Rationale = "Add a field", Mutations = [] },
            new(ToolInvocationId.New(), SessionId.New(), RunId.New(), new() { RepositoryPath = Path.GetTempPath(), WorkspaceId = WorkspaceId.New(), RequestedBy = "model" }),
            TestContext.Current.CancellationToken);

        if (reportsReuse)
        {
            var detail = Assert.IsType<string>(result.TransientActivityDetail);
            Assert.Contains("Candidate analysis reused for committed source", detail, StringComparison.Ordinal);
            Assert.Equal(pending, detail.Contains("broader analysis pending", StringComparison.Ordinal));
        }
        else
        {
            Assert.Null(result.TransientActivityDetail);
        }
    }

    /// <summary>Missing comparable evidence is absent in model counts and explicit in display text.</summary>
    [Fact]
    public void MissingComparisonOmitsUnmeasuredDeltas()
    {
        var analysis = new SourceEditAnalysis { EffectId = Guid.NewGuid(), ProjectsAnalyzed = 1, ProjectsInScope = 1, CurrentErrors = 2 };

        var projected = SourceEditAnalysisProjection.Create(analysis);
        var summary = SourceEditAnalysisProjection.CreateDisplaySummary(analysis);

        Assert.Equal(2, projected[nameof(SourceEditAnalysis.CurrentErrors)]!.GetValue<int>());
        Assert.False(projected.ContainsKey(nameof(SourceEditAnalysis.NewErrors)));
        Assert.False(projected.ContainsKey(nameof(SourceEditAnalysis.ResolvedErrors)));
        Assert.Contains("before/after comparison unavailable", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("0 new", summary, StringComparison.Ordinal);
    }

    private sealed class ReceiptHandler : ICommandHandler<ApplySourceEditCommand, SourceEditReceipt>
    {
        private readonly SourceEditReceipt _receipt;

        public ReceiptHandler(SourceEditReceipt receipt)
        {
            _receipt = receipt;
        }

        public Task<SourceEditReceipt> HandleAsync(ApplySourceEditCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_receipt);
        }
    }
}
