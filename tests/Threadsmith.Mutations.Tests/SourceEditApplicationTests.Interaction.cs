namespace Threadsmith.Mutations.Tests;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SourceEditApplicationTests
{
    private sealed class MutationReviewSurface : IInteractionSurface
    {
        private readonly string _decision;
        private MutationRunInput? _input;

        internal MutationReviewSurface(string decision) => _decision = decision;

        public InteractionSurfaceCapabilities Capabilities { get; } = new(SupportsRetainedActivity: true);

        public List<InteractionSelectionRequest> Selections { get; } = [];

        public List<ComposerRequest> ComposerRequests { get; } = [];

        public Task<InteractionInput> ReadComposerAsync(ComposerRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComposerRequests.Add(request);
            return Task.FromResult(new InteractionInput(true, ComposerRequests.Count == 1 ? "Rename the type and add a field." : "/quit", cancellationToken));
        }

        public Task<InteractionSelectionResult> SelectAsync(InteractionSelectionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(_input);
            Assert.True(_input.IsDisposed);
            Selections.Add(request);
            return Task.FromResult(_decision == "escape"
                ? new InteractionSelectionResult(null, IsCancelled: true)
                : new InteractionSelectionResult(request.Options[_decision is "approve" or "precheck" ? 0 : 1].Id));
        }

        public IActiveRunInputLease BeginActiveRunInput(TimeProvider timeProvider)
        {
            _input = new MutationRunInput();
            return _input;
        }

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default) => operation.WaitAsync(cancellationToken);
    }

    private sealed class MutationRunInput : IActiveRunInputLease
    {
        private readonly TaskCompletionSource<ActiveRunInputSignal> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }

        public Task<ActiveRunInputSignal> ReadAsync(CancellationToken cancellationToken = default) => _pending.Task.WaitAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            _pending.TrySetCanceled();
            return ValueTask.CompletedTask;
        }
    }
}
