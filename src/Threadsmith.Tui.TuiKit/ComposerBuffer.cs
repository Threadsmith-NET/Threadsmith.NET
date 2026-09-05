namespace Threadsmith.Tui.TuiKit;

using System.Text;
using TUIKit.Unicode;

/// <summary>One draft with text-element editing and byte-bounded delta history.</summary>
internal sealed class ComposerBuffer
{
    /// <summary>Maximum accepted UTF-8 draft size.</summary>
    internal const int MaximumDraftBytes = 1024 * 1024;
    private const int MaximumHistoryBytes = 1024 * 1024;
    private const int MaximumHistoryEntries = 200;
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private readonly List<Edit> _undo = [];
    private readonly Stack<Edit> _redo = [];
    private int _historyBytes;
    private int _textBytes;

    /// <summary>Gets the exact normalized draft.</summary>
    internal string Text { get; private set; } = string.Empty;

    /// <summary>Gets the UTF-16 caret offset at a grapheme boundary.</summary>
    internal int Caret { get; private set; }

    /// <summary>Gets the selection anchor at a grapheme boundary.</summary>
    internal int Anchor { get; private set; }

    /// <summary>Gets the edit revision used to invalidate history navigation.</summary>
    internal int Revision { get; private set; }

    /// <summary>Gets grapheme boundaries including the end of the draft.</summary>
    internal int[] Boundaries { get; private set; } = [0];

    /// <summary>Gets the backend-segmented graphemes corresponding to <see cref="Boundaries"/>.</summary>
    internal Grapheme[] Elements { get; private set; } = [];

    /// <summary>Gets the first selected UTF-16 offset.</summary>
    internal int SelectionStart => Math.Min(Caret, Anchor);

    /// <summary>Gets the exclusive last selected UTF-16 offset.</summary>
    internal int SelectionEnd => Math.Max(Caret, Anchor);

    /// <summary>Gets the first selected UTF-16 offset.</summary>
    internal string Selection => Text[SelectionStart..SelectionEnd];

    /// <summary>Replaces the draft and clears its undo history.</summary>
    internal void Reset(string text = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        text = Normalize(text);
        _textBytes = GetValidatedByteCount(text);
        Text = text;
        Reindex();
        Caret = Anchor = Text.Length;
        _undo.Clear();
        _redo.Clear();
        _historyBytes = 0;
    }

    /// <summary>Replaces the selection with normalized text as one reversible edit.</summary>
    internal void Insert(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Replace(SelectionStart, SelectionEnd, Normalize(text));
    }

    /// <summary>Deletes the selection or an adjacent grapheme or word.</summary>
    internal void Delete(bool backwards, bool word = false)
    {
        if (SelectionStart != SelectionEnd)
        {
            Replace(SelectionStart, SelectionEnd, string.Empty);
            return;
        }

        var target = word ? WordBoundary(backwards) : AdjacentBoundary(backwards);
        Replace(Math.Min(Caret, target), Math.Max(Caret, target), string.Empty);
    }

    /// <summary>Moves by grapheme or word, optionally extending selection.</summary>
    internal void MoveHorizontal(bool backwards, bool extend, bool word = false)
    {
        var target = !extend && SelectionStart != SelectionEnd
            ? backwards ? SelectionStart : SelectionEnd
            : word ? WordBoundary(backwards) : AdjacentBoundary(backwards);
        MoveTo(target, extend);
    }

