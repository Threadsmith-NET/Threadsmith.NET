namespace Threadsmith.Tests.Fixtures.CleanUnload;

using Threadsmith.Extensions.Abstractions;

/// <summary>A minimal extension with no capabilities, used only to verify clean ALC unload (plan-17 §17.19).</summary>
public sealed class CleanUnloadExtension : IThreadsmithExtension
{
    /// <inheritdoc />
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "threadsmith.tests.clean-unload",
        Name = "Clean Unload Fixture",
        Version = "1.0.0",
        ContractVersion = ExtensionContractVersion.Current,
        Capabilities = [],
        Permissions = [],
    };

    /// <inheritdoc />
    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return default;
    }

    /// <inheritdoc />
    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
        => default;
}