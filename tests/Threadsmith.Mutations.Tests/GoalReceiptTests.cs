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
}
