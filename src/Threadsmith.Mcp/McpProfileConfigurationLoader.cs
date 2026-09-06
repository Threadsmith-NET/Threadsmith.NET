namespace Threadsmith.Mcp;

using Microsoft.Extensions.Configuration;

/// <summary>Loads MCP connection profiles from configuration (strategy §20.2, §21.2).</summary>
public static class McpProfileConfigurationLoader
{
    /// <summary>Loads the configured MCP connection profiles.</summary>
    /// <param name="configuration">The configuration root.</param>
    /// <returns>The ordered connection profiles.</returns>
    public static IReadOnlyList<McpConnectionProfile> Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var profiles = new List<McpConnectionProfile>();
        var section = configuration.GetSection("mcp:profiles");
        if (!section.Exists())
        {
            return profiles;
        }

        foreach (var child in section.GetChildren())
        {
            var id = child["id"];
            var name = child["name"];
            var command = child["command"];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
            {
                throw new InvalidOperationException(
                    $"MCP profile '{child.Key}' requires non-empty id, name, and command values.");
            }

            var profile = new McpConnectionProfile
            {
                Id = id,
                DisplayName = name,
                ConfigurationSource = GetConfigurationSource(configuration, $"{child.Path}:id"),
                Command = command,
                Transport = ParseEnum(child, "transport", McpTransport.Stdio),
                Trust = ParseEnum(child, "trust", McpTrustLevel.Untrusted),
                Arguments = child.GetSection("arguments").Get<string[]>() ?? [],
                SecretScope = child.GetSection("secretScope").Get<string[]>() ?? [],
                StartupTimeout = TimeSpan.FromSeconds(child.GetValue("startupTimeoutSeconds", 30)),
                RequestTimeout = TimeSpan.FromSeconds(child.GetValue("requestTimeoutSeconds", 60)),
                DrainKillTimeout = TimeSpan.FromSeconds(child.GetValue(
                    "drainKillTimeoutSeconds",
                    configuration.GetValue("mcp:defaultDrainKillTimeoutSeconds", 10))),
                AllowedCapabilities = MapAllowedCapabilities(child.GetSection("allowedCapabilities").Get<string[]>()),
                Environment = LoadMap(child.GetSection("environment"), StringComparer.Ordinal),
                Headers = LoadMap(child.GetSection("headers"), StringComparer.OrdinalIgnoreCase),
                OAuth = LoadOAuth(child),
                WorkingDirectory = child["workingDirectory"],
                AutoConnect = child.GetValue("autoConnect", false),
            };
            ValidateOAuth(profile);
            profiles.Add(profile);
        }

