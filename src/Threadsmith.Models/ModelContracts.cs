namespace Threadsmith.Models;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;

/// <summary>Reasoning effort levels selectable for a model request.</summary>
public enum ReasoningLevel
{
    /// <summary>No reasoning effort; disables thinking when supported.</summary>
    None,

    /// <summary>Minimal reasoning effort.</summary>
    Minimal,

    /// <summary>Low reasoning effort.</summary>
    Low,

    /// <summary>Medium reasoning effort.</summary>
    Medium,

    /// <summary>High reasoning effort.</summary>
    High,
}

/// <summary>Provider-neutral model-visible tool definition.</summary>
public sealed record ModelToolDefinition
{
    /// <summary>Stable tool name.</summary>
    public required string Name { get; init; }

    /// <summary>Purpose shown to the model.</summary>
    public required string Description { get; init; }

    /// <summary>JSON object schema for the tool arguments.</summary>
    public required string ArgumentsJsonSchema { get; init; }

    /// <summary>Whether providers should use strict argument generation when their wire protocol supports it.</summary>
    /// <remarks>Canonical host validation remains authoritative regardless of this preference.</remarks>
    public bool PreferStrictArguments { get; init; }
}

/// <summary>Provider-neutral exact instructions contributed to one provider request.</summary>
public sealed record ModelProviderInstructions
{
    /// <summary>Stable section identity used for capacity and request inspection.</summary>
    public required string SectionId { get; init; }

    /// <summary>Exact provider-visible instruction content.</summary>
    public required string Content { get; init; }
}

/// <summary>Resolves an optional compiled provider instruction contribution for a selected profile.</summary>
public interface IModelProviderInstructionResolver
{
    /// <summary>Resolves the exact instruction contribution for one configured profile.</summary>
    /// <param name="profileId">Selected configured profile.</param>
    /// <returns>The exact contribution, or <see langword="null"/> when the provider declares none.</returns>
    ModelProviderInstructions? Resolve(ModelProfileId profileId);
}

/// <summary>Request passed to a host-owned model provider.</summary>
public sealed record ModelStreamRequest
{
    /// <summary>Request correlation id.</summary>
    public required RunId RunId { get; init; }

    /// <summary>User input.</summary>
    public required string Input { get; init; }

    /// <summary>Deterministic seed when supported.</summary>
    public int Seed { get; init; }

    /// <summary>Zero-based tool-continuation round for this run.</summary>
    public int ToolContinuationRound { get; init; }

    /// <summary>Monotonic provider-neutral history generation after host rewrites.</summary>
    /// <remarks>Opaque provider continuation identities from an older generation must not be reused.</remarks>
    public long HistoryRewriteGeneration { get; init; }

    /// <summary>Workload used for per-request configured-model selection.</summary>
    public WorkloadClass WorkloadClass { get; init; } = WorkloadClass.General;

    /// <summary>Whether the assembled request contains content classified as sensitive.</summary>
    public bool ContainsSensitiveData { get; init; }

    /// <summary>Capabilities required by the assembled request.</summary>
    public ModelCapabilitySet RequiredCapabilities { get; init; } = new() { Streaming = true };

    /// <summary>Hard selection constraints attached by the context governor.</summary>
    public ModelSelectionConstraints SelectionConstraints { get; init; } = new();

    /// <summary>Profile resolved by host policy before provider invocation.</summary>
    public ModelProfileId? ResolvedProfileId { get; init; }

    /// <summary>Optional per-request output ceiling; providers reject values above the resolved profile limit.</summary>
    public int? MaximumOutputTokens { get; init; }

    /// <summary>Reasoning effort level for this request; defaults to <see cref="ReasoningLevel.None"/>.</summary>
    public ReasoningLevel ReasoningLevel { get; init; } = ReasoningLevel.None;

    /// <summary>Host-authorized tools available during this request.</summary>
    public IReadOnlyList<ModelToolDefinition> Tools { get; init; } = [];

    /// <summary>
    /// Whether the provider may return multiple tool calls in one response; <see langword="null"/> preserves its default.
    /// </summary>
    /// <remarks>This does not authorize tool execution or require the host to execute accepted calls concurrently.</remarks>
    public bool? AllowMultipleToolCalls { get; init; }

    /// <summary>Structured chronological messages; empty retains legacy <see cref="Input"/> behavior.</summary>
    public IReadOnlyList<ModelMessage> Messages { get; init; } = [];

