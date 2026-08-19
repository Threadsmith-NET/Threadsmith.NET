namespace Threadsmith.Models.OpenAiCompatible;

using Threadsmith.Models;

/// <summary>Closed request-control modes supported by the OpenAI-compatible adapter.</summary>
public enum OpenAiReasoningControlMode
{
    /// <summary>Uses the standard <c>reasoning_effort</c> property.</summary>
    StandardEffort,

    /// <summary>Uses explicit values for <c>reasoning_effort</c>.</summary>
    MappedEffort,

    /// <summary>Uses a compiled <c>chat_template_kwargs</c> shape.</summary>
    ChatTemplate,

    /// <summary>Uses a compiled fixed request addition and exposes no effort control.</summary>
    Fixed,

    /// <summary>Reasoning is intrinsic and cannot be controlled.</summary>
    AlwaysOn,

    /// <summary>Reasoning is unavailable.</summary>
    Unsupported,
}

/// <summary>Allowlisted reasoning fields accepted from streamed response deltas.</summary>
public enum OpenAiReasoningResponseMode
{
    /// <summary>No reasoning field is accepted.</summary>
    None,

    /// <summary>Accepts <c>reasoning_content</c>.</summary>
    ReasoningContent,

    /// <summary>Accepts the legacy <c>reasoning</c> alias.</summary>
    Reasoning,
}

/// <summary>Compiled chat-template request shapes needed by supported endpoints.</summary>
public enum OpenAiChatTemplateKind
{
    /// <summary>Emits the enable/preserve thinking booleans.</summary>
    EnableThinkingWithPreservation,

    /// <summary>Emits the generic thinking boolean and mapped template effort.</summary>
    ThinkingWithEffort,
}

/// <summary>Compiled fixed request additions needed by supported endpoints.</summary>
public enum OpenAiFixedRequestKind
{
    /// <summary>Emits enabled thinking environment controls with a fixed 4096-token budget.</summary>
    ThinkingEnvironmentBudget4096,

    /// <summary>Emits the chat-template shape that disables thinking while preserving prior thinking content.</summary>
    DisableThinkingWithPreservation,
}

/// <summary>Versioned, closed reasoning compatibility configuration for one model.</summary>
public sealed record OpenAiReasoningCompatibilityConfiguration
{
    /// <summary>Supported schema version. Unknown versions fail closed.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Compiled request-control mode.</summary>
    public OpenAiReasoningControlMode Mode { get; init; }

    /// <summary>Allowlisted streamed reasoning extraction mode.</summary>
    public OpenAiReasoningResponseMode ResponseMode { get; init; }
        = OpenAiReasoningResponseMode.ReasoningContent;

    /// <summary>Explicit provider values keyed by supported host level.</summary>
    public IReadOnlyDictionary<ReasoningLevel, string> LevelMap { get; init; }
        = new Dictionary<ReasoningLevel, string>();

    /// <summary>Compiled chat-template shape when <see cref="Mode"/> is <see cref="OpenAiReasoningControlMode.ChatTemplate"/>.</summary>
    public OpenAiChatTemplateKind? ChatTemplateKind { get; init; }

    /// <summary>Compiled fixed request shape when <see cref="Mode"/> is <see cref="OpenAiReasoningControlMode.Fixed"/>.</summary>
    public OpenAiFixedRequestKind? FixedRequestKind { get; init; }
}