    /// <summary>Moves to a valid grapheme boundary, optionally extending selection.</summary>
    internal void MoveTo(int offset, bool extend = false)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        var index = Array.BinarySearch(Boundaries, offset);
        Caret = index >= 0 ? offset : Boundaries[Math.Max(0, ~index - 1)];
        if (!extend)
        {
            Anchor = Caret;
        }
    }

    /// <summary>Selects the complete draft.</summary>
    internal void SelectAll()
    {
        Anchor = 0;
        Caret = Text.Length;
    }

    /// <summary>Finds the logical line edge, with optional smart indentation handling.</summary>
    internal int LineBoundary(bool end, bool smartHome = false)
    {
        if (end)
        {
            var newline = Text.IndexOf('\n', Caret);
            return newline < 0 ? Text.Length : newline;
        }

        var start = Caret == 0 ? 0 : Text.LastIndexOf('\n', Caret - 1) + 1;
        if (smartHome)
        {
            var content = start;
            while (content < Text.Length && Text[content] is ' ' or '\t')
            {
                content++;
            }

            return Caret == content ? start : content;
        }

        return start;
    }

    /// <summary>Finds the logical line edge, with optional smart indentation handling.</summary>
    internal void DeleteToLineBoundary(bool end)
    {
        if (SelectionStart != SelectionEnd)
        {
            Delete(false);
            return;
        }

        var target = LineBoundary(end);
        Replace(Math.Min(Caret, target), Math.Max(Caret, target), string.Empty);
    }

    /// <summary>Returns the complete current logical line.</summary>
    internal string CurrentLine()
    {
        var end = LineBoundary(true);
        return Text[LineBoundary(false)..(end < Text.Length ? end + 1 : end)];
    }

    /// <summary>Deletes the current logical line as one edit.</summary>
    internal void DeleteLine()
    {
        var end = LineBoundary(true);
        Replace(LineBoundary(false), end < Text.Length ? end + 1 : end, string.Empty);
    }

    /// <summary>Indents or unindents the selected logical lines.</summary>
    internal void Indent(bool unindent)
    {
        if (!Text.AsSpan(SelectionStart, SelectionEnd - SelectionStart).Contains('\n'))
        {
            if (!unindent)
            {
                Insert("    ");
            }

            return;
        }

        var start = SelectionStart == 0 ? 0 : Text.LastIndexOf('\n', SelectionStart - 1) + 1;
        var end = SelectionEnd;
        var lines = Text[start..end].Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            // A selection ending at column zero does not include the following line.
            if (index == lines.Length - 1 && lines[index].Length == 0)
            {
                continue;
            }

            var removed = 0;
            if (unindent)
            {
                while (removed < Math.Min(4, lines[index].Length) && lines[index][removed] == ' ')
                {
                    removed++;
                }
            }

            lines[index] = unindent ? lines[index][removed..] : "    " + lines[index];
        }

        var replacement = string.Join('\n', lines);
        var revision = Revision;
        Replace(start, end, replacement);
        MoveTo(start);
        MoveTo(start + replacement.Length, true);
        if (Revision != revision && _undo.Count > 0)
        {
            _undo[^1] = _undo[^1] with { AfterCaret = Caret, AfterAnchor = Anchor };
        }
    }

    /// <summary>Reverses the most recent retained edit.</summary>
    internal void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Text = string.Concat(Text.AsSpan(0, edit.Offset), edit.Removed, Text.AsSpan(edit.Offset + edit.Inserted.Length));
        _textBytes += edit.RemovedUtf8Bytes - edit.InsertedUtf8Bytes;
        Reindex();
        Caret = edit.BeforeCaret;
        Anchor = edit.BeforeAnchor;
        _redo.Push(edit);
    }

    /// <summary>Reapplies the most recently undone edit.</summary>
    internal void Redo()
    {
        if (!_redo.TryPop(out var edit))
        {
            return;
        }

        Text = string.Concat(Text.AsSpan(0, edit.Offset), edit.Inserted, Text.AsSpan(edit.Offset + edit.Removed.Length));
        _textBytes += edit.InsertedUtf8Bytes - edit.RemovedUtf8Bytes;
        Reindex();
        Caret = edit.AfterCaret;
        Anchor = edit.AfterAnchor;
        _undo.Add(edit);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static int GetValidatedByteCount(string text)
    {
        // A strict encoder rejects malformed input before it can enter history or a cell.
        var bytes = _strictUtf8.GetByteCount(text);
        if (bytes > MaximumDraftBytes)
        {
            throw new InvalidOperationException("Composer input exceeds the 1 MiB input limit.");
        }

        return bytes;
    }

    private int AdjacentBoundary(bool backwards)
    {
        var index = Array.BinarySearch(Boundaries, Caret);
        return Boundaries[Math.Clamp(index + (backwards ? -1 : 1), 0, Boundaries.Length - 1)];
    }

    private int WordBoundary(bool backwards)
    {
        var index = Array.BinarySearch(Boundaries, Caret);
        var direction = backwards ? -1 : 1;
        for (index += direction; index > 0 && index < Boundaries.Length - 1; index += direction)
        {
            var left = Rune.GetRuneAt(Text, Boundaries[index - 1]);
            var right = Rune.GetRuneAt(Text, Boundaries[index]);
            var leftSpace = Rune.IsWhiteSpace(left);
            var rightSpace = Rune.IsWhiteSpace(right);
            if ((leftSpace && !rightSpace)
                || (!leftSpace && !rightSpace && Rune.IsLetterOrDigit(left) != Rune.IsLetterOrDigit(right)))
            {
                return Boundaries[index];
            }
        }

        return backwards ? 0 : Text.Length;
    }

    private void Replace(int start, int end, string inserted)
    {
        if (start == end && inserted.Length == 0)
        {
            return;
        }

        var removed = Text[start..end];
        var removedUtf8Bytes = _strictUtf8.GetByteCount(removed);
        var insertedUtf8Bytes = GetValidatedByteCount(inserted);
        var textBytes = _textBytes - removedUtf8Bytes + insertedUtf8Bytes;
        if (textBytes > MaximumDraftBytes)
        {
            throw new InvalidOperationException("Composer input exceeds the 1 MiB input limit.");
        }

        var text = string.Concat(Text.AsSpan(0, start), inserted, Text.AsSpan(end));
        var beforeCaret = Caret;
        var beforeAnchor = Anchor;
        Text = text;
        _textBytes = textBytes;
        Reindex();
        var after = Array.BinarySearch(Boundaries, start + inserted.Length);
        Caret = Anchor = Boundaries[after >= 0 ? after : ~after];
        var edit = new Edit(
            start,
            removed,
            inserted,
            removedUtf8Bytes,
            insertedUtf8Bytes,
            beforeCaret,
            beforeAnchor,
            Caret,
            Anchor);
        foreach (var discarded in _redo)
        {
            _historyBytes -= discarded.Bytes;
        }

        _redo.Clear();
        _undo.Add(edit);
        _historyBytes += edit.Bytes;
        while (_undo.Count > MaximumHistoryEntries || _historyBytes > MaximumHistoryBytes)
        {
            _historyBytes -= _undo[0].Bytes;
            _undo.RemoveAt(0);
        }
    }

    private void Reindex()
    {
        Elements = [.. Graphemes.Split(Text)];
        var boundaries = new int[Elements.Length + 1];
        for (var index = 0; index < Elements.Length; index++)
        {
            boundaries[index + 1] = boundaries[index] + Elements[index].Text.Length;
        }

        Boundaries = boundaries;
        Revision++;
    }

    private readonly record struct Edit(
        int Offset,
        string Removed,
        string Inserted,
        int RemovedUtf8Bytes,
        int InsertedUtf8Bytes,
        int BeforeCaret,
        int BeforeAnchor,
        int AfterCaret,
        int AfterAnchor)
    {
        internal int Bytes => (Removed.Length + Inserted.Length) * sizeof(char);
    }
}
