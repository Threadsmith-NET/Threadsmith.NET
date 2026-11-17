namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;

/// <summary>Source controls and exact selection mapping for the retained transcript.</summary>
internal sealed partial class TranscriptView
{
    private readonly Dictionary<string, string> _sourcePreviews = new(StringComparer.Ordinal);
    private SourceDocument? _sourceDocument;

    /// <summary>Gets or sets the host-approved full-source action for this view.</summary>
    internal Action<ToolSourceDetail>? SourceRequested { get; set; }

    /// <summary>Initializes a dedicated modal transcript, retaining the unchanged copy authority.</summary>
    internal void PresentSource(SourceDocument document)
    {
        Present(new PresentationBatch([]) { ReplaceOutput = true });
        _sourceDocument = document;
        Present(new PresentationBatch([new PresentationTextItem([new(document.DisplaySource, PresentationTextRole.MarkdownCode)])]));
    }

    private static string? ScriptControlId(ToolSourceDetail? source)
    {
        return source is null ? null : $"script:{source.ToolInvocationId.Value}";
    }

    private bool ActivateControl(string id)
    {
        if (!id.StartsWith("view-script:", StringComparison.Ordinal))
        {
            return ToggleDisclosure(id);
        }

        var detail = _items.OfType<PresentationTextItem>().Select(item => item.SourceDetail)
            .FirstOrDefault(source => source is { Availability: ToolSourceAvailability.Available, Source: not null }
                && id == "view-" + ScriptControlId(source));
        if (detail is null)
        {
            return false;
        }

        SourceRequested?.Invoke(detail);
        return true;
    }

    private void AppendScriptSource(PresentationItem item)
    {
        if (item is not PresentationTextItem { SourceDetail: { } detail } textItem)
        {
            return;
        }

        var id = ScriptControlId(detail) ?? throw new InvalidOperationException("Missing source identity.");
        AppendContent(new("\n", PresentationTextRole.Default));
        var firstId = _lines.LastOrDefault()?.Id ?? _nextId;
        if (detail is not { Availability: ToolSourceAvailability.Available, Source: { } source })
        {
            AppendContent(new("C# script execution — source unavailable (live retention limit or restored result).\n", PresentationTextRole.Muted));
        }
        else
        {
            var expanded = _expandedDisclosures.Contains(id);
            var header = $"[{(expanded ? '-' : '+')}] C# script execution\n";
            const string actionText = "   └ View full script…\n";
            string? visiblePreview = null;
            var footer = string.Empty;
            if (expanded)
            {
                var preview = SourceDocument.Preview(source);
                if (!_sourcePreviews.TryGetValue(id, out var body))
                {
                    // Match feedback detail lines without tokenizing source or interpreting markup.
                    body = "   │ " + Safe(preview.Text).Replace("\n", "\n   │ ", StringComparison.Ordinal) + "\n";
                    _sourcePreviews.Add(id, body);
                }

                footer = preview.Truncated ? "   │ [Preview truncated: first 20 lines / 8,192 characters]\n" : string.Empty;
                var result = string.Concat(textItem.Segments.Select(segment => segment.Text)) + "\n";
                if (FitsDisclosure(result + header + body + footer + actionText))
                {
                    visiblePreview = body;
                }
                else
                {
                    header = "[-] C# script execution — Preview omitted: retention limits.\n";
                    if (!FitsDisclosure(result + header + actionText))
                    {
                        // Same size as the collapsed header: do not evict output just to explain omission.
                        header = "[!] C# script execution\n";
                    }
                }
            }

            var headerId = _lines.LastOrDefault()?.Id ?? _nextId;
            AppendContent(new(header, expanded && visiblePreview is null ? PresentationTextRole.Warning : PresentationTextRole.Status));
            _disclosureHeaders[headerId] = id;
            if (visiblePreview is not null)
            {
                AppendContent(new(visiblePreview + footer, PresentationTextRole.Muted));
            }

            var actionId = _lines.LastOrDefault()?.Id ?? _nextId;
            AppendContent(new("   └ ", PresentationTextRole.Muted));
            AppendContent(new(actionText[5..], PresentationTextRole.Hyperlink));
            for (var index = FirstSourceLine(actionId); index < _lines.Count; index++)
            {
                if (_lines[index].Text.Length > 0)
                {
                    _disclosureHeaders[_lines[index].Id] = "view-" + id;
                }
            }
        }

        for (var index = FirstSourceLine(firstId); index < _lines.Count; index++)
        {
            var line = _lines[index];

            // The writable tail belongs to the next item, not this source attachment.
            if (index != _lines.Count - 1 || line.Text.Length > 0)
            {
                _disclosureLines[line.Id] = id;
            }
        }
    }

