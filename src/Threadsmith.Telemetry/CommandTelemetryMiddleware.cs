namespace Threadsmith.Telemetry;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>
/// Metadata-only telemetry middleware around application command dispatch. Records the compiled
/// command type, one terminal outcome (<see cref="CommandDispatchOutcome"/>), monotonic elapsed
/// duration, and one command-level activity. Never inspects command properties or responses, and
/// never records raw exception messages, exception objects, secrets, or inferred identifiers.
/// </summary>
public sealed class CommandTelemetryMiddleware : ICommandMiddleware
{
    /// <summary>Activity operation name for command dispatch spans.</summary>
    public const string ActivityName = "command";

    private readonly ILogger<CommandTelemetryMiddleware> _logger;

    /// <summary>Initializes a new instance of the <see cref="CommandTelemetryMiddleware"/> class.</summary>
    /// <param name="logger">The logger used for metadata-only command telemetry.</param>
    public CommandTelemetryMiddleware(ILogger<CommandTelemetryMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> InvokeAsync<TResponse>(
        ICommand<TResponse> command,
        Func<CancellationToken, Task<TResponse>> next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(next);
        var commandType = command.GetType().Name;
        var startTimestamp = Stopwatch.GetTimestamp();
        Activity? activity = ThreadsmithActivity.Source.StartActivity(ActivityName);
        try
        {
            TResponse response = await next(cancellationToken).ConfigureAwait(false);
            Record(commandType, CommandDispatchOutcome.Success, startTimestamp, activity);
            return response;
        }
        catch (OperationCanceledException)
        {
            Record(commandType, CommandDispatchOutcome.Cancelled, startTimestamp, activity);
            throw;
        }
        catch (Exception)
        {
            Record(commandType, CommandDispatchOutcome.Failure, startTimestamp, activity);
            throw;
        }
        finally
        {
            try
            {
                activity?.Dispose();
            }
            catch (Exception exception)
            {
                // Telemetry is best-effort: a failing activity listener must not change command semantics.
                _ = exception;
            }
        }
    }

    private void Record(string commandType, string outcome, long startTimestamp, Activity? activity)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        var elapsedMilliseconds = (long)elapsed.TotalMilliseconds;
        activity?.SetTag("threadsmith.command.type", commandType);
        activity?.SetTag("threadsmith.command.outcome", outcome);
        activity?.SetTag("threadsmith.command.elapsed_ms", elapsedMilliseconds);
        if (outcome == CommandDispatchOutcome.Failure)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        try
        {
            _logger.LogInformation(
                "Command {CommandType} completed with outcome {Outcome} in {ElapsedMs} ms",
                commandType,
                outcome,
                elapsedMilliseconds);
        }
        catch (Exception)
        {
            // Telemetry is best-effort: a failing provider must not change command semantics.
            return;
        }
    }
}

/// <summary>Closed terminal outcome values for command dispatch telemetry.</summary>
public static class CommandDispatchOutcome
{
    /// <summary>The command handler completed successfully.</summary>
    public const string Success = "Success";

    /// <summary>The dispatch was cancelled before or during handler execution.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>The dispatch failed with a non-cancellation exception.</summary>
    public const string Failure = "Failure";
}
