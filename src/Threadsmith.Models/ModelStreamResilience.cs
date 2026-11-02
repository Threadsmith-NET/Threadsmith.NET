namespace Threadsmith.Models;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Polly;
using Polly.Retry;

/// <summary>Shared resilience pipeline for provider attempts, including failures delivered inside streams.</summary>
public static class ModelStreamResilience
{
    /// <summary>Streams attempts with bounded retries and backpressure, preserving response ownership and cancellation.</summary>
    public static async IAsyncEnumerable<ModelChunk> ExecuteAsync(
        Func<int, CancellationToken, IAsyncEnumerable<ModelChunk>> attemptFactory,
        ModelRetryPolicy policy,
        TimeProvider timeProvider,
        TimeSpan timeout,
        CancellationToken requestCancellationToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attemptFactory);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(timeProvider);
        using var stopped = new CancellationTokenSource();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken, cancellationToken, stopped.Token);
        var channel = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
        });
        var committed = false;
        var reasoning = false;
        var busyReported = false;
        var attempt = 0;
        var builder = new ResiliencePipelineBuilder { TimeProvider = timeProvider };
        if (policy.MaxAttempts > 1)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = policy.MaxAttempts - 1,
                Delay = TimeSpan.FromMilliseconds(Math.Max(250, policy.Delay.TotalMilliseconds)),
                BackoffType = DelayBackoffType.Exponential,
                MaxDelay = TimeSpan.FromMilliseconds(Math.Max(5000, policy.Delay.TotalMilliseconds)),
                ShouldHandle = args => ValueTask.FromResult(!committed && args.Outcome.Exception is TransientModelException),
                DelayGenerator = args =>
                {
                    var failure = args.Outcome.Exception as TransientModelException;
                    if (failure?.RetryAfter is { } retryAfter)
                    {
                        return ValueTask.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(
                            Math.Max(retryAfter.TotalMilliseconds, Math.Max(policy.Delay.TotalMilliseconds, failure.IsRateLimit ? 250 : 0))));
                    }

                    // Null delegates exponential backoff and its ceiling to the resilience strategy.
                    return ValueTask.FromResult<TimeSpan?>(failure is { IsRateLimit: true } ? null : policy.Delay);
                },
                OnRetry = async args =>
                {
                    if (reasoning)
                    {
                        await DeliverAsync(ModelStreamRetry.ReasoningBoundary());
                    }

                    if (!busyReported && args.Outcome.Exception is TransientModelException { IsRateLimit: true })
                    {
                        busyReported = true;
                        await DeliverAsync(new ModelChunk { ProviderBusy = true });
                    }
                },
            });
        }

        var pipeline = builder.Build();
        var producer = ProduceAsync();
        try
        {
            await foreach (var delivery in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return delivery.Chunk;
                delivery.Consumed.TrySetResult();
            }
        }
        finally
        {
            await stopped.CancelAsync();
            await producer;
        }

        async Task DeliverAsync(ModelChunk chunk)
        {
            var delivery = new Delivery(chunk, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await channel.Writer.WriteAsync(delivery, stopped.Token);

            // Do not advance or dispose provider-owned replay state until the consumer has processed it.
            await delivery.Consumed.Task.WaitAsync(stopped.Token);
        }

        async Task ProduceAsync()
        {
            try
            {
                await pipeline.ExecuteAsync(
                    async token =>
                    {
                        committed = false;
                        reasoning = false;
                        await foreach (var chunk in attemptFactory(++attempt, token).WithCancellation(token))
                        {
                            committed |= !chunk.IsInterruptedUsage && !ModelStreamRetry.IsReasoningOnly(chunk);
                            reasoning |= chunk.Reasoning is not null;
                            await DeliverAsync(chunk);
                        }
                    },
                    execution.Token);
                channel.Writer.TryComplete();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                channel.Writer.TryComplete(new OperationCanceledException(cancellationToken));
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !stopped.IsCancellationRequested)
            {
                channel.Writer.TryComplete(new ModelProviderTimeoutException($"The model request exceeded its total deadline ({timeout}).", exception));
            }
            catch (Exception exception)
            {
                // The consumer owns reporting; completion transports the original failure without logging it twice.
                channel.Writer.TryComplete(exception);
            }
        }
    }

    private sealed record Delivery(ModelChunk Chunk, TaskCompletionSource Consumed);
}