    /// <summary>Versioned stable-prefix and cache-family layout.</summary>
    public ModelRequestLayout? Layout { get; init; }

    /// <summary>How canonical tool schemas are transported.</summary>
    public ToolTransportMode ToolTransportMode { get; init; } = ToolTransportMode.Native;

    /// <summary>Optional exact request-owned provider instructions counted before dispatch.</summary>
    public ModelProviderInstructions? ProviderInstructions { get; init; }

    /// <summary>Host-owned estimate of the exact serialized request capacity.</summary>
    public ModelWireEstimate? WireEstimate { get; init; }

    /// <summary>Provider cache/continuation capabilities captured for this request.</summary>
    public ModelCacheCapabilities CacheCapabilities { get; init; } = new();

    /// <summary>Deterministic explicit cache breakpoint plan when supported.</summary>
    public ModelCachePlan? CachePlan { get; init; }

    /// <summary>Binding metadata for an optional opaque continuation reference.</summary>
    public ModelContinuationBinding? ContinuationBinding { get; init; }
}

/// <summary>Provider-neutral usage and estimated monetary cost.</summary>
public sealed record ModelUsage(
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost = 0,
    bool IsEstimate = false,
    ModelCacheUsage? Cache = null);

/// <summary>Provider-neutral reason that a model stream finished.</summary>
public enum ModelFinishReason
{
    /// <summary>The provider completed the response normally.</summary>
    Stop,

    /// <summary>The provider requested one or more tools.</summary>
    ToolCalls,

    /// <summary>The provider reached its configured output limit.</summary>
    Length,

    /// <summary>The provider returned a reason the host does not recognize.</summary>
    Other,
}

/// <summary>Provider-neutral streaming chunk.</summary>
public sealed record ModelChunk
{
    /// <summary>Text delta.</summary>
    public string? Text { get; init; }

    /// <summary>Structured output.</summary>
    public ModelOutput? Output { get; init; }

    /// <summary>Usage when supplied.</summary>
    public ModelUsage? Usage { get; init; }

    /// <summary>Normalized completion reason when the provider ends a choice.</summary>
    public ModelFinishReason? FinishReason { get; init; }

    /// <summary>Reasoning text delta, separate from <see cref="Text"/>.</summary>
    public string? Reasoning { get; init; }
}

/// <summary>Host-owned model provider facade.</summary>
public interface IModelProvider
{
    /// <summary>Streams provider-neutral chunks.</summary>
    IAsyncEnumerable<ModelChunk> StreamAsync(
        ModelStreamRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Scripted failure kind.</summary>
public enum ScriptFailureKind
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>Transient provider error.</summary>
    TransientProvider,

    /// <summary>Malformed output.</summary>
    MalformedOutput,

    /// <summary>Cancellation.</summary>
    Cancellation,
}

/// <summary>A scripted turn.</summary>
public sealed record ScriptedTurn
{
    /// <summary>Reasoning emitted before answer text.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Text emitted in deterministic seeded chunks.</summary>
    public string? Text { get; init; }

    /// <summary>Optional tool name.</summary>
    public string? ToolName { get; init; }

    /// <summary>JSON tool arguments.</summary>
    public string ArgumentsJson { get; init; } = "{}";

    /// <summary>Optional usage.</summary>
    public ModelUsage? Usage { get; init; }

    /// <summary>Optional typed model output.</summary>
    public ModelOutput? Output { get; init; }

    /// <summary>Failure to inject.</summary>
    public ScriptFailureKind Failure { get; init; }
}

/// <summary>Versioned deterministic model script.</summary>
public sealed record ScriptedSession
{
    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Ordered turns.</summary>
    public IReadOnlyList<ScriptedTurn> Turns { get; init; } = [];
}

/// <summary>Exception representing a retryable provider failure.</summary>
public sealed class TransientModelException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TransientModelException"/> class.</summary>
    public TransientModelException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TransientModelException"/> class.</summary>
    public TransientModelException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TransientModelException"/> class.</summary>
    public TransientModelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Safe failure categories for malformed model-authored invocations.</summary>
public enum MalformedInvocationFailureKind
{
    /// <summary>Tool arguments were not valid JSON.</summary>
    InvalidJsonArguments,

    /// <summary>Tool arguments were valid JSON but not a JSON object.</summary>
    NonObjectArguments,

    /// <summary>The tool name was omitted or blank.</summary>
    MissingToolName,

