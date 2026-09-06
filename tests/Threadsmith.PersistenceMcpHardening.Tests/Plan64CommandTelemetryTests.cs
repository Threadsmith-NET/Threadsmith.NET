// Threadsmith.NET Milestone 23.1 — Plan 64: Command telemetry middleware tests.
//
// Covers: metadata-only success/cancellation/failure outcomes, exact response and exception
// identity preservation, next-once invocation, registration-order composition, activity tags,
// and canary non-disclosure across log state and activity tags.
namespace Threadsmith.PersistenceMcpHardening.Tests;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Command telemetry middleware and production-wiring tests (Plan 64, AR-01).</summary>
public static class Plan64CommandTelemetryTests
{
    /// <summary>A successful dispatch records the compiled command type, Success outcome, and a duration.</summary>
    [Fact]
    public static async Task Success_PreservesResponseAndRecordsCompiledTypeOutcomeDuration()
    {
        var logger = new RecordingLogger<CommandTelemetryMiddleware>();
        var middleware = new CommandTelemetryMiddleware(logger);
        var command = new CanaryCommand("visible-payload", "CANARY-RESPONSE-98765");

        var response = await middleware.InvokeAsync(
            command,
            _ => Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("ok", response);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains(nameof(CanaryCommand), entry.Message, StringComparison.Ordinal);
        Assert.Contains(CommandDispatchOutcome.Success, entry.Message, StringComparison.Ordinal);
        Assert.Contains(" ms", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>The next delegate is invoked exactly once per dispatch.</summary>
    [Fact]
    public static async Task Next_IsInvokedExactlyOnce()
    {
        var logger = new RecordingLogger<CommandTelemetryMiddleware>();
        var middleware = new CommandTelemetryMiddleware(logger);
        var invocations = 0;

        await middleware.InvokeAsync(
            new CanaryCommand("a", "b"),
            _ =>
            {
                invocations++;
                return Task.FromResult("x");
            },
            CancellationToken.None);

        Assert.Equal(1, invocations);
    }

    /// <summary>Cancellation before handler entry records Cancelled and rethrows OperationCanceledException unchanged.</summary>
    [Fact]
    public static async Task CancellationBeforeHandler_RecordsCancelledAndRethrows()
    {
        var logger = new RecordingLogger<CommandTelemetryMiddleware>();
        var middleware = new CommandTelemetryMiddleware(logger);
        var canary = new OperationCanceledException();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            middleware.InvokeAsync(
                new CanaryCommand("a", "b"),
                _ => Task.FromException<string>(canary),
                CancellationToken.None));

        Assert.Same(canary, thrown);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains(CommandDispatchOutcome.Cancelled, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    /// <summary>A pre-cancelled token reaching the terminal delegate records Cancelled without invoking the handler.</summary>
    [Fact]
    public static async Task PreCancelledToken_RecordsCancelledWithoutHandlerWork()
    {
        var logger = new RecordingLogger<CommandTelemetryMiddleware>();
        var middleware = new CommandTelemetryMiddleware(logger);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var handlerRan = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            middleware.InvokeAsync(
                new CanaryCommand("a", "b"),
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    handlerRan = true;
                    return Task.FromResult("x");
                },
                cancellation.Token));

        Assert.False(handlerRan);
        Assert.Contains(CommandDispatchOutcome.Cancelled, Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
    }

    /// <summary>A non-cancellation failure rethrows the exact exception instance and records Failure without its message.</summary>
    [Fact]
    public static async Task Failure_RethrowsSameInstanceAndOmitsExceptionMessage()
    {
        var logger = new RecordingLogger<CommandTelemetryMiddleware>();
        var middleware = new CommandTelemetryMiddleware(logger);
        const string secretMessage = "CANARY-EXCEPTION-MESSAGE-xyz";
        var canary = new InvalidOperationException(secretMessage);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(
                new CanaryCommand("a", "b"),
                _ => Task.FromException<string>(canary),
                CancellationToken.None));

        Assert.Same(canary, thrown);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains(CommandDispatchOutcome.Failure, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(secretMessage, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretMessage, entry.State, StringComparison.Ordinal);
    }

    /// <summary>A throwing logging provider cannot turn a successful dispatch into a failure.</summary>
    [Fact]
    public static async Task LoggingFailure_PreservesSuccessfulResponse()
    {
        var middleware = new CommandTelemetryMiddleware(new ThrowingLogger<CommandTelemetryMiddleware>());

        var response = await middleware.InvokeAsync(
            new CanaryCommand("a", "b"),
            _ => Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("ok", response);
    }

    /// <summary>A throwing logging provider cannot replace the handler's cancellation exception.</summary>
    [Fact]
    public static async Task LoggingFailure_PreservesCancellationException()
    {
        var middleware = new CommandTelemetryMiddleware(new ThrowingLogger<CommandTelemetryMiddleware>());
        var canary = new OperationCanceledException();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            middleware.InvokeAsync(
                new CanaryCommand("a", "b"),
                _ => Task.FromException<string>(canary),
                CancellationToken.None));

        Assert.Same(canary, thrown);
    }

    /// <summary>A throwing logging provider cannot replace the handler's failure exception.</summary>
    [Fact]
    public static async Task LoggingFailure_PreservesHandlerException()
    {
        var middleware = new CommandTelemetryMiddleware(new ThrowingLogger<CommandTelemetryMiddleware>());
        var canary = new InvalidOperationException("handler failure");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(
                new CanaryCommand("a", "b"),
                _ => Task.FromException<string>(canary),
                CancellationToken.None));

        Assert.Same(canary, thrown);
    }

    /// <summary>A throwing activity listener cannot turn a successful dispatch into a failure.</summary>
    [Fact]
    public static async Task ActivityListenerFailure_PreservesSuccessfulResponse()
    {
        var middleware = new CommandTelemetryMiddleware(new RecordingLogger<CommandTelemetryMiddleware>());
        using var listener = CreateThrowingActivityListener();
        ActivitySource.AddActivityListener(listener);

        var response = await middleware.InvokeAsync(
            new ListenerFailureCommand(),
            _ => Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("ok", response);
    }

    /// <summary>A throwing activity listener cannot replace the handler's original failure.</summary>
    [Fact]
    public static async Task ActivityListenerFailure_PreservesHandlerException()
    {
        var middleware = new CommandTelemetryMiddleware(new RecordingLogger<CommandTelemetryMiddleware>());
        var canary = new InvalidOperationException("handler failure");
        using var listener = CreateThrowingActivityListener();
        ActivitySource.AddActivityListener(listener);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(
                new ListenerFailureCommand(),
                _ => Task.FromException<string>(canary),
                CancellationToken.None));

        Assert.Same(canary, thrown);
    }

    /// <summary>Canary command properties and response values never appear in log state or activity tags.</summary>
    [Fact]
    public static async Task CanaryCommandPropertiesAndResponses_NeverAppearInLogsOrActivities()
    {
        var logger = new RecordingLogger<CommandTelemetryMiddleware>();
        var middleware = new CommandTelemetryMiddleware(logger);
        const string canaryProperty = "CANARY-PROPERTY-abc123";
        const string canaryResponse = "CANARY-RESPONSE-def456";
        var command = new CanaryCommand(canaryProperty, canaryResponse);
        var capturedActivities = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ThreadsmithActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        listener.ActivityStopped += capturedActivities.Add;
        ActivitySource.AddActivityListener(listener);

        var response = await middleware.InvokeAsync(
            command,
            _ => Task.FromResult(canaryResponse),
            CancellationToken.None);

        Assert.Equal(canaryResponse, response);
        var entry = Assert.Single(logger.Entries);
        Assert.DoesNotContain(canaryProperty, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(canaryResponse, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(canaryProperty, entry.State, StringComparison.Ordinal);
        Assert.DoesNotContain(canaryResponse, entry.State, StringComparison.Ordinal);

        var activity = Assert.Single(capturedActivities);
        Assert.Equal(CommandTelemetryMiddleware.ActivityName, activity.OperationName);
        foreach (var tag in activity.TagObjects)
        {
            var value = tag.Value?.ToString();
            Assert.NotEqual(canaryProperty, value);
            Assert.NotEqual(canaryResponse, value);
            Assert.DoesNotContain(canaryProperty, value ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(canaryResponse, value ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>The command activity carries compiled command type, outcome, elapsed duration, and Error status on failure.</summary>
    [Fact]
    public static async Task Activity_CarriesCommandTypeOutcomeDurationAndErrorStatusOnFailure()
    {
        var logger = new RecordingLogger<CommandTelemetryMiddleware>();
        var middleware = new CommandTelemetryMiddleware(logger);
        var capturedActivities = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ThreadsmithActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        listener.ActivityStopped += capturedActivities.Add;
        ActivitySource.AddActivityListener(listener);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(
                new CanaryCommand("a", "b"),
                _ => Task.FromException<string>(new InvalidOperationException("boom")),
                CancellationToken.None));

        var activity = Assert.Single(capturedActivities);
        Assert.Equal(CommandTelemetryMiddleware.ActivityName, activity.OperationName);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(nameof(CanaryCommand), GetTag(activity, "threadsmith.command.type"));
        Assert.Equal(CommandDispatchOutcome.Failure, GetTag(activity, "threadsmith.command.outcome"));
        Assert.NotNull(GetTag(activity, "threadsmith.command.elapsed_ms"));
    }

    /// <summary>Telemetry middleware composes in registration order with other middleware through the dispatcher.</summary>
    [Fact]
    public static async Task Telemetry_ComposesInRegistrationOrderThroughDispatcher()
    {
        var order = new List<string>();
        var dispatcher = new CommandDispatcher(
            [new OrderTrackingHandler(order)],
            new ICommandMiddleware[]
            {
                new CommandTelemetryMiddleware(new RecordingLogger<CommandTelemetryMiddleware>()),
                new OrderRecordingMiddleware("outer", order),
            });

        var result = await dispatcher.DispatchAsync(new OrderCommand("value"));

        Assert.Equal("value", result);
        // Telemetry is registered first; dispatcher wraps in reverse so the last-registered middleware
        // is innermost. Registration order around the handler: telemetry -> outer -> handler.
        Assert.Equal(["outer:before", "handler", "outer:after"], order);
    }

    /// <summary>Null command and null next are rejected at the middleware boundary.</summary>
    [Fact]
    public static async Task NullArguments_AreRejected()
    {
        var middleware = new CommandTelemetryMiddleware(new RecordingLogger<CommandTelemetryMiddleware>());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            middleware.InvokeAsync(null!, _ => Task.FromResult("x"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            middleware.InvokeAsync(new CanaryCommand("a", "b"), null!, CancellationToken.None));
    }

    private static string? GetTag(Activity activity, string key)
    {
        foreach (var tag in activity.TagObjects)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private static ActivityListener CreateThrowingActivityListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ThreadsmithActivity.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (string.Equals(
                    GetTag(activity, "threadsmith.command.type"),
                    nameof(ListenerFailureCommand),
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("activity listener failure");
                }
            },
        };
        return listener;
    }

    private sealed record CanaryCommand(string SecretProperty, string SecretResponse) : ICommand<string>;

    private sealed record ListenerFailureCommand : ICommand<string>;

    private sealed record OrderCommand(string Value) : ICommand<string>;

    private sealed class OrderTrackingHandler : ICommandHandler<OrderCommand, string>
    {
        private readonly List<string> _order;

        public OrderTrackingHandler(List<string> order)
        {
            _order = order;
        }

        public Task<string> HandleAsync(OrderCommand command, CancellationToken cancellationToken = default)
        {
            _order.Add("handler");
            return Task.FromResult(command.Value);
        }
    }

    private sealed class OrderRecordingMiddleware : ICommandMiddleware
    {
        private readonly string _name;
        private readonly List<string> _order;

        public OrderRecordingMiddleware(string name, List<string> order)
        {
            _name = name;
            _order = order;
        }

        public async Task<TResponse> InvokeAsync<TResponse>(
            ICommand<TResponse> command,
            Func<CancellationToken, Task<TResponse>> next,
            CancellationToken cancellationToken = default)
        {
            _order.Add($"{_name}:before");
            var response = await next(cancellationToken);
            _order.Add($"{_name}:after");
            return response;
        }
    }

    private sealed class RecordingLogger<TCategory> : ILogger<TCategory>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception), state?.ToString() ?? string.Empty));
        }
    }

    private sealed class ThrowingLogger<TCategory> : ILogger<TCategory>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            throw new InvalidOperationException("logging provider failure");
        }
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message, string State);

    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
