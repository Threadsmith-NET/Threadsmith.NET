namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Exercises structured semantic disclosures through the retained terminal widget.</summary>
[Collection("TUIKit terminal")]
public sealed class SemanticDisclosureTests
{
    /// <summary>Mouse expansion preserves findings, brackets, copy, and reflow.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClickExpandsBracketedEvidenceAndSurvivesResize(bool finalized)
    {
        var item = CreateItem("candidate", finalized);
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationTextItem([new("Unterminated answer", PresentationTextRole.Default)]), item]));
        var screen = Render(view);
        Assert.DoesNotContain("CS5001", screen, StringComparison.Ordinal);
        Assert.Contains("3 errors", screen, StringComparison.Ordinal);
        Assert.Equal(PresentationTextRole.Error, item.Disclosure!.Role);
        Assert.Contains("CS5001", string.Concat(TranscriptView.Project(item, 160).Select(segment => segment.Text)), StringComparison.Ordinal);

        ClickHeader(view, screen, "[+]");
        screen = Render(view);
        Assert.Contains("Unterminated answer", screen, StringComparison.Ordinal);
        Assert.Contains("CS5001", screen, StringComparison.Ordinal);
        Assert.Contains("├", screen, StringComparison.Ordinal);
        Assert.Contains("└", screen, StringComparison.Ordinal);
        Assert.Contains("[-]", screen, StringComparison.Ordinal);
        view.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        Assert.Contains("CS5001", view.SelectedText(), StringComparison.Ordinal);
        screen = Render(view, 80);
        Assert.Contains("CS5001", screen, StringComparison.Ordinal);
        ClickHeader(view, screen, "[-]");
        Assert.DoesNotContain("CS5001", Render(view), StringComparison.Ordinal);
        Assert.Contains("Unterminated answer", string.Join(' ', view.Lines), StringComparison.Ordinal);
    }

    /// <summary>Disclosure reflow respects the cleared viewport boundary.</summary>
    [Fact]
    public void ExpandingAfterClearDoesNotRevealClearedHistory()
    {
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationTextItem([new("Cleared history\n", PresentationTextRole.Default)])]));
        Render(view);
        view.ClearView();
        view.Present(new PresentationBatch([CreateItem("new check", false)]));
        var screen = Render(view);
        Assert.DoesNotContain("Cleared history", screen, StringComparison.Ordinal);
        ClickHeader(view, screen, "[+]");
        Assert.DoesNotContain("Cleared history", Render(view), StringComparison.Ordinal);
        Assert.DoesNotContain("Cleared history", Render(view, 80), StringComparison.Ordinal);
    }

    /// <summary>Keyboard navigation reaches each block and replacement resets state.</summary>
    [Fact]
    public void KeyboardCanChooseSecondHeaderAndReplacementResetsExpansion()
    {
        var view = new TranscriptView();
        var first = CreateItem("first", false, "CS1001");
        var second = CreateItem("second", false, "CS1002");
        view.Present(new PresentationBatch([first, second]));
        Render(view);
        Assert.True(view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl)));
        Assert.True(view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl)));
        Assert.True(view.HandleKey(KeyEvent.Special(KeyCode.Enter)));
        var screen = Render(view);
        Assert.Contains("CS1002", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("CS1001", screen, StringComparison.Ordinal);
        view.Present(new PresentationBatch([second]) { ReplaceOutput = true });
        Assert.DoesNotContain("CS1002", Render(view), StringComparison.Ordinal);
    }

    /// <summary>Bounded retention discloses omissions and evicted content stays evicted.</summary>
    [Fact]
    public void OversizedDetailsRemainVisibleAsBoundedOmissionAndCannotResurrect()
    {
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptBytes = 512 });
        var item = CreateItem("large", false);
        item = item with { Segments = [.. item.Segments, new(new string('x', 4000), PresentationTextRole.Muted)] };
        view.Present(new PresentationBatch([item]));
        Assert.Contains("Details omitted by transcript retention limits", Render(view), StringComparison.Ordinal);
        Assert.DoesNotContain("[+]", string.Join(' ', view.Lines), StringComparison.Ordinal);
        Assert.InRange(view.RetainedBytes, 0, 512);
        Assert.Contains("Details omitted", Render(view, 80), StringComparison.Ordinal);
        view.Present(new PresentationBatch([new PresentationTextItem([new(new string('z', 700), PresentationTextRole.Default)])]));
        Assert.DoesNotContain("SEMANTIC CHECKS", Render(view), StringComparison.Ordinal);
        Assert.DoesNotContain("SEMANTIC CHECKS", Render(view, 160), StringComparison.Ordinal);
    }

    /// <summary>Overall roles indicate findings and uncertainty.</summary>
    [Theory]
    [InlineData(0, 0, false, false, PresentationTextRole.ToolSuccess)]
    [InlineData(1, 0, false, false, PresentationTextRole.Error)]
    [InlineData(0, 2, false, false, PresentationTextRole.Warning)]
    [InlineData(0, 0, true, false, PresentationTextRole.Warning)]
    [InlineData(0, 0, false, true, PresentationTextRole.ToolSuccess)]
    public void OverallRoleReflectsEvidence(int errors, int warnings, bool pending, bool filtered, PresentationTextRole expected)
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            CommittedGeneration = 1,
            ProjectsAnalyzed = 1,
            ProjectsInScope = 1,
            CurrentErrors = errors,
            CurrentWarnings = warnings,
            Pending = pending,
            OutputFiltered = filtered,
        };
        Assert.Equal(expected, InteractionPresentationFormatter.GetSemanticFeedbackRole(analysis, SemanticCheckOutcome.Completed));
        Assert.Equal(expected, InteractionPresentationFormatter.GetFinalizedEditFeedbackRole(analysis));
    }

    /// <summary>Unavailable totals remain unknown in collapsed summaries.</summary>
    [Fact]
    public void DegradedSummaryDoesNotClaimZeroErrors()
    {
        var completed = new SemanticCheckCompleted(SessionId.New(), DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), SemanticCheckPhase.PostMutation, "check", SemanticCheckOutcome.Degraded)
        {
            Analysis = new() { EffectId = Guid.NewGuid(), ProjectsAnalyzed = 1, ProjectsInScope = 2, CurrentWarnings = 0 },
        };
        var item = ProjectEvent(completed);
        Assert.Contains("error totals unknown", item.Disclosure!.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("0 errors", item.Disclosure.Summary, StringComparison.Ordinal);
    }

    /// <summary>Reducing retention cannot turn a surviving detail row into a disclosure header.</summary>
    [Fact]
    public void ReducedBudgetKeepsRealHeaderAndDisclosesOmittedDetails()
    {
        var view = new TranscriptView();
        view.Present(new PresentationBatch([CreateItem("shrinking budget", false)]));
        Render(view);
        view.SetRetentionBudget(8192, 5);
        var screen = Render(view);
        ClickHeader(view, screen, "[+]");
        screen = Render(view);
        Assert.Contains("[-]", screen, StringComparison.Ordinal);
        Assert.Contains("Details omitted", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("CS5001", screen, StringComparison.Ordinal);
        ClickHeader(view, screen, "[-]");
        Assert.Contains("[+]", Render(view), StringComparison.Ordinal);
    }

    private static PresentationTextItem CreateItem(string name, bool finalized, string code = "CS5001")
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            CommittedGeneration = 5,
            CandidateReused = finalized,
            ProjectsAnalyzed = 5,
            ProjectsInScope = 5,
            CurrentErrors = 3,
            CurrentWarnings = 0,
            ErrorComparisonAvailable = true,
            SyntaxDocumentsAnalyzed = 1,
            SyntaxErrors = 0,
            Diagnostics = [new(code, "Missing entry point", "Example.cs", 1, "Example", "net10.0", "initial")],
        };
        IDomainEvent domainEvent = finalized
            ? new DiagnosticObserved(SessionId.New(), DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "summary") { EditAnalysis = analysis }
            : new SemanticCheckCompleted(SessionId.New(), DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), SemanticCheckPhase.PreMutation, name, SemanticCheckOutcome.Completed) { Analysis = analysis };
        return ProjectEvent(DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(domainEvent), 1, DomainEventJson.Serialize(domainEvent)));
    }

    private static PresentationTextItem ProjectEvent(IDomainEvent domainEvent)
    {
        var transcript = new ConversationTranscript(string.Empty);
        Assert.True(transcript.Apply(domainEvent));
        var segments = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(segments, domainEvent, transcript.Text);
        return new(segments) { Disclosure = InteractionEventSegments.CreateDisclosure(domainEvent, segments) };
    }

    private static string Render(TranscriptView view, int width = 160)
    {
        var buffer = new CellBuffer(width, 40);
        view.Render(new BufferSurface(buffer));
        return TUIKit.Testing.Snapshot.ToText(buffer);
    }

    private static void ClickHeader(TranscriptView view, string screen, string marker)
    {
        var row = Array.FindIndex(screen.Split('\n'), line => line.Contains(marker, StringComparison.Ordinal));
        Assert.True(row >= 0);
        Assert.True(view.HandleMouse(new MouseEvent(MouseEventKind.Press, MouseButton.Left, 1, row, KeyModifiers.None, 1)));
    }
}
