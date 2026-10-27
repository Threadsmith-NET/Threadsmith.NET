namespace Threadsmith.ModelTooling.Tests;

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

public static partial class SourceEditAnalysisTests
{
    /// <summary>Warnings for separate nullable arguments on one line retain their source columns.</summary>
    [Fact]
    public static async Task SameLineWarningsRetainDistinctColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        const string relative = "Contracts/Services.cs";
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative), ct);
        const string declaration = "public class NullableItems { public void M(string? first, string? second) { System.Collections.Generic.List<object> items = [first, second]; } }";
        var changed = original + "\n#nullable enable\n" + declaration;

        var analysis = await AnalyzeCompletedAsync(engine, events, command, fixture.Root, Snapshot(relative, original, changed), ct);

        var findings = analysis.Diagnostics.Where(item => item.Code == "CS8604").ToArray();
        Assert.Equal(2, findings.Length);
        Assert.Equal(findings[0].Line, findings[1].Line);
        Assert.Equal<int?>([declaration.IndexOf("[first", StringComparison.Ordinal) + 2, declaration.IndexOf("second]", StringComparison.Ordinal) + 1], findings.Select(item => item.Column).Order());
    }

    /// <summary>Disabled analyzer feedback leaves ordinary edits on their existing compiler-only path.</summary>
    [Fact]
    public static async Task AutomaticEditDoesNotExecuteSelectedAnalyzers()
    {
        using var fixture = new TestSemanticSolution();
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false, DiagnosticIds = ["TS1170"] } } });
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        const string relative = "Contracts/Services.cs";
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative), ct);
        // The fixture analyzer throws on this declaration if its actions execute.
        var changed = original + "\npublic class AnalyzerFailure {}";
        var command = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        var result = await AnalyzeCompletedAsync(engine, events, command, fixture.Root, Snapshot(relative, original, changed), ct);
        Assert.False(result.Pending);
        Assert.Equal(result.ProjectsInScope, result.ProjectsAnalyzed);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code.StartsWith("TS117", StringComparison.Ordinal) || diagnostic.Code == "AD0001");
        Assert.True(engine.EditAnalysisStatistics.DiagnosticPasses > 0);
    }

    /// <summary>Checks mixed severities, exclusions, retention and compilation reuse.</summary>
    [Theory]
    [InlineData(false, false, false, 64, true)]
    [InlineData(true, false, false, 64, false)]
    [InlineData(false, true, false, 64, true)]
    [InlineData(false, true, true, 1, false)]
    [InlineData(false, false, true, 1, false)]
    [InlineData(false, false, false, 1, false)]
    public static async Task MixedCompilerFindingsRespectEffectiveSeverityAndExclusions(bool suppress, bool promote, bool exclude, int retained, bool visibleWarning)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        var props = Path.Combine(fixture.Root, "Directory.Build.props");
        var properties = await File.ReadAllTextAsync(props, ct);
        await File.WriteAllTextAsync(props, properties.Replace("</PropertyGroup>", $"<Nullable>enable</Nullable><TreatWarningsAsErrors>false</TreatWarningsAsErrors><WarningsAsErrors>{(promote ? "CS8602" : string.Empty)}</WarningsAsErrors></PropertyGroup>", StringComparison.Ordinal), ct);
        await using var events = new DomainEventStream();
        var options = new EditAnalysisOptions
        {
            CompilerWarningIds = ["CS8602"],
            ExcludedDiagnosticIds = exclude ? [" cs0246 ", "Cs8602"] : [],
            MaximumRetainedDiagnostics = retained,
            MaximumReportedDiagnostics = retained,
        };
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = options });
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        var path = Path.Combine(fixture.Root, "Contracts", "Services.cs");
        var original = await File.ReadAllTextAsync(path, ct);
        var changed = original + "\n#nullable enable\n" + (suppress ? "#pragma warning disable CS8602\n" : string.Empty)
            + "public class WarningEdit { public int M(string? value) => value.Length; public MissingType Value; }";
        var analysis = await AnalyzeCompletedAsync(engine, events, command, fixture.Root, Snapshot("Contracts/Services.cs", original, changed), ct);

        Assert.Equal(visibleWarning, analysis.Diagnostics.Any(item => item.Code == "CS8602"));
        if (visibleWarning)
        {
            Assert.Contains(analysis.Diagnostics, item => item.Code == "CS8602" && item.Severity == (promote ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning));
        }

        Assert.Equal(exclude, analysis.OutputFiltered);
        Assert.Equal(retained == 1 && !exclude, analysis.DiagnosticsTruncated);
        Assert.Equal(retained != 1 || exclude, analysis.ErrorComparisonAvailable);
        Assert.Equal(0, analysis.ResolvedErrors);
        if (exclude)
        {
            Assert.DoesNotContain(analysis.Diagnostics, item => item.Code is "CS0246" or "CS8602");
            Assert.Equal(1, analysis.CurrentErrors); // Unrelated fixture entry-point error remains visible.
            Assert.Contains(analysis.Diagnostics, item => item.Code == "CS8805");
            var compilation = await engine.CaptureMutationSnapshot().Solution.Projects.Single(project => project.Name == "Contracts").GetCompilationAsync(ct);
            Assert.NotNull(compilation);
            // Filtering is not installed as a Roslyn suppression option.
            Assert.False(compilation.Options.SpecificDiagnosticOptions.ContainsKey("CS0246"));
        }
        else
        {
            Assert.True(analysis.CurrentErrors >= 2);
            Assert.Equal(promote && !suppress ? 1 : 0, analysis.NewErrors - 1);
        }

        var passes = engine.EditAnalysisStatistics.DiagnosticPasses;
        engine.ConfirmEditApplied(command.EffectId);
        var snapshot = Snapshot("Contracts/Services.cs", original, changed);
        await engine.RefreshDocumentsAsync([new(path, changed, snapshot.Endpoints[0].AfterSha256!)], ct);
        Assert.Equal(passes, engine.EditAnalysisStatistics.DiagnosticPasses);
        var committed = engine.CaptureMutationSnapshot().Solution;
        var committedCompilation = await committed.Projects.Single(project => project.Name == "Contracts").GetCompilationAsync(ct);
        Assert.Contains(committedCompilation!.GetDiagnostics(ct), item => item.Id == "CS0246" && item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.False(committedCompilation.Emit(Stream.Null, cancellationToken: ct).Success);
    }

    /// <summary>Warning changes preserve error deltas and initial warning origins.</summary>
    [Fact]
    public static async Task WarningRepairDoesNotResolveErrorsAndNewWarningsPrecedeInitialWarnings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        var path = Path.Combine(fixture.Root, "Contracts", "Services.cs");
        var original = await File.ReadAllTextAsync(path, ct)
            + "\n#nullable enable\npublic class InitialWarning { public int M(string? value) => value.Length; }";
        await File.WriteAllTextAsync(path, original, ct);
        var props = Path.Combine(fixture.Root, "Directory.Build.props");
        var properties = await File.ReadAllTextAsync(props, ct);
        await File.WriteAllTextAsync(props, properties.Replace("</PropertyGroup>", "<Nullable>enable</Nullable><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>", StringComparison.Ordinal), ct);
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { CompilerWarningIds = ["CS8602", "CS8604"], MaximumReportedDiagnostics = 2 } });
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
        mixed = mixed with { Endpoints = [.. mixed.Endpoints, new("Contracts/New.cs", null, Hash(added)) { FinalBytes = Encoding.UTF8.GetBytes(added) }] };
        var partial = await AnalyzeCompletedAsync(engine, events, Command(command.SessionId, command.RunId, command.WorkspaceId), fixture.Root, mixed, ct);
        Assert.Null(partial.CurrentWarnings);
        Assert.False(partial.FindingRetentionTruncated);
        Assert.True(partial.FindingProjectionTruncated);
        Assert.True(partial.DiagnosticsTruncated);
        Assert.False(partial.ErrorComparisonAvailable);
        Assert.Equal(2, partial.Diagnostics.Count);
    }

    /// <summary>Syntax warnings await effective compilation severity; unassigned syntax does not leak them.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task SyntaxWarningsRespectProjectPolicy(bool suppress)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        var props = Path.Combine(fixture.Root, "Directory.Build.props");
        var properties = await File.ReadAllTextAsync(props, ct);
        var policy = suppress ? "<NoWarn>CS1030</NoWarn>" : "<WarningsAsErrors>CS1030</WarningsAsErrors>";
        await File.WriteAllTextAsync(props, properties.Replace("</PropertyGroup>", policy + "</PropertyGroup>", StringComparison.Ordinal), ct);
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { CompilerWarningIds = ["CS1030"] } });
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "Contracts", "Services.cs"), ct);
        var existing = Snapshot("Contracts/Services.cs", original, original + "\n#warning assigned warning\n");
        const string added = "#warning unassigned warning\nclass NewFile {}";
        var snapshot = existing with { Endpoints = [.. existing.Endpoints, new("Contracts/New.cs", null, Hash(added)) { FinalBytes = Encoding.UTF8.GetBytes(added) }] };
        var analysis = await AnalyzeCompletedAsync(engine, events, command, fixture.Root, snapshot, ct);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Message.Contains("unassigned warning", StringComparison.Ordinal));
        Assert.Equal(0, analysis.SyntaxErrors);
        if (suppress)
        {
            Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "CS1030");
        }
        else
        {
            Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "CS1030" && diagnostic.Severity == DiagnosticSeverity.Error);
        }
    }

    private static async Task<SourceEditAnalysis> AnalyzeCompletedAsync(SemanticEngine engine, DomainEventStream events, ApplySourceEditCommand command, string root, MutationEffectSnapshot snapshot, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<SourceEditAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { Analysis: { } analysis } && analysis.EffectId == command.EffectId)
            {
                completion.TrySetResult(analysis);
            }

            return Task.CompletedTask;
        });
        Assert.NotNull(await engine.AnalyzeCandidateAsync(command, root, snapshot, TimeSpan.Zero, cancellationToken));
        return await completion.Task.WaitAsync(cancellationToken);
    }
}
