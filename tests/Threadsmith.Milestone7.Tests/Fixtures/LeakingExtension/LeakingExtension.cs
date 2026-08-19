namespace Threadsmith.Tests.Fixtures.Leaking;

using System.Runtime.CompilerServices;
using Threadsmith.Extensions.Abstractions;

/// <summary>
/// Fixture extension that deliberately leaks: it subscribes a handler to a static event in the
/// default load context (<see cref="AppDomain.ProcessExit"/>) and never detaches it, retaining a
/// strong reference to the extension's collectible context so unload verification reports
/// <c>UnloadBlocked</c> (strategy §17.18, §17.19; plan-17 §26.5 mandatory leak fixture).
/// </summary>
public sealed class LeakingExtension : IThreadsmithExtension
{
    private static int _hookCount;

    /// <inheritdoc />
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "threadsmith.tests.leaking",
        Name = "Leaking Fixture",
        Version = "1.0.0",
    };

    /// <summary>The number of leaking hooks installed across all instances.</summary>
    public static int HookCount => _hookCount;

    /// <inheritdoc />
    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken)
    {
        // The handler captures an extension-owned object, so the static event in the default
        // context retains the extension's collectible load context after Unload() is called.
        var sink = new LeakSink();
        AppDomain.CurrentDomain.ProcessExit += sink.OnProcessExit;
        Interlocked.Increment(ref _hookCount);
        return default;
    }

    /// <inheritdoc />
    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
        => default;

    private sealed class LeakSink
    {
        internal void OnProcessExit(object? sender, EventArgs e)
        {
            // Intentionally captures this instance via the delegate, pinning the extension context.
            _ = sender;
            _ = e;
        }
    }
}