namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Receipt observation provenance and bounded selection regressions.</summary>
public sealed class ConversationReceiptChecksTests
{
    /// <summary>Preserves source and authority limits across observation boundaries.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaselineChangeDistinguishesEarlierAndInterveningChecks(bool during)
    {
        var ct = TestContext.Current.CancellationToken;
        var checks = new ConversationReceiptChecks();
        var baseline = new WorkspaceBaseline(WorkspaceId.New(), "/repo", DateTimeOffset.UnixEpoch, [], "same-sha");
        var context = CreateContext(checks) with { Invocation = CreateContext(checks).Invocation with { ReceiptBaseline = () => baseline } };
        await ConversationReceiptChecks.ObserveAsync(
            context,
            "dotnet_build",
            "a.csproj",
            "build",
            _ =>
        {
            if (during)
            {
                baseline = baseline with { CapturedAt = baseline.CapturedAt.AddSeconds(1) };
            }

            return Task.FromResult(Result(true));
        },
            ct);
        if (!during)
        {
            baseline = baseline with { CapturedAt = baseline.CapturedAt.AddSeconds(1) };
        }

        var observation = Assert.Single(checks.Snapshot(baseline).Checks);
        Assert.Equal(during ? "Indeterminate: baseline changed during check" : "EarlierBaseline", observation.Applicability);
        Assert.Contains("unverified", observation.SourceProof, StringComparison.Ordinal);
    }

    /// <summary>Retains unresolved work within the fixed run-local capacity.</summary>
    [Fact]
    public async Task BoundedSelectionRetainsFailureAndCountsOmissionsWithoutCollapsingDifferentChecks()
    {
        var ct = TestContext.Current.CancellationToken;
        var checks = new ConversationReceiptChecks();
        for (var index = 0; index < 12; index++)
        {
            await ConversationReceiptChecks.ObserveAsync(CreateContext(checks), "dotnet_build", "a.csproj", "build", _ => Task.FromResult(Result(index != 0)), ct);
        }

        var snapshot = checks.Snapshot(null);
        Assert.Equal(12, snapshot.Total);
        Assert.Equal(8, snapshot.Checks.Length);
        Assert.Single(snapshot.Checks, check => !check.Succeeded);
    }

    /// <summary>Preserves source and authority limits across observation boundaries.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildAndArbitraryProcessResultsNeverBecomeParentVerification(bool child)
    {
        var checks = new ConversationReceiptChecks();
        var context = CreateContext(checks);
        context = context with { Invocation = context.Invocation with { IsChildAgent = child } };
        if (child)
        {
            await ConversationReceiptChecks.ObserveAsync(context, "dotnet_build", "a", "b", _ => Task.FromResult(Result(true)), TestContext.Current.CancellationToken);
        }
        else
        {
            await ConversationReceiptChecks.ObserveAsync(context, "dotnet_build", "a", "b", _ => Task.FromResult("tests passed"), TestContext.Current.CancellationToken);
        }

        Assert.False(checks.HasObservations);
    }

    /// <summary>Identically shaped validation requests retain operation identity for every interruption.</summary>
    [Theory]
    [InlineData("dotnet_build", "TimedOut")]
    [InlineData("dotnet_analyzers", "TimedOut")]
    [InlineData("dotnet_build", "Cancelled")]
    [InlineData("dotnet_analyzers", "Cancelled")]
    [InlineData("dotnet_build", "Incomplete")]
    [InlineData("dotnet_analyzers", "Incomplete")]
    public async Task InterruptedChecksRetainOperationAndPipelineCancellation(string toolId, string result)
    {
        var checks = new ConversationReceiptChecks();
        var context = CreateContext(checks) with { CallerCancellation = new CancellationToken(canceled: result == "Cancelled") };
        Exception failure = result == "Incomplete" ? new IOException("infrastructure failure") : new OperationCanceledException();
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => ConversationReceiptChecks.ObserveAsync<ValidationToolResult>(context, toolId, "a.csproj", "{}", _ => Task.FromException<ValidationToolResult>(failure), TestContext.Current.CancellationToken));
        Assert.Same(failure, thrown);
        var observation = Assert.Single(checks.Snapshot(null).Checks);
        Assert.Equal(toolId + ":" + result, observation.Result);
        Assert.Equal(result == "TimedOut", observation.TimedOut);
    }

    private static ToolExecutionContext CreateContext(ConversationReceiptChecks checks)
    {
        return new(ToolInvocationId.New(), SessionId.New(), RunId.New(), new ToolInvocationContext { RepositoryPath = "/repo", RequestedBy = "model", ReceiptChecks = checks });
    }

    private static ValidationToolResult Result(bool succeeded)
    {
        return new("typed-id", ValidationInvocationKind.Build, ValidationAuthority.Exploratory, succeeded, "a.csproj", ["build", "a.csproj"], [], string.Empty, succeeded ? 0 : 1, TimeSpan.Zero, false, false);
    }
}
