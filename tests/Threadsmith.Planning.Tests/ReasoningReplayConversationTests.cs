namespace Threadsmith.Planning.Tests;

using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

/// <summary>Actual provider serialization through the parent conversation's existing replay lifecycle.</summary>
public static class ReasoningReplayConversationTests
{
    /// <summary>Pressure compacts completed history while the newest signed exchange retains its exact wire identity.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public static async Task CompactionWithReplay_PreservesNewestResponseAndExecutesToolsOnce(bool codex, bool openRouter, bool repeat = false)
    {
        var responses = new List<string>
        {
            openRouter ? RouterStream(0) : ReasoningReplayFixture.Stream(codex, 0, "deterministic_output", "{\"sequence\":1}"),
            openRouter ? RouterStream(1) : ReasoningReplayFixture.Stream(codex, 1, "deterministic_output", "{\"sequence\":2}"),
        };
        if (repeat)
        {
            responses.Add(ReasoningReplayFixture.Stream(codex, 2, "deterministic_output", "{\"sequence\":3}"));
        }

        responses.Add(ReasoningReplayFixture.Final(codex, openRouter));
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(codex, openRouter: openRouter), [.. responses]);
        harness.ToolEvidence = new string('e', 4096);
        harness.CompactionPolicy = new ActiveTurnCompactionPolicy { PressureTargetPercent = 1, RetainedRecentTokens = 1 };
        harness.Compactor = new ReplayCompactor();
        await harness.RunToCompletionAsync();

        Assert.Equal(repeat ? 4 : 3, harness.Handler.Requests.Count);
        Assert.Equal(repeat ? 3 : 2, harness.Observed.OfType<ToolInvocationCompleted>().Count());
        Assert.Equal(1, harness.Model.Requests[2].HistoryRewriteGeneration);
        Assert.Contains(harness.Model.Requests[2].Messages, message => message.SectionId == "active-turn-summary");
        var body = harness.Handler.Requests[2].GetRawText();
        Assert.DoesNotContain(codex || openRouter ? "opaque-0-state" : "private-0-state", body, StringComparison.Ordinal);
        Assert.Contains(codex || openRouter ? "opaque-1-state" : "private-1-state", body, StringComparison.Ordinal);
        Assert.Contains("wire-1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("wire-0", body, StringComparison.Ordinal);
        Assert.Contains(harness.Observed.OfType<ActiveTurnCompactionCompleted>(), item => item.Status == ActiveTurnCompactionInspectionStatus.Completed);
        if (repeat)
        {
            Assert.Equal(2, harness.Model.Requests[3].HistoryRewriteGeneration);
            var finalBody = harness.Handler.Requests[3].GetRawText();
            Assert.DoesNotContain(codex ? "opaque-1-state" : "private-1-state", finalBody, StringComparison.Ordinal);
            Assert.Contains(codex ? "opaque-2-state" : "private-2-state", finalBody, StringComparison.Ordinal);
        }
    }

    /// <summary>Exercises signed replay through the actual parent loop.</summary>
    [Fact]
    public static async Task OpenRouter_TwoSignedToolRoundsUseMainLoopAndRemainPrivate()
    {
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(false, openRouter: true), RouterStream(0), RouterStream(1), ReasoningReplayFixture.Final(false, openRouter: true));
        harness.Handler.FragmentResponses = true;
        await harness.RunToCompletionAsync();
        Assert.Equal(3, harness.Handler.Requests.Count);
        ReasoningReplayFixture.AssertReplay(harness.Handler.Requests[1], false, 1, openRouter: true);
        ReasoningReplayFixture.AssertReplay(harness.Handler.Requests[2], false, 2, openRouter: true);

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
    [InlineData(false, true)]
    public static async Task Cancellation_ReleasesAcceptedReplay(bool codex, bool openRouter = false)
    {
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(codex, openRouter: openRouter),
            ReasoningReplayFixture.Stream(codex, 0, "deterministic_output", "{\"sequence\":1}", openRouter: openRouter));
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

    /// <summary>Rejected batches recover in MAIN without executing siblings or repeating previous work.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task OpenRouter_RejectedBatchCorrectsWithoutRepeatingSuccessfulWork(bool orphanedArguments)
    {
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(false, openRouter: true),
            RouterStream(0),
            orphanedArguments ? ReasoningReplayFixture.Stream(false, 99, "deterministic_output", "{\"sequence\":999}", openRouter: true, orphanedArguments: true) : ExhaustedRouterBatch(),
            RouterStream(1),
            RouterStream(2),
            ReasoningReplayFixture.Final(false, openRouter: true));
        harness.Limits = new() { MaxModelRounds = 6, MaxCorrectiveTurns = 2 };

        await harness.RunToCompletionAsync();

