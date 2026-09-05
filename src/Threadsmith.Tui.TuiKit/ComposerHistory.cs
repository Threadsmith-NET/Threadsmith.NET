namespace Threadsmith.Tui.TuiKit;

using System.Text;

// Session-local history. Store exact inputs once, with bounded entry/byte retention.

/// <summary>Retains bounded submissions with prefix-first navigation and draft restoration.</summary>
internal sealed class ComposerHistory
{
    /// <summary>Maximum number of submitted entries retained.</summary>
    internal const int EntryLimit = 1000;

    /// <summary>Maximum retained UTF-8 history size.</summary>
    internal const int ByteLimit = 1024 * 1024;
    private readonly List<Entry> _entries = [];
    private readonly Stack<int> _path = [];
    private string _draft = string.Empty;
    private string? _recalled;
    private bool _lastWasNavigation;
    private int _bytes;

    /// <summary>Gets the number of retained submissions.</summary>
    internal int Count => _entries.Count;

    /// <summary>Retains a submission without consecutive duplicates.</summary>
    internal void Add(string text)
    {
        _path.Clear();
        _recalled = null;
        _lastWasNavigation = false;
        if (text.Length == 0 || (_entries.Count > 0 && _entries[^1].Text == text))
        {
            return;
        }

        var entry = new Entry(text, Encoding.UTF8.GetByteCount(text));
        _entries.Add(entry);
        _bytes += entry.Bytes;
        while (_entries.Count > EntryLimit || _bytes > ByteLimit)
        {
            _bytes -= _entries[0].Bytes;
            _entries.RemoveAt(0);
        }
    }

    /// <summary>Recalls a matching submission or restores the unsubmitted draft.</summary>
    internal bool Navigate(ComposerBuffer buffer, bool previous, bool atEdge)
    {
        var continuing = _lastWasNavigation && buffer.Text == _recalled;
        _lastWasNavigation = false;
        if (buffer.Text != _recalled)
        {
            _path.Clear();
        }

        if (_entries.Count == 0 || (!continuing && (!atEdge || (previous && buffer.Text.Contains('\n')))))
        {
            return false;
        }

        if (previous)
        {
            if (_path.Count == 0)
            {
                _draft = buffer.Text;
            }

            var start = _path.Count == 0 ? _entries.Count - 1 : _path.Peek() - 1;
            var match = Find(start);
            if (match < 0)
            {
                return false;
            }

            _path.Push(match);
            _recalled = _entries[match].Text;
        }
        else
        {
            if (!_path.TryPop(out _))
            {
                return false;
            }

            _recalled = _path.Count > 0 ? _entries[_path.Peek()].Text : _draft;
        }

        // Recall is one edit, so undo can return to the draft as in the existing editor.
        buffer.SelectAll();
        buffer.Insert(_recalled);
        _lastWasNavigation = _path.Count > 0;
        return true;
    }

    /// <summary>Ends consecutive history-key navigation while preserving the draft.</summary>
    internal void LeaveNavigationKey() => _lastWasNavigation = false;

    private int Find(int start)
    {
        // Existing prompt priority: exact prefix, folded prefix, exact substring,
        // folded substring, then newest different entry. Preserve host input order.
        for (var pass = 0; pass < 5; pass++)
        {
            for (var index = start; index >= 0; index--)
            {
                var entry = _entries[index].Text;
                if (entry == _draft)
                {
                    continue;
                }

                var matches = pass switch
                {
                    0 => entry.StartsWith(_draft, StringComparison.Ordinal),
                    1 => entry.StartsWith(_draft, StringComparison.OrdinalIgnoreCase),
                    2 => entry.Contains(_draft, StringComparison.Ordinal),
                    3 => entry.Contains(_draft, StringComparison.OrdinalIgnoreCase),
                    _ => true,
                };
                if (matches)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private readonly record struct Entry(string Text, int Bytes);
}
