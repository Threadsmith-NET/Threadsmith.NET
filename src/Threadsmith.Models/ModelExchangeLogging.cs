namespace Threadsmith.Models;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;

/// <summary>Appends provider-neutral model exchange diagnostics for explicitly enabled troubleshooting sessions.</summary>
public sealed class JsonlModelExchangeLog
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly SemaphoreSlim _appendGate = new(1, 1);
    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="JsonlModelExchangeLog"/> class.</summary>
    /// <param name="path">The JSONL file path that receives one diagnostic event per line.</param>
    public JsonlModelExchangeLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>Appends one compact request summary event.</summary>
    /// <param name="request">The model-visible request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AppendRequestSummaryAsync(ModelStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppendAsync(
            new ModelExchangeLogEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                Kind = ModelExchangeLogEntryKind.RequestSummary,
                RunId = request.RunId,
                ToolContinuationRound = request.ToolContinuationRound,
                Payload = JsonSerializer.SerializeToElement(CreateRequestSummary(request), SerializerOptions),
            },
            cancellationToken);
    }

    /// <summary>Appends one raw provider-neutral request event.</summary>
    /// <param name="request">The model-visible request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AppendRequestAsync(ModelStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppendAsync(
            new ModelExchangeLogEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                Kind = ModelExchangeLogEntryKind.Request,
                RunId = request.RunId,
                ToolContinuationRound = request.ToolContinuationRound,
                Payload = JsonSerializer.SerializeToElement(
                    CreateProviderVisibleRequest(request),
                    SerializerOptions),
            },
            cancellationToken);
    }

    /// <summary>Appends one streamed chunk event.</summary>
    /// <param name="runId">The request correlation id.</param>
    /// <param name="toolContinuationRound">The tool-continuation round.</param>
    /// <param name="sequence">The zero-based chunk sequence.</param>
    /// <param name="chunk">The normalized streamed model chunk.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AppendChunkAsync(
        RunId runId,
        int toolContinuationRound,
        int sequence,
        ModelChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return AppendAsync(
            new ModelExchangeLogEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                Kind = ModelExchangeLogEntryKind.Chunk,
                RunId = runId,
                ToolContinuationRound = toolContinuationRound,
                Sequence = sequence,
                Payload = JsonSerializer.SerializeToElement(CreateChunkLogPayload(chunk), SerializerOptions),
            },
            cancellationToken);
    }

    /// <summary>Appends a compact response summary event.</summary>
    /// <param name="runId">The request correlation id.</param>
    /// <param name="toolContinuationRound">The tool-continuation round.</param>
    /// <param name="summary">The accumulated response summary.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AppendResponseSummaryAsync(
        RunId runId,
        int toolContinuationRound,
        ModelExchangeResponseSummary summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return AppendAsync(
            new ModelExchangeLogEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                Kind = ModelExchangeLogEntryKind.ResponseSummary,
                RunId = runId,
                ToolContinuationRound = toolContinuationRound,
                Sequence = summary.ChunkCount,
                Payload = JsonSerializer.SerializeToElement(summary, SerializerOptions),
            },
            cancellationToken);
    }

    /// <summary>Appends a terminal completion event.</summary>
    /// <param name="runId">The request correlation id.</param>
    /// <param name="toolContinuationRound">The tool-continuation round.</param>
    /// <param name="chunkCount">The number of chunks observed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AppendCompletionAsync(
        RunId runId,
        int toolContinuationRound,
        int chunkCount,
        CancellationToken cancellationToken = default)
    {
        return AppendAsync(
            new ModelExchangeLogEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                Kind = ModelExchangeLogEntryKind.Completion,
                RunId = runId,
                ToolContinuationRound = toolContinuationRound,
                Sequence = chunkCount,
            },
            cancellationToken);
    }

    /// <summary>Appends a terminal provider failure event without serializing exception objects.</summary>
    /// <param name="runId">The request correlation id.</param>
    /// <param name="toolContinuationRound">The tool-continuation round.</param>
    /// <param name="exception">The provider exception.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AppendFailureAsync(
        RunId runId,
        int toolContinuationRound,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return AppendAsync(
            new ModelExchangeLogEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                Kind = ModelExchangeLogEntryKind.Failure,
                RunId = runId,
                ToolContinuationRound = toolContinuationRound,
                ErrorType = exception.GetType().Name,
                ErrorMessage = exception.Message,
                Payload = CreateFailurePayload(exception),
            },
            cancellationToken);
    }

    private static JsonElement? CreateFailurePayload(Exception exception)
    {
        return exception is MalformedInvocationException malformed
            ? JsonSerializer.SerializeToElement(
                new ModelExchangeFailurePayload
                {
                    MalformedInvocation = ModelExchangeMalformedInvocationSummary.FromDiagnostic(
                        malformed.Diagnostic),
                },
                SerializerOptions)
            : null;
    }

    private async Task AppendAsync(ModelExchangeLogEntry entry, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(entry, SerializerOptions) + Environment.NewLine;
        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_path, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _appendGate.Release();
        }
    }

    private static ModelExchangeRequestSummary CreateRequestSummary(ModelStreamRequest request)
    {
        return new ModelExchangeRequestSummary
        {
            InputCharacters = request.Input.Length,
            MessageCount = request.Messages.Count,
            ToolCount = request.Tools.Count,
            ToolTransportMode = request.ToolTransportMode.ToString(),
            WorkloadClass = request.WorkloadClass.ToString(),
            ReasoningLevel = request.ReasoningLevel.ToString(),
            ContainsSensitiveData = request.ContainsSensitiveData,
            ResolvedProfileId = request.ResolvedProfileId?.Value.ToString(),
            AdvertisedTools = request.Tools
                .Select(tool => new ModelExchangeToolSummary
                {
                    Name = tool.Name,
                    DescriptionCharacters = tool.Description.Length,
                    SchemaCharacters = tool.ArgumentsJsonSchema.Length,
                })
                .ToArray(),
            Messages = request.Messages
                .Select((message, index) => new ModelExchangeMessageSummary
                {
                    Index = index,
                    Role = message.Role.ToString(),
                    SectionId = message.SectionId,
                    ToolCallId = message.ToolCallId,
                    ToolName = message.ToolName,
                    PartCount = message.Content.Count(static part => part.IsModelVisible),
                    ContentCharacters = message.GetModelVisibleContentLength(),
                    ContentKinds = message.Content
                        .Where(static part => part.IsModelVisible)
                        .Select(part => part.Kind.ToString())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                })
                .ToArray(),
            WireEstimate = request.WireEstimate,
        };
    }

    private static ModelStreamRequest CreateProviderVisibleRequest(ModelStreamRequest request)
    {
        return request with
        {
            Messages = [.. request.Messages.Select(message => message with
            {
                Content = [.. message.Content.Where(static part => part.IsModelVisible)],
            })],
        };
    }

    private static object CreateChunkLogPayload(ModelChunk chunk)
    {
        return new
        {
            chunk.Text,
            chunk.Output,
            chunk.Usage,
            chunk.FinishReason,
            ReasoningCharacters = chunk.Reasoning?.Length,
        };
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

/// <summary>Wraps a model provider and records the provider-neutral exchange when explicitly enabled.</summary>
public sealed class LoggingModelProvider : IModelProvider
{
    private readonly IModelProvider _inner;
    private readonly JsonlModelExchangeLog _log;

    /// <summary>Initializes a new instance of the <see cref="LoggingModelProvider"/> class.</summary>
    /// <param name="inner">The provider that owns model semantics.</param>
    /// <param name="log">The explicit diagnostic log sink.</param>
    public LoggingModelProvider(IModelProvider inner, JsonlModelExchangeLog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);
        _inner = inner;
        _log = log;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelChunk> StreamAsync(
        ModelStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _log.AppendRequestSummaryAsync(request, cancellationToken).ConfigureAwait(false);
        await _log.AppendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        var sequence = 0;
        var textCharacters = 0;
        var reasoningCharacters = 0;
        var toolRequestCount = 0;
        string? finishReason = null;
        ModelUsage? usage = null;
        List<ModelExchangeToolCallSummary> toolCalls = [];
        await using var enumerator = _inner
            .StreamAsync(request, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await _log.AppendFailureAsync(
                    request.RunId,
                    request.ToolContinuationRound,
                    exception,
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            if (!hasNext)
            {
                break;
            }

            var chunk = enumerator.Current;
            textCharacters += chunk.Text?.Length ?? 0;
            reasoningCharacters += chunk.Reasoning?.Length ?? 0;
            if (chunk.Output is ToolRequestModelOutput toolRequest)
            {
                toolRequestCount++;
                toolCalls.Add(new ModelExchangeToolCallSummary
                {
                    Sequence = sequence,
                    ToolName = toolRequest.ToolName,
                    ArgumentsJson = toolRequest.ArgumentsJson,
                });
            }

            finishReason = chunk.FinishReason?.ToString() ?? finishReason;
            usage = chunk.Usage ?? usage;
            await _log.AppendChunkAsync(
                request.RunId,
                request.ToolContinuationRound,
                sequence,
                chunk,
                cancellationToken).ConfigureAwait(false);
            sequence++;
            yield return chunk;
        }

        await _log.AppendResponseSummaryAsync(
            request.RunId,
            request.ToolContinuationRound,
            new ModelExchangeResponseSummary
            {
                ChunkCount = sequence,
                TextCharacters = textCharacters,
                ReasoningCharacters = reasoningCharacters,
                ToolRequestCount = toolRequestCount,
                FinishReason = finishReason,
                Usage = usage,
                ToolCalls = toolCalls,
            },
            cancellationToken).ConfigureAwait(false);
        await _log.AppendCompletionAsync(
            request.RunId,
            request.ToolContinuationRound,
            sequence,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Serialized provider-neutral model exchange diagnostic event.</summary>
public sealed record ModelExchangeLogEntry
{
    /// <summary>Gets when the event was observed by the host.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Gets the diagnostic event kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the run correlation id.</summary>
    public RunId RunId { get; init; }

    /// <summary>Gets the tool-continuation round for the request.</summary>
    public int ToolContinuationRound { get; init; }

    /// <summary>Gets the streamed chunk sequence, when applicable.</summary>
    public int? Sequence { get; init; }

    /// <summary>Gets the event payload, when applicable.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>Gets the exception type name for failure events.</summary>
    public string? ErrorType { get; init; }

    /// <summary>Gets the exception message for failure events.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>Compact failure summary for model exchange diagnostics.</summary>
public sealed record ModelExchangeFailurePayload
{
    /// <summary>Gets safe malformed-invocation metadata, when the provider failure has it.</summary>
    public ModelExchangeMalformedInvocationSummary? MalformedInvocation { get; init; }
}

/// <summary>Safe malformed-invocation metadata for raw model exchange diagnostics.</summary>
public sealed record ModelExchangeMalformedInvocationSummary
{
    /// <summary>Gets the safe machine-readable failure kind.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets the sanitized bounded diagnostic message.</summary>
    public string SafeMessage { get; init; } = string.Empty;

    /// <summary>Gets the safe tool name when known.</summary>
    public string? ToolName { get; init; }

    /// <summary>Gets the zero-based tool-call ordinal when known.</summary>
    public int? ToolOrdinal { get; init; }

    /// <summary>Gets the total sibling tool-call count when known.</summary>
    public int? ToolCallCount { get; init; }

    /// <summary>Gets the provider family when known.</summary>
    public string? ProviderFamily { get; init; }

    /// <summary>Gets the raw argument character count without retaining argument content.</summary>
    public int? ArgumentCharacterCount { get; init; }

    /// <summary>Gets the SHA-256 digest of raw arguments without retaining argument content.</summary>
    public string? ArgumentSha256 { get; init; }

    /// <summary>Gets the JSON parser path when available.</summary>
    public string? JsonPath { get; init; }

    /// <summary>Gets the JSON parser line number when available.</summary>
    public long? JsonLineNumber { get; init; }

    /// <summary>Gets the JSON parser byte position in line when available.</summary>
    public long? JsonBytePositionInLine { get; init; }

    /// <summary>Creates a safe summary from provider-neutral malformed-invocation diagnostics.</summary>
    /// <param name="diagnostic">Malformed invocation diagnostic.</param>
    /// <returns>Safe log summary.</returns>
    public static ModelExchangeMalformedInvocationSummary FromDiagnostic(
        MalformedInvocationDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new ModelExchangeMalformedInvocationSummary
        {
            Kind = diagnostic.Kind.ToString(),
            SafeMessage = diagnostic.SafeMessage,
            ToolName = diagnostic.ToolName,
            ToolOrdinal = diagnostic.ToolOrdinal,
            ToolCallCount = diagnostic.ToolCallCount,
            ProviderFamily = diagnostic.ProviderFamily,
            ArgumentCharacterCount = diagnostic.ArgumentCharacterCount,
            ArgumentSha256 = diagnostic.ArgumentSha256,
            JsonPath = diagnostic.JsonPath,
            JsonLineNumber = diagnostic.JsonLineNumber,
            JsonBytePositionInLine = diagnostic.JsonBytePositionInLine,
        };
    }
}

/// <summary>Compact per-request summary for model exchange diagnostics.</summary>
public sealed record ModelExchangeRequestSummary
{
    /// <summary>Gets the legacy input character count.</summary>
    public int InputCharacters { get; init; }

    /// <summary>Gets the structured message count.</summary>
    public int MessageCount { get; init; }

    /// <summary>Gets the advertised tool count.</summary>
    public int ToolCount { get; init; }

    /// <summary>Gets how tools were transported to the provider.</summary>
    public string? ToolTransportMode { get; init; }

    /// <summary>Gets the selected workload class.</summary>
    public string? WorkloadClass { get; init; }

    /// <summary>Gets the request reasoning level.</summary>
    public string? ReasoningLevel { get; init; }

    /// <summary>Gets whether the request was classified as containing sensitive data.</summary>
    public bool ContainsSensitiveData { get; init; }

    /// <summary>Gets the selected profile id, when resolved.</summary>
    public string? ResolvedProfileId { get; init; }

    /// <summary>Gets compact summaries of advertised tools.</summary>
    public IReadOnlyList<ModelExchangeToolSummary> AdvertisedTools { get; init; } = [];

    /// <summary>Gets compact chronological message summaries.</summary>
    public IReadOnlyList<ModelExchangeMessageSummary> Messages { get; init; } = [];

    /// <summary>Gets the host-owned wire estimate, when available.</summary>
    public ModelWireEstimate? WireEstimate { get; init; }
}

/// <summary>Compact advertised-tool summary.</summary>
public sealed record ModelExchangeToolSummary
{
    /// <summary>Gets the advertised tool name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the description length in characters.</summary>
    public int DescriptionCharacters { get; init; }

    /// <summary>Gets the JSON schema length in characters.</summary>
    public int SchemaCharacters { get; init; }
}

/// <summary>Compact chronological message summary.</summary>
public sealed record ModelExchangeMessageSummary
{
    /// <summary>Gets the zero-based message index.</summary>
    public int Index { get; init; }

    /// <summary>Gets the message role.</summary>
    public required string Role { get; init; }

    /// <summary>Gets the stable section id.</summary>
    public required string SectionId { get; init; }

    /// <summary>Gets the tool-call id, when applicable.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Gets the tool name, when applicable.</summary>
    public string? ToolName { get; init; }

    /// <summary>Gets the number of content parts.</summary>
    public int PartCount { get; init; }

    /// <summary>Gets the aggregate content length in characters.</summary>
    public int ContentCharacters { get; init; }

    /// <summary>Gets the distinct content kinds in this message.</summary>
    public IReadOnlyList<string> ContentKinds { get; init; } = [];
}

/// <summary>Compact per-response summary for model exchange diagnostics.</summary>
public sealed record ModelExchangeResponseSummary
{
    /// <summary>Gets the streamed chunk count.</summary>
    public int ChunkCount { get; init; }

    /// <summary>Gets aggregate visible text characters streamed.</summary>
    public int TextCharacters { get; init; }

    /// <summary>Gets aggregate reasoning characters observed without persisting reasoning text.</summary>
    public int ReasoningCharacters { get; init; }

    /// <summary>Gets the number of tool-request outputs.</summary>
    public int ToolRequestCount { get; init; }

    /// <summary>Gets model-requested tool calls in sequence order.</summary>
    public IReadOnlyList<ModelExchangeToolCallSummary> ToolCalls { get; init; } = [];

    /// <summary>Gets the final finish reason, when supplied.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Gets final usage, when supplied.</summary>
    public ModelUsage? Usage { get; init; }
}

/// <summary>Compact model-requested tool-call summary.</summary>
public sealed record ModelExchangeToolCallSummary
{
    /// <summary>Gets the chunk sequence containing the tool request.</summary>
    public int Sequence { get; init; }

    /// <summary>Gets the requested tool name.</summary>
    public required string ToolName { get; init; }

    /// <summary>Gets the requested JSON arguments.</summary>
    public required string ArgumentsJson { get; init; }
}

/// <summary>Closed JSONL event-kind names for model exchange diagnostics.</summary>
public static class ModelExchangeLogEntryKind
{
    /// <summary>A compact model request summary was submitted.</summary>
    public const string RequestSummary = "requestSummary";

    /// <summary>A model request was submitted.</summary>
    public const string Request = "request";

    /// <summary>A normalized model chunk was observed.</summary>
    public const string Chunk = "chunk";

    /// <summary>A compact model response summary was observed.</summary>
    public const string ResponseSummary = "responseSummary";

    /// <summary>The provider stream completed successfully.</summary>
    public const string Completion = "completion";

    /// <summary>The provider stream failed.</summary>
    public const string Failure = "failure";
}
