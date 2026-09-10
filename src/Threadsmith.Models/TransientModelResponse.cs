namespace Threadsmith.Models;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;

/// <summary>Exact authority and generation binding for one private provider response.</summary>
public sealed record ModelReplayBinding
{
    /// <summary>Protocol envelope version.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Provider instance identity.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Exact provider model identity.</summary>
    public required string ModelId { get; init; }

    /// <summary>Resolved profile identity.</summary>
    public required ModelProfileId ProfileId { get; init; }

    /// <summary>Owning active run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Owning provider round.</summary>
    public required int ModelRound { get; init; }

    /// <summary>History generation at submission.</summary>
    public long HistoryRewriteGeneration { get; init; }

    /// <summary>Private credential generation used only for same-turn matching.</summary>
    [JsonIgnore]
    public required string CredentialGeneration { get; init; }

    /// <summary>Canonical tool inventory identity.</summary>
    public required string ToolInventoryDigest { get; init; }

    /// <summary>Stable policy and instruction identity.</summary>
    public required string InstructionDigest { get; init; }

    /// <summary>Normalized completed-response identity before host rendering.</summary>
    public required string NormalizedRoundDigest { get; init; }

    /// <inheritdoc />
    public override string ToString() => "ModelReplayBinding { private protocol binding }";
}

/// <summary>Detached completed provider response retained only by an active host loop.</summary>
[JsonConverter(typeof(ModelResponseReplayEnvelopeJsonConverter))]
public sealed class ModelResponseReplayEnvelope : IDisposable
{
    private byte[] _payload;

    /// <summary>Initializes a new instance of the <see cref="ModelResponseReplayEnvelope"/> class with detached protocol data.</summary>
    public ModelResponseReplayEnvelope(
        ModelReplayBinding binding,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<string> wireToolCallIds,
        long retainedOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(wireToolCallIds);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedOutputTokens);
        if (binding.Version != 1 || binding.ModelRound < 0 || payload.IsEmpty
            || payload.Length > ModelProfile.DefaultMaximumStreamedBytes
            || wireToolCallIds.Any(string.IsNullOrWhiteSpace)
            || wireToolCallIds.Distinct(StringComparer.Ordinal).Count() != wireToolCallIds.Count)
        {
            throw new ModelProviderException("Invalid or oversized private model response.");
        }

        Binding = binding;
        _payload = payload.ToArray();
        WireToolCallIds = Array.AsReadOnly(wireToolCallIds.ToArray());
        RetainedOutputTokens = retainedOutputTokens;
    }

    /// <summary>Transient authority binding.</summary>
    [JsonIgnore]
    public ModelReplayBinding Binding { get; }

    /// <summary>Ordered wire identities corresponding to normalized tool ordinals.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> WireToolCallIds { get; }

    /// <summary>Conservative reported output-token contribution, including hidden thinking.</summary>
    [JsonIgnore]
    public long RetainedOutputTokens { get; }

    /// <summary>Currently retained byte count.</summary>
    [JsonIgnore]
    public int ByteCount => _payload.Length;

    /// <inheritdoc />
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_payload);
        _payload = [];
    }

    /// <inheritdoc />
    public override string ToString() => "ModelResponseReplayEnvelope { private protocol data }";

    /// <summary>Whether the host accepted ownership of this completed response.</summary>
    [JsonIgnore]
    internal bool IsRetained { get; private set; }

    /// <summary>Transfers disposal responsibility to the validated active-loop state.</summary>
    internal void MarkRetained()
    {
        ObjectDisposedException.ThrowIf(_payload.Length == 0, this);
        if (IsRetained)
        {
            throw new ModelProviderException("Private model response ownership was already transferred.");
        }

        IsRetained = true;
    }

    /// <summary>Copies protocol data exclusively for the isolated provider's request projection.</summary>
    internal byte[] CopyProtocolPayload()
    {
        ObjectDisposedException.ThrowIf(_payload.Length == 0, this);
        return (byte[])_payload.Clone();
    }
}

/// <summary>Excludes replay from generic JSON and rejects attempts to import private protocol state.</summary>
public sealed class ModelResponseReplayEnvelopeJsonConverter : JsonConverter<ModelResponseReplayEnvelope>
{
    /// <inheritdoc />
    public override ModelResponseReplayEnvelope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new JsonException("Private model replay cannot be restored from JSON.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ModelResponseReplayEnvelope value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    }
}

/// <summary>Owns bounded response replay and ordinal correlations for exactly one active model loop.</summary>
public sealed class ModelRequestTransientState : IDisposable
{
    private readonly long _maximumBytes;
    private readonly List<ModelResponseReplayEnvelope> _responses = [];
    private readonly Dictionary<(int Round, string CallId), string> _wireIds = [];
    private readonly Dictionary<int, HashSet<int>> _boundOrdinals = [];
    private readonly Dictionary<int, string> _historyDigests = [];
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ModelRequestTransientState"/> class with a hard byte ceiling.</summary>
    public ModelRequestTransientState(long maximumBytes = ModelProfile.DefaultMaximumStreamedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        _maximumBytes = maximumBytes == 0
            ? ModelProfile.DefaultMaximumStreamedBytes
            : Math.Min(maximumBytes, ModelProfile.DefaultMaximumStreamedBytes);
    }

