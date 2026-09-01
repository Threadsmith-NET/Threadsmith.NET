namespace Threadsmith.Execution;

/// <summary>Signals bounded scheduler admission exhaustion.</summary>
internal sealed class AgentQueueCapacityException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="AgentQueueCapacityException"/> class.</summary>
    public AgentQueueCapacityException()
        : base("The bounded agent queue is full.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AgentQueueCapacityException"/> class.</summary>
    public AgentQueueCapacityException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AgentQueueCapacityException"/> class.</summary>
    public AgentQueueCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
