namespace Threadsmith.Mutations.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Tests the host-owned evidence rules without a model or a clock.</summary>
public static class GoalReceiptTests
{
    /// <summary>A long losing streak cannot evict the passing check or its mutation boundary.</summary>
    [Fact]
    public static void JournalKeepsPassingResultAndLatestMutationThroughFailures()
    {
        var journal = new GoalJournal();
        var pass = new GoalReceipt(Guid.NewGuid(), "test", "{}", "Passed", true, true);
        var edit = pass with { Invocation = Guid.NewGuid(), Tool = "edit_source", Outcome = "Applied" };
        journal.AddReceipt(pass, new string('x', 4096), false);
        journal.AddReceipt(edit, "Applied", false);
        for (var i = 0; i < 40; i++)
        {
            journal.AddReceipt(pass with { Invocation = Guid.NewGuid(), Succeeded = false, Outcome = "Failed" }, "Failed", false);
        }

        var (entries, _, omitted) = journal.Capture();
        Assert.True(omitted);
        Assert.Equal(32, entries.Length);
        Assert.True(Assert.Single(entries, item => item.Receipt == pass).Omitted);
        Assert.Same(edit, Assert.Single(entries, item => item.Receipt!.MayHaveEdited).Receipt);
    }

    /// <summary>Checks retain run and source identity across assessment and goal resume.</summary>
    [Fact]
    public static void CheckRequiresMatchingRunAndSourceIdentity()
    {
        var run = RunId.New();
        var workspace = WorkspaceId.New();
        var baseline = new WorkspaceBaseline(workspace, "repo", DateTimeOffset.UnixEpoch, [], "revision");
        var check = new GoalReceipt(Guid.NewGuid(), "test_run_targeted", "{}", "TargetedTest:Passed", true, true)
        {
            Run = run, Workspace = workspace.Value.ToString(), Revision = baseline.GitRevision, Baseline = baseline.CapturedAt, SourceProof = "RecordedBaseline", ActionKind = "test",
        };
        Assert.False(check.IsHistorical(run, workspace, baseline));
        Assert.True(check.IsHistorical(RunId.New(), workspace, baseline));
        Assert.True(check.IsHistorical(run, workspace, baseline with { GitRevision = "different" }));
        Assert.True(check.IsHistorical(run, workspace, null));
        var required = new GoalVerdict.GoalRequirement("Test the final source", "action", "test_run_targeted", string.Empty, []);
        Assert.True(required.NeedsUnavailableSourceProof(check));
        Assert.True((check with { Tool = "shell", CanChangeFiles = true }).MayHaveEdited);
    }

    /// <summary>A successful invocation must match the complete target, not just its suffix.</summary>
    [Fact]
    public static void ActionMatchesExactArgumentAndSuccessfulOutcome()
    {
        var required = new GoalVerdict.GoalRequirement("Run ParserTests", "action", "test", "ParserTests", []);
        var receipt = new GoalReceipt(Guid.NewGuid(), "test", """{"filter":"OtherParserTests"}""", "Passed", true, true) { ActionKind = "test" };
        Assert.False(required.Matches(receipt));
        Assert.False((required with { Scope = string.Empty }).Matches(receipt));
        Assert.False(required.Matches(receipt with { Tool = "read_file", Filter = "ParserTests", ActionKind = null }));
        Assert.False((required with { Tool = "read_file" }).Matches(receipt with { Tool = "read_file", Filter = "ParserTests", ActionKind = null }));
        var build = required with { Clause = "Please build website", Tool = "dotnet_build", Scope = string.Empty };
        Assert.False(build.Matches(receipt with { Tool = "dotnet_build", ActionKind = "Build", Target = "unrelated" }));
        Assert.True((build with { Scope = "website" }).Matches(receipt with { Tool = "dotnet_build", ActionKind = "Build", Target = "website" }));
        Assert.False(required.Matches(receipt with { Historical = true, Filter = "ParserTests" }));
        Assert.False(required.Matches(receipt with { Arguments = """{"notes":"ParserTests"}""" }));
        Assert.False(required.Matches(receipt with { Filter = "Other", Arguments = """{"filter":"ParserTests"}""" }));
        Assert.True(required.Matches(receipt with { Filter = "ParserTests" }));
        Assert.False(required.Matches(receipt with { Filter = "ParserTests", Succeeded = false }));
        Assert.True(required.Matches(receipt with { Arguments = """{"filter":"ParserTests"}""" }));
    }

