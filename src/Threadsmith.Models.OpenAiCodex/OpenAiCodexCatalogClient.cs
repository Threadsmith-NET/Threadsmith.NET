namespace Threadsmith.Models.OpenAiCodex;

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Discovers every model exposed by the authenticated Codex backend.</summary>
public sealed class OpenAiCodexCatalogClient
{
    private const int DefaultContextWindow = 128_000;
    private const int DefaultOutputReserve = 32_768;
    private const int MaximumReleaseMetadataBytes = 1024 * 1024;

    // The Codex backend filters `/models` rows by Codex client compatibility, not by
    // Threadsmith's product version. Use this only when release lookup is unavailable.
    private const string CodexModelsClientCompatibilityVersion = "0.155.1";
    private static readonly Uri LatestCodexRelease = new("https://api.github.com/repos/openai/codex/releases/latest");
    private static readonly TimeSpan ReleaseLookupTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex ReleaseTagPattern = new(@"^rust-v(?<version>\d{1,4}\.\d{1,4}\.\d{1,4})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

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
        var clientVersion = await ResolveClientVersionAsync(cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage request = new(HttpMethod.Get, BuildModelsUri(clientVersion));
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

    private async Task<string> ResolveClientVersionAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReleaseLookupTimeout);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, LatestCodexRelease);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("Threadsmith.NET");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return CodexModelsClientCompatibilityVersion;
            }

            await response.Content.LoadIntoBufferAsync(MaximumReleaseMetadataBytes, timeout.Token).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tag_name", out var tag)
                || tag.ValueKind != JsonValueKind.String)
            {
                return CodexModelsClientCompatibilityVersion;
            }

            var match = ReleaseTagPattern.Match(tag.GetString() ?? string.Empty);
            if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version)
                || version <= Version.Parse(CodexModelsClientCompatibilityVersion))
            {
                return CodexModelsClientCompatibilityVersion;
            }

            return version.ToString();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return CodexModelsClientCompatibilityVersion;
        }
    }

    private static Uri BuildModelsUri(string clientVersion)
    {
        return new Uri(
            $"{OpenAiCodexProviderRegistration.ModelsEndpoint}?client_version={Uri.EscapeDataString(clientVersion)}");
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

        return [.. levels];
    }

    private static ReasoningLevel ResolveReasoningLevel(string? value)
    {
        return ReasoningLevel.TryParse(value, out var level) ? level : ReasoningLevel.Medium;
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
