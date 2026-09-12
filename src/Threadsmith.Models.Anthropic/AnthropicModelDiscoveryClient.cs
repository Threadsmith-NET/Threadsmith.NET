namespace Threadsmith.Models.Anthropic;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using global::Anthropic.Exceptions;
using global::Anthropic.Models.Models;

/// <summary>Page boundary for bounded authenticated model discovery.</summary>
public interface IAnthropicModelDiscoveryClient
{
    /// <summary>Reads a page after the exact supplied cursor.</summary>
    Task<AnthropicModelDiscoveryPage> ListAsync(string? afterId, int limit, CancellationToken cancellationToken);
}

/// <summary>Detached page metadata for complete-snapshot acquisition.</summary>
public sealed record AnthropicModelDiscoveryPage
{
    /// <summary>Detached models returned by this page.</summary>
    public required IReadOnlyList<AnthropicDiscoveredModel> Models { get; init; }

    /// <summary>Explicit API pagination indicator.</summary>
    public required bool HasMore { get; init; }

    /// <summary>Explicit API next-page cursor.</summary>
    public string? LastId { get; init; }

    /// <summary>Actual decompressed response bytes read before SDK parsing.</summary>
    public long ResponseBytes { get; init; }
}

/// <summary>Uses the official SDK Models service with host-controlled response and pagination bounds.</summary>
public static class AnthropicModelDiscoveryClient
{
    /// <summary>Creates one discovery-scoped SDK reader; aggregate bytes include every page.</summary>
    public static IAnthropicModelDiscoveryClient Create(HttpClient httpClient, string apiKey, TimeSpan timeout, AnthropicResourceLimits? limits = null)
    {
        return new Client(httpClient, apiKey, timeout, limits);
    }

    private sealed class Client : IAnthropicModelDiscoveryClient
    {
        private readonly AnthropicResourceLimits _limits;
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;
        private readonly TimeSpan _timeout;
        private long _aggregateBytes;

        /// <summary>Initializes a new instance of the <see cref="Client"/> class.</summary>
        internal Client(HttpClient httpClient, string apiKey, TimeSpan timeout, AnthropicResourceLimits? limits = null)
        {
            ArgumentNullException.ThrowIfNull(httpClient);
            ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            _limits = limits ?? new();
            _limits.Validate();
            _httpClient = httpClient;
            _apiKey = apiKey;
            _timeout = timeout;
        }

        /// <inheritdoc />
        public async Task<AnthropicModelDiscoveryPage> ListAsync(string? afterId, int limit, CancellationToken cancellationToken)
        {
            if (limit is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            long pageBytes = 0;
            using var client = AnthropicSdkClientFactory.Create(
                _httpClient,
                _apiKey,
                _timeout,
                maximumResponseBytes: _limits.MaximumMetadataBytes,
                responseBytesObserved: count =>
                {
                    pageBytes = checked(pageBytes + count);
                    _aggregateBytes = checked(_aggregateBytes + count);
                    if (_aggregateBytes > _limits.MaximumMetadataBytes)
                    {
                        throw new AnthropicDiscoveryException("Anthropic discovery exceeded its aggregate response byte limit.");
                    }
                });
            try
            {
                using var response = await client.Models.WithRawResponse.List(
                    new ModelListParams { AfterID = afterId, Limit = limit }, timeout.Token).ConfigureAwait(false);

                // The SDK convenience page's HasNext silently ignores malformed/missing cursors.
                var page = await response.Deserialize<ModelListPageResponse>(timeout.Token).ConfigureAwait(false);
                return new AnthropicModelDiscoveryPage
                {
                    Models = Array.AsReadOnly(page.Data.Select(Map).ToArray()),
                    HasMore = page.HasMore,
                    LastId = page.LastID,
                    ResponseBytes = pageBytes,
                };
            }
            catch (AnthropicHttpFailureException exception)
            {
                var rejected = exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                var transient = exception.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                    || (int)exception.StatusCode >= 500;
                throw new AnthropicDiscoveryException(
                    rejected ? "Anthropic discovery credential rejected."
                    : "Anthropic discovery HTTP request failed.",
                    rejected,
                    transient);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AnthropicDiscoveryException("Anthropic discovery exceeded its deadline.", transient: true);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or AnthropicIOException)
            {
                throw new AnthropicDiscoveryException("Anthropic discovery transport unavailable.", transient: true);
            }
            catch (Exception exception) when (exception is JsonException or AnthropicInvalidDataException)
            {
                throw new AnthropicDiscoveryException("Anthropic discovery returned malformed metadata.");
            }
        }

        private static AnthropicDiscoveredModel Map(ModelInfo model)
        {
            var raw = JsonSerializer.SerializeToElement(model);
            var displayName = model.DisplayName;
            var sanitized = string.Concat(displayName.Where(character => !char.IsControl(character)
                && CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.Format)).Trim();
            var capabilities = raw.TryGetProperty("capabilities", out var value) && value.ValueKind == JsonValueKind.Object
                ? MapCapabilities(value) : null;
            return new AnthropicDiscoveredModel
            {
                ModelId = model.ID,
                DisplayName = string.IsNullOrWhiteSpace(sanitized) ? model.ID : sanitized[..Math.Min(sanitized.Length, 512)],
                MaximumInputTokens = ToInt32(model.MaxInputTokens),
                MaximumOutputTokens = ToInt32(model.MaxTokens),
                Capabilities = capabilities,
            };
        }

        private static AnthropicDiscoveredCapabilities MapCapabilities(JsonElement value)
        {
            var thinking = value.TryGetProperty("thinking", out var thinkingValue) ? thinkingValue : default;
            var types = thinking.ValueKind == JsonValueKind.Object && thinking.TryGetProperty("types", out var typeValue) ? typeValue : default;
            var effort = value.TryGetProperty("effort", out var effortValue) ? effortValue : default;
            Dictionary<string, bool>? levels = null;
            if (effort.ValueKind == JsonValueKind.Object)
            {
                levels = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var level in effort.EnumerateObject().Where(property => property.Name != "supported"))
                {
                    if (GetSupported(effort, level.Name) is { } supported)
                    {
                        levels.Add(level.Name, supported);
                    }
                }
            }

            return new AnthropicDiscoveredCapabilities
            {
                StructuredOutputs = GetSupported(value, "structured_outputs"),
                Thinking = GetSupported(value, "thinking"),
                AdaptiveThinking = GetSupported(types, "adaptive"),
                ManualThinking = GetSupported(types, "enabled"),
                Effort = GetSupported(value, "effort"),
                EffortLevels = levels is null ? null : new ReadOnlyDictionary<string, bool>(levels),
            };
        }

        private static bool? GetSupported(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var capability)
                || capability.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (capability.ValueKind != JsonValueKind.Object || !capability.TryGetProperty("supported", out var supported))
            {
                return null;
            }

            return supported.ValueKind is JsonValueKind.True or JsonValueKind.False ? supported.GetBoolean()
                : throw new AnthropicDiscoveryException("Anthropic discovery returned an invalid capability value.");
        }

        private static int? ToInt32(long? value)
        {
            return value is null ? null : value is > int.MaxValue or <= 0
                ? throw new AnthropicDiscoveryException("Anthropic discovery returned an invalid token limit.") : (int)value.Value;
        }
    }
}
