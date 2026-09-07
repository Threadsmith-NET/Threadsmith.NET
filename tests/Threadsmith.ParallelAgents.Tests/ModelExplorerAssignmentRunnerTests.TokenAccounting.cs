namespace Threadsmith.ParallelAgents.Tests;

using System.Runtime.CompilerServices;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Reported output respects the profile maximum, not its smaller request reserve or character estimates.</summary>
    [Theory]
    [InlineData(10, false, true)]
    [InlineData(1_025, false, true)]
    [InlineData(4_097, false, false)]
    [InlineData(4_097, true, true)]
    public async Task RunAsync_ReportedTokenUsage_RespectsProfileMaximumNotReserve(int reportedTokens, bool isEstimate, bool succeeds)
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        const string response = "The implementation checks cancellation before each iteration.";
        var registry = new ToolRegistry([]);
        var provider = new ReportedTokenProvider(response, reportedTokens, isEstimate);
        var usage = new SessionUsageProjection();
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            [],
            usage);

        // Act / Assert
        if (succeeds)
        {
            var result = await runner.RunAsync(plan, assignment);
            Assert.Equal(AgentRunStatus.Completed, result.Status);
            Assert.Equal(response, result.Response);
            Assert.Null(result.Findings);
            Assert.Equal(0, result.Usage.Corrections);
            Assert.True(result.Usage.ModelTokens > reportedTokens);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(plan, assignment));
            Assert.Contains("output token bound", exception.Message, StringComparison.Ordinal);
            Assert.True(ChildAgentFailureDetails.TryGet(exception, out var failure));
            Assert.NotNull(failure);
            Assert.Equal(profile.Id, failure.ModelProfileId);
        }

        Assert.Equal(1, provider.RequestCount);
        var snapshot = usage.GetSnapshot(plan.Provenance.SessionId);
        Assert.Equal(reportedTokens, snapshot.OutputTokens);
        Assert.Equal(isEstimate, snapshot.IsEstimate);
    }

    private sealed class ReportedTokenProvider : IModelProvider
    {
        private readonly string _response;
        private readonly int _reportedTokens;
        private readonly bool _isEstimate;

        public ReportedTokenProvider(string response, int reportedTokens, bool isEstimate)
        {
            _response = response;
            _reportedTokens = reportedTokens;
            _isEstimate = isEstimate;
        }

        public int RequestCount { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            await Task.Yield();
            yield return new ModelChunk
            {
                Reasoning = new string('a', 5_000),
                Output = new TextModelOutput(_response),
                Usage = new ModelUsage(20, _reportedTokens, IsEstimate: _isEstimate),
            };
        }
    }
}
