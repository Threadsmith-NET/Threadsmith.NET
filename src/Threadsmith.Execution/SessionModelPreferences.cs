namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Owns the active model identity and reasoning preference for one session.</summary>
public sealed class SessionModelPreferences : IThinkingTextPreferences
{
    private readonly Lock _sync = new();
    private ModelProfileId? _currentProfileId;
    private ReasoningLevel _reasoning;
    private long _generation;
    private bool _includeReasoningText;

    /// <summary>Initializes a new instance of the <see cref="SessionModelPreferences"/> class.</summary>
    public SessionModelPreferences()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SessionModelPreferences"/> class.</summary>
    /// <param name="profileId">The effective profile.</param>
    /// <param name="defaultReasoningLevel">The profile's validated default level.</param>
    public SessionModelPreferences(
        ModelProfileId profileId,
        ReasoningLevel defaultReasoningLevel)
    {
        if (profileId == default)
        {
            throw new ArgumentException("The model profile id cannot be default.", nameof(profileId));
        }

        _currentProfileId = profileId;
        _reasoning = defaultReasoningLevel;
    }

    /// <summary>Gets the reasoning effort currently effective for the session.</summary>
    public ReasoningLevel Reasoning
    {
        get
        {
            lock (_sync)
            {
                return _reasoning;
            }
        }
    }

    /// <summary>Gets the last concrete model profile resolved for the session.</summary>
    public ModelProfileId? CurrentProfileId
    {
        get
        {
            lock (_sync)
            {
                return _currentProfileId;
            }
        }
    }

    /// <summary>Gets the monotonic selection generation captured by new model requests.</summary>
    public long Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    /// <inheritdoc />
    public bool IncludeReasoningText
    {
        get
        {
            lock (_sync)
            {
                return _includeReasoningText;
            }
        }
    }

    /// <inheritdoc />
    public void SetIncludeReasoningText(bool include)
    {
        lock (_sync)
        {
            _includeReasoningText = include;
        }
    }

    /// <summary>
    /// Resolves the level for a model request and durably resets it when the effective profile changes.
    /// </summary>
    /// <param name="resolvedProfileId">The profile resolved for the current run, or <see langword="null"/>.</param>
    /// <param name="fallbackReasoning">Validated default used when the resolved profile cannot disable reasoning.</param>
    /// <returns>The effective reasoning level.</returns>
    public ReasoningLevel ResolveFor(ModelProfileId? resolvedProfileId, ReasoningLevel? fallbackReasoning = null)
    {
        lock (_sync)
        {
            if (resolvedProfileId is null)
            {
                return _reasoning;
            }

            if (_currentProfileId is null)
            {
                _currentProfileId = resolvedProfileId;
            }
            else if (_currentProfileId.Value != resolvedProfileId.Value)
            {
                _currentProfileId = resolvedProfileId;
                _reasoning = fallbackReasoning ?? ReasoningLevel.None;
                _generation++;
            }

            return _reasoning;
        }
    }

    /// <summary>Sets a validated reasoning level against the effective profile as one operation.</summary>
    /// <param name="profileId">The effective profile.</param>
    /// <param name="reasoningLevel">The supported reasoning level selected by the user.</param>
    public void SetReasoning(ModelProfileId profileId, ReasoningLevel reasoningLevel)
    {
        if (profileId == default)
        {
            throw new ArgumentException("The model profile id cannot be default.", nameof(profileId));
        }

        lock (_sync)
        {
            var changed = _currentProfileId != profileId || _reasoning != reasoningLevel;
            _currentProfileId = profileId;
            _reasoning = reasoningLevel;
            if (changed)
            {
                _generation++;
            }
        }
    }

    /// <summary>Captures the active model and reasoning as one immutable request boundary.</summary>
    public SessionModelPreferenceSnapshot Capture()
    {
        lock (_sync)
        {
            return new SessionModelPreferenceSnapshot(
                _currentProfileId,
                _reasoning,
                _generation);
        }
    }
}

/// <summary>One immutable active-model snapshot retained by an in-flight request.</summary>
/// <param name="ProfileId">Selected configured profile.</param>
/// <param name="Reasoning">Effective host reasoning level.</param>
/// <param name="Generation">Selection generation.</param>
public sealed record SessionModelPreferenceSnapshot(
    ModelProfileId? ProfileId,
    ReasoningLevel Reasoning,
    long Generation)
{
    /// <summary>Resolves request reasoning from this immutable generation without changing live session state.</summary>
    public ReasoningLevel ResolveFor(ModelProfileId? resolvedProfileId, ReasoningLevel? fallbackReasoning = null)
    {
        return resolvedProfileId is null
            || ProfileId is null
            || ProfileId == resolvedProfileId
                ? Reasoning
                : fallbackReasoning ?? ReasoningLevel.None;
    }
}
