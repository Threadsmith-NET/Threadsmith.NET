namespace Threadsmith.Extensions.Neo4j;

/// <summary>Reports model-supplied arguments rejected before any database work began.</summary>
internal sealed class Neo4jArgumentException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="Neo4jArgumentException"/> class.</summary>
    public Neo4jArgumentException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Neo4jArgumentException"/> class.</summary>
    /// <param name="message">The safe rejection reason.</param>
    public Neo4jArgumentException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Neo4jArgumentException"/> class.</summary>
    /// <param name="message">The safe rejection reason.</param>
    /// <param name="innerException">The underlying failure.</param>
    public Neo4jArgumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
