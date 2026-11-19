namespace Threadsmith.RepositoryLifecycle.Tests;

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.RepositoryIntelligence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Collection checkpoints retain commit work arriving during preparation.</summary>
public static class RepositoryIntelligenceOperationsTests
{
    private sealed class CheckpointPrompts : IPromptLoader, IDisposable
    {
        private readonly CancellationToken _cancellationToken;

        public CheckpointPrompts(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new();

        public string Get(string promptFileName)
        {
            Entered.TrySetResult();
            Release.Wait(_cancellationToken);
            return string.Empty;
        }

        public string Render(string promptFileName, IReadOnlyDictionary<string, string> tokens) => throw new NotSupportedException();

        public void Dispose() => Release.Dispose();
    }
}