        Assert.Equal(5, harness.Handler.Requests.Count);
        int[] expectedSequences = [1, 2, 3];
        Assert.Equal(expectedSequences, harness.ExecutedSequences);
        Assert.Equal(3, harness.Observed.OfType<ToolInvocationCompleted>().Count());
        var correctionEvent = Assert.Single(harness.Observed.OfType<ModelCorrectionAttempted>());
        Assert.Equal(ModelCorrectionCategory.ProviderInvocation, correctionEvent.Category);
        Assert.Equal(1, correctionEvent.AttemptNumber);
        Assert.Equal(2, correctionEvent.MaximumAttempts);
        if (orphanedArguments)
        {
            Assert.Contains("tool parameters as assistant text", correctionEvent.SafeReason, StringComparison.Ordinal);
            Assert.DoesNotContain("999", correctionEvent.SafeReason, StringComparison.Ordinal);
            Assert.All(harness.Handler.Requests.Skip(2), request => Assert.DoesNotContain("opaque-99", request.GetRawText(), StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(ModelOutputValidator.TruncatedInvocationMessage, correctionEvent.SafeReason);
        }

        var correction = harness.Model.Requests[2].Messages[^1];
        Assert.Equal(ModelMessageRole.Developer, correction.Role);
        Assert.Contains(correction, harness.Model.Requests[4].Messages);
        ReasoningReplayFixture.AssertReplay(harness.Handler.Requests[2], false, 1, openRouter: true);
        ReasoningReplayFixture.AssertReplay(harness.Handler.Requests[4], false, 3, openRouter: true);
        Assert.All(harness.Handler.Requests.Skip(2), request => Assert.DoesNotContain("rejected-", request.GetRawText(), StringComparison.Ordinal));
        Assert.DoesNotContain("opaque-", JsonSerializer.Serialize(harness.Observed), StringComparison.Ordinal);
        Assert.All(harness.Model.Requests, request => Assert.False(request.TransientState?.HasResponses));
    }

    /// <summary>Repeated malformed batches use the existing bounded correction policy.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task OpenRouter_RejectedBatchStopsAtCorrectionLimitWithoutExecutingTools(bool orphanedArguments)
    {
        var rejected = orphanedArguments ? ReasoningReplayFixture.Stream(false, 99, "deterministic_output", "{\"sequence\":999}", openRouter: true, orphanedArguments: true) : ExhaustedRouterBatch();
        await using var harness = new AnthropicConversationLoopTests.NativeLoopHarness(
            ReasoningReplayFixture.Catalog(false, openRouter: true), rejected, rejected, rejected);

        var error = await Assert.ThrowsAsync<MalformedInvocationException>(() => harness.RunToCompletionAsync());
        Assert.Equal(orphanedArguments ? MalformedInvocationFailureKind.ArgumentSchemaMismatch : MalformedInvocationFailureKind.TruncatedArguments, error.Diagnostic.Kind);

        Assert.Equal(3, harness.Handler.Requests.Count);
        Assert.Equal(2, harness.Observed.OfType<ModelCorrectionAttempted>().Count());
        Assert.Empty(harness.ExecutedSequences);
        Assert.Empty(harness.Observed.OfType<ToolInvocationStarted>());
        Assert.All(harness.Model.Requests, request => Assert.False(request.TransientState?.HasResponses));
    }

    private static string ExhaustedRouterBatch() => "data: " + JsonSerializer.Serialize(new
    {
        type = "response.incomplete",
        response = new
        {
            status = "incomplete",
            incomplete_details = new { reason = "max_output_tokens" },
            output = new[]
            {
                new { type = "function_call", call_id = "rejected-valid", name = "deterministic_output", arguments = "{\"sequence\":2}" },
                new { type = "function_call", call_id = "rejected-truncated", name = "deterministic_output", arguments = "{\"sequence\":" },
            },
            usage = new { input_tokens = 100, output_tokens = 4096 },
        },
    }) + "\n\n";

    private static string RouterStream(int round) => ReasoningReplayFixture.Stream(false, round, "deterministic_output", "{\"sequence\":" + (round + 1) + "}", openRouter: true);

    internal sealed class ReplayCompactor : IActiveTurnCompactor
    {
        public Task<ActiveTurnCompactionResult> CompactAsync(ActiveTurnCompactionRequest request, IActiveTurnCompactionAttemptObserver attemptObserver, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ActiveTurnCompactionResult
            {
                Outcome = ActiveTurnCompactionOutcome.Completed,
                Rationale = "Fixture summary.",
                Summary = new ActiveTurnCompactionSummary
                {
                    Version = (request.PriorSummary?.Version ?? 0) + 1,
                    ThroughGroupSequence = request.EligiblePrefix[^1].Sequence,
                    CoveredGroupSequences = [.. request.PriorSummary?.CoveredGroupSequences ?? [], .. request.EligiblePrefix.Select(group => group.Sequence)],
                    Content = "Earlier inspection completed.",
                    ContentHash = "fixture-summary",
                    FilesRead = [],
                    FilesChanged = [],
                },
            });
        }
    }
}
