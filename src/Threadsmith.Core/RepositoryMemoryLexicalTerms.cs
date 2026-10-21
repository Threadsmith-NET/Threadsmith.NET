namespace Threadsmith.Core;

/// <summary>Recognizes explicit mixed-case code names without treating ordinary title-case prose as identifiers.</summary>
public static class RepositoryMemoryLexicalTerms
{
    /// <summary>Whether a token contains an internal lower-to-upper case boundary, such as FindSymbolAsync.</summary>
    public static bool IsCodeIdentifier(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        for (var index = 1; index < term.Length; index++)
        {
            if (char.IsLower(term[index - 1]) && char.IsUpper(term[index]))
            {
                return true;
            }
        }

        return false;
    }
}
