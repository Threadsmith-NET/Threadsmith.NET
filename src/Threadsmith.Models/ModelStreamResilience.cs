namespace Threadsmith.Models;

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
        const int waiting = 0;
        const int committed = 1;
        const int busyExpired = 2;
        const int requestExpired = 3;
        var responseState = waiting;
        using var stopped = new CancellationTokenSource();
        using var deadlineCancellation = new CancellationTokenSource();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopped.Token, deadlineCancellation.Token);
        await using var requestDeadline = requestCancellationToken.Register(() =>
        {
            Interlocked.CompareExchange(ref responseState, requestExpired, waiting);
            deadlineCancellation.Cancel();
        });
        await using var busyTimer = timeProvider.CreateTimer(
            _ =>
            {
                if (Interlocked.CompareExchange(ref responseState, busyExpired, waiting) == waiting)
                {
                    deadlineCancellation.Cancel();
                }
            },
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        var channel = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
        });
        var reasoning = false;
        var busyReported = false;
        var attempt = 0;
        var ordinaryFailures = 0;
        long? busyStarted = null;
        TransientModelException? lastBusyFailure = null;
        var builder = new ResiliencePipelineBuilder { TimeProvider = timeProvider };
        if (policy.MaxAttempts > 1 || policy.BusyTimeout > TimeSpan.Zero)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                // Busy failures are time-bounded; other transient failures retain the attempt limit.
                MaxRetryAttempts = int.MaxValue,
                Delay = TimeSpan.FromMilliseconds(Math.Max(250, policy.Delay.TotalMilliseconds)),
                BackoffType = DelayBackoffType.Exponential,
                MaxDelay = TimeSpan.FromMilliseconds(Math.Max(5000, policy.Delay.TotalMilliseconds)),
                ShouldHandle = args =>
                {
                    if (Volatile.Read(ref responseState) != waiting || args.Outcome.Exception is not TransientModelException failure)
                    {
                        return ValueTask.FromResult(false);
                    }

                    if (failure.IsRateLimit || failure.IsOverloaded)
                    {
                        lastBusyFailure = failure;
                        if (busyStarted is null)
                        {
                            busyStarted = timeProvider.GetTimestamp();
                            if (policy.BusyTimeout > TimeSpan.Zero)
                            {
                                busyTimer.Change(policy.BusyTimeout, Timeout.InfiniteTimeSpan);
                            }
                        }

                        return ValueTask.FromResult(RemainingBusyTime() > TimeSpan.Zero);
                    }

                    return ValueTask.FromResult(++ordinaryFailures < policy.MaxAttempts && RemainingBusyTime() > TimeSpan.Zero);
                },
                DelayGenerator = args =>
                {
                    var failure = args.Outcome.Exception as TransientModelException;
                    var busy = failure is { IsRateLimit: true } or { IsOverloaded: true };
                    var delay = failure?.RetryAfter is { } retryAfter
                        ? TimeSpan.FromMilliseconds(Math.Max(retryAfter.TotalMilliseconds, Math.Max(policy.Delay.TotalMilliseconds, busy ? 250 : 0)))
                        : busy
                            ? TimeSpan.FromMilliseconds(Math.Min(
                                Math.Max(5000, policy.Delay.TotalMilliseconds),
                                Math.Max(250, policy.Delay.TotalMilliseconds) * Math.Pow(2, args.AttemptNumber)))
                            : policy.Delay;

                    // Clip the final wait, but do not submit another request before Retry-After or after expiry.
                    return ValueTask.FromResult<TimeSpan?>(delay < RemainingBusyTime() ? delay : RemainingBusyTime());
                },
                OnRetry = async args =>
                {
                    if (reasoning)
                    {
                        await DeliverAsync(ModelStreamRetry.ReasoningBoundary());
                    }

                    if (!busyReported && args.Outcome.Exception is TransientModelException { IsRateLimit: true } or TransientModelException { IsOverloaded: true })
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

        TimeSpan RemainingBusyTime() => busyStarted is { } started
            ? TimeSpan.FromTicks(Math.Max(0, (policy.BusyTimeout - timeProvider.GetElapsedTime(started)).Ticks))
            : TimeSpan.MaxValue;

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
                        token.ThrowIfCancellationRequested();
                        if (lastBusyFailure is { } busyFailure && RemainingBusyTime() == TimeSpan.Zero)
                        {
                            ExceptionDispatchInfo.Capture(busyFailure).Throw();
                        }

                        reasoning = false;
                        await foreach (var chunk in attemptFactory(++attempt, token).WithCancellation(token))
                        {
                            // Providers still report billable partial usage after cancellation.
                            if (!chunk.IsInterruptedUsage)
                            {
                                token.ThrowIfCancellationRequested();
                                var state = Volatile.Read(ref responseState);
                                if (state == waiting && !ModelStreamRetry.IsReasoningOnly(chunk))
                                {
                                    state = Interlocked.CompareExchange(ref responseState, committed, waiting);
                                    if (state == waiting && busyStarted is not null)
                                    {
                                        // Disarm the timer; the state also rejects a callback already queued.
                                        busyTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                                    }
                                }

                                if (state is busyExpired or requestExpired)
                                {
                                    throw new OperationCanceledException(token);
                                }
                            }

                            reasoning |= chunk.Reasoning is not null;
                            await DeliverAsync(chunk);
                        }

                        token.ThrowIfCancellationRequested();
                    },
                    execution.Token);
                channel.Writer.TryComplete();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                channel.Writer.TryComplete(new OperationCanceledException(cancellationToken));
            }
            catch (Exception) when (Volatile.Read(ref responseState) == busyExpired
                && !stopped.IsCancellationRequested && lastBusyFailure is not null)
            {
                // Adapters may translate cancellation into their own timeout exception; retain the busy cause.
                channel.Writer.TryComplete(lastBusyFailure);
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
