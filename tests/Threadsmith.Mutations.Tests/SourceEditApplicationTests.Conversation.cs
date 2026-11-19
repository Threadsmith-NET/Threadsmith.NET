namespace Threadsmith.Mutations.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises the ordinary provider loop with real tools, source transactions, and semantic refresh.</summary>
public sealed partial class SourceEditApplicationTests
{
    private sealed class CompletingAnalyzer : ISourceEditAnalyzer
    {
        private readonly ISourceEditAnalyzer _inner;
        private readonly IDomainEventStream _events;
        private ApplySourceEditCommand? _latestCommand;
        private SourceEditAnalysis? _latestAnalysis;

        public CompletingAnalyzer(ISourceEditAnalyzer inner, IDomainEventStream events)
        {
            _inner = inner;
            _events = events;
        }

        public SourceEditAnalysis? LateFeedback { get; private set; }

        public bool HasSemanticInputs(WorkspaceId workspaceId, string repositoryPath, MutationEffectSnapshot snapshot)
        {
            return _inner.HasSemanticInputs(workspaceId, repositoryPath, snapshot);
        }

        public async Task<SourceEditAnalysis?> AnalyzeCandidateAsync(ApplySourceEditCommand command, string repositoryPath, MutationEffectSnapshot snapshot, TimeSpan immediateAllowance, CancellationToken cancellationToken = default)
        {
            _latestCommand = command;
            _latestAnalysis = null;
            LateFeedback = null;
            var completed = new TaskCompletionSource<SourceEditAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = _events.Subscribe((item, _) =>
            {
                if (item is SemanticCheckCompleted { Phase: SemanticCheckPhase.PreMutation } check
                    && check.SessionId == command.SessionId && check.RunId == command.RunId && check.Analysis is { } analysis && analysis.EffectId == command.EffectId
                    && check.CheckName == "advisory edit analysis")
                {
                    completed.TrySetResult(analysis);
                }

                return Task.CompletedTask;
            });
            var admitted = await _inner.AnalyzeCandidateAsync(command, repositoryPath, snapshot, TimeSpan.Zero, cancellationToken);
            if (admitted is not null)
            {
                _latestAnalysis = await completed.Task.WaitAsync(cancellationToken);
            }

            return admitted;
        }

        public void PublishLateFeedback()
        {
            var command = _latestCommand ?? throw new InvalidOperationException("No candidate was analyzed.");
            var preview = _latestAnalysis ?? throw new InvalidOperationException("No candidate analysis completed.");
            var latest = _inner.GetLatestAnalysis(command.SessionId, command.RunId, command.WorkspaceId, command.EffectId);

            // This fixture owns the injected result's readiness and publication generation.
            // Real committed compiler coverage is exercised by GraphChangingSourceContinuesDiagnosticsAfterGraphRefresh.
            LateFeedback = preview with
            {
                Revision = Math.Max(preview.Revision, latest?.Revision ?? 0) + 100,
                CommittedGeneration = latest?.CommittedGeneration ?? preview.SourceGeneration,
                Pending = false,
                Obsolete = false,
                CurrentErrors = 1,
                NewErrors = 0,
                Diagnostics = [new("CS5001", "Missing entry point", null, null, "Example.Tests", "net10.0", "initial")],
            };
        }

        public SourceEditAnalysis? GetLatestAnalysis(SessionId sessionId, RunId runId, WorkspaceId workspaceId, Guid effectId)
        {
            return _latestCommand is { } command && command.SessionId == sessionId && command.RunId == runId
                && command.WorkspaceId == workspaceId && LateFeedback is { } feedback && feedback.EffectId == effectId
                ? feedback
                : _inner.GetLatestAnalysis(sessionId, runId, workspaceId, effectId);
        }

        public void ConfirmApplied(WorkspaceId workspaceId, Guid effectId)
        {
            _inner.ConfirmApplied(workspaceId, effectId);
        }

        public void DiscardCandidate(WorkspaceId workspaceId, Guid effectId)
        {
            _inner.DiscardCandidate(workspaceId, effectId);
        }
    }
}
