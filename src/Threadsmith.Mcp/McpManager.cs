namespace Threadsmith.Mcp;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Single host-owned authority for MCP startup, user lifecycle operations, and shutdown.</summary>
public interface IMcpManager : IAsyncDisposable
{
    /// <summary>Best-effort connects every eligible trusted profile configured for auto-connect.</summary>
    Task AutoConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Invalidates live repository-bound capability authority and reapplies trusted auto-connect.</summary>
    Task RebindRepositoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes one provider-neutral lifecycle operation.</summary>
    Task<McpManagementResult> ExecuteAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Serialized, generation-fenced MCP lifecycle authority and shared command handler.</summary>
public sealed class McpManager :
    IMcpManager,
    ICommandHandler<ExecuteMcpManagementCommand, McpManagementResult>
{
    private const int MaximumArguments = 32;
    private const int MaximumArgumentCharacters = 16 * 1024;
    private const int MaximumCapabilities = 256;
    private const int MaximumFailureCharacters = 1024;
    private const int MaximumProfiles = 64;
    private const int MaximumRecentLatencySamples = 32;

    private readonly IMcpAdapter _adapter;
    private readonly Func<McpConnectionResult, CancellationToken, Task>? _connectedCallback;
    private readonly SemaphoreSlim _connectionLimiter;
    private readonly IMcpIdentityManager _identityManager;
    private readonly ILogger<McpManager> _logger;
    private readonly Func<McpConnectionProfile, McpImportedCapability, CancellationToken, Task<bool>>? _explicitReadAuthorizer;
    private readonly IReadOnlyDictionary<string, McpConnectionProfile> _profiles;
    private readonly IOutputSanitizer _sanitizer;
    private readonly ConcurrentDictionary<string, ProfileRuntime> _states;
    private readonly TimeProvider _timeProvider;
    private readonly IToolStateManager _toolState;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="McpManager"/> class.</summary>
    public McpManager(
        IEnumerable<McpConnectionProfile> profiles,
        IMcpAdapter adapter,
        IToolStateManager toolState,
        IMcpIdentityManager identityManager,
        IOutputSanitizer sanitizer,
        ILogger<McpManager> logger,
        TimeProvider? timeProvider = null,
        int maximumConcurrentConnections = 4,
        Func<McpConnectionResult, CancellationToken, Task>? connectedCallback = null,
        Func<McpConnectionProfile, McpImportedCapability, CancellationToken, Task<bool>>? explicitReadAuthorizer = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(toolState);
        ArgumentNullException.ThrowIfNull(identityManager);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(logger);
        if (maximumConcurrentConnections is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentConnections),
                "MCP concurrent connection limit must be between 1 and 16.");
        }

        McpConnectionProfile[] profileArray = [.. profiles];
        foreach (var profile in profileArray)
        {
            ValidateProfile(profile);
        }

        if (profileArray.Length > MaximumProfiles)
        {
            throw new InvalidOperationException($"At most {MaximumProfiles} MCP profiles may be configured.");
        }

        if (profileArray.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count()
            != profileArray.Length)
        {
            throw new InvalidOperationException("MCP profile ids must be unique.");
        }

