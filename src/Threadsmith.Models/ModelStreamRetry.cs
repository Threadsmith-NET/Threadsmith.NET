namespace Threadsmith.Models;

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Shared delivery boundary for retries of interrupted provider streams.</summary>
public static class ModelStreamRetry
{
    /// <summary>Combines distinct attempts into the cumulative snapshot expected by request accounting.</summary>
    public static ModelUsage CombineUsage(ModelUsage? previous, ModelUsage current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            return current;
        }

        checked
        {
            var reads = AddReported(previous.Cache?.CacheReadTokens, current.Cache?.CacheReadTokens);
            var writes = AddReported(previous.Cache?.CacheWriteTokens, current.Cache?.CacheWriteTokens);
            return new ModelUsage(
                previous.InputTokens + current.InputTokens,
                previous.OutputTokens + current.OutputTokens,
                previous.EstimatedCost + current.EstimatedCost,
                previous.IsEstimate || current.IsEstimate,
                new ModelCacheUsage
                {
                    Availability = reads.HasValue || writes.HasValue ? CacheUsageAvailability.Reported : CacheUsageAvailability.Unavailable,
                    CacheReadTokens = reads,
                    CacheWriteTokens = writes,
                    ReadInputSemantics = current.Cache?.ReadInputSemantics ?? previous.Cache?.ReadInputSemantics ?? CacheReadInputSemantics.IncludedInInput,
                    Provenance = current.Cache?.Provenance ?? previous.Cache?.Provenance,
                })
            {
                ReportedCost = previous.ReportedCost.HasValue || current.ReportedCost.HasValue
                    ? previous.ReportedCost.GetValueOrDefault() + current.ReportedCost.GetValueOrDefault() : null,
                CostProvenance = current.CostProvenance ?? previous.CostProvenance,
                ReasoningTokens = AddReported(previous.ReasoningTokens, current.ReasoningTokens),
                MaximumAttemptOutputTokens = Math.Max(previous.MaximumAttemptOutputTokens ?? previous.OutputTokens, current.MaximumAttemptOutputTokens ?? current.OutputTokens),
            };
        }
    }

    /// <summary>Host status text shared by lead and child output projections.</summary>
    public const string ProviderBusyMessage = "Model provider is currently busy ... please wait";

    /// <summary>Reads a provider-directed delay, bounded to thirty seconds and the request's existing deadline.</summary>
    public static TimeSpan? ReadRetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var delay = headers.RetryAfter?.Delta;
        if (delay is null && headers.RetryAfter?.Date is { } date)
        {
            delay = date - now;
        }

        return delay is { } value ? TimeSpan.FromMilliseconds(Math.Clamp(value.TotalMilliseconds, 0, 30000)) : null;
    }

    /// <summary>Recognizes structured rate-limit errors without inspecting provider prose.</summary>
    public static bool IsRateLimitError(JsonElement error) => HasErrorCode(error, overloaded: false);

    /// <summary>Recognizes structured rate limits in a mutable JSON tree.</summary>
    public static bool IsRateLimitError(JsonObject error) => HasErrorCode(error, overloaded: false);

    /// <summary>Recognizes structured provider overload and temporary unavailability.</summary>
    public static bool IsOverloadError(JsonElement error) => HasErrorCode(error, overloaded: true);

    /// <summary>Recognizes structured overload and temporary unavailability in a mutable JSON tree.</summary>
    public static bool IsOverloadError(JsonObject error) => HasErrorCode(error, overloaded: true);

    /// <summary>Only reasoning without response data can be followed by another attempt.</summary>
    public static bool IsReasoningOnly(ModelChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return chunk.Reasoning is not null && chunk.Text is null && chunk.Output is null
            && chunk.GeneratedImage is null && chunk.Usage is null && chunk.FinishReason is null && chunk.ResponseEnvelope is null;
    }

    /// <summary>Separates failed preliminary reasoning from a new attempt without contributing model usage.</summary>
    public static ModelChunk ReasoningBoundary()
    {
        return new ModelChunk
        {
            Reasoning = "\n\n[Interrupted response; retrying]\n\n",
            IsDisplayOnlyReasoning = true,
            SuppressReasoningDiagnostics = true,
        };
    }

    /// <summary>Matches structured failure identifiers without inspecting provider prose.</summary>
    private static bool HasErrorCode(JsonElement error, bool overloaded)
    {
        if (error.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (error.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            error = nested;
        }

        foreach (var property in error.EnumerateObject())
        {
            if (property.Name is not ("code" or "type" or "status" or "status_code" or "error_type"))
            {
                continue;
            }

            if ((property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var status) && MatchesErrorStatus(status, overloaded))
                || (property.Value.ValueKind == JsonValueKind.String
                    && MatchesErrorCode(property.Value.GetString(), overloaded)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Matches structured failure identifiers in a mutable JSON tree without re-encoding it.</summary>
    private static bool HasErrorCode(JsonObject error, bool overloaded)
    {
        ArgumentNullException.ThrowIfNull(error);
        error = error["error"] as JsonObject ?? error;
        foreach (var property in error)
        {
            if (property.Key is not ("code" or "type" or "status" or "status_code" or "error_type") || property.Value is not JsonValue value)
            {
                continue;
            }

            if ((value.TryGetValue<int>(out var status) && MatchesErrorStatus(status, overloaded))
                || (value.TryGetValue<string>(out var text) && MatchesErrorCode(text, overloaded)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesErrorStatus(int status, bool overloaded)
    {
        return overloaded ? status is 503 or 529 : status == 429;
    }

    private static bool MatchesErrorCode(string? code, bool overloaded)
    {
        return overloaded
            ? code is "503" or "529" or "server_is_overloaded" or "service_unavailable_error"
                or "overloaded_error" or "provider_overloaded" or "provider_unavailable"
            : code is "429" or "rate_limit_exceeded" or "rate_limit_error";
    }

    private static long? AddReported(long? previous, long? current)
    {
        return previous.HasValue || current.HasValue ? checked(previous.GetValueOrDefault() + current.GetValueOrDefault()) : null;
    }
}
