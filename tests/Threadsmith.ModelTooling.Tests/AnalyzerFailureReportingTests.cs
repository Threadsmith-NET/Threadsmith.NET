namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.DotNet;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Verifies safe, actionable analyzer failure explanations without running a workspace.</summary>
public static class AnalyzerFailureReportingTests
{
    /// <summary>Failure detail retains the useful cause while redacting secrets and bounding output.</summary>
    [Fact]
    public static void AnalyzerFailureReasonIsSanitizedBoundedAndUnwrapped()
    {
        var exception = new AggregateException(
            "Generic analyzer wrapper",
            new InvalidOperationException("Analyzer crashed.\r\npassword=private-value; " + new string('x', 600)));

        var reason = SemanticEngine.CreateAnalyzerFailureReason(exception, new SecretOutputSanitizer(), 200);

        Assert.StartsWith("InvalidOperationException: Analyzer crashed.", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Generic analyzer wrapper", reason, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', reason);
        Assert.DoesNotContain('\n', reason);
        Assert.EndsWith("…", reason, StringComparison.Ordinal);
        Assert.Equal(200, reason.Length);
    }
}
