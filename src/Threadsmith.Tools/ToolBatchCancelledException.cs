namespace Threadsmith.Tools;

/// <summary>Cancellation raised after a sibling batch has already produced one or more tool results.</summary>
public sealed class ToolBatchCancelledException : OperationCanceledException
{
    /// <summary>Initializes a new instance of the <see cref="ToolBatchCancelledException"/> class.</summary>
    public ToolBatchCancelledException()
        : base("The tool batch was cancelled.")
    {
        Results = [];
    }

    /// <summary>Initializes a new instance of the <see cref="ToolBatchCancelledException"/> class.</summary>
    /// <param name="cancellationToken">Cancellation token that requested interruption.</param>
    public ToolBatchCancelledException(CancellationToken cancellationToken)
        : base(cancellationToken)
    {
        Results = [];
    }

    /// <summary>Initializes a new instance of the <see cref="ToolBatchCancelledException"/> class.</summary>
    /// <param name="message">Exception message.</param>
    public ToolBatchCancelledException(string message)
        : base(message)
    {
        Results = [];
    }

    /// <summary>Initializes a new instance of the <see cref="ToolBatchCancelledException"/> class.</summary>
    /// <param name="message">Exception message.</param>
    /// <param name="cancellationToken">Cancellation token that requested interruption.</param>
    public ToolBatchCancelledException(string message, CancellationToken cancellationToken)
        : base(message, cancellationToken)
    {
        Results = [];
    }

    /// <summary>Initializes a new instance of the <see cref="ToolBatchCancelledException"/> class.</summary>
    /// <param name="message">Exception message.</param>
    /// <param name="innerException">Inner exception.</param>
    public ToolBatchCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
        Results = [];
    }

    /// <summary>Initializes a new instance of the <see cref="ToolBatchCancelledException"/> class.</summary>
    /// <param name="message">Exception message.</param>
    /// <param name="innerException">Inner exception.</param>
    /// <param name="cancellationToken">Cancellation token that requested interruption.</param>
    public ToolBatchCancelledException(string message, Exception innerException, CancellationToken cancellationToken)
        : base(message, innerException, cancellationToken)
    {
        Results = [];
    }

    /// <summary>Initializes a new instance of the <see cref="ToolBatchCancelledException"/> class.</summary>
    /// <param name="results">Completed tool results observed before cancellation was raised.</param>
    /// <param name="cancellationToken">Cancellation token that requested interruption.</param>
    public ToolBatchCancelledException(
        IReadOnlyList<ToolBatchResult> results,
        CancellationToken cancellationToken)
        : base("The tool batch was cancelled after one or more sibling results completed.", cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        Results = results;
    }

    /// <summary>Gets the completed tool results observed before cancellation was raised.</summary>
    public IReadOnlyList<ToolBatchResult> Results { get; }
}
