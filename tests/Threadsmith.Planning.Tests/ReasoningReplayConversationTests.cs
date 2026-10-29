namespace Threadsmith.Planning.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

/// <summary>Actual provider serialization through the parent conversation's existing replay lifecycle.</summary>
public static class ReasoningReplayConversationTests
{
    /// <summary>Exercises signed replay through the actual parent loop.</summary>
    [Fact]
    public static async Task OpenRouter_TwoSignedToolRoundsUseMainLoopAndRemainPrivate()
    {
        static string RouterStream(int round) => ReasoningReplayFixture.Stream(false, round, "deterministic_output", "{\"sequence\":" + (round + 1) + "}")
            .Replace("\"reasoning_content\":\"private-" + round + "-\"", "\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"index\":0,\"data\":\"opaque-" + round + "-\"}]", StringComparison.Ordinal)
            .Replace("\"reasoning_content\":\"state\"", "\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"index\":0,\"data\":\"state\"}]", StringComparison.Ordinal);
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(false, openRouter: true), RouterStream(0), RouterStream(1), ReasoningReplayFixture.Final(false));
        harness.Handler.FragmentResponses = true;
        await harness.RunToCompletionAsync();
        Assert.Equal(3, harness.Handler.Requests.Count);
        var messages = harness.Handler.Requests[2].GetProperty("messages").EnumerateArray().Where(message => message.TryGetProperty("reasoning_details", out _)).ToArray();
        Assert.Equal(2, messages.Length);
        for (var round = 0; round < 2; round++)
        {
            Assert.Equal($"opaque-{round}-state", messages[round].GetProperty("reasoning_details")[0].GetProperty("data").GetString());
            Assert.Equal($"wire-{round}", messages[round].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        }

        Assert.Equal(2, harness.Observed.OfType<ToolInvocationCompleted>().Count());
        Assert.DoesNotContain("opaque-", JsonSerializer.Serialize(harness.Observed), StringComparison.Ordinal);
        Assert.All(harness.Model.Requests, request => Assert.False(request.TransientState?.HasResponses));
    }

    /// <summary>Chronological developer corrections preserve earlier reasoning and can precede a new retained round.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public static async Task CorrectionAfterRetainedToolRound_PreservesReplay(bool codex, bool malformed)
    {
        var invalid = malformed
            ? ReasoningReplayFixture.Stream(codex, 1, "deterministic_output", "[", reasoning: false)
            : ReasoningReplayFixture.Final(codex).Replace("Inspection complete.", string.Empty, StringComparison.Ordinal);
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(codex),
            ReasoningReplayFixture.Stream(codex, 0, "deterministic_output", "{\"sequence\":1}"),
            invalid,
            ReasoningReplayFixture.Stream(codex, 1, "deterministic_output", "{\"sequence\":2}"),
            ReasoningReplayFixture.Final(codex));

        await harness.RunToCompletionAsync();

        Assert.Equal(4, harness.Handler.Requests.Count);
        var correction = harness.Model.Requests[2].Messages[^1];
        Assert.Equal(ModelMessageRole.Developer, correction.Role);
        Assert.Contains(correction, harness.Model.Requests[3].Messages);
        ReasoningReplayFixture.AssertReplay(harness.Handler.Requests[2], codex, 1);
        ReasoningReplayFixture.AssertReplay(harness.Handler.Requests[3], codex, 2);
        Assert.Equal(2, harness.Observed.OfType<ToolInvocationCompleted>().Count());
    }

    /// <summary>Returned reasoning alone cannot opt an endpoint into replay.</summary>
    [Fact]
    public static async Task OptOut_ReplaysOnlyNormalizedToolHistory()
    {
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(false, replay: false),
            ReasoningReplayFixture.Stream(false, 0, "deterministic_output", "{\"sequence\":1}"),
            ReasoningReplayFixture.Final(false));
        await harness.RunToCompletionAsync();
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.False(harness.Model.HadReplay[1]);
        Assert.DoesNotContain("reasoning_content", harness.Handler.Requests[1].GetRawText(), StringComparison.Ordinal);
        Assert.Contains(harness.Handler.Requests[1].GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "tool");
    }

    /// <summary>Canceling the active main loop after a tool releases the accepted response.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Cancellation_ReleasesAcceptedReplay(bool codex)
    {
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(codex),
            ReasoningReplayFixture.Stream(codex, 0, "deterministic_output", "{\"sequence\":1}"));
        await harness.RunToCompletionAsync(cancelAfterTool: true);
        Assert.Single(harness.Handler.Requests);
        Assert.All(harness.Model.Requests, request => Assert.False(request.TransientState?.HasResponses));
    }

    /// <summary>Both protocols preserve successive reasoning and dispose it at turn completion.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    public static async Task TwoToolRounds_PreservePrivateStateAndReleaseAtCompletion(bool codex, bool reasoningAlias = false)
    {
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(codex, reasoningAlias: reasoningAlias),
            ReasoningReplayFixture.Stream(codex, 0, "deterministic_output", "{\"sequence\":1}").Replace("reasoning_content", reasoningAlias ? "reasoning" : "reasoning_content", StringComparison.Ordinal),
            ReasoningReplayFixture.Stream(codex, 1, "deterministic_output", "{\"sequence\":2}").Replace("reasoning_content", reasoningAlias ? "reasoning" : "reasoning_content", StringComparison.Ordinal),
            ReasoningReplayFixture.Final(codex));

        harness.Handler.FragmentResponses = true;
        await harness.RunToCompletionAsync();

        Assert.Equal(3, harness.Handler.Requests.Count);
        ReasoningReplayFixture.AssertReplay(harness.Handler.Requests[1], codex, 1, reasoningAlias);
        ReasoningReplayFixture.AssertReplay(harness.Handler.Requests[2], codex, 2, reasoningAlias);
        Assert.All(harness.Model.Requests.Skip(1), request =>
        {
            var allowance = Assert.Single(request.WireEstimate!.Components, item => item.Id == "retained-allowance");
            Assert.True(allowance.Tokens > 0);
            Assert.Equal(request.WireEstimate.WireInputTokens, request.WireEstimate.Components.Sum(item => item.Tokens));
        });
        Assert.Equal(2, harness.Observed.OfType<ToolInvocationCompleted>().Count());
        Assert.All(harness.Model.Requests, request => Assert.False(request.TransientState?.HasResponses));
        var visible = JsonSerializer.Serialize(harness.Observed.Where(item => item is not ModelReasoningObserved));
        Assert.DoesNotContain("opaque-", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("private-", visible, StringComparison.Ordinal);
    }

    /// <summary>Requesting reasoning does not require the endpoint to return it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task MissingReasoning_ContinuesNormally(bool codex)
    {
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(codex),
            ReasoningReplayFixture.Stream(codex, 0, "deterministic_output", "{\"sequence\":1}", reasoning: false),
            ReasoningReplayFixture.Final(codex));
        await harness.RunToCompletionAsync();
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.False(harness.Model.HadReplay[1]);
        Assert.DoesNotContain("reasoning_content", harness.Handler.Requests[1].GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted_content\":", harness.Handler.Requests[1].GetRawText(), StringComparison.Ordinal);
    }
}
