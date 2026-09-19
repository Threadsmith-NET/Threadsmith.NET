namespace Threadsmith.Tools.Jira;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>Performs one bounded Jira Cloud issue read using a trusted account binding.</summary>
public sealed class JiraCloudClient
{
    private readonly HttpClient _http;
    private readonly ISecretResolver _secrets;
    private readonly JiraOptions _options;

    /// <summary>Initializes a new instance of the <see cref="JiraCloudClient"/> class.</summary>
    public JiraCloudClient(HttpClient http, ISecretResolver secrets, JiraOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _secrets = secrets;
        _options = options;
    }

    /// <summary>Reads one issue and projects only the requested attribution and description fields.</summary>
    internal async Task<JiraIssueData> ReadIssueAsync(
        string providerId,
        JiraProviderOptions provider,
        string key,
        int maximumBodyBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBodyBytes);
        var requestUri = BuildRequestUri(provider, key);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("Threadsmith.NET/1.0");
        var secretRequest = new SecretResolutionRequest
        {
            Reference = SecretReference.Parse(provider.Authentication.SecretReference),
            ComponentId = $"jira:{providerId}",
            Purpose = "read a Jira issue description",
            MinimumTrust = SecretProviderTrust.UserOwned,
        };
        var token = (await _secrets.ResolveAsync(secretRequest, cancellationToken)).RequireValue(secretRequest);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(provider.Authentication.Username + ":" + token)));

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentType?.MediaType is not { } mediaType
            || !mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolExecutionException("Jira returned an unexpected response format.");
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await content.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + count > _options.MaximumResponseBytes)
            {
                throw new ToolExecutionException(
                    "The Jira response exceeded tools.jira.maximumResponseBytes.",
                    ToolErrorClassification.OutputLimitExceeded);
            }

            await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken);
        }

        buffer.Position = 0;
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                buffer,
                new JsonDocumentOptions { MaxDepth = 64 },
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ToolExecutionException("Jira returned malformed JSON.", exception);
        }

        using (document)
        {
            return ReadIssue(document.RootElement, maximumBodyBytes, cancellationToken);
        }
    }

    private static Uri BuildRequestUri(JiraProviderOptions provider, string key)
    {
        var root = provider.EndpointMode == "scopedGateway"
            ? new Uri($"https://api.atlassian.com/ex/jira/{provider.CloudId}/", UriKind.Absolute)
            : JiraAddress.ValidateSiteUrl(provider.SiteUrl);
        return new Uri(
            root,
            $"rest/api/3/issue/{Uri.EscapeDataString(key)}?fields=summary,description,updated&updateHistory=false");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = response.StatusCode;
        var message = status switch
        {
            HttpStatusCode.Unauthorized => "Jira authentication failed. Check the configured email and API token.",
            HttpStatusCode.Forbidden => "Jira denied access. Check token scope and issue permissions.",
            HttpStatusCode.NotFound => "The Jira issue was not found or is not visible to the configured account.",
            HttpStatusCode.TooManyRequests => FormatRateLimit(response),
            >= HttpStatusCode.InternalServerError => "Jira is temporarily unavailable.",
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => "Jira returned an unsupported redirect.",
            _ => $"Jira returned HTTP {(int)status}.",
        };
        throw new ToolExecutionException(message);
    }

    private static string FormatRateLimit(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var retry = retryAfter?.Delta;
        if (retry is null && retryAfter?.Date is { } retryDate)
        {
            retry = retryDate - DateTimeOffset.UtcNow;
        }

        if (retry is null || retry <= TimeSpan.Zero)
        {
            return "Jira rate-limited the request. Retry later.";
        }

        var seconds = (int)Math.Ceiling(Math.Min(retry.Value.TotalSeconds, 86400d));
        return $"Jira rate-limited the request. Retry after about {seconds} seconds.";
    }

    private static JiraIssueData ReadIssue(
        JsonElement root,
        int maximumBodyBytes,
        CancellationToken cancellationToken)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ToolExecutionException("Jira returned an invalid issue response.");
        }

        var id = RequiredString(root, "id");
        if (id.Length > 128 || id.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new ToolExecutionException("Jira returned an invalid issue ID.");
        }

        var key = RequiredString(root, "key").ToUpperInvariant();
        if (!JiraTool.IsValidIssueKey(key))
        {
            throw new ToolExecutionException("Jira returned an invalid issue key.");
        }

        if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            throw new ToolExecutionException("Jira returned issue data without fields.");
        }

        var summary = OptionalString(fields, "summary");
        summary = JiraTextBounds.UnicodeScalarPrefix(summary, 512, out var summaryTruncated);
        var updated = ReadUpdated(fields);
        if (!fields.TryGetProperty("description", out var description))
        {
            throw new ToolExecutionException("Jira returned issue data without a description field.");
        }

        JiraDescriptionResult projected;
        try
        {
            projected = JiraDescriptionReader.Read(
                description,
                maximumBodyBytes,
                cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new ToolExecutionException(exception.Message, exception);
        }

        var limitations = new SortedSet<string>(projected.Limitations, StringComparer.Ordinal);
        if (summaryTruncated)
        {
            limitations.Add("summary-truncated");
        }

        return new JiraIssueData(
            id,
            key,
            summary,
            updated,
            projected.Body,
            projected.BodyState,
            projected.BodyComplete,
            limitations.ToArray(),
            projected.IsTruncated || summaryTruncated);
    }

    private static DateTimeOffset? ReadUpdated(JsonElement fields)
    {
        if (!fields.TryGetProperty("updated", out var updated) || updated.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (updated.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                updated.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            throw new ToolExecutionException("Jira returned an invalid issue update timestamp.");
        }

        return timestamp;
    }

    private static string RequiredString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var member)
            || member.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(member.GetString()))
        {
            throw new ToolExecutionException($"Jira returned issue data without a valid {property}.");
        }

        return member.GetString() ?? string.Empty;
    }

    private static string OptionalString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var member) || member.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (member.ValueKind != JsonValueKind.String)
        {
            throw new ToolExecutionException($"Jira returned an invalid {property} field.");
        }

        return member.GetString() ?? string.Empty;
    }
}
