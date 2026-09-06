namespace Threadsmith.Tests.Fixtures.Throwing;

using Threadsmith.Extensions.Abstractions;

/// <summary>Fixture extension whose activation throws, to prove the host stays operational.</summary>
public sealed class ThrowingExtension : IThreadsmithExtension
{
    /// <inheritdoc />
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "threadsmith.tests.throwing",
        Name = "Throwing Fixture",
        Version = "1.0.0",
    };

    /// <inheritdoc />
    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Deliberate activation failure for testing.");
    }

    /// <inheritdoc />
    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
        => default;
}