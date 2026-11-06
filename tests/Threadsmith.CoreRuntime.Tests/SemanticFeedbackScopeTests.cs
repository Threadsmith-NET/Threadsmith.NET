namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Verifies producer-isolated semantic completion messages and combined final evidence.</summary>
public static class SemanticFeedbackScopeTests
{
    /// <summary>Optional analyzer progress and findings do not change a clean compiler completion.</summary>
    [Theory]
    [InlineData("pending", DiagnosticSeverity.Info, false)]
    [InlineData("failed", DiagnosticSeverity.Info, false)]
    [InlineData("completed", DiagnosticSeverity.Warning, false)]
    [InlineData("completed", DiagnosticSeverity.Error, false)]
    [InlineData("completed", DiagnosticSeverity.Info, false)]
    [InlineData("completed", DiagnosticSeverity.Info, true)]
    public static void CleanCompilerIgnoresAnalyzerEvidence(string status, DiagnosticSeverity severity, bool truncated)
    {
        var analysis = CleanAnalysis() with
        {
            Analyzers = new()
            {
                Status = status,
                DocumentsAnalyzed = 1,
                DocumentsInScope = 1,
                ReportableFindings = 1,
                Truncated = truncated,
                OutputFiltered = true,
                Omissions = ["Analyzer-only omission"],
            },
            Diagnostics = [Finding("AN0001", "analyzer", severity)],
        };

        var (text, disclosure) = Project(Completion(analysis, false));

        Assert.Equal(PresentationTextRole.ToolSuccess, disclosure.Role);
        Assert.Contains("Reportable errors in analyzed coverage: 0 current", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Analyzers:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AN0001", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Analyzer-only omission", text, StringComparison.Ordinal);
        Assert.DoesNotContain("exclusions filtered", text, StringComparison.Ordinal);
        Assert.DoesNotContain("analyzers ", disclosure.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("limited findings", disclosure.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("exclusions apply", disclosure.Summary, StringComparison.Ordinal);
    }

    /// <summary>Compiler progress and findings do not change a clean analyzer completion.</summary>
    [Theory]
    [InlineData(true, 0, 0, false)]
    [InlineData(false, 2, 0, false)]
    [InlineData(false, 0, 2, false)]
    [InlineData(false, 0, null, false)]
    [InlineData(false, 0, 0, true)]
    public static void CleanAnalyzersIgnoreCompilerEvidence(bool pending, int errors, int? warnings, bool truncated)
    {
        var analysis = CleanAnalysis() with
        {
            Pending = pending,
            ProjectsAnalyzed = 0,
            ProjectsInScope = 2,
            SyntaxErrors = errors,
            CurrentErrors = errors,
            CurrentWarnings = warnings,
            FindingRetentionTruncated = truncated,
            FindingProjectionTruncated = truncated,
            OutputFiltered = true,
            Omissions = ["Compiler-only omission"],
            Diagnostics = [Finding("CS0001", "compiler", DiagnosticSeverity.Error)],
        };

        var (text, disclosure) = Project(Completion(analysis, true));

        Assert.Equal(PresentationTextRole.ToolSuccess, disclosure.Role);
        Assert.Contains("Reportable analyzer findings: 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiler:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Syntax:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Selected reportable warnings", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0001", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiler-only omission", text, StringComparison.Ordinal);
        Assert.DoesNotContain("exclusions filtered", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pending", disclosure.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("projects", disclosure.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("limited findings", disclosure.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("exclusions apply", disclosure.Summary, StringComparison.Ordinal);
    }

    /// <summary>Only owned findings and truncation determine each operation's evidence role.</summary>
    [Theory]
    [InlineData(false, DiagnosticSeverity.Error, false, PresentationTextRole.Error)]
    [InlineData(false, DiagnosticSeverity.Warning, false, PresentationTextRole.Warning)]
    [InlineData(false, DiagnosticSeverity.Info, false, PresentationTextRole.ToolSuccess)]
    [InlineData(false, DiagnosticSeverity.Info, true, PresentationTextRole.Warning)]
    [InlineData(true, DiagnosticSeverity.Error, false, PresentationTextRole.Error)]
    [InlineData(true, DiagnosticSeverity.Warning, false, PresentationTextRole.Warning)]
    [InlineData(true, DiagnosticSeverity.Info, false, PresentationTextRole.ToolSuccess)]
    [InlineData(true, DiagnosticSeverity.Info, true, PresentationTextRole.Warning)]
    public static void OwnFindingsDetermineRole(bool analyzer, DiagnosticSeverity severity, bool truncated, PresentationTextRole expected)
    {
        var analysis = CleanAnalysis() with
        {
            FindingProjectionTruncated = !analyzer && truncated,
            Analyzers = new() { Status = "completed", DocumentsAnalyzed = 1, DocumentsInScope = 1, ReportableFindings = analyzer ? 1 : 0, Truncated = analyzer && truncated },
            Diagnostics = [Finding("OWN0001", analyzer ? "analyzer" : "compiler", severity)],
        };

        var (text, disclosure) = Project(Completion(analysis, analyzer));

        Assert.Equal(expected, disclosure.Role);
        Assert.Contains("OWN0001", text, StringComparison.Ordinal);
    }

    /// <summary>Analyzer availability and operation failures remain visible independently of compiler coverage.</summary>
    [Theory]
    [InlineData("partial", false, SemanticCheckOutcome.Completed, PresentationTextRole.Warning)]
    [InlineData("completed", true, SemanticCheckOutcome.Completed, PresentationTextRole.Warning)]
    [InlineData("cancelled", false, SemanticCheckOutcome.Cancelled, PresentationTextRole.Error)]
    [InlineData("failed", false, SemanticCheckOutcome.Degraded, PresentationTextRole.Warning)]
    [InlineData("skipped", false, SemanticCheckOutcome.Skipped, PresentationTextRole.ToolSuccess)]
    [InlineData("disabled", false, SemanticCheckOutcome.Skipped, PresentationTextRole.ToolSuccess)]
    public static void AnalyzerAvailabilityRemainsHonest(string status, bool obsolete, SemanticCheckOutcome outcome, PresentationTextRole expected)
    {
        var analysis = CleanAnalysis() with { Obsolete = obsolete, Analyzers = new() { Status = status, DocumentsInScope = 2 } };
        var (text, disclosure) = Project(Completion(analysis, true) with { Outcome = outcome });
        Assert.Equal(expected, disclosure.Role);
        Assert.Contains(obsolete ? "obsolete" : status, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiler:", text, StringComparison.Ordinal);
    }

    /// <summary>Each completion gets its own display budget; finalized feedback preserves both producers.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void ProducerSelectionPrecedesDisplayBounds(bool analyzer)
    {
        var compiler = Finding("CS0001", "compiler", DiagnosticSeverity.Warning);
        var analyzerFinding = Finding("AN0001", "analyzer", DiagnosticSeverity.Info);
        var analysis = CleanAnalysis() with
        {
            CurrentWarnings = 1,
            OutputFiltered = !analyzer,
            Omissions = ["Compiler omission", "Second compiler omission"],
            Analyzers = new() { Status = "completed", DocumentsAnalyzed = 1, DocumentsInScope = 1, ReportableFindings = 1, OutputFiltered = analyzer, Omissions = ["Analyzer omission", "Second analyzer omission"] },
            Diagnostics = analyzer ? [compiler, analyzerFinding] : [analyzerFinding, compiler],
        };
        var limits = new TuiResourceLimits { MaximumEditAnalysisFindings = 1, MaximumEditAnalysisOmissions = 1 };

        var (text, disclosure) = Project(Completion(analysis, analyzer), limits);

        Assert.Contains(analyzer ? "AN0001" : "CS0001", text, StringComparison.Ordinal);
        Assert.DoesNotContain(analyzer ? "CS0001" : "AN0001", text, StringComparison.Ordinal);
        Assert.Contains(analyzer ? "Analyzer omission" : "Compiler omission", text, StringComparison.Ordinal);
        Assert.DoesNotContain(analyzer ? "Compiler omission" : "Analyzer omission", text, StringComparison.Ordinal);
        Assert.Contains("1 additional omissions", text, StringComparison.Ordinal);
        Assert.Contains("exclusions filtered", text, StringComparison.Ordinal);
        Assert.Contains("exclusions apply", disclosure.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Findings shown are bounded", text, StringComparison.Ordinal);
        Assert.DoesNotContain("limited findings", disclosure.Summary, StringComparison.Ordinal);

        var finalized = new DiagnosticObserved(SessionId.New(), DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "Feedback") { EditAnalysis = analysis };
        var (combinedText, combinedDisclosure) = Project(finalized);
        Assert.Contains("CS0001", combinedText, StringComparison.Ordinal);
        Assert.Contains("AN0001", combinedText, StringComparison.Ordinal);
        Assert.Contains("Compiler omission", combinedText, StringComparison.Ordinal);
        Assert.Contains("Analyzer omission", combinedText, StringComparison.Ordinal);
        Assert.Contains("1 analyzer findings", combinedDisclosure.Summary, StringComparison.Ordinal);
        Assert.Contains("1 warnings", combinedDisclosure.Summary, StringComparison.Ordinal);
        Assert.Equal(PresentationTextRole.Warning, combinedDisclosure.Role);
    }

    /// <summary>Display bounds preserve ordering and report overflow only when needed in every scope.</summary>
    [Theory]
    [InlineData("compiler", 0)]
    [InlineData("compiler", 3)]
    [InlineData("compiler", 4)]
    [InlineData("analyzer", 0)]
    [InlineData("analyzer", 3)]
    [InlineData("analyzer", 4)]
    [InlineData("combined", 0)]
    [InlineData("combined", 3)]
    [InlineData("combined", 4)]
    public static void DisplayBoundsPreserveFindingsAndOverflow(string scope, int count)
    {
        var analysis = CleanAnalysis() with
        {
            Diagnostics = Enumerable.Range(0, count)
                .Select(index => Finding($"OWN{index:0000}", scope == "analyzer" || (scope == "combined" && index % 2 == 0) ? "analyzer" : "compiler", DiagnosticSeverity.Info))
                .ToArray(),
        };
        IDomainEvent domainEvent = scope == "combined"
            ? new DiagnosticObserved(SessionId.New(), DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "Feedback") { EditAnalysis = analysis }
            : Completion(analysis, scope == "analyzer");

        var (text, _) = Project(domainEvent, new TuiResourceLimits { MaximumEditAnalysisFindings = 3 });

        for (var index = 0; index < Math.Min(count, 3); index++)
        {
            Assert.Contains($"OWN{index:0000}", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("OWN0003", text, StringComparison.Ordinal);
        Assert.Equal(count > 3, text.Contains("Findings shown are bounded", StringComparison.Ordinal));
    }

    /// <summary>Finding selection stops after one extra matching item rather than materializing all retained diagnostics.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void DisplaySelectionStopsAtFirstScopedOverflow(bool analyzer)
    {
        var diagnostics = new StubBoundedReadDiagnostics(analyzer);
        var analysis = CleanAnalysis() with { Diagnostics = diagnostics };
        // A failed operation supplies its own header role, isolating the display enumeration from severity scans.
        var completed = Completion(analysis, analyzer) with { Outcome = SemanticCheckOutcome.Failed };
        var started = new SemanticCheckStarted(completed.SessionId, completed.OccurredAt, completed.RunId, completed.SemanticCheckId, completed.Phase, completed.CheckName);

        var text = InteractionPresentationFormatter.FormatSemanticCheckCompletion(started, completed, false, new TuiResourceLimits { MaximumEditAnalysisFindings = 3 });

        Assert.Equal(5, diagnostics.ItemsRead);
        Assert.Contains("OWN0002", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OWN0003", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OTHER", text, StringComparison.Ordinal);
        Assert.Contains("Findings shown are bounded", text, StringComparison.Ordinal);
    }

    private static SourceEditAnalysis CleanAnalysis() => new()
    {
        EffectId = Guid.NewGuid(),
        CommittedGeneration = 1,
        ProjectsAnalyzed = 1,
        ProjectsInScope = 1,
        CurrentWarnings = 0,
        Analyzers = new() { Status = "completed", DocumentsAnalyzed = 1, DocumentsInScope = 1 },
    };

    private static SourceEditDiagnostic Finding(string code, string producer, DiagnosticSeverity severity) =>
        new(code, "Finding", "Example.cs", 1, "Example", "net10.0", "initial") { Producer = producer, Severity = severity };

    private static SemanticCheckCompleted Completion(SourceEditAnalysis analysis, bool analyzer) =>
        new(SessionId.New(), DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), SemanticCheckPhase.PreMutation, analyzer ? "advisory edit analyzers" : "advisory edit analysis", SemanticCheckOutcome.Completed) { Analysis = analysis };

    private static (string Text, PresentationDisclosure Disclosure) Project(IDomainEvent domainEvent, TuiResourceLimits? limits = null)
    {
        // Exercise the same durable event path used by replay, not just formatter helpers.
        var restored = DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(domainEvent), 1, DomainEventJson.Serialize(domainEvent));
        var transcript = new ConversationTranscript(string.Empty, limits: limits);
        Assert.True(transcript.Apply(restored));
        var segments = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(segments, restored, transcript.Text);
        var disclosure = InteractionEventSegments.CreateDisclosure(restored, segments);
        Assert.NotNull(disclosure);
        Assert.Contains(segments, segment => segment.Text.Contains("SEMANTIC CHECKS:", StringComparison.Ordinal) && segment.Role == disclosure.Role);
        Assert.Equal(transcript.Text, string.Concat(segments.Select(segment => segment.Text)));
        return (transcript.Text, disclosure);
    }

    private sealed class StubBoundedReadDiagnostics : IReadOnlyList<SourceEditDiagnostic>
    {
        private readonly bool _analyzer;

        public StubBoundedReadDiagnostics(bool analyzer)
        {
            _analyzer = analyzer;
        }

        public int Count => 10000;

        public int ItemsRead { get; private set; }

        public SourceEditDiagnostic this[int index]
        {
            get
            {
                Assert.InRange(index, 0, 4);
                ItemsRead++;
                return index == 0
                    ? Finding("OTHER", _analyzer ? "compiler" : "analyzer", DiagnosticSeverity.Info)
                    : Finding($"OWN{index - 1:0000}", _analyzer ? "analyzer" : "compiler", DiagnosticSeverity.Info);
            }
        }

        public IEnumerator<SourceEditDiagnostic> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
