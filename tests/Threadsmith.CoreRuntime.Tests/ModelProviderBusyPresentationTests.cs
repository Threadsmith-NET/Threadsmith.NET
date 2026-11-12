namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Ensures retry status uses the existing failure presentation without becoming answer text.</summary>
public static class ModelProviderBusyPresentationTests
{
    /// <summary>Readable metadata keeps the existing bracketed classification and diagnostic role.</summary>
    [Fact]
    public static void ProviderMetadataUsesExistingDiagnosticLabelAndRole()
    {
        var message = "Capacity unavailable" + Environment.NewLine + "HTTP 503 · Code: capacity · Request ID: req-1";
        var segments = new List<PresentationTextSegment>();

        InteractionEventSegments.Append(segments, new DiagnosticObserved(SessionId.New(), DateTimeOffset.UtcNow, "TransientProvider", message), string.Empty);

        var segment = Assert.Single(segments);
        Assert.Equal("[TransientProvider] " + message + Environment.NewLine, segment.Text);
        Assert.Equal(PresentationTextRole.Warning, segment.Role);
    }

    /// <summary>Busy text has the failure role and a newline on both sides.</summary>
    [Fact]
    public static void BusyNoticeUsesFailureRoleAndSurroundingLineBreaks()
    {
        const string message = "Model provider is currently busy ... please wait";
        var session = SessionId.New();
        var busy = new List<PresentationTextSegment>();
        var failure = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(busy, new DiagnosticObserved(session, DateTimeOffset.UtcNow, "ModelProviderBusy", message), string.Empty);
        InteractionEventSegments.Append(failure, new DiagnosticObserved(session, DateTimeOffset.UtcNow, "Permanent", "HTTP 429"), string.Empty);

        Assert.Equal(Environment.NewLine + message + Environment.NewLine, string.Concat(busy.Select(segment => segment.Text)));
        var role = Assert.Single(failure).Role;
        Assert.Equal(PresentationTextRole.Warning, role);
        Assert.All(busy, segment => Assert.Equal(role, segment.Role));
    }
}
