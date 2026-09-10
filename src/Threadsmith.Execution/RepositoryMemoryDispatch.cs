namespace Threadsmith.Execution;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Records final inclusions at the provider transport boundary without affecting retries.</summary>
internal static class RepositoryMemoryDispatch
{
    /// <summary>Observes transport submission and finishes best-effort bookkeeping before returning.</summary>
    internal static async IAsyncEnumerable<ModelChunk> StreamAsync(
        IModelProvider provider,
        ModelStreamRequest request,
        IManagedRepositoryMemoryService? memories,
        IContextAssembler? contextAssembler,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var submitted = 0;
        var receipt = Task.CompletedTask;
        var observedRequest = request with { SubmissionObserver = RecordSubmission };
        try
        {
            await foreach (var chunk in provider.StreamAsync(observedRequest, cancellationToken))
            {
                // A first response also proves submission for injected providers without transport hooks.
                RecordSubmission();
                yield return chunk;
            }
        }
        finally
        {
            await receipt;
        }

        void RecordSubmission()
        {
            if (Interlocked.Exchange(ref submitted, 1) == 0)
            {
                receipt = RecordAsync();
            }
        }

        async Task RecordAsync()
        {
            if (memories is null || request.MemorySubmission is not { Inclusions.Count: > 0 } submission)
            {
                return;
            }

            var start = Stopwatch.GetTimestamp();
            var outcome = "recorded";
            try
            {
                await memories.RecordInclusionsAsync(submission.RepositoryIdentity, request.RunId, submission.Inclusions, CancellationToken.None);
            }
            catch (Exception exception)
            {
                outcome = "failed";
                logger.LogWarning(exception, "Repository memory inclusion accounting failed for run {RunId}; provider dispatch will not be retried for accounting.", request.RunId.Value);
            }

            if (contextAssembler is not null)
            {
                try
                {
                    await contextAssembler.UpdateRepositoryMemoryDispatchInspectionAsync(
                        submission.SessionId,
                        request.RunId,
                        new RepositoryMemoryDispatchInspection(submission.Inclusions, outcome, Stopwatch.GetElapsedTime(start).TotalMilliseconds),
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Repository memory dispatch diagnostics failed for run {RunId}; provider dispatch will not be retried.", request.RunId.Value);
                }
            }
        }
    }
}