    /// <summary>The requested tool is unknown to the host.</summary>
    UnknownTool,

    /// <summary>The requested tool is not currently available to the model.</summary>
    UnavailableTool,

    /// <summary>The arguments did not match the tool's schema or invariants.</summary>
    ArgumentSchemaMismatch,

    /// <summary>The requested tool is invalid in the current run phase.</summary>
    PhaseInvalidTool,

    /// <summary>The response mixed plan/mutation output with another tool-producing output.</summary>
    MultipleToolProducingOutputs,

    /// <summary>The structured plan payload did not match the required schema.</summary>
    PlanSchemaMismatch,

    /// <summary>The structured mutation payload did not match the required schema.</summary>
    MutationSchemaMismatch,

    /// <summary>Plan sanity checks found repairable issues.</summary>
    PlanSanityRepair,

    /// <summary>Pre-mutation analysis found repairable diagnostics.</summary>
    PreMutationDiagnostics,

    /// <summary>Post-apply validation failed and can be corrected by another mutation proposal.</summary>
    PostApplyValidation,
}

/// <summary>Sanitized diagnostic metadata for a recoverable malformed model invocation.</summary>
public sealed record MalformedInvocationDiagnostic
{
    /// <summary>Safe machine-readable failure kind.</summary>
    public required MalformedInvocationFailureKind Kind { get; init; }

    /// <summary>Short sanitized explanation that excludes raw arguments and provider bodies.</summary>
    public required string SafeMessage { get; init; }

    /// <summary>Tool name when it is safe and known.</summary>
    public string? ToolName { get; init; }

    /// <summary>Zero-based tool-call ordinal in the model response when known.</summary>
    public int? ToolOrdinal { get; init; }

    /// <summary>Total sibling tool-call count when known.</summary>
    public int? ToolCallCount { get; init; }

    /// <summary>Provider family that produced the malformed invocation when known.</summary>
    public string? ProviderFamily { get; init; }

    /// <summary>Raw argument character count, without retaining the argument content.</summary>
    public int? ArgumentCharacterCount { get; init; }

    /// <summary>SHA-256 digest of the raw argument content, without retaining the content itself.</summary>
    public string? ArgumentSha256 { get; init; }

    /// <summary>JSON parser path when available.</summary>
    public string? JsonPath { get; init; }

    /// <summary>JSON parser line number when available.</summary>
    public long? JsonLineNumber { get; init; }

    /// <summary>JSON parser byte position within the line when available.</summary>
    public long? JsonBytePositionInLine { get; init; }
}

/// <summary>Exception representing invalid structured provider output.</summary>
public class MalformedModelOutputException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="MalformedModelOutputException"/> class.</summary>
    public MalformedModelOutputException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MalformedModelOutputException"/> class.</summary>
    public MalformedModelOutputException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MalformedModelOutputException"/> class.</summary>
    public MalformedModelOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Exception carrying safe metadata for a recoverable malformed model invocation.</summary>
public sealed class MalformedInvocationException : MalformedModelOutputException
{
    /// <summary>Initializes a new instance of the <see cref="MalformedInvocationException"/> class.</summary>
    public MalformedInvocationException()
        : this(CreateCompatibilityDiagnostic("The model emitted a malformed invocation."))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MalformedInvocationException"/> class.</summary>
    public MalformedInvocationException(string message)
        : this(CreateCompatibilityDiagnostic(message))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MalformedInvocationException"/> class.</summary>
    public MalformedInvocationException(string message, Exception innerException)
        : this(CreateCompatibilityDiagnostic(message), innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MalformedInvocationException"/> class.</summary>
    public MalformedInvocationException(MalformedInvocationDiagnostic diagnostic)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).SafeMessage)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>Initializes a new instance of the <see cref="MalformedInvocationException"/> class.</summary>
    public MalformedInvocationException(MalformedInvocationDiagnostic diagnostic, Exception innerException)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).SafeMessage, innerException)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the sanitized diagnostic metadata.</summary>
    public MalformedInvocationDiagnostic Diagnostic { get; }

    private static MalformedInvocationDiagnostic CreateCompatibilityDiagnostic(string message)
    {
        return new MalformedInvocationDiagnostic
        {
            Kind = MalformedInvocationFailureKind.ArgumentSchemaMismatch,
            SafeMessage = string.IsNullOrWhiteSpace(message)
                ? "The model emitted a malformed invocation."
                : message,
        };
    }
}

