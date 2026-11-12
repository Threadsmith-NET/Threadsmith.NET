namespace Threadsmith.Policies.Tests;

using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Grants;
using Xunit;

/// <summary>Checks exact correlated continuation and final one-use admission.</summary>
public static class PolicyGrantAuthorityTests
{
    /// <summary>Only one matching refresh can preserve an Always continuation.</summary>
    [Theory]
    [InlineData("own", true)]
    [InlineData("none", false)]
    [InlineData("other", false)]
    [InlineData("twice", false)]
    [InlineData("reset-before", false)]
    [InlineData("reset-after", false)]
    public static void PersistentContinuationCannotAdoptUnrelatedEpoch(string transition, bool expected)
    {
        var grants = new PolicyGrantAuthority();
        var request = Request(grants);
        var response = new PolicyApprovalResponse(request.RequestId, PolicyApprovalChoice.Always);
        var operation = grants.BeginPersistent(request, response, "rules");
        Assert.NotNull(operation);
        Assert.Null(grants.BeginPersistent(request, response, "rules"));
        Assert.False(grants.Consume(request.RequestId, request.ContextFingerprint, request.SessionEpoch));

        if (transition == "reset-before")
        {
            grants.Reset();
        }

        if (transition != "none")
        {
            grants.Reset(transition == "other" ? Guid.NewGuid() : operation.Value);
        }

        if (transition is "twice" or "reset-after")
        {
            grants.Reset(transition == "twice" ? operation.Value : null);
        }

        var completed = grants.CompletePersistent(operation.Value, request, "rules", out var epoch);

        Assert.Equal(expected, completed);
        Assert.False(grants.CompletePersistent(operation.Value, request, "rules", out _));
        Assert.Equal(expected, grants.Consume(request.RequestId, request.ContextFingerprint, epoch));
        Assert.False(grants.Consume(request.RequestId, request.ContextFingerprint, epoch));
    }

    /// <summary>Reset rejects all old responses before any persistent reservation.</summary>
    [Fact]
    public static void ResetCannotBeUndoneByOldOrUnpublishedResponse()
    {
        var grants = new PolicyGrantAuthority();
        var request = Request(grants);
        var response = new PolicyApprovalResponse(request.RequestId, PolicyApprovalChoice.Always);
        Assert.False(grants.Approve(request, response, "rules"));

        grants.Reset();

        Assert.Null(grants.BeginPersistent(request, response, "rules"));
        Assert.False(grants.Approve(request, response with { Choice = PolicyApprovalChoice.Once }, "rules"));
        Assert.False(grants.Approve(request, response with { Choice = PolicyApprovalChoice.Session }, "rules"));
    }

    private static PolicyApprovalRequest Request(PolicyGrantAuthority grants) => new(
        Guid.NewGuid(),
        "fixture",
        "fixture",
        "context",
        "policy",
        grants.Epoch,
        [],
        [PolicyApprovalChoice.Once, PolicyApprovalChoice.Session, PolicyApprovalChoice.Always, PolicyApprovalChoice.Deny]);
}
