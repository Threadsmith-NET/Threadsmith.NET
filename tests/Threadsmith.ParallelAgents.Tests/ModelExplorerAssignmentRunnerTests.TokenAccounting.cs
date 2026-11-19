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
    private sealed class ReportedTokenProvider : IModelProvider
    {
        private readonly string _response;
        private readonly int _reportedTokens;
        private readonly bool _isEstimate;
        private readonly int? _maximumAttemptOutputTokens;

        public ReportedTokenProvider(string response, int reportedTokens, bool isEstimate, int? maximumAttemptOutputTokens)
        {
            _response = response;
            _reportedTokens = reportedTokens;
            _isEstimate = isEstimate;
            _maximumAttemptOutputTokens = maximumAttemptOutputTokens;
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
                Usage = new ModelUsage(20, _reportedTokens, IsEstimate: _isEstimate) { MaximumAttemptOutputTokens = _maximumAttemptOutputTokens },
            };
        }
    }
}
