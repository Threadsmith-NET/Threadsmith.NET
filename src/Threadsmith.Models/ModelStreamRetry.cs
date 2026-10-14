namespace Threadsmith.Models;

/// <summary>Shared delivery boundary for retries of interrupted provider streams.</summary>
public static class ModelStreamRetry
{
    /// <summary>Only reasoning without response data can be followed by another attempt.</summary>
    public static bool IsReasoningOnly(ModelChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return chunk.Reasoning is not null && chunk.Text is null && chunk.Output is null
            && chunk.Usage is null && chunk.FinishReason is null && chunk.ResponseEnvelope is null;
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
}
