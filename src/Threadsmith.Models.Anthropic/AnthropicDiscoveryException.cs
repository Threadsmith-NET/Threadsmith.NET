namespace Threadsmith.Models.Anthropic;

/// <summary>Safe metadata-acquisition failure without SDK or HTTP payload retention.</summary>
internal sealed class AnthropicDiscoveryException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AnthropicDiscoveryException"/> class.</summary>
    public AnthropicDiscoveryException()
        : this("Anthropic discovery failed.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AnthropicDiscoveryException"/> class.</summary>
    public AnthropicDiscoveryException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AnthropicDiscoveryException"/> class.</summary>
    public AnthropicDiscoveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AnthropicDiscoveryException"/> class with safe classification.</summary>
    public AnthropicDiscoveryException(string message, bool credentialRejected = false, bool transient = false)
        : base(message)
    {
        CredentialRejected = credentialRejected;
        Transient = transient;
    }

    /// <summary>Whether account access was explicitly rejected.</summary>
    public bool CredentialRejected { get; }

    /// <summary>Whether stale validated metadata may be used.</summary>
    public bool Transient { get; }
}
