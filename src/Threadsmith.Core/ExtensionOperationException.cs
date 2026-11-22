namespace Threadsmith.Core;

/// <summary>Reports an extension operation failure whose message is safe to show to the operator.</summary>
/// <remarks>Consumers display the message only, not inner exception details or stack traces.</remarks>
public class ExtensionOperationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ExtensionOperationException"/> class.</summary>
    public ExtensionOperationException()
        : base("The extension operation failed.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionOperationException"/> class with a safe explanation.</summary>
    public ExtensionOperationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionOperationException"/> class with a safe explanation and diagnostic cause.</summary>
    public ExtensionOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
