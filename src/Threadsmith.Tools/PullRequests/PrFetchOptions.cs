namespace Threadsmith.Tools.PullRequests;

using Microsoft.Extensions.Configuration;

/// <summary>Trusted account configuration; credential values never enter ordinary configuration.</summary>
public sealed record PullRequestProviderOptions
{
    /// <summary>Compiled adapter discriminator.</summary>
    public required string Type { get; init; }

    /// <summary>Whether this account is eligible for selection.</summary>
    public bool Enabled { get; init; }

    /// <summary>URL globs for automatic account selection; empty uses the adapter defaults. An asterisk matches any characters.</summary>
    public IReadOnlyList<string> UrlPatterns { get; init; } = [];

    /// <summary>Adapter-supported authentication settings.</summary>
    public PullRequestAuthentication Authentication { get; init; } = new();
}

/// <summary>Authentication method and logical credential reference.</summary>
public sealed record PullRequestAuthentication
{
    /// <summary>none, bearer, or basic, subject to adapter validation.</summary>
    public string Mode { get; init; } = "none";

    /// <summary>Non-secret account name required for basic authentication.</summary>
    public string? Username { get; init; }

    /// <summary>Logical reference resolved only at the HTTP boundary.</summary>
    public string? SecretReference { get; init; }
}

/// <summary>PR acquisition bounds and configured accounts, fixed until host restart.</summary>
public sealed record PrFetchOptions
{
    /// <summary>Stable configured account IDs, independent of array position.</summary>
    public IReadOnlyDictionary<string, PullRequestProviderOptions> Providers { get; init; } = new Dictionary<string, PullRequestProviderOptions>();

    /// <summary>Maximum HTTP response bytes; zero disables this operational ceiling.</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Maximum retained UTF-8 evidence bytes per operation; zero disables this ceiling.</summary>
    public int MaximumCacheBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Maximum provider pages per PR acquisition; zero disables this ceiling.</summary>
    public int MaximumFilePages { get; init; } = 200;

    /// <summary>Active page acquisition timeout in seconds, excluding idle time between calls; zero disables it.</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>Character size of diff chunks, leaving space for escaped JSON and metadata.</summary>
    public int PageCharacters { get; init; } = 8192;

    /// <summary>Reads trusted account bindings and repository-narrowable operational limits.</summary>
    public static PrFetchOptions FromConfiguration(IConfiguration configuration, IConfiguration trustedConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(trustedConfiguration);
        var providers = new Dictionary<string, PullRequestProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in trustedConfiguration.GetSection("tools:prFetch:providers").GetChildren())
        {
            if (section.Key.Length > 80 || section.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            {
                throw new InvalidOperationException("PR provider IDs must contain at most 80 ASCII letters, digits, hyphens or underscores.");
            }

            var provider = section.Get<PullRequestProviderOptions>(binder => binder.ErrorOnUnknownConfiguration = true)
                ?? throw new InvalidOperationException("PR provider configuration is empty.");
            var enabled = provider.Enabled && configuration.GetValue($"tools:prFetch:providers:{section.Key}:enabled", provider.Enabled);
            providers.Add(section.Key, provider with { Enabled = enabled });
        }

        var defaults = new PrFetchOptions();
        var result = defaults with
        {
            Providers = providers,
            MaximumResponseBytes = ReadLimit("maximumResponseBytes", defaults.MaximumResponseBytes),
            MaximumCacheBytes = ReadLimit("maximumCacheBytes", defaults.MaximumCacheBytes),
            MaximumFilePages = ReadLimit("maximumFilePages", defaults.MaximumFilePages),
            TimeoutSeconds = ReadLimit("timeoutSeconds", defaults.TimeoutSeconds),
            PageCharacters = ReadLimit("pageCharacters", defaults.PageCharacters),
        };
        ArgumentOutOfRangeException.ThrowIfLessThan(result.PageCharacters, 256);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(result.PageCharacters, 16384);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(result.TimeoutSeconds, 86400);
        return result;

        int ReadLimit(string name, int fallback)
        {
            var key = $"tools:prFetch:{name}";
            var trusted = trustedConfiguration.GetValue(key, fallback);
            var effective = configuration.GetValue(key, trusted);
            ArgumentOutOfRangeException.ThrowIfNegative(trusted, key);
            ArgumentOutOfRangeException.ThrowIfNegative(effective, key);
            return trusted == 0 ? effective : effective == 0 ? trusted : Math.Min(trusted, effective);
        }
    }
}
