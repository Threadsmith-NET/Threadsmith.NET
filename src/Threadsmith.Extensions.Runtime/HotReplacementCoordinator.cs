namespace Threadsmith.Extensions.Runtime;

using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>
/// Coordinates hot replacement (strategy §17.20): loads a new generation in a fresh collectible ALC,
/// validates the metadata of its declared capabilities, atomically switches the capability registry
/// resolution to the new generation, then drains and unloads the prior generation. In-flight calls
/// complete on the old generation; new calls use the new generation.
/// </summary>
public sealed class HotReplacementCoordinator
{
    private readonly ExtensionHost _host;
    private readonly ILogger<HotReplacementCoordinator> _logger;

    /// <summary>Initializes a new instance of the <see cref="HotReplacementCoordinator"/> class.</summary>
    public HotReplacementCoordinator(
        ExtensionHost host,
        ICapabilityRegistry capabilities,
        ILogger<HotReplacementCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(logger);
        if (!ReferenceEquals(host.Capabilities, capabilities))
        {
            throw new ArgumentException(
                "The capability registry must belong to the extension host.",
                nameof(capabilities));
        }

        _host = host;
        _logger = logger;
    }

    /// <summary>
    /// Replaces <paramref name="current"/> with a new generation loaded from <paramref name="request"/>,
    /// validating the new generation's capability metadata before the atomic switch.
    /// </summary>
    /// <param name="current">The generation being retired.</param>
    /// <param name="request">The load request for the replacement.</param>
    /// <param name="sessionId">The session owning the operation.</param>
    /// <param name="cancellationToken">A token that cancels the replacement.</param>
    /// <returns>The activated new generation and the old generation's unload result.</returns>
    public async Task<(ExtensionGeneration NewGeneration, UnloadResult OldUnload)> ReplaceAsync(
        ExtensionGeneration current,
        ExtensionLoadRequest request,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);

        // Load, validate, and atomically publish the new generation as the explicitly authorized
        // successor. In-flight calls retain the old proxy while new calls resolve to the successor.
        var newGeneration = await _host.LoadReplacementAsync(
            current,
            request,
            cancellationToken);

        // Drain and unload the prior generation via the host (which also drops the host's strong
        //    reference to the retired generation).
        var oldUnload = await _host.UnloadAsync(current, sessionId, cancellationToken);
        if (oldUnload.Outcome == UnloadOutcome.UnloadBlocked)
        {
            _logger.LogWarning(
                "The retired generation {GenerationId} for extension {ExtensionId} did not unload; a restart may be necessary.",
                current.GenerationId,
                current.ExtensionId);
        }

        return (newGeneration, oldUnload);
    }
}
