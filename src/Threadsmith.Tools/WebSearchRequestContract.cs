namespace Threadsmith.Tools;

using System.Text.RegularExpressions;

/// <summary>Shared argument bounds and locale normalization for search binding and transport.</summary>
internal static class WebSearchRequestContract
{
    /// <summary>Maximum number of characters disclosed in one query.</summary>
    internal const int MaximumQueryCharacters = 600;

    /// <summary>Maximum number of whitespace-delimited words accepted by the provider.</summary>
    internal const int MaximumQueryWords = 75;

    /// <summary>Maximum number of requested results.</summary>
    internal const int MaximumResults = 20;

    /// <summary>Maximum supported freshness window in days.</summary>
    internal const int MaximumFreshnessDays = 365;

    /// <summary>Supported locale shapes; language support is validated separately.</summary>
    internal const string LocalePattern = "^(?:[A-Za-z]{2,3}(?:-[A-Za-z]{2})?|[Zz][Hh]-[Hh][Aa][Nn][SsTt])$";

    private static readonly Regex _locale = new(LocalePattern, RegexOptions.CultureInvariant);

    // Brave's search-language values differ from regional locale tags. Keep the provider
    // enumeration at this boundary rather than forwarding arbitrary model strings to HTTP.
    private static readonly HashSet<string> _languages = new(
        "ar eu bn bg ca zh-hans zh-hant hr cs da nl en en-gb et fi fr gl de el gu he hi hu is it ja jp kn ko lv lt ms ml mr nb pl pt-br pt-pt pa ro ru sr sk sl es sv ta te th tr uk vi".Split(' '),
        StringComparer.Ordinal);

    private static readonly HashSet<string> _countries = new(
        "ar au at be br ca cl dk fi fr de gr hk in id it jp kr my mx nl nz no cn pl pt ph ru sa za es se ch tw tr gb us".Split(' '),
        StringComparer.Ordinal);

    /// <summary>Rejects invalid input before credentials or network access at either entry point.</summary>
    internal static void Validate(WebSearchRequest input, int maximumFreshnessDays = MaximumFreshnessDays, int maximumQueryCharacters = 500)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Query)
            || input.Query.Length > Math.Min(maximumQueryCharacters, MaximumQueryCharacters)
            || input.Query.Any(char.IsControl))
        {
            throw new ToolArgumentValidationException($"query must contain 1 through {maximumQueryCharacters} plain-text characters, with no control characters.");
        }

        if (input.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > MaximumQueryWords)
        {
            throw new ToolArgumentValidationException("query must contain at most 75 whitespace-delimited words.");
        }

        if (input.MaximumResults is < 1 or > MaximumResults)
        {
            throw new ToolArgumentValidationException("maximumResults must be an integer from 1 through 20; omit it to use 5.");
        }

        if (input.FreshnessDays < 1 || input.FreshnessDays > maximumFreshnessDays)
        {
            throw new ToolArgumentValidationException("freshnessDays must be an integer within the configured range; omit it for no freshness filter.");
        }

        if (input.Locale is not null)
        {
            ResolveLocale(input.Locale);
        }
    }

    /// <summary>Maps language/region hints to a supported search language and optional supported country.</summary>
    internal static (string Language, string? Country) ResolveLocale(string locale)
    {
        if (locale.Length > 10 || !_locale.IsMatch(locale))
        {
            throw new ToolArgumentValidationException("locale must be a supported language or language-region hint, such as en, en-US, pt-BR, or zh-Hant; omit it for the provider default.");
        }

        var normalized = locale.ToLowerInvariant();
        var parts = normalized.Split('-');
        var language = _languages.Contains(normalized) ? normalized : parts[0] switch
        {
            "zh" => parts.Length == 2 && parts[1] is "tw" or "hk" ? "zh-hant" : "zh-hans",
            "pt" => "pt-pt",
            "no" => "nb",
            _ => parts[0],
        };
        if (!_languages.Contains(language))
        {
            throw new ToolArgumentValidationException("locale specifies a search language unsupported by the provider; use a supported language such as en or omit locale.");
        }

        var country = parts.Length == 2 && _countries.Contains(parts[1])
            ? parts[1].ToUpperInvariant()
            : null;
        return (language, country);
    }
}
