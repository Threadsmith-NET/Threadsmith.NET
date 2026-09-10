namespace Threadsmith.Core;

/// <summary>Transient session choice over inclusion of displayable model thinking summaries.</summary>
public interface IThinkingTextPreferences
{
    /// <summary>Whether future requests may ask for displayable thinking text.</summary>
    bool IncludeReasoningText { get; }

    /// <summary>Changes the preference for future requests without resubmitting in-flight work.</summary>
    void SetIncludeReasoningText(bool include);
}