    /// <summary>Whether a completed response still requires exact replay.</summary>
    [JsonIgnore]
    public bool HasResponses => _responses.Count > 0;

    /// <summary>Completed private responses in model-round order.</summary>
    [JsonIgnore]
    public IReadOnlyList<ModelResponseReplayEnvelope> Responses => _responses.AsReadOnly();

    /// <summary>Total retained protocol byte count.</summary>
    [JsonIgnore]
    public long RetainedBytes => _responses.Sum(item => (long)item.ByteCount);

    /// <summary>Reported upper-bound output tokens retained for continuation admission.</summary>
    [JsonIgnore]
    public long RetainedOutputTokens => _responses.Aggregate(
        0L,
        static (total, item) => checked(total + item.RetainedOutputTokens));

    /// <summary>Commits one completely validated response before its normalized tools are consumed.</summary>
    public void Accept(ModelStreamRequest request, ModelResponseReplayEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(envelope);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var binding = envelope.Binding;
        if (binding.RunId != request.RunId
            || binding.ProfileId != request.ResolvedProfileId
            || binding.ModelRound != request.ToolContinuationRound
            || binding.HistoryRewriteGeneration != request.HistoryRewriteGeneration
            || envelope.ByteCount == 0
            || _responses.Any(item => item.Binding.ModelRound >= binding.ModelRound)
            || envelope.ByteCount > _maximumBytes - RetainedBytes
            || envelope.RetainedOutputTokens > long.MaxValue - RetainedOutputTokens)
        {
            envelope.Dispose();
            throw new ModelProviderException("Private model continuation exceeded its bounds or changed identity; start a fresh turn.");
        }

        envelope.MarkRetained();
        _responses.Add(envelope);
        _boundOrdinals.Add(binding.ModelRound, []);
    }

    /// <summary>Binds one host audit identity to a completed response's exact ordinal.</summary>
    public void BindToolCall(int modelRound, int ordinal, string hostCallId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostCallId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var response = _responses.SingleOrDefault(item => item.Binding.ModelRound == modelRound)
            ?? throw new ModelProviderException("Private response correlation is missing.");
        if (ordinal < 0 || ordinal >= response.WireToolCallIds.Count
            || !_boundOrdinals[modelRound].Add(ordinal)
            || !_wireIds.TryAdd((modelRound, hostCallId), response.WireToolCallIds[ordinal]))
        {
            throw new ModelProviderException("Private response tool correlation is invalid.");
        }
    }

    /// <summary>Resolves an exact local result identity; names and arguments are never used as keys.</summary>
    public string GetWireToolCallId(int modelRound, string hostCallId)
    {
        return _wireIds.TryGetValue((modelRound, hostCallId), out var wireId)
            ? wireId
            : throw new ModelProviderException("Private response tool correlation is missing.");
    }

    /// <summary>Seals the normalized assistant messages after host sanitization and ordinal binding.</summary>
    public void SealRound(int modelRound, IReadOnlyList<ModelMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var response = _responses.SingleOrDefault(item => item.Binding.ModelRound == modelRound)
            ?? throw new ModelProviderException("Private response correlation is missing.");
        if (_boundOrdinals[modelRound].Count != response.WireToolCallIds.Count
            || !_historyDigests.TryAdd(modelRound, ComputeHistoryDigest(modelRound, messages)))
        {
            throw new ModelProviderException("Private response history was not completed exactly once.");
        }
    }

    /// <summary>Rejects a rewritten or mismatched active exchange before another provider submission.</summary>
    public void ValidateHistory(ModelStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var response in _responses)
        {
            var binding = response.Binding;
            if (binding.RunId != request.RunId || binding.ProfileId != request.ResolvedProfileId
                || binding.HistoryRewriteGeneration != request.HistoryRewriteGeneration
                || binding.ModelRound >= request.ToolContinuationRound
                || !_historyDigests.TryGetValue(binding.ModelRound, out var digest)
                || digest != ComputeHistoryDigest(binding.ModelRound, request.Messages))
            {
                throw new ModelProviderException("Private model continuation no longer matches active history; start a fresh turn.");
            }
        }
    }

    /// <summary>Releases private data only at a completed host turn boundary.</summary>
    public void Clear()
    {
        foreach (var response in _responses)
        {
            response.Dispose();
        }

        _responses.Clear();
        _wireIds.Clear();
        _boundOrdinals.Clear();
        _historyDigests.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Clear();
        _disposed = true;
    }

    /// <inheritdoc />
    public override string ToString() => "ModelRequestTransientState { private active-loop state }";

    private static string ComputeHistoryDigest(int modelRound, IReadOnlyList<ModelMessage> messages)
    {
        var round = messages.Where(item => item.ModelRound == modelRound
            && item.Role == ModelMessageRole.Assistant).ToArray();
        if (round.Length == 0)
        {
            throw new ModelProviderException("Required private response history is missing.");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(round))));
    }
}
