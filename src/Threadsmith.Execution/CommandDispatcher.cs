namespace Threadsmith.Execution;

using System.Reflection;
using Threadsmith.Core;

/// <summary>Dispatches commands through ordered middleware to registered handlers.</summary>
public sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly IReadOnlyDictionary<Type, HandlerRegistration> _handlers;
    private readonly IReadOnlyList<ICommandMiddleware> _middleware;

    /// <summary>Initializes a new instance of the <see cref="CommandDispatcher"/> class.</summary>
    public CommandDispatcher(IEnumerable<object> handlers, IEnumerable<ICommandMiddleware>? middleware = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers
            .SelectMany(handler => handler.GetType().GetInterfaces()
                .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ICommandHandler<,>))
                .Select(type =>
                {
                    var method = type.GetMethod(
                        nameof(ICommandHandler<,>.HandleAsync))
                        ?? throw new InvalidOperationException(
                            $"Handler contract {type.FullName} has no HandleAsync method.");
                    return new KeyValuePair<Type, HandlerRegistration>(
                        type.GetGenericArguments()[0],
                        new HandlerRegistration(handler, method));
                }))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        _middleware = middleware?.ToArray() ?? [];
    }

    /// <inheritdoc />
    public Task<TResponse> DispatchAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_handlers.TryGetValue(command.GetType(), out var handler))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"No handler is registered for {command.GetType().Name}.");
        }

        // The pre-invocation cancellation check lives inside the terminal delegate so a pre-cancelled
        // known command is still observed by registered middleware while its handler remains uncalled.
        Func<CancellationToken, Task<TResponse>> pipeline = token =>
        {
            token.ThrowIfCancellationRequested();
            return InvokeHandlerAsync<TResponse>(handler, command, token);
        };
        foreach (var stage in _middleware.Reverse())
        {
            var next = pipeline;
            pipeline = token => stage.InvokeAsync(command, next, token);
        }

        return pipeline(cancellationToken);
    }

    private static Task<TResponse> InvokeHandlerAsync<TResponse>(
        HandlerRegistration registration,
        ICommand<TResponse> command,
        CancellationToken cancellationToken)
    {
        try
        {
            object? result = registration.Method.Invoke(
                registration.Handler,
                [command, cancellationToken]);
            return result as Task<TResponse>
                ?? Task.FromException<TResponse>(new InvalidOperationException(
                    $"Handler {registration.Handler.GetType().FullName} returned an invalid task."));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            return Task.FromException<TResponse>(exception.InnerException);
        }
    }

    private sealed record HandlerRegistration(object Handler, MethodInfo Method);
}
