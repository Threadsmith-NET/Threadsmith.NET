namespace Threadsmith.Models.OpenAiCodex;

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Discovers every model exposed by the authenticated Codex backend.</summary>
public sealed class OpenAiCodexCatalogClient
{
    private const int DefaultContextWindow = 128_000;
    private const int DefaultOutputReserve = 32_768;

    // The Codex backend filters `/models` rows by Codex client compatibility, not by
    // Threadsmith's product version. Upstream Codex model metadata reviewed for this
    // implementation currently requires up to 0.144.0.
    private const string CodexModelsClientCompatibilityVersion = "0.144.0";

    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new instance of the <see cref="OpenAiCodexCatalogClient"/> class.</summary>
    public OpenAiCodexCatalogClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>Fetches and projects the account's current Codex model catalog.</summary>
    public async Task<OpenAiCodexProviderConfiguration> DiscoverAsync(
        string accessToken,
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using HttpRequestMessage request = new(HttpMethod.Get, BuildModelsUri());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("originator", "threadsmith");
        var effectiveAccountId = accountId ?? OpenAiCodexTokenClaims.TryGetAccountId(accessToken);
        if (!string.IsNullOrWhiteSpace(effectiveAccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", effectiveAccountId);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Codex model discovery failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Codex model response does not contain a models array.");
        }

        var returnedCount = models.GetArrayLength();
        OpenAiCodexModelConfiguration[] discovered =
        [
            .. models.EnumerateArray()
                .Select(ProjectModel)
                .Where(model => model is not null)
                .Select(model => model!)
                .DistinctBy(model => model.ModelId, StringComparer.Ordinal),
        ];
        if (returnedCount is 0 or > 256)
        {
            throw new InvalidDataException("The authenticated Codex account returned an invalid model count.");
        }

        if (discovered.Length == 0)
        {
            throw new InvalidDataException("The Codex model response did not contain any usable model identifiers.");
        }

        return new OpenAiCodexProviderConfiguration
        {
            Id = "openai-codex",
            Name = "OpenAI Codex",
            Enabled = true,
            SecretKeyReference = OpenAiCodexProviderRegistration.OAuthSecretReference,
            Models = discovered,
        };
    }

    private static Uri BuildModelsUri()
    {
        return new Uri(
            $"{OpenAiCodexProviderRegistration.ModelsEndpoint}?client_version={Uri.EscapeDataString(CodexModelsClientCompatibilityVersion)}");
    }

    private static OpenAiCodexModelConfiguration? ProjectModel(JsonElement element)
    {
        var slug = GetString(element, "slug");
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 256)
        {
            return null;
        }

        var name = GetString(element, "display_name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
        {
            name = slug;
        }

        var contextWindow = GetPositiveInt(element, "context_window")
            ?? GetPositiveInt(element, "max_context_window")
            ?? DefaultContextWindow;
        var reserve = Math.Min(DefaultOutputReserve, Math.Max(1, contextWindow / 4));
        var supported = ResolveReasoningLevels(element);
        var defaultLevel = ResolveReasoningLevel(GetString(element, "default_reasoning_level"));
        if (!supported.Contains(defaultLevel))
        {
            defaultLevel = supported.Contains(ReasoningLevel.Medium) ? ReasoningLevel.Medium : supported[0];
        }

        return new OpenAiCodexModelConfiguration
        {
            Id = StableProfileId(slug),
            Name = name,
            ModelId = slug,
            Enabled = true,
            ContextWindow = contextWindow,
            MaximumOutputTokens = contextWindow,
            RequestOutputTokenReserve = reserve,
            Capabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
                StructuredOutput = true,
            },
            Cost = new ModelCostMetadata(),
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            IntendedWorkloadClasses = Enum.GetValues<WorkloadClass>(),
            DefaultReasoningLevel = defaultLevel,
            SupportedReasoningLevels = supported,
            TimeoutSeconds = 120,
            RetryMaxAttempts = 2,
            RetryDelayMilliseconds = 1000,
        };
    }

    private static ReasoningLevel[] ResolveReasoningLevels(JsonElement model)
    {
        HashSet<ReasoningLevel> levels = [ReasoningLevel.None];
        if (model.TryGetProperty("supported_reasoning_levels", out var values)
            && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                var effort = value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : GetString(value, "effort");
                levels.Add(ResolveReasoningLevel(effort));
            }
        }

        if (levels.Count == 1)
        {
            levels.UnionWith([ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High]);
        }

        return [.. levels.OrderBy(level => level)];
    }

    private static ReasoningLevel ResolveReasoningLevel(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "none" => ReasoningLevel.None,
            "minimal" => ReasoningLevel.Minimal,
            "low" => ReasoningLevel.Low,
            "medium" => ReasoningLevel.Medium,
            "high" or "xhigh" or "max" => ReasoningLevel.High,
            _ => ReasoningLevel.Medium,
        };
    }

    private static ModelProfileId StableProfileId(string slug)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"openai-codex:{slug}"));
        var guidBytes = digest.AsSpan(0, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new ModelProfileId(new Guid(guidBytes));
    }

    private static int? GetPositiveInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
        && value.TryGetInt32(out var result)
        && result > 0
            ? result
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
