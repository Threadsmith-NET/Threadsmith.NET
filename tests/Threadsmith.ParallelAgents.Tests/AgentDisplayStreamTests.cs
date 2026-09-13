namespace Threadsmith.ParallelAgents.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Regression checks for agent display stream tests.</summary>
public static class AgentDisplayStreamTests
{
    /// <summary>Verifies chunk split credentials are sanitized before public text and oversize lines are omitted.</summary>
    [Fact]
    public static void ChunkSplitCredentialsAreSanitizedBeforePublicTextAndOversizeLinesAreOmitted()
    {
        var stream = new AgentDisplayStream();
        stream.Attach();
        var writer = new AgentDisplayTextWriter(stream, new TestSanitizer(), SessionId.New(), RunId.New(), false);
        writer.Append("safe secret-");
        Assert.Empty(stream.Drain(out _));
        writer.Append("canary\n");
        writer.Flush(true);
        var output = string.Concat(stream.Drain(out _).Select(item => item.Text));
        Assert.DoesNotContain("secret-canary", output, StringComparison.Ordinal);
        Assert.Contains("[redacted]", output, StringComparison.Ordinal);
        writer.Append(new string('x', 16385));
        writer.Flush(true);
        Assert.Contains("Oversized sensitive display suffix omitted", string.Concat(stream.Drain(out _).Select(item => item.Text)), StringComparison.Ordinal);
    }

    /// <summary>Verifies display overflow never blocks and reports loss without retaining headless text.</summary>
    [Fact]
    public static void DisplayOverflowNeverBlocksAndReportsLossWithoutRetainingHeadlessText()
    {
        var stream = new AgentDisplayStream();
        var item = new AgentDisplayText(SessionId.New(), RunId.New(), "text", false);
        stream.Publish(item);
        Assert.Empty(stream.Drain(out _));
        stream.Attach();
        for (var index = 0; index < 257; index++)
        {
            stream.Publish(item);
        }

        Assert.Equal(256, stream.Drain(out var omitted).Count);
        Assert.Equal(1, omitted);
        stream.Detach();
        Assert.Empty(stream.Drain(out _));
    }

    /// <summary>Verifies hidden reasoning never enters display queue.</summary>
    [Fact]
    public static void HiddenReasoningNeverEntersDisplayQueue()
    {
        var stream = new AgentDisplayStream();
        stream.Attach();
        var writer = new AgentDisplayTextWriter(stream, new TestSanitizer(), SessionId.New(), RunId.New(), true);
        writer.Append("private signed canary\n");
        writer.Flush(false);
        Assert.Empty(stream.Drain(out _));
    }

    /// <summary>Production credential grammar stays intact across every chunk boundary and multiline values.</summary>
    [Theory]
    [InlineData("password:\nsecret-canary\n")]
    [InlineData("password: 'first-line\nsecret-canary")]
    [InlineData("MY_API_KEY=\"first-line\nsecret-canary")]
    [InlineData("\"my-api-key\": \"first-line\nsecret-canary")]
    [InlineData("PASSWORD\n:\n'secret-canary\nsecond-line'\n")]
    [InlineData("Server=localhost;Pwd=secret-canary\ncontinued;Database=test\n")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nsecret-canary\n-----END RSA PRIVATE KEY-----\n")]
    public static void ProductionSanitizerNeverLeaksSplitSensitiveUnits(string content)
    {
        for (var split = 0; split <= content.Length; split++)
        {
            var stream = new AgentDisplayStream();
            stream.Attach();
            var writer = new AgentDisplayTextWriter(stream, new Threadsmith.Telemetry.SecretOutputSanitizer(), SessionId.New(), RunId.New(), false);
            writer.Append(content[..split]);
            var early = string.Concat(stream.Drain(out _).Select(item => item.Text));
            writer.Append(content[split..]);
            writer.Flush(true);
            var output = early + string.Concat(stream.Drain(out _).Select(item => item.Text));
            Assert.DoesNotContain("secret-canary", output, StringComparison.Ordinal);
            Assert.Contains("REDACTED", output, StringComparison.Ordinal);
        }
    }

    /// <summary>Once a sensitive suffix exceeds its bound, no subsequent line can escape the omission state.</summary>
    [Fact]
    public static void OversizedSensitiveSuffixRemainsOmittedThroughResponseEnd()
    {
        var stream = new AgentDisplayStream();
        stream.Attach();
        var writer = new AgentDisplayTextWriter(stream, new Threadsmith.Telemetry.SecretOutputSanitizer(), SessionId.New(), RunId.New(), false);
        writer.Append("ordinary safe line\n");
        Assert.Contains("ordinary safe", string.Concat(stream.Drain(out _).Select(item => item.Text)), StringComparison.Ordinal);
        writer.Append("password: '" + new string('x', 16385) + "\nsecret-canary\n'\n");
        writer.Flush(true);
        var text = string.Concat(stream.Drain(out _).Select(item => item.Text));
        Assert.DoesNotContain("secret-canary", text, StringComparison.Ordinal);
        Assert.Contains("omitted", text, StringComparison.Ordinal);
    }

    /// <summary>Visibility changes never discard sanitizer context or reveal an already hidden response.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void ReasoningVisibilityIsFrozenForEachResponse(bool initiallyVisible)
    {
        var stream = new AgentDisplayStream { IncludeReasoningText = initiallyVisible };
        stream.Attach();
        var sanitizer = new Threadsmith.Telemetry.SecretOutputSanitizer();
        var writer = new AgentDisplayTextWriter(stream, sanitizer, SessionId.New(), RunId.New(), true);
        writer.Append("password:\n");
        stream.IncludeReasoningText = !initiallyVisible;
        writer.Append("secret-canary\n");
        stream.IncludeReasoningText = true;
        writer.Flush(true);
        var output = stream.Drain(out _);
        Assert.DoesNotContain("secret-canary", string.Concat(output.Select(item => item.Text)), StringComparison.Ordinal);
        if (!initiallyVisible)
        {
            Assert.Empty(output);
        }
        else
        {
            Assert.Contains("REDACTED", string.Concat(output.Select(item => item.Text)), StringComparison.Ordinal);
        }

        var next = new AgentDisplayTextWriter(stream, sanitizer, SessionId.New(), RunId.New(), true);
        next.Append("safe public reasoning\n");
        next.Flush(true);
        Assert.Contains("safe public reasoning", string.Concat(stream.Drain(out _).Select(item => item.Text)), StringComparison.Ordinal);
    }

    /// <summary>The smallest supported fragment retains complete surrogate pairs and flush always advances.</summary>
    [Fact]
    public static void FragmentLimitPreservesUnicodeScalarsAndRejectsOneCodeUnit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentDisplayStream(
            new ExecutionLimits { MaxAgentDisplayFragmentCharacters = 1 }));
        var stream = new AgentDisplayStream(new ExecutionLimits { MaxAgentDisplayFragmentCharacters = 2 });
        stream.Attach();
        var writer = new AgentDisplayTextWriter(stream, new TestSanitizer(), SessionId.New(), RunId.New(), false);
        writer.Append("a😀b😀c");
        writer.Flush(true);
        var fragments = stream.Drain(out var omitted);
        Assert.Equal(0, omitted);
        Assert.Equal("a😀b😀c", string.Concat(fragments.Select(item => item.Text)));
        Assert.All(fragments, item => Assert.InRange(item.Text.Length, 0, 2));
    }

    private sealed class TestSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value.Replace("secret-canary", "[redacted]", StringComparison.Ordinal);
    }
}
