namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Provides complete operator-only review details from the same input used for execution.</summary>
public interface IToolStatementReview
{
    /// <summary>Maximum length of the unabridged operator review in UTF-16 characters.</summary>
    const int MaximumCharacters = 8192;

    /// <summary>Returns null when this tool has no review contract. Throws if a complete safe review is unavailable.</summary>
    string? CreateReview(object input, IOutputSanitizer sanitizer);
}
