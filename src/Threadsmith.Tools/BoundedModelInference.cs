namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Host-owned admission for bounded internal inference without tools or conversation replay.</summary>
public interface IBoundedModelInference
{
    /// <summary>Freezes model selection and shares the caller's execution authority for a live operation.</summary>
    IBoundedModelOperation Open(ToolExecutionContext context, int maximumCalls, int maximumResultBytes);
}

/// <summary>One invocation's selected model and cumulative model-call allowance.</summary>
public interface IBoundedModelOperation : IDisposable
{
    /// <summary>Immutable selected provider/profile/reasoning provenance.</summary>
    BoundedModelSelection Selection { get; }

    /// <summary>Maximum response tokens admitted by the frozen selected profile.</summary>
    int MaximumOutputTokens { get; }

    /// <summary>Executes a bounded structured request through the host model lifecycle.</summary>
    Task<string> ExecuteAsync(BoundedModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Credential-free immutable inference provenance.</summary>
public sealed record BoundedModelSelection(ModelProfileId ProfileId, string Provider, string Model, string Reasoning, int ContextWindowTokens);

/// <summary>Host instructions and separately untrusted data for one schema-bound request.</summary>
public sealed record BoundedModelRequest(string Instructions, string DataJson, string SchemaId, string JsonSchema, int MaximumOutputTokens);
