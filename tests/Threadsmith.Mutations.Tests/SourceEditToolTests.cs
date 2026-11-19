namespace Threadsmith.Mutations.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies the model-facing edit receipt preserves evidence without claiming unmeasured success.</summary>
public sealed partial class SourceEditToolTests
{
    /// <summary>Completed analyzer projection remains bounded and explicit while compiler totals are unknown.</summary>
    [Fact]
    public void PendingCompilerPreservesAnalyzerTruncationAndIdentity()
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            Pending = true,
            Analyzers = new() { Status = "completed", DocumentsInScope = 1, DocumentsAnalyzed = 1, ReportableFindings = 9 },
            Diagnostics = [new("CA2016", "Forward cancellation", "Example.cs", 1, "Example", "net10.0", "unknown") { Column = 14, Producer = "analyzer", AnalyzerIdentity = "qualified fixture" }],
        };
        var projected = SourceEditAnalysisProjection.Create(analysis);
        Assert.True(projected["DiagnosticsTruncated"]!.GetValue<bool>());
        Assert.False(projected.ContainsKey("CurrentErrors"));
        Assert.Equal(9, projected["Analyzers"]!["ReportableFindings"]!.GetValue<int>());
        Assert.Equal("analyzer", projected["Diagnostics"]![0]!["Producer"]!.GetValue<string>());
        Assert.Equal(14, projected["Diagnostics"]![0]!["Column"]!.GetValue<int>());
    }

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

    /// <summary>Older durable findings retain error semantics.</summary>
    [Fact]
    public void LegacyFindingWithoutSeverityRemainsAnError()
    {
        var finding = JsonSerializer.Deserialize<SourceEditDiagnostic>("""
            {"Code":"CS0246","Message":"Missing type","Project":"Example","TargetFramework":"net10.0","Origin":"initial"}
            """);
        Assert.Equal(DiagnosticSeverity.Error, finding!.Severity);
        Assert.Null(finding.Column);
    }

    private sealed class ReceiptHandler : ICommandHandler<ApplySourceEditCommand, SourceEditReceipt>
    {
        private readonly SourceEditReceipt _receipt;

        public ReceiptHandler(SourceEditReceipt receipt)
        {
            _receipt = receipt;
        }

        public int Calls { get; private set; }

        public Task<SourceEditReceipt> HandleAsync(ApplySourceEditCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(_receipt);
        }
    }
}