    private void AppendSourceDocument(SourceDocument document)
    {
        var firstId = _lines.LastOrDefault()?.Id ?? _nextId;

        // Append and wrap text once, rather than rebuilding every highlighted token's prefix.
        AppendContent(new(document.DisplaySource, PresentationTextRole.MarkdownCode));
        var first = FirstSourceLine(firstId);
        var offset = document.DisplaySource.Length;
        for (var index = first; index < _lines.Count; index++)
        {
            offset -= _lines[index].Text.Length + (index > first && !_lines[index].Continued ? 1 : 0);
        }

        var segmentIndex = 0;
        for (var index = first; index < _lines.Count; index++)
        {
            var line = _lines[index];
            var lineEnd = offset + line.Text.Length;
            var ranges = new List<StyledRange>();
            while (segmentIndex < document.Ranges.Count)
            {
                var segment = document.Ranges[segmentIndex];
                var segmentEnd = segment.Start + segment.Length;
                if (segment.Start >= lineEnd)
                {
                    break;
                }

                var start = Math.Max(offset, segment.Start);
                var end = Math.Min(lineEnd, segmentEnd);
                if (end > start)
                {
                    ranges.Add(new(start - offset, end - offset, segment.Role, null));
                }

                if (segmentEnd > lineEnd)
                {
                    break;
                }

                segmentIndex++;
            }

            _styles[line.Id] = ranges;
            offset = lineEnd + (index + 1 < _lines.Count && !_lines[index + 1].Continued ? 1 : 0);
        }
    }

    private int FirstSourceLine(long id)
    {
        // Appending can trim the head, so an earlier list index is not stable. IDs remain ordered.
        var low = 0;
        var high = _lines.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_lines[middle].Id < id)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private void RemoveScriptSource(PresentationItem item)
    {
        if (item is not PresentationTextItem { SourceDetail: { } source })
        {
            return;
        }

        var id = ScriptControlId(source) ?? throw new InvalidOperationException("Missing source identity.");
        _sourcePreviews.Remove(id);
        _expandedDisclosures.Remove(id);
        if (_focusedDisclosure == id || _focusedDisclosure == "view-" + id)
        {
            _focusedDisclosure = null;
        }

        for (var index = _lines.Count - 1; index >= 0; index--)
        {
            var line = _lines[index];
            if (_disclosureLines.GetValueOrDefault(line.Id) != id)
            {
                continue;
            }

            _lines.RemoveAt(index);
            RetainedBytes -= line.Bytes;
            _styles.Remove(line.Id);
            _disclosureLines.Remove(line.Id);
            _disclosureHeaders.Remove(line.Id);
            _top = Math.Max(0, _top - RemoveRows(line.Id));
            Evicted++;
            if (_anchor?.Line == line.Id || _end?.Line == line.Id)
            {
                _anchor = _end = null;
            }
        }
    }

    private int SourceDisplayOffset(Point point)
    {
        var offset = 0;
        for (var index = 0; index < _lines.Count; index++)
        {
            var line = _lines[index];
            if (line.Id == point.Line)
            {
                return offset + point.Offset;
            }

            offset += line.Text.Length;
            if (index + 1 < _lines.Count && !_lines[index + 1].Continued)
            {
                offset++;
            }
        }

        return offset;
    }
}
