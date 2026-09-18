namespace Threadsmith.Tools.Jira;

using Microsoft.Extensions.Configuration;

/// <summary>Basic Jira authentication using an Atlassian account email and API token reference.</summary>
public sealed record JiraAuthenticationOptions
{
    /// <summary>Authentication mode. The initial adapter accepts only basic.</summary>
    public string Mode { get; init; } = "basic";

    /// <summary>Atlassian account email paired with the API token.</summary>
    public required string Username { get; init; }

    /// <summary>Logical token reference resolved at request time.</summary>
    public required string SecretReference { get; init; }
}

/// <summary>Trusted Jira Cloud account binding.</summary>
public sealed record JiraProviderOptions
{
    /// <summary>Compiled adapter discriminator.</summary>
    public string Type { get; init; } = "jiraCloud";

    /// <summary>Whether this account is eligible for selection.</summary>
    public bool Enabled { get; init; }

    /// <summary>Canonical Jira Cloud browse origin.</summary>
    public required string SiteUrl { get; init; }

    /// <summary>Optional exact browse-only host aliases mapped to this account.</summary>
    public IReadOnlyList<string> BrowseHostAliases { get; init; } = [];

    /// <summary>site for unscoped tokens or scopedGateway for tokens with scopes.</summary>
    public required string EndpointMode { get; init; }

    /// <summary>Trusted tenant UUID required by scopedGateway.</summary>
    public string? CloudId { get; init; }

    /// <summary>Logical authentication binding.</summary>
    public required JiraAuthenticationOptions Authentication { get; init; }
}

/// <summary>Jira acquisition bounds and fixed trusted accounts.</summary>
public sealed record JiraOptions
{
    /// <summary>Maximum configured profiles, including disabled profiles.</summary>
    public const int MaximumProviders = 16;

    /// <summary>Maximum browse aliases per profile.</summary>
    public const int MaximumAliasesPerProvider = 4;

    /// <summary>Maximum aggregate provider-description bytes.</summary>
    public const int MaximumProviderDescriptionBytes = 8 * 1024;

    /// <summary>Stable trusted account IDs.</summary>
    public IReadOnlyDictionary<string, JiraProviderOptions> Providers { get; init; } =
        new Dictionary<string, JiraProviderOptions>();

    /// <summary>Maximum decompressed HTTP response bytes.</summary>
    public int MaximumResponseBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Maximum retained UTF-8 body bytes before result framing.</summary>
    public int MaximumBodyBytes { get; init; } = 64 * 1024;

    /// <summary>Total active invocation timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Reads trusted account bindings and repository-narrowable Jira limits.</summary>
    public static JiraOptions FromConfiguration(IConfiguration configuration, IConfiguration trustedConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(trustedConfiguration);
        var providerSections = trustedConfiguration.GetSection("tools:jira:providers").GetChildren().ToArray();
        if (providerSections.Length > MaximumProviders)
        {
            throw new InvalidOperationException($"At most {MaximumProviders} Jira providers may be configured.");
        }

        var providers = new Dictionary<string, JiraProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in providerSections)
        {
            ValidateProviderId(section.Key);
            var provider = section.Get<JiraProviderOptions>(binder => binder.ErrorOnUnknownConfiguration = true)
                ?? throw new InvalidOperationException("Jira provider configuration is empty.");
            var enabled = provider.Enabled
                && configuration.GetValue($"tools:jira:providers:{section.Key}:enabled", provider.Enabled);
            providers.Add(section.Key, provider with { Enabled = enabled });
        }