        return profiles;
    }

    private static string GetConfigurationSource(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return "trusted-host";
        }

        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out _))
            {
                continue;
            }

            var providerName = provider.GetType().Name;
            if (providerName.Contains("Environment", StringComparison.Ordinal))
            {
                return "trusted-environment";
            }

            if (providerName.Contains("Json", StringComparison.Ordinal))
            {
                return "trusted-json";
            }

            if (providerName.Contains("Memory", StringComparison.Ordinal))
            {
                return "trusted-memory";
            }

            return "trusted-host";
        }

        return "trusted-host";
    }

    private static void ValidateOAuth(McpConnectionProfile profile)
    {
        if (profile.OAuth?.Enabled is not true)
        {
            return;
        }

        if (profile.Transport == McpTransport.Stdio)
        {
            throw new InvalidOperationException($"MCP OAuth profile '{profile.Id}' requires an HTTP transport.");
        }

        if (profile.OAuth.ClientMetadataDocumentUri is not null
            && !string.IsNullOrWhiteSpace(profile.OAuth.ClientId))
        {
            throw new InvalidOperationException(
                $"MCP OAuth profile '{profile.Id}' cannot configure both clientId and clientMetadataDocumentUri.");
        }

        if (profile.OAuth.ClientMetadataDocumentUri is not null && profile.OAuth.RedirectPort == 0)
        {
            throw new InvalidOperationException(
                $"MCP OAuth profile '{profile.Id}' must configure a fixed redirectPort when using clientMetadataDocumentUri.");
        }

        if (string.IsNullOrWhiteSpace(profile.OAuth.ClientId)
            && !string.IsNullOrWhiteSpace(profile.OAuth.ClientSecret))
        {
            throw new InvalidOperationException(
                $"MCP OAuth profile '{profile.Id}' cannot configure clientSecret without clientId.");
        }

        if (profile.Headers.ContainsKey("Authorization"))
        {
            throw new InvalidOperationException(
                $"MCP OAuth profile '{profile.Id}' cannot also configure an Authorization header.");
        }
    }

    private static IReadOnlyList<McpCapabilityKind> MapAllowedCapabilities(string[]? names)
    {
        if (names is null || names.Length == 0)
        {
            return
            [
                McpCapabilityKind.Tool,
                McpCapabilityKind.Resource,
                McpCapabilityKind.ResourceTemplate,
                McpCapabilityKind.Prompt,
            ];
        }

        var kinds = new List<McpCapabilityKind>();
        foreach (var name in names)
        {
            McpCapabilityKind? kind = name.ToLowerInvariant() switch
            {
                "tool" or "tools" => McpCapabilityKind.Tool,
                "resource" or "resources" => McpCapabilityKind.Resource,
                "resource-template" or "resource-templates" => McpCapabilityKind.ResourceTemplate,
                "prompt" or "prompts" => McpCapabilityKind.Prompt,
                _ => null,
            };
            if (kind is not null)
            {
                kinds.Add(kind.Value);
                continue;
            }

            throw new InvalidOperationException($"Unknown MCP capability kind '{name}'.");
        }

        return kinds.Distinct().ToArray();
    }

    private static TEnum ParseEnum<TEnum>(IConfigurationSection section, string key, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (Enum.TryParse(value, ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Unknown MCP {key} value '{value}' in profile '{section["id"]}'.");
    }

    private static McpOAuthOptions? LoadOAuth(IConfigurationSection profile)
    {
        var section = profile.GetSection("oauth");
        if (!section.Exists())
        {
            return null;
        }

        var redirectPort = section.GetValue("redirectPort", 0);
        if (redirectPort is < 0 or > 65535)
        {
            throw new InvalidOperationException(
                $"MCP OAuth redirectPort in profile '{profile["id"]}' must be zero or a valid TCP port.");
        }

        var discoveryUrl = section["discoveryUrl"];
        if (discoveryUrl is not null)
        {
            throw new InvalidOperationException(
                $"MCP OAuth discoveryUrl is not supported for profile '{profile["id"]}'; "
                + "the authorization server must be advertised by the MCP endpoint.");
        }

        var clientMetadataDocumentUriText = section["clientMetadataDocumentUri"];
        Uri? clientMetadataDocumentUri = null;
        if (!string.IsNullOrWhiteSpace(clientMetadataDocumentUriText)
            && !McpOAuthClientMetadataDocumentUri.TryCreate(clientMetadataDocumentUriText, out clientMetadataDocumentUri))
        {
            throw new InvalidOperationException(
                $"MCP OAuth clientMetadataDocumentUri in profile '{profile["id"]}' "
                + McpOAuthClientMetadataDocumentUri.Requirements + ".");
        }

        var clientId = NormalizeOptional(section["clientId"]);
        var clientSecret = NormalizeOptional(section["clientSecret"]);
        if (clientSecret is not null && !clientSecret.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"MCP OAuth clientSecret in profile '{profile["id"]}' must be a logical secrets: reference.");
        }

        var secretScope = profile.GetSection("secretScope").Get<string[]>() ?? [];
        if (clientSecret is not null && !secretScope.Contains(clientSecret, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"MCP OAuth clientSecret in profile '{profile["id"]}' must appear in secretScope.");
        }

        return new McpOAuthOptions
        {
            Enabled = section.GetValue("enabled", false),
            Scopes = section.GetSection("scopes").Get<string[]>() ?? [],
            ClientId = clientId,
            ClientMetadataDocumentUri = clientMetadataDocumentUri,
            ClientSecret = clientSecret,
            RedirectPort = redirectPort,
            DiscoveryUrl = discoveryUrl,
        };
    }

    private static IReadOnlyDictionary<string, string> LoadMap(
        IConfigurationSection section,
        StringComparer comparer)
    {
        var values = new Dictionary<string, string>(comparer);
        if (!section.Exists())
        {
            return values;
        }

        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Key) && child.Value is string value)
            {
                values[child.Key] = value;
            }
        }

        return values;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