/// <summary>Exception representing a non-transient model-provider rejection.</summary>
public sealed class ModelProviderException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ModelProviderException"/> class.</summary>
    public ModelProviderException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelProviderException"/> class.</summary>
    public ModelProviderException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelProviderException"/> class.</summary>
    public ModelProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Exception raised when a provider exceeds the configured request timeout.</summary>
public sealed class ModelProviderTimeoutException : TimeoutException
{
    /// <summary>Initializes a new instance of the <see cref="ModelProviderTimeoutException"/> class.</summary>
    public ModelProviderTimeoutException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelProviderTimeoutException"/> class.</summary>
    public ModelProviderTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelProviderTimeoutException"/> class.</summary>
    public ModelProviderTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Classifies model and host failures for bounded retry decisions.</summary>
public static class ModelFailureClassifier
{
    /// <summary>Maps a failure to the host retry taxonomy.</summary>
    public static RetryClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            TransientModelException => RetryClassification.TransientProvider,
            ModelProviderTimeoutException => RetryClassification.TransientProvider,
            MalformedModelOutputException => RetryClassification.MalformedOutput,
            _ => RetryClassification.Permanent,
        };
    }
}

/// <summary>Replays an immutable model script deterministically.</summary>
public sealed class FakeModelProvider : IModelProvider
{
    private readonly ScriptedSession _script;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _tokenDelay;

    /// <summary>Initializes a new instance of the <see cref="FakeModelProvider"/> class.</summary>
    public FakeModelProvider(
        ScriptedSession script,
        TimeSpan? tokenDelay = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.SchemaVersion != 1)
        {
            throw new NotSupportedException($"Unsupported script schema {script.SchemaVersion}.");
        }

        if (tokenDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenDelay));
        }

        _script = script;
        _tokenDelay = tokenDelay ?? TimeSpan.Zero;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Loads a JSON model script.</summary>
    public static ScriptedSession Load(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<ScriptedSession>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() },
            }) ?? throw new JsonException("The scripted session was empty.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelChunk> StreamAsync(
        ModelStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ToolContinuationRound < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.ToolContinuationRound,
                "The tool continuation round cannot be negative.");
        }

        var random = new Random(request.Seed);
        var skippedToolRounds = 0;
        foreach (var turn in _script.Turns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skippedToolRounds < request.ToolContinuationRound)
            {
                if (turn.ToolName is not null)
                {
                    skippedToolRounds++;
                }

                continue;
            }

            if (turn.Failure == ScriptFailureKind.TransientProvider)
            {
                throw new TransientModelException("Scripted transient provider failure.");
            }

            if (turn.Failure == ScriptFailureKind.MalformedOutput)
            {
                throw new MalformedModelOutputException("Scripted malformed model output.");
            }

            if (turn.Failure == ScriptFailureKind.Cancellation)
            {
                throw new OperationCanceledException("Scripted cancellation.", cancellationToken);
            }

            if (turn.Reasoning is not null)
            {
                yield return new ModelChunk { Reasoning = turn.Reasoning };
            }

            if (turn.Text is not null)
            {
                var tokens = turn.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < tokens.Length;)
                {
                    var count = Math.Min(random.Next(1, 3), tokens.Length - index);
                    if (_tokenDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_tokenDelay, _timeProvider, cancellationToken);
                    }

                    yield return new ModelChunk
                    {
                        Text = string.Join(' ', tokens.Skip(index).Take(count)) + " ",
                    };
                    index += count;
                }
            }

            var endsWithToolRequest = false;
            if (turn.ToolName is not null)
            {
                try
                {
                    using var arguments = JsonDocument.Parse(turn.ArgumentsJson);
                    if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new MalformedModelOutputException(
                            "Scripted tool arguments must be a JSON object.");
                    }
                }
                catch (JsonException exception)
                {
                    throw new MalformedModelOutputException(
                        "Scripted tool arguments are not valid JSON.",
                        exception);
                }

                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(turn.ToolName, turn.ArgumentsJson),
                };
                endsWithToolRequest = true;
            }

            if (turn.Output is not null)
            {
                ModelOutputValidator.Validate(turn.Output);
                yield return new ModelChunk { Output = turn.Output };
            }

            if (turn.Usage is not null)
            {
                yield return new ModelChunk { Usage = turn.Usage };
            }

            if (endsWithToolRequest)
            {
                yield break;
            }
        }
    }
}