        var defaults = new JiraOptions();
        var options = defaults with
        {
            Providers = providers,
            MaximumResponseBytes = ReadPositiveLimit(
                configuration,
                trustedConfiguration,
                "maximumResponseBytes",
                defaults.MaximumResponseBytes),
            MaximumBodyBytes = ReadPositiveLimit(
                configuration,
                trustedConfiguration,
                "maximumBodyBytes",
                defaults.MaximumBodyBytes),
            TimeoutSeconds = ReadPositiveLimit(
                configuration,
                trustedConfiguration,
                "timeoutSeconds",
                defaults.TimeoutSeconds),
        };
        Validate(options);
        return options;
    }

    /// <summary>Validates constructed options before any account is advertised or used.</summary>
    public static void Validate(JiraOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Providers.Count > MaximumProviders)
        {
            throw new InvalidOperationException($"At most {MaximumProviders} Jira providers may be configured.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumResponseBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumBodyBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumResponseBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumBodyBytes, options.MaximumResponseBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.TimeoutSeconds, 86400);
        foreach (var pair in options.Providers)
        {
            ValidateProviderId(pair.Key);
            ValidateProvider(pair.Value);
        }
    }

    private static int ReadPositiveLimit(
        IConfiguration configuration,
        IConfiguration trustedConfiguration,
        string name,
        int fallback)
    {
        var key = $"tools:jira:{name}";
        var trusted = trustedConfiguration.GetValue(key, fallback);
        var effective = configuration.GetValue(key, trusted);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trusted, key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(effective, key);
        return Math.Min(trusted, effective);
    }

    private static void ValidateProviderId(string id)
    {
        if (id.Length is 0 or > 80
            || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidOperationException(
                "Jira provider IDs must contain at most 80 ASCII letters, digits, hyphens or underscores.");
        }
    }

    private static void ValidateProvider(JiraProviderOptions provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!string.Equals(provider.Type, "jiraCloud", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Jira provider type must be jiraCloud.");
        }

        var site = JiraAddress.ValidateSiteUrl(provider.SiteUrl);
        if (provider.SiteUrl.Length > 261)
        {
            throw new InvalidOperationException("Jira site URLs may contain at most 261 characters.");
        }

        if (provider.BrowseHostAliases is null
            || provider.BrowseHostAliases.Count > MaximumAliasesPerProvider)
        {
            throw new InvalidOperationException(
                $"A Jira provider may contain at most {MaximumAliasesPerProvider} browse host aliases.");
        }

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in provider.BrowseHostAliases)
        {
            var normalized = JiraAddress.ValidateBrowseHost(alias);
            if (normalized.Equals(site.IdnHost, StringComparison.OrdinalIgnoreCase) || !aliases.Add(normalized))
            {
                throw new InvalidOperationException("Jira browse hosts and aliases must be unique within a provider.");
            }
        }

        if (provider.EndpointMode == "scopedGateway")
        {
            if (!Guid.TryParseExact(provider.CloudId, "D", out _))
            {
                throw new InvalidOperationException("Jira scopedGateway providers require a canonical cloudId UUID.");
            }
        }
        else if (provider.EndpointMode == "site")
        {
            if (provider.CloudId is not null)
            {
                throw new InvalidOperationException("Jira site providers must omit cloudId.");
            }
        }
        else
        {
            throw new InvalidOperationException("Jira endpointMode must be site or scopedGateway.");
        }

        if (provider.Authentication is not { } authentication
            || !string.Equals(authentication.Mode, "basic", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(authentication.Username)
            || authentication.Username.Length > 254
            || authentication.Username.IndexOf('@', StringComparison.Ordinal) is <= 0
            || authentication.Username.LastIndexOf('@') == authentication.Username.Length - 1
            || authentication.Username.Contains(':', StringComparison.Ordinal)
            || authentication.Username.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || string.IsNullOrWhiteSpace(authentication.SecretReference)
            || authentication.SecretReference.Length > 512
            || !SecretReference.TryParse(authentication.SecretReference, out _))
        {
            throw new InvalidOperationException(
                "Jira authentication requires basic mode, a bounded Atlassian account email and a valid logical secret reference.");
        }
    }
}

/// <summary>Shared strict Jira URL and DNS-name validation.</summary>
internal static class JiraAddress
{
    /// <summary>Validates and returns a canonical standard Jira Cloud origin.</summary>
    internal static Uri ValidateSiteUrl(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException("Jira siteUrl must be an HTTPS origin with no path, query or fragment.");
        }

        const string suffix = ".atlassian.net";
        var host = uri.IdnHost;
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || host[..^suffix.Length].Length == 0
            || host[..^suffix.Length].Contains('.', StringComparison.Ordinal)
            || Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException("Jira siteUrl must identify one standard atlassian.net tenant.");
        }

        return uri;
    }

    /// <summary>Validates one exact browse-only DNS host alias.</summary>
    internal static string ValidateBrowseHost(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 253
            || value.EndsWith(".", StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal)
            || value.Contains('/', StringComparison.Ordinal)
            || !value.Contains('.', StringComparison.Ordinal)
            || Uri.CheckHostName(value) != UriHostNameType.Dns
            || !Uri.TryCreate($"https://{value}/", UriKind.Absolute, out var uri)
            || !uri.IsDefaultPort)
        {
            throw new InvalidOperationException("Jira browse aliases must be exact bounded public-style DNS hostnames.");
        }

        return uri.IdnHost.ToLowerInvariant();
    }
}
