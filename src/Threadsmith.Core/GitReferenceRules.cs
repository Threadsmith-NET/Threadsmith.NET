namespace Threadsmith.Core;

/// <summary>Shared validation for operations accepting literal branch or object names, without revision expressions.</summary>
public static class GitReferenceRules
{
    /// <summary>Rejects refspec injection and non-literal revision syntax before Git execution.</summary>
    public static void ValidateLiteralReference(
        string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('-') || reference.Any(
            char.IsControl)
            || reference.Contains(':') || reference.Contains('\\') || reference.Contains(
                "..",
                StringComparison.Ordinal)
            || reference.Contains("@{", StringComparison.Ordinal) || reference.IndexOfAny([' ', '~', '^', '?', '*', '[']) >= 0)
        {
            throw new InvalidDataException("A literal valid branch or revision reference is required.");
        }
    }
}
