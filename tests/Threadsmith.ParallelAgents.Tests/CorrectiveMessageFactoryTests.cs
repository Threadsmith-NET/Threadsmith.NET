namespace Threadsmith.ParallelAgents.Tests;

using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Checks mutation recovery evidence before it enters a model prompt.</summary>
public sealed class CorrectiveMessageFactoryTests
{
    /// <summary>Quoted credentials are redacted before JSON escaping and character hints are withheld.</summary>
    [Fact]
    public void CreateMutationProposalDeveloperMessage_RedactsSourceBeforeSerialization()
    {
        // Arrange
        const string secret = "fixture-password-value";
        var baseline = "{\"password\":\"" + secret + "\"}";
        var factory = new CorrectiveMessageFactory(TestPromptLoader.Instance, new SecretOutputSanitizer());
        var diagnostic = new MalformedInvocationDiagnostic
        {
            Kind = MalformedInvocationFailureKind.ArgumentSchemaMismatch,
            SafeMessage = "Expected text was not found.",
        };
        var evidence = new ReplaceTextMismatchCorrectionEvidence
        {
            Path = "src/config.json",
            RejectedExpectedText = baseline,
            ClosestUniqueBaselineText = baseline,
            BaselineLine = 3,
            FirstDifference = new ReplaceTextFirstDifference(12, "v", "a", "U+0076", "U+0061"),
            ProposalFingerprint = "fixture",
        };

        // Act
        var message = factory.CreateMutationProposalDeveloperMessage(diagnostic, 1, 3, evidence);
        var content = message.GetModelVisibleContent();

        // Assert
        Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
        Assert.Contains("\"firstDifference\":null", content, StringComparison.Ordinal);
        Assert.DoesNotContain("U+0076", content, StringComparison.Ordinal);
    }
}
