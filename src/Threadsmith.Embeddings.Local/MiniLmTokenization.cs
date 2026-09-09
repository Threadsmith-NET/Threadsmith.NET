namespace Threadsmith.Embeddings.Local;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

/// <summary>Preserves BERT whitespace, supplementary symbols, and exact special-token behavior.</summary>
/// <remarks>The package defaults drop symbols and control whitespace; the pinned model reference retains them.</remarks>
internal sealed partial class MiniLmNormalizer : Normalizer
{
    /// <inheritdoc />
    public override string Normalize(string original)
    {
        var result = new StringBuilder(original.Length);
        foreach (var part in SpecialTokens().Split(original))
        {
            if (part is "[CLS]" or "[SEP]" or "[MASK]" or "[PAD]" or "[UNK]")
            {
                result.Append(' ').Append(part).Append(' ');
                continue;
            }

            foreach (var rune in part.Normalize(NormalizationForm.FormD).EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                if (rune.Value is '\t' or '\r' or '\n' || category == UnicodeCategory.SpaceSeparator)
                {
                    result.Append(' ');
                }
                else if (rune.Value is not (0 or 0xFFFD) && category is not (UnicodeCategory.Control
                    or UnicodeCategory.Format or UnicodeCategory.NonSpacingMark))
                {
                    var cjk = rune.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
                        or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2A6DF
                        or >= 0x2A700 and <= 0x2B73F or >= 0x2B740 and <= 0x2B81F
                        or >= 0x2B820 and <= 0x2CEAF or >= 0x2F800 and <= 0x2FA1F;
                    if (cjk)
                    {
                        result.Append(' ');
                    }

                    result.Append(Rune.ToLowerInvariant(rune));
                    if (cjk)
                    {
                        result.Append(' ');
                    }
                }
            }
        }

        return result.ToString();
    }

    /// <inheritdoc />
    public override string Normalize(ReadOnlySpan<char> original) => Normalize(original.ToString());

    [GeneratedRegex(@"(\[CLS\]|\[SEP\]|\[MASK\]|\[PAD\]|\[UNK\])", RegexOptions.CultureInvariant)]
    private static partial Regex SpecialTokens();
}

/// <summary>Splits BERT punctuation without dropping symbols or supplementary Unicode characters.</summary>
internal sealed partial class MiniLmPreTokenizer : PreTokenizer
{
    /// <inheritdoc />
    public override IEnumerable<(int Offset, int Length)> PreTokenize(string text) =>
        Tokens().Matches(text).Select(match => (match.Index, match.Length));

    /// <inheritdoc />
    public override IEnumerable<(int Offset, int Length)> PreTokenize(ReadOnlySpan<char> text) => PreTokenize(text.ToString());

    [GeneratedRegex(@"\[CLS\]|\[SEP\]|\[MASK\]|\[PAD\]|\[UNK\]|[\p{P}\x21-\x2F\x3A-\x40\x5B-\x60\x7B-\x7E]|[^\s\p{P}\x21-\x2F\x3A-\x40\x5B-\x60\x7B-\x7E]+", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
}
