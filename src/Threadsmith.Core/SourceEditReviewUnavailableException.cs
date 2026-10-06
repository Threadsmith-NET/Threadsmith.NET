namespace Threadsmith.Core;

/// <summary>The queued exact-edit review was cancelled, completed, or superseded before it could be displayed.</summary>
public sealed class SourceEditReviewUnavailableException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="SourceEditReviewUnavailableException"/> class.</summary>
    public SourceEditReviewUnavailableException()
        : base("This edit no longer has a pending review.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SourceEditReviewUnavailableException"/> class.</summary>
    public SourceEditReviewUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SourceEditReviewUnavailableException"/> class.</summary>
    public SourceEditReviewUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
