namespace Threadsmith.Mutations.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

public sealed partial class SourceEditApplicationTests
{
    /// <summary>Completed reported findings reject before intent, retain feedback, and replay without another analysis.</summary>
    [Theory]
    [InlineData(DiagnosticSeverity.Error, false)]
    [InlineData(DiagnosticSeverity.Warning, false)]
    [InlineData(DiagnosticSeverity.Error, true)]
    [InlineData(DiagnosticSeverity.Warning, true)]
    public async Task CompletedPreEditFindingsRejectWithoutWritingAndReplayTheRejection(DiagnosticSeverity severity, bool omitDetails)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new ControlledCandidateAnalyzer();
        var edits = fixture.CreateApplication(analyzer: analyzer);
        var command = fixture.Replace("Example", "Changed", false);

        var edit = edits.HandleAsync(command, ct);
        await analyzer.Entered.Task.WaitAsync(ct);
        Assert.False(edit.IsCompleted);
        Assert.Equal(0, fixture.Commits.Count);
        Assert.Equal("class Example { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        analyzer.Result.SetResult(new()
        {
            EffectId = command.EffectId,
            CandidateInputsComplete = true,
            CompilerCompletedSuccessfully = true,
            ProjectsAnalyzed = 1,
            ProjectsInScope = 1,
            CurrentErrors = severity == DiagnosticSeverity.Error ? 1 : 0,
            CurrentWarnings = severity == DiagnosticSeverity.Warning ? 1 : 0,
            Diagnostics = omitDetails ? [] : [new("CS0001", "Repair the candidate", "Example.cs", 1, "Example", "net10.0", "initial") { Severity = severity }],
        });
        var receipt = await edit;

        Assert.Equal(TimeSpan.FromSeconds(5), analyzer.Allowance);
        Assert.Equal(SourceEditStatus.NotApplied, receipt.Status);
        Assert.Empty(receipt.ChangedFiles);
        Assert.NotNull(receipt.Analysis);
        Assert.Equal(0, fixture.Commits.Count);
        Assert.True(analyzer.Discarded);
        Assert.False(analyzer.Applied);
        Assert.Equal("class Example { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        var recorded = await fixture.Store.GetEffectAsync(command.EffectId, ct);
        Assert.NotNull(recorded);
        Assert.False(recorded.HasWriteIntent);
        Assert.Equal(SourceEditStatus.NotApplied, (await edits.HandleAsync(command, ct)).Status);
        Assert.Equal(1, analyzer.Calls);
    }

    /// <summary>Run cancellation interrupts observation without publishing source effects.</summary>
    [Fact]
    public async Task CancellationDuringPreEditObservationDiscardsWithoutWriting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new ControlledCandidateAnalyzer();
        var edits = fixture.CreateApplication(analyzer: analyzer);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var edit = edits.HandleAsync(fixture.Replace("Example", "Changed", false), cancellation.Token);
        await analyzer.Entered.Task.WaitAsync(ct);
        await cancellation.CancelAsync();

#pragma warning disable VSTHRD003 // This test owns the edit task whose cancellation it asserts.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edit);
#pragma warning restore VSTHRD003
        Assert.True(analyzer.Discarded);
        Assert.Equal(0, fixture.Commits.Count);
        Assert.Equal("class Example { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
    }

    /// <summary>The normal tool response carries rejection and actionable findings to the model.</summary>
    [Fact]
    public async Task EditToolReturnsRejectedCandidateDiagnosticsToTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new ControlledCandidateAnalyzer();
        var tool = new SourceEditTool(fixture.CreateApplication(analyzer: analyzer), TestPromptLoader.Instance);
        var resultTask = tool.ExecuteAsync(
            new() { Rationale = "Apply requested change", Mutations = fixture.Replace("Example", "Changed", false).Instructions.Mutations },
            new(ToolInvocationId.New(), fixture.SessionId, fixture.RunId, new() { RepositoryPath = fixture.Repository, WorkspaceId = fixture.WorkspaceId, RequestedBy = "model" }),
            ct);
        var command = await analyzer.Entered.Task.WaitAsync(ct);
        analyzer.Result.SetResult(new()
        {
            EffectId = command.EffectId,
            CandidateInputsComplete = true,
            CompilerCompletedSuccessfully = true,
            CurrentErrors = 1,
            CurrentWarnings = 0,
            ProjectsAnalyzed = 1,
            ProjectsInScope = 1,
            Diagnostics = [new("CS0246", "Missing type", "Example.cs", 1, "Example", "net10.0", "introduced")],
        });
        var result = await resultTask;
        using var json = JsonDocument.Parse(Assert.IsType<string>(result.ModelResultContent));
        Assert.Equal("NotApplied", json.RootElement.GetProperty("Status").GetString());
        Assert.Contains("not applied", json.RootElement.GetProperty("Detail").GetString(), StringComparison.Ordinal);
        Assert.Equal("CS0246", json.RootElement.GetProperty("Analysis").GetProperty("Diagnostics")[0].GetProperty("Code").GetString());
        Assert.Equal(0, fixture.Commits.Count);
    }

    private sealed class ControlledCandidateAnalyzer : ISourceEditAnalyzer
    {
        public TaskCompletionSource<ApplySourceEditCommand> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<SourceEditAnalysis> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SourceEditAnalysis? Latest { get; set; }

        public TimeSpan Allowance { get; private set; }

        public int Calls { get; private set; }

        public bool Applied { get; private set; }

        public bool Discarded { get; private set; }

        public bool HasSemanticInputs(WorkspaceId workspaceId, string repositoryPath, MutationEffectSnapshot snapshot) => true;

        public async Task<SourceEditAnalysis?> AnalyzeCandidateAsync(ApplySourceEditCommand command, string repositoryPath, MutationEffectSnapshot snapshot, TimeSpan immediateAllowance, CancellationToken cancellationToken = default)
        {
            Calls++;
            Allowance = immediateAllowance;
            Entered.TrySetResult(command);
            Latest = await Result.Task.WaitAsync(cancellationToken);
            return Latest;
        }

        public SourceEditAnalysis? GetLatestAnalysis(SessionId sessionId, RunId runId, WorkspaceId workspaceId, Guid effectId) => Latest;

        public void ConfirmApplied(WorkspaceId workspaceId, Guid effectId)
        {
            Applied = true;
            Latest = Latest! with { CommittedGeneration = 1 };
        }

        public void DiscardCandidate(WorkspaceId workspaceId, Guid effectId) => Discarded = true;
    }
}
