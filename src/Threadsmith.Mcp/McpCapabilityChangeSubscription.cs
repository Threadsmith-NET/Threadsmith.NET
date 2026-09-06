namespace Threadsmith.Mcp;

using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>Debounces SDK list-change notifications into one bounded complete host snapshot.</summary>
internal sealed class McpCapabilityChangeSubscription : IAsyncDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

    private readonly Lock _gate = new();
    private readonly ILogger<McpCapabilityChangeSubscription> _logger;
    private readonly McpClient _client;
    private readonly McpConnectionProfile _profile;
    private readonly IReadOnlyList<IAsyncDisposable> _registrations;
    private readonly CancellationTokenSource _lifetime = new();
    private Func<IReadOnlyList<McpImportedCapability>, CancellationToken, Task>? _handler;
    private Task _refreshWorker = Task.CompletedTask;
    private bool _refreshRequested;
    private bool _workerRunning;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="McpCapabilityChangeSubscription"/> class.</summary>
    internal McpCapabilityChangeSubscription(
        McpClient client,
        McpConnectionProfile profile,
        ILogger<McpCapabilityChangeSubscription> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _profile = profile;
        _logger = logger;

        var registrations = new List<IAsyncDisposable>();
        if (client.ServerCapabilities.Tools?.ListChanged is true)
        {
            registrations.Add(Register(NotificationMethods.ToolListChangedNotification));
        }

        if (client.ServerCapabilities.Resources?.ListChanged is true)
        {
            registrations.Add(Register(NotificationMethods.ResourceListChangedNotification));
        }

        if (client.ServerCapabilities.Prompts?.ListChanged is true)
        {
            registrations.Add(Register(NotificationMethods.PromptListChangedNotification));
        }

        _registrations = registrations;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task refresh;
        lock (_gate)
        {
            _handler = null;
            refresh = _refreshWorker;
        }

        await _lifetime.CancelAsync();
        foreach (var registration in _registrations)
        {
            await registration.DisposeAsync();
        }

        try
        {
            await refresh;
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }

    /// <summary>Sets the current generation-bound host callback.</summary>
    internal void SetHandler(
        Func<IReadOnlyList<McpImportedCapability>, CancellationToken, Task>? handler)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _handler = handler;
            if (handler is not null && _refreshRequested && !_workerRunning)
            {
                _workerRunning = true;
                _refreshWorker = RefreshLoopAsync(_lifetime.Token);
            }
        }
    }

    private IAsyncDisposable Register(string method)
    {
        return _client.RegisterNotificationHandler(
            method,
            (_, _) =>
            {
                ScheduleRefresh();
                return ValueTask.CompletedTask;
            });
    }

    private void ScheduleRefresh()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _refreshRequested = true;
            if (_handler is not null && !_workerRunning)
            {
                _workerRunning = true;
                _refreshWorker = RefreshLoopAsync(_lifetime.Token);
            }
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        // SetHandler and notification callbacks start the worker while holding _gate. Yield before
        // entering the serialized loop so the task never attempts to reacquire that lock inline.
        await Task.Yield();
        while (true)
        {
            Func<IReadOnlyList<McpImportedCapability>, CancellationToken, Task>? handler;
            lock (_gate)
            {
                if (!_refreshRequested || _handler is null)
                {
                    _workerRunning = false;
                    return;
                }

                _refreshRequested = false;
                handler = _handler;
            }

            try
            {
                await Task.Delay(DebounceDelay, cancellationToken);
                var capabilities = await SdkHttpTransport.DiscoverCapabilitiesAsync(
                    _client,
                    _profile,
                    cancellationToken);
                await handler(capabilities, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _workerRunning = false;
                }

                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "MCP capability rediscovery for profile '{ProfileId}' failed with {FailureType}; reconnect is required.",
                    _profile.Id,
                    exception.GetType().Name);
            }
        }
    }
}
