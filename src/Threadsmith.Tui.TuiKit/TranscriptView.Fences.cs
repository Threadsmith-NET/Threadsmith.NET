namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Widgets;

/// <summary>Fenced frame drawing and copy actions under the retained transcript lifecycle.</summary>
internal sealed partial class TranscriptView
{
    private readonly Dictionary<long, FencedRow> _fencedRows = [];
    private readonly ClickRegionMap<BlockAction> _blockClicks = new();
    private readonly Dictionary<PresentationMarkdownItem, Dictionary<int, long>> _fencedIds = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PresentationMarkdownItem> _retiredFencedItems = new(ReferenceEqualityComparer.Instance);
    private long _blockGeneration;
    private long _nextBlockId;

    /// <summary>Gets or sets active clipboard transport availability without probing the clipboard.</summary>
    internal Func<bool>? ClipboardAvailable { get; set; }

    /// <summary>Gets or sets the existing surface-owned clipboard operation.</summary>
    internal Func<string, bool>? CopyRequested { get; set; }

    /// <summary>Gets or sets the active frontend's ASCII frame preference.</summary>
    internal Func<bool>? UseAsciiBorders { get; set; }

    /// <summary>Rejects coordinates from an earlier viewport or retained-content generation.</summary>
    internal void InvalidateBlockActions()
    {
        _blockGeneration++;
        _blockClicks.Clear();
    }

    /// <summary>Revokes prior-session block authority without changing the retained visual text.</summary>
    internal void RetireBlockActions()
    {
        InvalidateBlockActions();
        foreach (var item in _items.OfType<PresentationMarkdownItem>())
        {
            _retiredFencedItems.Add(item);
        }
    }

    private long GetBlockId(PresentationMarkdownItem item, int ordinal)
    {
        if (!_fencedIds.TryGetValue(item, out var identities))
        {
            identities = [];
            _fencedIds.Add(item, identities);
        }

        if (!identities.TryGetValue(ordinal, out var id))
        {
            id = ++_nextBlockId;
            identities.Add(ordinal, id);
        }

        return id;
    }

    private static string BlockControlId(FencedBlock block) => "fence:" + block.Id;

    private static int FencedMetadataBytes(IEnumerable<MarkdownBlock> blocks)
    {
        var bytes = 0;
        foreach (var block in blocks)
        {
            bytes += block switch
            {
                MarkdownCodeBlock { FencedBody: { } body } => body.MetadataBytes + 128,
                MarkdownQuote quote => FencedMetadataBytes(quote.Blocks),
                MarkdownList list => FencedMetadataBytes(list.Items.SelectMany(item => item.Blocks)),
                _ => 0,
            };
        }

        return bytes;
    }

    private bool CanCopyBlock(FencedBlock block)
    {
        return ClipboardAvailable?.Invoke() == true && CopyRequested is not null
            && block.Body is { IsClosed: true, CharacterCount: > 0 }
            && block.Item is { } item && !_retiredFencedItems.Contains(item) && block.Body.IsAssociatedWith(item.RawSource)
            && _fencedIds.ContainsKey(item)
            && _lines.Count > 0 && _lines[0].Id <= block.FirstId
            && _lines[^1].Id >= block.LastId && block.HeaderId >= 0;
    }

    private string? GetActionableHeader(long lineId)
    {
        if (!_disclosureHeaders.TryGetValue(lineId, out var header))
        {
            return null;
        }

        return !header.Id.StartsWith("fence:", StringComparison.Ordinal)
            || (_fencedRows.TryGetValue(lineId, out var frame) && CanCopyBlock(frame.Block)) ? header.Id : null;
    }

    private bool ActivateBlock(BlockAction action)
    {
        if (action.Generation != _blockGeneration || !CanCopyBlock(action.Block)
            || action.Block.Item is not { } item || action.Block.Body is not { } body
            || !body.TryGetText(item.RawSource, out var text))
        {
            return false;
        }

        _ = CopyRequested?.Invoke(text);
        return true;
    }

    private void DrawFencedFrame(ISurface surface, int y, Row row)
    {
        if (!_fencedRows.TryGetValue(row.Line.Id, out var frame))
        {
            return;
        }

        var left = UnicodeWidth.GetWidth(frame.Prefix);
        var width = surface.Size.Width - left;
        if (width < 17)
        {
            return;
        }

        var block = frame.Block;
        var part = frame.Part;
        var borderStyle = ResolveStyle(PresentationTextRole.MarkdownTableBorder);
        var copy = part == 0 && row.Line.Id == block.HeaderId && CanCopyBlock(block);
        var state = block.Body switch
        {
            null => " · omitted",
            { IsClosed: false } => " · incomplete",
            { CharacterCount: 0 } => " · empty",
            _ => string.Empty,
        };
        var label = block.Language + state;
        var room = width - (copy ? 13 : 6);
        label = label[..TuiKitTextMetrics.GetLengthThatFits(label, Math.Max(1, room))];
        var strip = new SurfaceView(surface, new Rect(left, y, width, 1));
        strip.DrawBox(new Rect(0, -part, width, 3), borderStyle, UseAsciiBorders?.Invoke() == true ? BorderStyle.Ascii : BorderStyle.Rounded, part == 0 ? label : null, ResolveStyle(PresentationTextRole.MarkdownHeading), TitleAlignment.Left, 1);
        if (copy)
        {
            var style = ResolveStyle(PresentationTextRole.Hyperlink);
            if (_focusedDisclosure == BlockControlId(block))
            {
                style = style.WithAttribute(CellAttributes.Reverse, true);
            }

            InlineButton.Draw(surface, surface.Size.Width - 8, y, "Copy", null, new BlockAction(block, _blockGeneration), _blockClicks, new InlineButtonStyle { Label = style, Hover = style.WithAttribute(CellAttributes.Underline, true) });
        }
    }

    private void RemoveFencedSource(PresentationMarkdownItem item)
    {
        _retiredFencedItems.Remove(item);
        if (!_fencedIds.Remove(item))
        {
            return;
        }

        InvalidateBlockActions();
        foreach (var frame in _fencedRows.Values)
        {
            if (ReferenceEquals(frame.Block.Item, item))
            {
                frame.Block.Item = null;
                frame.Block.Body = null;
            }
        }
    }

    private sealed class FencedBlock
    {
        internal FencedBlock(PresentationMarkdownItem item, MarkdownFencedBody body, string language, long id)
        {
            Item = item;
            Body = body;
            Language = language;
            Id = id;
        }

        internal PresentationMarkdownItem? Item { get; set; }

        internal MarkdownFencedBody? Body { get; set; }

        internal string Language { get; }

        internal long Id { get; }

        internal long FirstId { get; set; } = long.MaxValue;

        internal long LastId { get; set; }

        internal long HeaderId { get; set; } = -1;
    }

    private sealed record FencedRow(FencedBlock Block, string Prefix, int Part, bool SoftWrapped = false, int DecorationCharacters = 0);

    private sealed record BlockAction(FencedBlock Block, long Generation);
}