        _profiles = profileArray.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        _adapter = adapter;
        _toolState = toolState;
        _identityManager = identityManager;
        _sanitizer = sanitizer;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionLimiter = new SemaphoreSlim(maximumConcurrentConnections, maximumConcurrentConnections);
        _connectedCallback = connectedCallback;
        _explicitReadAuthorizer = explicitReadAuthorizer;
        _states = new ConcurrentDictionary<string, ProfileRuntime>(
            profileArray.Select(profile => new KeyValuePair<string, ProfileRuntime>(
                profile.Id,
                new ProfileRuntime(profile))),
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task AutoConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        foreach (var profile in _profiles.Values
            .Where(profile => profile.AutoConnect && profile.Trust != McpTrustLevel.Untrusted)
            .OrderBy(profile => profile.Id, StringComparer.Ordinal))
        {
            var request = new McpManagementRequest
            {
                Action = McpManagementAction.Connect,
                ProfileId = profile.Id,
            };
            var result = await ExecuteAsync(
                request,
                allowOAuthUserInteraction: false,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "MCP profile '{ProfileId}' failed to auto-connect: {FailureKind}.",
                    profile.Id,
                    result.FailureKind);
            }
        }
    }

    /// <inheritdoc />
    public async Task RebindRepositoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        foreach (var state in _states.Values
            .OrderBy(state => state.Profile.Id, StringComparer.Ordinal))
        {
            if (GetLiveStatus(state.Profile.Id) is null)
            {
                continue;
            }

            var result = await ExecuteAsync(
                new McpManagementRequest
                {
                    Action = McpManagementAction.Disconnect,
                    ProfileId = state.Profile.Id,
                },
                cancellationToken);
            if (!result.Succeeded && result.FailureKind != McpManagementFailureKind.Killed)
            {
                throw new InvalidOperationException(
                    $"MCP profile '{state.Profile.Id}' could not reach a repository transition boundary: {result.FailureKind}.");
            }
        }

        await AutoConnectAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<McpManagementResult> HandleAsync(
        ExecuteMcpManagementCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(command.Request, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var state in _states.Values)
        {
            await state.Gate.WaitAsync(CancellationToken.None);
            try
            {
                await _adapter.DisconnectAsync(state.Profile.Id, CancellationToken.None);
            }
            finally
            {
                state.Gate.Release();
                state.Gate.Dispose();
            }
        }

        await _adapter.DisposeAsync();
        if (_identityManager is IDisposable disposableIdentity)
        {
            disposableIdentity.Dispose();
        }

        _connectionLimiter.Dispose();
    }

    /// <inheritdoc />
    public Task<McpManagementResult> ExecuteAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(request, allowOAuthUserInteraction: true, cancellationToken);
    }

    private async Task<McpManagementResult> ExecuteAsync(
        McpManagementRequest request,
        bool allowOAuthUserInteraction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        long started = _timeProvider.GetTimestamp();
        try
        {
            ValidateRequest(request);
            var result = request.Action switch
            {
                McpManagementAction.List => await ListAsync(request, cancellationToken),
                McpManagementAction.Inspect => await InspectAsync(request, cancellationToken),
                McpManagementAction.Connect => await TransitionAsync(
                    request,
                    (state, operation, token) => ConnectCoreAsync(
                        state,
                        operation,
                        allowOAuthUserInteraction,
                        token),
                    cancellationToken),
                McpManagementAction.Disconnect => await TransitionAsync(request, DisconnectCoreAsync, cancellationToken),
                McpManagementAction.Reconnect => await TransitionAsync(request, ReconnectCoreAsync, cancellationToken),
                McpManagementAction.ListCapabilities => await ListCapabilitiesAsync(request, cancellationToken),
                McpManagementAction.InspectCapability => await InspectCapabilityAsync(request, cancellationToken),
                McpManagementAction.EnableTool => await SetToolEnabledAsync(request, true, cancellationToken),
                McpManagementAction.DisableTool => await SetToolEnabledAsync(request, false, cancellationToken),
                McpManagementAction.ReadResource => await ReadResourceAsync(request, cancellationToken),
                McpManagementAction.GetPrompt => await GetPromptAsync(request, cancellationToken),
                McpManagementAction.Authenticate => await TransitionAsync(request, AuthenticateCoreAsync, cancellationToken),
                McpManagementAction.Logout => await TransitionAsync(request, LogoutCoreAsync, cancellationToken),
                McpManagementAction.Revoke => await TransitionAsync(request, RevokeCoreAsync, cancellationToken),
                McpManagementAction.SwitchAccount => await TransitionAsync(request, SwitchAccountCoreAsync, cancellationToken),
                McpManagementAction.Diagnose => await DiagnoseAsync(request, cancellationToken),
                _ => Failure(request.Action, McpManagementFailureKind.Failed, "Unsupported MCP management action."),
            };
            var completed = result with
            {
                DurationMilliseconds = ToElapsedMilliseconds(_timeProvider.GetElapsedTime(started)),
            };
            _logger.LogInformation(
                "MCP lifecycle {Action} for profile {ProfileId} completed with {FailureKind} in {DurationMilliseconds} ms.",
                request.Action,
                request.ProfileId ?? "all",
                completed.FailureKind,
                completed.DurationMilliseconds);
            return completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(request.Action, McpManagementFailureKind.Cancelled, "The MCP operation was cancelled.") with
            {
                DurationMilliseconds = ToElapsedMilliseconds(_timeProvider.GetElapsedTime(started)),
            };
        }
        catch (OperationCanceledException)
        {
            return Failure(
                request.Action,
                McpManagementFailureKind.Timeout,
                "The MCP operation exceeded its configured timeout.") with
            {
                DurationMilliseconds = ToElapsedMilliseconds(_timeProvider.GetElapsedTime(started)),
            };
        }
        catch (Exception exception)
        {
            var failure = Classify(exception);
            string message = NormalizeFailureMessage(exception.Message);
            _logger.LogError(
                "MCP lifecycle {Action} for profile {ProfileId} failed with {FailureKind}: {Message}",
                request.Action,
                request.ProfileId ?? "all",
                failure,
                message);
            return Failure(
                request.Action,
                failure,
                message) with
            {
                DurationMilliseconds = ToElapsedMilliseconds(_timeProvider.GetElapsedTime(started)),
            };
        }
    }

    private async Task<McpManagementResult> ListAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        McpProfileSummary[] summaries =
        [
            .. (await Task.WhenAll(_states.Values
                    .OrderBy(state => state.Profile.Id, StringComparer.Ordinal)
                    .Take(request.MaximumCount)
                    .Select(state => CreateSummaryAsync(state, cancellationToken))))
                .OrderBy(summary => summary.ProfileId, StringComparer.Ordinal),
        ];
        return Success(request.Action, $"{summaries.Length} MCP profile(s).") with { Profiles = summaries };
    }

    private async Task<McpManagementResult> InspectAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        var state = GetState(request);
        var detail = await CreateDetailAsync(state, cancellationToken);
        return Success(request.Action, $"MCP profile '{state.Profile.Id}'.") with { Profile = detail };
    }

    private async Task<McpManagementResult> TransitionAsync(
        McpManagementRequest request,
        Func<ProfileRuntime, McpManagementRequest, CancellationToken, Task<McpManagementResult>> operation,
        CancellationToken cancellationToken)
    {
        var state = GetState(request);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            return await operation(state, request, cancellationToken);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private Task<McpManagementResult> ConnectCoreAsync(
        ProfileRuntime state,
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        return ConnectCoreAsync(state, request, allowOAuthUserInteraction: true, cancellationToken);
    }

    private async Task<McpManagementResult> ConnectCoreAsync(
        ProfileRuntime state,
        McpManagementRequest request,
        bool allowOAuthUserInteraction,
        CancellationToken cancellationToken)
    {
        if (state.Profile.Trust == McpTrustLevel.Untrusted || !IsEndpointEligible(state.Profile))
        {
            string message = state.Profile.Trust == McpTrustLevel.Untrusted
                ? "The profile is configured as untrusted and cannot connect."
                : "The profile executable or endpoint does not satisfy MCP eligibility policy.";
            return RecordFailure(
                state,
                request.Action,
                McpManagementFailureKind.Ineligible,
                message);
        }

        var current = GetLiveStatus(state.Profile.Id);
        if (current?.State == McpConnectionState.Connected)
        {
            return Success(request.Action, $"MCP profile '{state.Profile.Id}' is already connected.") with
            {
                Profiles = [await CreateSummaryAsync(state, cancellationToken)],
            };
        }

        state.ProjectedState = McpConnectionState.Connecting;
        state.LastTransitionAt = _timeProvider.GetUtcNow();
        await _connectionLimiter.WaitAsync(cancellationToken);
        try
        {
            var connectionProfile = state.Profile with
            {
                AllowOAuthUserInteraction = allowOAuthUserInteraction,
            };
            var connection = await _adapter.ConnectAsync(connectionProfile, cancellationToken);
            state.Generation++;
            state.LastTransitionAt = _timeProvider.GetUtcNow();
            if (connection.Capabilities.Count > MaximumCapabilities)
            {
                await _adapter.DisconnectAsync(state.Profile.Id, CancellationToken.None);
                state.ProjectedState = McpConnectionState.Failed;
                state.Capabilities = [];
                return RecordFailure(
                    state,
                    request.Action,
                    McpManagementFailureKind.InvalidCapability,
                    $"The MCP server advertises more than {MaximumCapabilities} total capabilities.");
            }

            state.Capabilities = connection.Capabilities
                .OrderBy(capability => capability.Id, StringComparer.Ordinal)
                .ToArray();
            state.ProjectedState = connection.Status.State;
            state.StartupDurationMilliseconds = connection.Status.StartupDurationMilliseconds;
            if (!connection.Succeeded)
            {
                return RecordFailure(
                    state,
                    request.Action,
                    ClassifyConnectionFailure(connection.Status.Error),
                    connection.Status.Error ?? "MCP connection failed.");
            }

            state.LastFailure = McpManagementFailureKind.None;
            state.LastOutcome = "Connected.";
            if (_connectedCallback is not null)
            {
                try
                {
                    await _connectedCallback(connection, cancellationToken);
                }
                catch
                {
                    await _adapter.DisconnectAsync(state.Profile.Id, CancellationToken.None);
                    state.Generation++;
                    state.Capabilities = [];
                    state.ProjectedState = McpConnectionState.Failed;
                    throw;
                }
            }

            return Success(request.Action, $"MCP profile '{state.Profile.Id}' connected.") with
            {
                Profiles = [await CreateSummaryAsync(state, cancellationToken)],
            };
        }
        finally
        {
            _connectionLimiter.Release();
        }
    }

    private async Task<McpManagementResult> DisconnectCoreAsync(
        ProfileRuntime state,
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        var current = GetLiveStatus(state.Profile.Id);
        if (current is null || current.State is McpConnectionState.Disconnected)
        {
            state.ProjectedState = McpConnectionState.Disconnected;
            state.Capabilities = [];
            return Success(request.Action, $"MCP profile '{state.Profile.Id}' is already disconnected.") with
            {
                Profiles = [await CreateSummaryAsync(state, cancellationToken)],
            };
        }

        state.ProjectedState = McpConnectionState.Draining;
        var outcome = await _adapter.DisconnectWithOutcomeAsync(
            state.Profile.Id,
            cancellationToken);
        state.Generation++;
        state.Capabilities = [];
        state.LastTransitionAt = _timeProvider.GetUtcNow();
        state.ProjectedState = outcome;
        if (outcome == McpConnectionState.Killed)
        {
            var killed = RecordFailure(
                state,
                request.Action,
                McpManagementFailureKind.Killed,
                "The MCP connection was removed, but bounded shutdown required forced termination.");
            return killed with { Profiles = [await CreateSummaryAsync(state, cancellationToken)] };
        }

        state.LastFailure = McpManagementFailureKind.None;
        state.LastOutcome = "Disconnected and live capabilities cleared.";
        return Success(request.Action, $"MCP profile '{state.Profile.Id}' disconnected.") with
        {
            Profiles = [await CreateSummaryAsync(state, cancellationToken)],
        };
    }

    private async Task<McpManagementResult> ReconnectCoreAsync(
        ProfileRuntime state,
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        _ = await DisconnectCoreAsync(state, request with { Action = McpManagementAction.Disconnect }, cancellationToken);
        return await ConnectCoreAsync(state, request, cancellationToken);
    }

    private async Task<McpManagementResult> ListCapabilitiesAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        var state = GetState(request);
        RefreshCapabilitySnapshot(state);
        McpCapabilityDescriptor[] capabilities =
        [
            .. state.Capabilities
                .Where(capability => request.CapabilityKind is null
                    || MapKind(capability.Kind) == request.CapabilityKind)
                .OrderBy(capability => capability.Id, StringComparer.Ordinal)
                .Take(request.MaximumCount)
                .Select(capability => MapCapability(state.Profile.Id, capability)),
        ];
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return Success(request.Action, $"{capabilities.Length} active MCP capability descriptor(s).") with
        {
            Capabilities = capabilities,
        };
    }

    private Task<McpManagementResult> InspectCapabilityAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        var state = GetState(request);
        var capability = GetCapability(state, request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Success(request.Action, $"MCP capability '{capability.Id}'.") with
        {
            Capabilities = [MapCapability(state.Profile.Id, capability, includeSchema: true)],
        });
    }

    private async Task<McpManagementResult> SetToolEnabledAsync(
        McpManagementRequest request,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var state = GetState(request);
        var capability = GetCapability(state, request);
        if (capability.Kind != McpCapabilityKind.Tool)
        {
            return Failure(
                request.Action,
                McpManagementFailureKind.InvalidCapability,
                "Only imported MCP tools can be enabled or disabled.");
        }

        if (enabled)
        {
            await _toolState.EnableAsync(capability.Id, $"mcp-1-{capability.Digest}", cancellationToken);
        }
        else
        {
            await _toolState.DisableAsync(capability.Id, cancellationToken);
        }

        return Success(
            request.Action,
            $"MCP tool '{capability.Id}' {(enabled ? "enabled" : "disabled")} for the active repository.") with
        {
            Capabilities = [MapCapability(state.Profile.Id, capability)],
        };
    }

    private async Task<McpManagementResult> ReadResourceAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        var state = GetState(request);
        var capability = GetCapability(state, request);
        if (capability.Kind is not (McpCapabilityKind.Resource or McpCapabilityKind.ResourceTemplate))
        {
            return Failure(
                request.Action,
                McpManagementFailureKind.InvalidCapability,
                "The selected MCP capability is not a resource.");
        }

        if (capability.Kind == McpCapabilityKind.Resource && request.Arguments.Count != 0)
        {
            return Failure(
                request.Action,
                McpManagementFailureKind.InvalidCapability,
                "A fixed MCP resource does not accept template arguments.");
        }

        if (capability.Kind == McpCapabilityKind.ResourceTemplate
            && !AreResourceTemplateArgumentsDeclared(capability.ResourceIdentity, request.Arguments.Keys))
        {
            return Failure(
                request.Action,
                McpManagementFailureKind.InvalidCapability,
                "An MCP resource-template argument was not declared by the active capability.");
        }

        if (!await IsExplicitReadAuthorizedAsync(state, capability, cancellationToken))
        {
            return Failure(
                request.Action,
                McpManagementFailureKind.PolicyDenied,
                "Central tool policy denied the explicit MCP resource request.");
        }

        long generation = state.Generation;
        long started = _timeProvider.GetTimestamp();
        var result = await _adapter.ReadResourceAsync(
            state.Profile.Id,
            capability.Id,
            request.Arguments,
            cancellationToken);
        RefreshCapabilitySnapshot(state);
        EnsureGeneration(state, generation);
        state.AddLatency("resource", ToElapsedMilliseconds(_timeProvider.GetElapsedTime(started)));
        return Success(request.Action, "Untrusted MCP resource content retrieved explicitly.") with
        {
            Content = [.. result.Content.Select(MapContent)],
            IsTruncated = result.IsTruncated,
        };
    }

    private async Task<McpManagementResult> GetPromptAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        var state = GetState(request);
        var capability = GetCapability(state, request);
        if (capability.Kind != McpCapabilityKind.Prompt)
        {
            return Failure(
                request.Action,
                McpManagementFailureKind.InvalidCapability,
                "The selected MCP capability is not a prompt.");
        }

        ValidatePromptArguments(capability, request.Arguments);
        if (!await IsExplicitReadAuthorizedAsync(state, capability, cancellationToken))
        {
            return Failure(
                request.Action,
                McpManagementFailureKind.PolicyDenied,
                "Central tool policy denied the explicit MCP prompt request.");
        }

        long generation = state.Generation;
        long started = _timeProvider.GetTimestamp();
        var result = await _adapter.GetPromptAsync(
            state.Profile.Id,
            capability.Id,
            request.Arguments,
            cancellationToken);
        RefreshCapabilitySnapshot(state);
        EnsureGeneration(state, generation);
        state.AddLatency("prompt", ToElapsedMilliseconds(_timeProvider.GetElapsedTime(started)));
        return Success(
            request.Action,
            "Untrusted MCP prompt material rendered explicitly; it was not added to model context.") with
        {
            Content = [.. result.Content.Select(MapContent)],
            IsTruncated = result.IsTruncated,
        };
    }

    private Task<bool> IsExplicitReadAuthorizedAsync(
        ProfileRuntime state,
        McpImportedCapability capability,
        CancellationToken cancellationToken)
    {
        if (_explicitReadAuthorizer is not null)
        {
            return _explicitReadAuthorizer(state.Profile, capability, cancellationToken);
        }

        return Task.FromResult(state.Profile.Transport == McpTransport.Stdio
            && state.Profile.SecretScope.Count == 0);
    }

    private async Task<McpManagementResult> AuthenticateCoreAsync(
        ProfileRuntime state,
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        if (state.Profile.OAuth?.Enabled is not true)
        {
            return RecordFailure(
                state,
                request.Action,
                McpManagementFailureKind.UnsupportedAuthentication,
                "This profile does not use host-owned OAuth.");
        }

        _ = await DisconnectCoreAsync(state, request with { Action = McpManagementAction.Disconnect }, cancellationToken);
        return await ConnectCoreAsync(state, request, cancellationToken);
    }

    private async Task<McpManagementResult> LogoutCoreAsync(
        ProfileRuntime state,
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Confirmed)
        {
            return RecordFailure(
                state,
                request.Action,
                McpManagementFailureKind.PolicyDenied,
                "Logout requires exact confirmation because it clears the selected profile's local identity.");
        }

        _ = await DisconnectCoreAsync(state, request with { Action = McpManagementAction.Disconnect }, cancellationToken);
        var result = await _identityManager.LogoutAsync(state.Profile, cancellationToken);
        state.Generation++;
        return RecordIdentityResult(state, request.Action, result) with
        {
            Profiles = [await CreateSummaryAsync(state, cancellationToken)],
        };
    }

    private async Task<McpManagementResult> RevokeCoreAsync(
        ProfileRuntime state,
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Confirmed)
        {
            return RecordFailure(
                state,
                request.Action,
                McpManagementFailureKind.PolicyDenied,
                "Remote revocation requires exact confirmation.");
        }

        _ = await DisconnectCoreAsync(state, request with { Action = McpManagementAction.Disconnect }, cancellationToken);
        var result = await _identityManager.RevokeAsync(
            state.Profile,
            request.AllowLocalCleanupAfterUnconfirmedRevocation,
            cancellationToken);
        state.Generation++;
        return RecordIdentityResult(state, request.Action, result) with
        {
            Profiles = [await CreateSummaryAsync(state, cancellationToken)],
        };
    }

    private async Task<McpManagementResult> SwitchAccountCoreAsync(
        ProfileRuntime state,
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Confirmed)
        {
            return RecordFailure(
                state,
                request.Action,
                McpManagementFailureKind.PolicyDenied,
                "Switch-account requires exact confirmation and replaces the selected profile's sole identity.");
        }

        _ = await DisconnectCoreAsync(state, request with { Action = McpManagementAction.Disconnect }, cancellationToken);
        McpIdentityMutationResult identityMutation;
        if (request.RevokeCurrentIdentityBeforeSwitch)
        {
            identityMutation = await _identityManager.RevokeAsync(
                state.Profile,
                request.AllowLocalCleanupAfterUnconfirmedRevocation,
                cancellationToken);
        }
        else
        {
            using var identityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            identityCancellation.CancelAfter(state.Profile.RequestTimeout);
            identityMutation = await _identityManager.LogoutAsync(
                state.Profile,
                identityCancellation.Token);
        }

        if (!identityMutation.Succeeded)
        {
            return RecordIdentityResult(state, request.Action, identityMutation);
        }

        state.Generation++;
        return await ConnectCoreAsync(state, request, cancellationToken);
    }

    private async Task<McpManagementResult> DiagnoseAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken)
    {
        var state = GetState(request);
        var checks = new List<McpDiagnosticCheck>
        {
            new()
            {
                Name = "profile-eligibility",
                Succeeded = state.Profile.Trust != McpTrustLevel.Untrusted,
                Detail = state.Profile.Trust == McpTrustLevel.Untrusted
                    ? "Profile trust is Untrusted; connection is denied."
                    : "Profile comes from repository-excluding trusted host configuration.",
            },
            new()
            {
                Name = state.Profile.Transport == McpTransport.Stdio ? "executable-preflight" : "endpoint-preflight",
                Succeeded = IsEndpointEligible(state.Profile),
                Detail = EndpointIdentity(state.Profile),
            },
            new()
            {
                Name = "secret-scope",
                Succeeded = true,
                Detail = $"{state.Profile.SecretScope.Count} logical secret reference(s); values withheld.",
            },
            new()
            {
                Name = "capability-translation",
                Succeeded = state.Capabilities.Count <= MaximumCapabilities,
                Detail = $"{state.Capabilities.Count} active bounded descriptor(s).",
            },
        };
        var authState = await _identityManager.GetStateAsync(state.Profile, cancellationToken);
        checks.Add(new McpDiagnosticCheck
        {
            Name = "authentication",
            Succeeded = authState is not (McpAuthenticationState.Failed or McpAuthenticationState.AuthenticationRequired),
            Detail = authState.ToString(),
        });

        if (GetLiveStatus(state.Profile.Id)?.State == McpConnectionState.Connected)
        {
            long pingStarted = _timeProvider.GetTimestamp();
            try
            {
                await _adapter.PingAsync(state.Profile.Id, cancellationToken);
                long? pingMilliseconds = ToElapsedMilliseconds(_timeProvider.GetElapsedTime(pingStarted));
                state.AddLatency("ping", pingMilliseconds);
                checks.Add(new McpDiagnosticCheck
                {
                    Name = "protocol-ping",
                    Succeeded = true,
                    Detail = "Protocol-defined ping succeeded.",
                    DurationMilliseconds = pingMilliseconds,
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                checks.Add(new McpDiagnosticCheck
                {
                    Name = "protocol-ping",
                    Succeeded = false,
                    Detail = NormalizeFailureMessage(exception.Message),
                    DurationMilliseconds = ToElapsedMilliseconds(_timeProvider.GetElapsedTime(pingStarted)),
                });
            }
        }
        else
        {
            checks.Add(new McpDiagnosticCheck
            {
                Name = "protocol-ping",
                Succeeded = false,
                Detail = "Unavailable while disconnected; diagnostics did not connect or invoke an arbitrary tool.",
            });
        }

        return Success(request.Action, "Structured MCP diagnostics complete.") with
        {
            Profile = await CreateDetailAsync(state, cancellationToken),
            Diagnostics = [.. checks.Take(request.MaximumCount)],
        };
    }

    private async Task<McpProfileSummary> CreateSummaryAsync(
        ProfileRuntime state,
        CancellationToken cancellationToken)
    {
        var live = GetLiveStatus(state.Profile.Id);
        if (live is not null)
        {
            RefreshCapabilitySnapshot(state);
        }

        var authentication = await _identityManager.GetStateAsync(
            state.Profile,
            cancellationToken);
        if (live?.State == McpConnectionState.Connected && state.Profile.OAuth?.Enabled is true)
        {
            authentication = McpAuthenticationState.Authenticated;
        }

        var capabilities = state.Capabilities;
        return new McpProfileSummary
        {
            ProfileId = state.Profile.Id,
            DisplayName = Bound(_sanitizer.Sanitize(state.Profile.DisplayName), 256),
            ConfigurationSource = state.Profile.ConfigurationSource,
            Transport = state.Profile.Transport.ToString(),
            Trust = state.Profile.Trust.ToString(),
            AutoConnect = state.Profile.AutoConnect,
            Eligible = state.Profile.Trust != McpTrustLevel.Untrusted && IsEndpointEligible(state.Profile),
            EndpointIdentity = EndpointIdentity(state.Profile),
            AuthenticationState = authentication,
            State = (live?.State ?? state.ProjectedState).ToString(),
            Generation = state.Generation,
            ProcessPresent = live?.ProcessPresent is true || live?.ProcessId is not null,
            InFlightCount = Math.Max(0, live?.InFlightRequests ?? 0),
            CapabilityCounts = capabilities
                .GroupBy(capability => capability.Kind.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            EnabledToolCount = capabilities.Count(capability => capability.Kind == McpCapabilityKind.Tool
                && IsToolEnabled(capability.Id)),
            LastTransitionAt = state.LastTransitionAt,
            LastOutcome = state.LastOutcome,
            LastFailure = state.LastFailure,
        };
    }

    private async Task<McpProfileDetail> CreateDetailAsync(
        ProfileRuntime state,
        CancellationToken cancellationToken)
    {
        return new McpProfileDetail
        {
            Summary = await CreateSummaryAsync(state, cancellationToken),
            AllowedCapabilities = [.. state.Profile.AllowedCapabilities.Select(kind => kind.ToString())],
            StartupTimeoutMilliseconds = (long)state.Profile.StartupTimeout.TotalMilliseconds,
            RequestTimeoutMilliseconds = (long)state.Profile.RequestTimeout.TotalMilliseconds,
            DrainKillTimeoutMilliseconds = (long)state.Profile.DrainKillTimeout.TotalMilliseconds,
            SecretReferenceCount = state.Profile.SecretScope.Count,
            Latencies = state.GetLatencySummaries(),
        };
    }

    private McpCapabilityDescriptor MapCapability(
        string profileId,
        McpImportedCapability capability,
        bool includeSchema = false)
    {
        return new McpCapabilityDescriptor
        {
            CapabilityId = capability.Id,
            ProfileId = profileId,
            Kind = MapKind(capability.Kind),
            Name = Bound(_sanitizer.Sanitize(capability.ServerName), 256),
            Description = Bound(_sanitizer.Sanitize(capability.Description), 2048),
            Digest = capability.Digest,
            Enabled = capability.Kind == McpCapabilityKind.Tool ? IsToolEnabled(capability.Id) : null,
            MimeType = capability.MimeType is null
                ? null
                : Bound(_sanitizer.Sanitize(capability.MimeType), 256),
            ResourceIdentity = capability.ResourceIdentity is null
                ? null
                : Bound(_sanitizer.Sanitize(capability.ResourceIdentity), 4096),
            Arguments =
            [
                .. capability.PromptArguments.Select(argument => new McpPromptArgumentDescriptor
                {
                    Name = Bound(_sanitizer.Sanitize(argument.Name), 128),
                    Description = Bound(_sanitizer.Sanitize(argument.Description), 1024),
                    Required = argument.Required,
                }),
            ],
            InputSchemaJson = includeSchema && capability.InputSchemaJson is { } schema
                ? Bound(_sanitizer.Sanitize(schema), 64 * 1024)
                : null,
        };
    }

    private bool IsToolEnabled(string capabilityId)
    {
        try
        {
            return _toolState.IsEnabled(capabilityId);
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private ProfileRuntime GetState(McpManagementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileId)
            || !_states.TryGetValue(request.ProfileId, out var state))
        {
            throw new KeyNotFoundException("An exact configured MCP profile id is required.");
        }

        return state;
    }

    private McpImportedCapability GetCapability(
        ProfileRuntime state,
        McpManagementRequest request)
    {
        RefreshCapabilitySnapshot(state);
        if (string.IsNullOrWhiteSpace(request.CapabilityId))
        {
            throw new KeyNotFoundException("An exact MCP capability id is required.");
        }

        return state.Capabilities.FirstOrDefault(capability => string.Equals(
                capability.Id,
                request.CapabilityId,
                StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("The exact capability is not active for this connection generation.");
    }

    private void RefreshCapabilitySnapshot(ProfileRuntime state)
    {
        if (GetLiveStatus(state.Profile.Id)?.State != McpConnectionState.Connected)
        {
            return;
        }

        McpImportedCapability[] snapshot =
        [
            .. _adapter.GetCapabilities(state.Profile.Id)
                .OrderBy(capability => capability.Id, StringComparer.Ordinal),
        ];
        lock (state.CapabilitySnapshotGate)
        {
            var current = state.Capabilities;
            bool unchanged = current.Count == snapshot.Length
                && current.Zip(snapshot).All(pair => pair.First.Kind == pair.Second.Kind
                    && string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal)
                    && string.Equals(pair.First.Digest, pair.Second.Digest, StringComparison.Ordinal));
            if (!unchanged)
            {
                state.Generation++;
                state.Capabilities = snapshot;
                state.LastTransitionAt = _timeProvider.GetUtcNow();
                state.LastOutcome = "Capabilities refreshed after a server list-change notification.";
            }
        }
    }

    private McpConnectionStatus? GetLiveStatus(string profileId)
    {
        return _adapter.GetConnections().FirstOrDefault(status => string.Equals(
            status.ProfileId,
            profileId,
            StringComparison.Ordinal));
    }

    private McpManagementResult RecordIdentityResult(
        ProfileRuntime state,
        McpManagementAction action,
        McpIdentityMutationResult result)
    {
        state.LastTransitionAt = _timeProvider.GetUtcNow();
        state.LastOutcome = result.Message;
        state.LastFailure = result.FailureKind;
        return new McpManagementResult
        {
            Action = action,
            Succeeded = result.Succeeded,
            ExitCode = result.Succeeded ? 0 : ExitCode(result.FailureKind),
            FailureKind = result.FailureKind,
            Message = result.Message,
        };
    }

    private McpManagementResult RecordFailure(
        ProfileRuntime state,
        McpManagementAction action,
        McpManagementFailureKind failure,
        string message)
    {
        state.LastTransitionAt = _timeProvider.GetUtcNow();
        state.LastFailure = failure;
        state.LastOutcome = NormalizeFailureMessage(message);
        return Failure(action, failure, state.LastOutcome);
    }

    private string NormalizeFailureMessage(string message)
    {
        return Bound(_sanitizer.Sanitize(message), MaximumFailureCharacters);
    }

    private static McpManagementResult Success(McpManagementAction action, string message)
    {
        return new McpManagementResult
        {
            Action = action,
            Succeeded = true,
            ExitCode = 0,
            FailureKind = McpManagementFailureKind.None,
            Message = message,
        };
    }

    private static McpManagementResult Failure(
        McpManagementAction action,
        McpManagementFailureKind failure,
        string message)
    {
        return new McpManagementResult
        {
            Action = action,
            Succeeded = false,
            ExitCode = ExitCode(failure),
            FailureKind = failure,
            Message = message,
        };
    }

    private static int ExitCode(McpManagementFailureKind failure)
    {
        return failure switch
        {
            McpManagementFailureKind.None => 0,
            McpManagementFailureKind.NotFound => 2,
            McpManagementFailureKind.PolicyDenied or McpManagementFailureKind.Ineligible => 3,
            McpManagementFailureKind.AuthenticationRequired
                or McpManagementFailureKind.UnsupportedAuthentication
                or McpManagementFailureKind.RevocationUnsupported
                or McpManagementFailureKind.RemoteRevocationUnconfirmed => 4,
            McpManagementFailureKind.Timeout => 124,
            McpManagementFailureKind.Cancelled => 130,
            _ => 1,
        };
    }

    private static McpManagementFailureKind Classify(Exception exception)
    {
        return exception switch
        {
            KeyNotFoundException => McpManagementFailureKind.NotFound,
            TimeoutException => McpManagementFailureKind.Timeout,
            UnauthorizedAccessException => McpManagementFailureKind.PolicyDenied,
            InvalidOperationException when IsDiscoveryFailure(exception.Message)
                => McpManagementFailureKind.DiscoveryFailed,
            InvalidOperationException when exception.Message.Contains("schema", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("capability", StringComparison.OrdinalIgnoreCase)
                => McpManagementFailureKind.InvalidCapability,
            InvalidOperationException when exception.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                => McpManagementFailureKind.RegistryConflict,
            _ => McpManagementFailureKind.Failed,
        };
    }

    private static McpManagementFailureKind ClassifyConnectionFailure(string? error)
    {
        if (error?.Contains("conflict", StringComparison.OrdinalIgnoreCase) is true)
        {
            return McpManagementFailureKind.RegistryConflict;
        }

        if (IsDiscoveryFailure(error))
        {
            return McpManagementFailureKind.DiscoveryFailed;
        }

        if (error?.Contains("cancel", StringComparison.OrdinalIgnoreCase) is true
            || error?.Contains("timeout", StringComparison.OrdinalIgnoreCase) is true
            || error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) is true)
        {
            return McpManagementFailureKind.Timeout;
        }

        if (error?.Contains("OAuth", StringComparison.OrdinalIgnoreCase) is true
            || error?.Contains("auth", StringComparison.OrdinalIgnoreCase) is true)
        {
            return McpManagementFailureKind.AuthenticationRequired;
        }

        return McpManagementFailureKind.HandshakeFailed;
    }

    private static bool IsDiscoveryFailure(string? message)
    {
        return message?.Contains("advertis", StringComparison.OrdinalIgnoreCase) is true
                || message?.Contains("discovery", StringComparison.OrdinalIgnoreCase) is true
                || message?.Contains("duplicate normalized", StringComparison.OrdinalIgnoreCase) is true
                || message?.Contains("resource template", StringComparison.OrdinalIgnoreCase) is true
                || message?.Contains("resource URI", StringComparison.OrdinalIgnoreCase) is true
                || message?.Contains("prompt declares", StringComparison.OrdinalIgnoreCase) is true;
    }

    private static McpManagedCapabilityKind MapKind(McpCapabilityKind kind)
    {
        return kind switch
        {
            McpCapabilityKind.Tool => McpManagedCapabilityKind.Tool,
            McpCapabilityKind.Resource => McpManagedCapabilityKind.Resource,
            McpCapabilityKind.ResourceTemplate => McpManagedCapabilityKind.ResourceTemplate,
            McpCapabilityKind.Prompt => McpManagedCapabilityKind.Prompt,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private McpExternalContent MapContent(McpTransportContentItem content)
    {
        const int maximumTextCharacters = 256 * 1024;
        string sanitizedText = _sanitizer.Sanitize(content.Text);
        return new McpExternalContent
        {
            Label = Bound(_sanitizer.Sanitize(content.Label), 1024),
            Text = Bound(sanitizedText, maximumTextCharacters),
            MimeType = content.MimeType is null
                ? null
                : Bound(_sanitizer.Sanitize(content.MimeType), 256),
            IsTruncated = content.IsTruncated || sanitizedText.Length > maximumTextCharacters,
        };
    }

    private static void ValidateProfile(McpConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id)
            || profile.Id.Length > 128
            || profile.Id.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_' and not '.'))
        {
            throw new InvalidOperationException("MCP profile ids must use at most 128 ASCII letters, digits, dots, underscores, or hyphens.");
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 256)
        {
            throw new InvalidOperationException($"MCP profile '{profile.Id}' has an invalid display name.");
        }

        if (string.IsNullOrWhiteSpace(profile.Command) || profile.Command.Length > 4096)
        {
            throw new InvalidOperationException($"MCP profile '{profile.Id}' has an invalid command or endpoint.");
        }

        if (profile.StartupTimeout <= TimeSpan.Zero
            || profile.RequestTimeout <= TimeSpan.Zero
            || profile.DrainKillTimeout <= TimeSpan.Zero
            || profile.StartupTimeout > TimeSpan.FromMinutes(30)
            || profile.RequestTimeout > TimeSpan.FromMinutes(30)
            || profile.DrainKillTimeout > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException($"MCP profile '{profile.Id}' has a timeout outside host bounds.");
        }

        if (profile.Arguments.Count > 64
            || profile.Arguments.Any(argument => argument.Length > 8192)
            || profile.Environment.Count > 64
            || profile.Environment.Any(pair => pair.Key.Length is 0 or > 128
                || pair.Key.Any(char.IsControl)
                || pair.Value.Length > 32 * 1024)
            || profile.Headers.Count > 64
            || profile.SecretScope.Count > 64
            || profile.SecretScope.Any(reference => reference.Length > 512)
            || profile.WorkingDirectory?.Length > 4096
            || profile.AllowedCapabilities.Count > Enum.GetValues<McpCapabilityKind>().Length
            || profile.AllowedCapabilities.Any(kind => !Enum.IsDefined(kind))
            || profile.OAuth?.Scopes.Count > 64
            || profile.OAuth?.Scopes.Any(scope => scope.Length is 0 or > 256) is true
            || profile.OAuth?.ClientId?.Length > 1024)
        {
            throw new InvalidOperationException($"MCP profile '{profile.Id}' exceeds host configuration bounds.");
        }
    }

    private static void ValidateRequest(McpManagementRequest request)
    {
        if (request.MaximumCount is < 1 or > MaximumCapabilities)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"MCP maximumCount must be between 1 and {MaximumCapabilities}.");
        }

        if (request.Arguments.Count > MaximumArguments
            || request.Arguments.Any(pair => pair.Key.Length is 0 or > 128
                || pair.Value.Length > MaximumArgumentCharacters))
        {
            throw new ArgumentException("MCP arguments exceed host bounds.", nameof(request));
        }
    }

    private static bool AreResourceTemplateArgumentsDeclared(
        string? template,
        IEnumerable<string> argumentNames)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return false;
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        int searchIndex = 0;
        while (searchIndex < template.Length)
        {
            int opening = template.IndexOf('{', searchIndex);
            if (opening < 0)
            {
                break;
            }

            int closing = template.IndexOf('}', opening + 1);
            if (closing < 0)
            {
                return false;
            }

            string expression = template[(opening + 1)..closing];
            if (expression.Length > 0 && expression[0] is '+' or '#' or '.' or '/' or ';' or '?' or '&')
            {
                expression = expression[1..];
            }

            foreach (string variable in expression.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string name = variable.Split(':', 2)[0].TrimEnd('*');
                if (name.Length > 0)
                {
                    declared.Add(name);
                }
            }

            searchIndex = closing + 1;
        }

        return argumentNames.All(declared.Contains);
    }

    private static void ValidatePromptArguments(
        McpImportedCapability capability,
        IReadOnlyDictionary<string, string> arguments)
    {
        var declared = capability.PromptArguments.ToDictionary(argument => argument.Name, StringComparer.Ordinal);
        if (arguments.Keys.Any(key => !declared.ContainsKey(key)))
        {
            throw new InvalidOperationException("An MCP prompt argument was not declared by the active capability.");
        }

        if (declared.Values.Any(argument => argument.Required && !arguments.ContainsKey(argument.Name)))
        {
            throw new InvalidOperationException("A required MCP prompt argument is missing.");
        }
    }

    private static void EnsureGeneration(ProfileRuntime state, long generation)
    {
        if (state.Generation != generation)
        {
            throw new InvalidOperationException("The MCP connection generation changed while the operation was active.");
        }
    }

    private static bool IsEndpointEligible(McpConnectionProfile profile)
    {
        if (profile.Transport == McpTransport.Stdio)
        {
            return !string.IsNullOrWhiteSpace(profile.Command)
                && !Path.IsPathFullyQualified(profile.Command)
                && !profile.Command.Contains('/')
                && !profile.Command.Contains('\\');
        }

        return Uri.TryCreate(profile.Command, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(endpoint.UserInfo);
    }

    private string EndpointIdentity(McpConnectionProfile profile)
    {
        if (profile.Transport == McpTransport.Stdio)
        {
            return Bound(_sanitizer.Sanitize(Path.GetFileName(profile.Command)), 256);
        }

        return Uri.TryCreate(profile.Command, UriKind.Absolute, out var endpoint)
            ? endpoint.GetLeftPart(UriPartial.Authority)
            : "invalid-endpoint";
    }

    private static string Bound(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static long? ToElapsedMilliseconds(TimeSpan elapsed)
    {
        return elapsed < TimeSpan.Zero ? null : elapsed.Ticks / TimeSpan.TicksPerMillisecond;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class ProfileRuntime
    {
        private readonly Lock _latencyGate = new();
        private readonly Dictionary<string, Queue<long>> _latencies = new(StringComparer.Ordinal);

        internal ProfileRuntime(McpConnectionProfile profile)
        {
            Profile = profile;
        }

        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal Lock CapabilitySnapshotGate { get; } = new();

        internal McpConnectionProfile Profile { get; }

        internal IReadOnlyList<McpImportedCapability> Capabilities { get; set; } = [];

        internal long Generation { get; set; }

        internal McpManagementFailureKind LastFailure { get; set; }

        internal string? LastOutcome { get; set; }

        internal DateTimeOffset? LastTransitionAt { get; set; }

        internal McpConnectionState ProjectedState { get; set; } = McpConnectionState.Disconnected;

        internal long? StartupDurationMilliseconds { get; set; }

        internal void AddLatency(string measurement, long? milliseconds)
        {
            if (milliseconds is null)
            {
                return;
            }

            lock (_latencyGate)
            {
                if (!_latencies.TryGetValue(measurement, out var samples))
                {
                    samples = new Queue<long>();
                    _latencies.Add(measurement, samples);
                }

                samples.Enqueue(milliseconds.Value);
                while (samples.Count > MaximumRecentLatencySamples)
                {
                    _ = samples.Dequeue();
                }
            }
        }

        internal IReadOnlyList<McpLatencySummary> GetLatencySummaries()
        {
            lock (_latencyGate)
            {
                var summaries = new List<McpLatencySummary>();
                if (StartupDurationMilliseconds is { } startup)
                {
                    summaries.Add(new McpLatencySummary
                    {
                        Measurement = "startup-and-discovery",
                        SampleCount = 1,
                        MinimumMilliseconds = startup,
                        MaximumMilliseconds = startup,
                        MeanMilliseconds = startup,
                    });
                }

                summaries.AddRange(_latencies
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new McpLatencySummary
                    {
                        Measurement = pair.Key,
                        SampleCount = pair.Value.Count,
                        MinimumMilliseconds = pair.Value.Count == 0 ? null : pair.Value.Min(),
                        MaximumMilliseconds = pair.Value.Count == 0 ? null : pair.Value.Max(),
                        MeanMilliseconds = pair.Value.Count == 0 ? null : pair.Value.Average(),
                    }));
                return summaries;
            }
        }
    }
}
