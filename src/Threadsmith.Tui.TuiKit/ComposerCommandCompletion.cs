namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Contracts;

/// <summary>A completion destination bound to one unchanged draft and input lifetime.</summary>
internal sealed record ComposerCompletionTarget(
    ComposerBuffer Buffer,
    int Revision,
    int Caret,
    int Anchor,
    long InputEpoch,
    int End,
    string Prefix,
    bool AllowEmpty);

/// <summary>Owns eligibility and the single reversible edit used by both command views.</summary>
internal sealed class ComposerCommandCompletion
{
    private readonly TuiKitCommandDiscovery _discovery;

    /// <summary>Initializes a new instance of the <see cref="ComposerCommandCompletion"/> class.</summary>
    internal ComposerCommandCompletion(TuiKitCommandDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        _discovery = discovery;
    }

    /// <summary>Captures a single partial command token; the palette also accepts an empty draft.</summary>
    internal ComposerCompletionTarget? Capture(
        ComposerBuffer buffer,
        ComposerPurpose purpose,
        long inputEpoch,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (purpose != ComposerPurpose.Conversation || buffer.Caret != buffer.Anchor)
        {
            return null;
        }

        var text = buffer.Text;
        if (text.Any(character => (char.IsControl(character) && character != '\t') || character is '\u2028' or '\u2029'))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return allowEmpty ? CreateTarget(buffer, inputEpoch, text.Length, string.Empty, allowEmpty) : null;
        }

        if (!text.StartsWith('/') || buffer.Caret == 0)
        {
            return null;
        }

        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        if (buffer.Caret > end || !string.IsNullOrWhiteSpace(text[end..])
            || _discovery.TryGet(text[..end], out _))
        {
            return null;
        }

        return CreateTarget(buffer, inputEpoch, end, text[..buffer.Caret], allowEmpty);
    }

    /// <summary>Revalidates the destination and inserts a canonical name as one undoable edit.</summary>
    internal bool TryApply(
        ComposerCompletionTarget target,
        ComposerBuffer buffer,
        ComposerPurpose purpose,
        long inputEpoch,
        string name)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(name);
        if (target != Capture(buffer, purpose, inputEpoch, target.AllowEmpty)
            || !_discovery.TryGet(name, out var descriptor))
        {
            return false;
        }

        buffer.ReplaceRange(0, target.End, descriptor.Name);
        return true;
    }

    private static ComposerCompletionTarget CreateTarget(ComposerBuffer buffer, long inputEpoch, int end, string prefix, bool allowEmpty)
    {
        return new(buffer, buffer.Revision, buffer.Caret, buffer.Anchor, inputEpoch, end, prefix, allowEmpty);
    }
}
