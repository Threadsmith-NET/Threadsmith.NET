namespace Threadsmith.Models;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Telemetry;

/// <summary>Bounds and formats explanatory response data without copying requests or headers.</summary>
internal static class ProviderErrorDetails
{
    private const int MaximumBytes = 4096;
    private const int MaximumIdentifierCharacters = 128;
    private const string TruncationMarker = " [message truncated]";
    private static readonly SecretOutputSanitizer Sanitizer = new();

    /// <summary>Reads bounded details while retaining HTTP status and allowlisted correlation headers.</summary>
    internal static async Task<Detail> ReadAsync(HttpResponseMessage response, string? credential, CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        var headerRequestId = Identifier(Header("x-request-id") ?? Header("request-id"), credential);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ITimer? timer = null;
        Detail details;
        try
        {
            var opening = response.Content.ReadAsStreamAsync(reading.Token);
            if (!opening.IsCompleted)
            {
                StartBudget();
            }

            await using var stream = await opening.ConfigureAwait(false);
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var pending = stream.ReadAsync(bytes.AsMemory(count), reading.Token);
                if (!pending.IsCompleted)
                {
                    StartBudget();
                }

                var read = await pending.ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            if (count > MaximumBytes)
            {
                details = new(string.Empty, "Error body: truncated (omitted)");
            }
            else if (count == 0)
            {
                details = new(string.Empty, string.Empty);
            }
            else
            {
                using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
                details = Format(document.RootElement, credential);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            details = new(string.Empty, "Error body: unavailable");
        }
        catch (Exception exception) when (exception is JsonException or IOException or HttpRequestException)
        {
            details = new(string.Empty, "Error body: unreadable");
        }
        finally
        {
            if (timer is not null)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var metadata = details.Metadata;
        if (headerRequestId is not null && !metadata.Contains("Request ID:", StringComparison.Ordinal))
        {
            metadata = Join(metadata, "Request ID: " + headerRequestId);
        }

        return details with { Metadata = metadata, HttpStatus = (int)response.StatusCode };

        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

        // One waiting budget covers opening and all reads, independently of the request deadline.
        void StartBudget() => timer ??= (timeProvider ?? TimeProvider.System).CreateTimer(
            _ => reading.Cancel(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Extracts only explanatory text and allowlisted scalar identifiers from a parsed error.</summary>
    internal static Detail Format(JsonElement root, string? credential, string? provider = null, JsonElement requestId = default, JsonElement generationId = default)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new(string.Empty, string.Empty);
        }

        var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        return Extract(
            name => Scalar(error, name),
            name => Scalar(root, name) ?? name switch
            {
                "provider" => provider,
                "request_id" => requestId.ValueKind == JsonValueKind.String ? requestId.GetString() : null,
                "id" => generationId.ValueKind == JsonValueKind.String ? generationId.GetString() : null,
                _ => null,
            },
            credential);
    }

    /// <summary>Reads the existing node without serializing unrelated output or private replay data.</summary>
    internal static Detail Format(JsonObject root, string? credential)
    {
        var error = root["error"] as JsonObject ?? root;
        return Extract(name => Scalar(error, name), name => Scalar(root, name), credential);
    }

    /// <summary>Combines the explanation or adapter fallback with readable, safe diagnostic context.</summary>
    internal static string PreferMessage(Detail details, string fallback)
    {
        var hasMessage = !string.IsNullOrWhiteSpace(details.Message);
        var message = hasMessage ? details.Message : fallback.Trim();
        var metadata = details.Metadata;
        if (details.HttpStatus is { } status && (hasMessage || !message.Contains("HTTP", StringComparison.Ordinal)))
        {
            metadata = Join("HTTP " + status.ToString(CultureInfo.InvariantCulture), metadata);
        }

        return string.IsNullOrEmpty(metadata) ? message : message + Environment.NewLine + metadata;
    }

    /// <summary>Separates absent explanatory text from metadata and omission markers until adapter fallback selection.</summary>
    internal sealed record Detail(string Message, string Metadata, int? HttpStatus = null);

    private static Detail Extract(Func<string, string?> error, Func<string, string?> root, string? credential)
    {
        var message = Sanitize(error("message") ?? string.Empty, credential);
        if (message.Length > MaximumBytes)
        {
            message = message[..(MaximumBytes - TruncationMarker.Length)] + TruncationMarker;
        }

        var metadata = string.Empty;
        Add("Code", error("code"));
        Add("Type", error("type") ?? root("error_type"));
        Add("Parameter", error("param"));
        Add("Provider", root("provider") ?? error("provider"));
        Add("Request ID", root("request_id") ?? error("request_id"));
        Add("Generation ID", root("id"));
        return new(message, metadata);

        void Add(string label, string? value)
        {
            if (Identifier(value, credential, allowSpaces: label == "Provider") is { } identifier)
            {
                metadata = Join(metadata, label + ": " + identifier);
            }
        }
    }

    private static string? Scalar(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var scalar))
        {
            return null;
        }

        return scalar.ValueKind == JsonValueKind.String ? scalar.GetString()
            : name != "message" && scalar.ValueKind == JsonValueKind.Number && scalar.TryGetInt64(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static string? Scalar(JsonObject value, string name)
    {
        if (value[name] is not JsonValue scalar)
        {
            return null;
        }

        return scalar.TryGetValue<string>(out var text) ? text
            : name != "message" && scalar.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static string? Identifier(string? value, string? credential, bool allowSpaces = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdentifierCharacters)
        {
            return null;
        }

        var safe = Sanitize(value, credential);
        return safe.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or ':' || (allowSpaces && character == ' '))
            ? safe : null;
    }

    private static string Sanitize(string value, string? credential)
    {
        // Remove the complete credential before any truncation can cut it into a publishable fragment.
        if (!string.IsNullOrEmpty(credential))
        {
            value = value.Replace(credential, "[REDACTED]", StringComparison.Ordinal);
        }

        return Sanitizer.Sanitize(value);
    }

    private static string Join(string first, string second) =>
        string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : first + " · " + second;
}
