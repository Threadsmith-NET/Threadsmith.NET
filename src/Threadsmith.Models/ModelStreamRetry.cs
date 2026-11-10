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

    /// <summary>Recognizes structured rate-limit errors without inspecting or exposing provider prose.</summary>
    public static bool IsRateLimitError(JsonElement error)
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
            if (property.Name is not ("code" or "type" or "status" or "status_code"))
            {
                continue;
            }

            if ((property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var status) && status == 429)
                || (property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is "429" or "rate_limit_exceeded" or "rate_limit_error"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Recognizes structured rate limits in an already parsed mutable JSON tree without re-encoding it.</summary>
    public static bool IsRateLimitError(JsonObject error)
    {
        ArgumentNullException.ThrowIfNull(error);
        error = error["error"] as JsonObject ?? error;
        foreach (var property in error)
        {
            if (property.Key is not ("code" or "type" or "status" or "status_code") || property.Value is not JsonValue value)
            {
                continue;
            }

            if ((value.TryGetValue<int>(out var status) && status == 429)
                || (value.TryGetValue<string>(out var text) && text is "429" or "rate_limit_exceeded" or "rate_limit_error"))
            {
                return true;
            }
        }

        return false;
    }

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

    private static long? AddReported(long? previous, long? current)
    {
        return previous.HasValue || current.HasValue ? checked(previous.GetValueOrDefault() + current.GetValueOrDefault()) : null;
    }
}
