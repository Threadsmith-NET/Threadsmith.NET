namespace Threadsmith.Tests.Fixtures.BadContract;

using Threadsmith.Extensions.Abstractions;

/// <summary>Fixture extension that bundles a duplicate contract assembly (rejected at load).</summary>
public sealed class BadContractExtension : IThreadsmithExtension
{
    /// <inheritdoc />
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "threadsmith.tests.bad-contract",
        Name = "Bad Contract Fixture",
        Version = "1.0.0",
    };

    /// <inheritdoc />
    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken)
        => default;

    /// <inheritdoc />
    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
        => default;
}