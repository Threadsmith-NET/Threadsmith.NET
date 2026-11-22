namespace Threadsmith.Extensions.Abstractions;

/// <summary>Rejects activation with an explanation intended for the operator.</summary>
/// <remarks>
/// Throw from <see cref="IThreadsmithExtension.ActivateAsync"/> when extension-owned validation
/// fails. Messages must describe the issue and corrective action without credentials, configured
/// values, or other private details. The host may display and record the message.
/// </remarks>
public sealed class ExtensionValidationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ExtensionValidationException"/> class.</summary>
    public ExtensionValidationException()
        : base("Extension validation failed.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionValidationException"/> class with a safe explanation.</summary>
    public ExtensionValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionValidationException"/> class with a safe explanation and diagnostic cause.</summary>
    public ExtensionValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