    /// <summary>Scope and exclusions carried in an action clause still require a matching action receipt.</summary>
    [Fact]
    public static void DefectRemediationMatchesEditWithScopeAndExclusions()
    {
        const string clause = "Address the remaining defects above. Linux support is in scope. Ignore feedback on missing tests or manual verification for now.";
        var requirement = new GoalVerdict.GoalRequirement(clause, "action", "edit_source", string.Empty, []);
        var edit = new GoalReceipt(Guid.NewGuid(), "edit_source", "{}", "Applied", true, false) { ActionKind = "edit" };

        Assert.True(requirement.Matches(edit));
        Assert.False(requirement.Matches(edit with { Succeeded = false }));
    }

    /// <summary>The build and test receipts in the Chaos run jointly prove the goal before the final status message.</summary>
    [Fact]
    public static void AssessesBuildAndTestsAcrossTheTurn()
    {
        const string goal = "do a full build and run all tests and verify no errors or failed tests";
        const string response = """
            {"version":2,"verdict":"Met","summary":"The solution built and all 158 tests passed.","reason":"The rebuild had zero errors and the solution test run had zero failures.","evidence":[1,2],"nextStep":"","requirements":[
            {"clause":"do a full build","kind":"action","tool":"run_process","scope":"src/CHAOS.sln","evidence":[1]},
            {"clause":"run all tests","kind":"action","tool":"run_process","scope":"src/CHAOS.sln","evidence":[2]},
            {"clause":"verify no errors or failed tests","kind":"action","tool":"run_process","scope":"src/CHAOS.sln","evidence":[1,2]}]}
            """;
        var journal = new GoalJournal();
        var build = new GoalReceipt(Guid.NewGuid(), "run_process", """{"command":"dotnet build src/CHAOS.sln --no-incremental"}""", "None", true, false) { ActionKind = "process", CanChangeFiles = true };
        var test = build with { Invocation = Guid.NewGuid(), Arguments = """{"command":"dotnet test --solution src/CHAOS.sln --no-build"}""" };
        journal.AddReceipt(build, "0 Warning(s)\n0 Error(s)", false);
        journal.AddReceipt(test, "failed: 0\nsucceeded: 158\nskipped: 0", false);
        journal.AddReceipt(build with { Invocation = Guid.NewGuid(), Arguments = """{"command":"git status --short --branch"}""" }, "## main", false);
        journal.Add("answer", "The working tree is clean.", false);
        var evidence = journal.Capture().Evidence;

        Assert.Equal("Met", GoalVerdict.Parse(response, goal, evidence).Verdict);
        evidence[1] = evidence[1] with { Receipt = test with { Succeeded = false }, Text = "failed: 1" };
        Assert.Throws<InvalidDataException>(() => GoalVerdict.Parse(response, goal, evidence));
    }

    /// <summary>A confirmed Git commit can prove both the action and the message it supplied.</summary>
    [Theory]
    [InlineData("action", "run_process")]
    [InlineData("text", "")]
    public static void CommitGoalUsesObservedGitResult(string messageKind, string messageTool)
    {
        const string goal = "create a commit message and commit all changes";
        var response = $$"""
            {"version":2,"verdict":"Met","summary":"Committed all changes.","reason":"Git confirmed the commit and message.","evidence":[1],"nextStep":"","requirements":[
            {"clause":"create a commit message","kind":"{{messageKind}}","tool":"{{messageTool}}","scope":"","evidence":[1]},
            {"clause":"commit all changes","kind":"action","tool":"run_process","scope":"","evidence":[1]}]}
            """;
        var generic = new GoalReceipt(Guid.NewGuid(), "run_process", "[omitted: overlong arguments]", "None", true, false) { ActionKind = "process" };
        var entry = new GoalEvidence(1, "tool:run_process:None", "[main 9708c98] A useful subject", false, generic);
        var confirmed = entry with { Receipt = generic with { GitCommitHasExplicitMessage = true } };

        Assert.Throws<InvalidDataException>(() => GoalVerdict.Parse(response, goal, [entry]));
        Assert.Equal("Met", GoalVerdict.Parse(response, goal, [confirmed]).Verdict);
    }
}
