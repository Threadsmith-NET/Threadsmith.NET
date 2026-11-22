namespace Threadsmith.Extensions.Abstractions;

/// <summary>Resolves logical references declared for the current tool invocation.</summary>
public interface IExtensionSecretAccess
{
    /// <summary>Returns the admitted credential for immediate use by the extension.</summary>
    Task<ExtensionSecretValue> ResolveAsync(string reference, CancellationToken cancellationToken = default);
}

/// <summary>A credential that requires an explicit reveal and has no serializable value property.</summary>
public sealed class ExtensionSecretValue
{
    private readonly string _value;

    /// <summary>Initializes a new instance of the <see cref="ExtensionSecretValue"/> class.</summary>
    public ExtensionSecretValue(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    /// <summary>Reveals the credential for use at the service boundary.</summary>
    public string Reveal() => _value;

    /// <inheritdoc />
    public override string ToString() => "[secret]";
}
