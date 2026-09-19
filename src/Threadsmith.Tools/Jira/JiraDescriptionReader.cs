namespace Threadsmith.Tools.Jira;

using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>Projects a bounded Jira Atlassian Document Format description to readable plain text.</summary>
internal static class JiraDescriptionReader
{
    private const int MaximumDepth = 32;
    private const int MaximumNodesAndMarks = 50_000;

    /// <summary>Reads a Jira description value with explicit completeness and truncation state.</summary>
    internal static JiraDescriptionResult Read(
        JsonElement description,
        int maximumBodyBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBodyBytes);
        if (description.ValueKind == JsonValueKind.Null)
        {
            return new JiraDescriptionResult(string.Empty, "absent", true, [], false);
        }

        if (description.ValueKind != JsonValueKind.Object
            || ReadRequiredString(description, "type") != "doc"
            || !description.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var versionNumber)
            || versionNumber != 1
            || !description.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Jira returned a malformed ADF description root.");
        }

        var state = new ProjectionState(maximumBodyBytes, cancellationToken);
        var body = new StringBuilder(Math.Min(maximumBodyBytes, 4096));
        var bodyBytes = 0;
        var blockIndex = 0;
        foreach (var node in content.EnumerateArray())
        {
            if (state.LimitReached)
            {
                break;
            }

            if (blockIndex > 0
                && !TryAppendBounded(body, "\n\n", ref bodyBytes, state))
            {
                break;
            }

            if (bodyBytes == state.MaximumBodyBytes)
            {
                state.BodyLimitReached = true;
                break;
            }

            var rendered = RenderNode(node, state, 1, 0);
            AppendBounded(body, ref bodyBytes, string.Empty, rendered, state);
            blockIndex++;
        }

        if (state.BodyLimitReached)
        {
            state.AddLimitation("body-byte-limit");
        }

        if (state.WorkLimitReached)
        {
            state.AddLimitation("projection-work-limit");
        }

        var isTruncated = state.BodyLimitReached || state.WorkLimitReached;
        return new JiraDescriptionResult(
            body.ToString(),
            "present",
            !state.HasCoverageLoss && !isTruncated,
            state.GetLimitations(),
            isTruncated);
    }

    private static string RenderNode(
        JsonElement node,
        ProjectionState state,
        int depth,
        int listDepth)
    {
        if (state.LimitReached)
        {
            return string.Empty;
        }

        state.CountItem();
        if (depth > MaximumDepth || state.ItemsVisited > MaximumNodesAndMarks)
        {
            state.WorkLimitReached = true;
            return string.Empty;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Jira returned a malformed ADF node.");
        }

        var type = ReadRequiredString(node, "type");
        var rendered = type switch
        {
            "paragraph" or "heading" => RenderChildren(node, state, depth, listDepth, requireContent: true),
            "text" => RenderText(node, state),
            "hardBreak" => "\n",
            "bulletList" => RenderList(node, state, depth, listDepth, ordered: false),
            "orderedList" => RenderList(node, state, depth, listDepth, ordered: true),
            "listItem" => RenderChildren(node, state, depth, listDepth, "\n", requireContent: true),
            "codeBlock" => RenderChildren(node, state, depth, listDepth, requireContent: true),
            "blockquote" => PrefixLinesBounded(
                RenderChildren(node, state, depth, listDepth, "\n\n", requireContent: true),
                "> ",
                state),
            "rule" => "---",
            "table" => RenderTable(node, state, depth),
            "tableRow" => RenderChildren(node, state, depth, listDepth, "\t"),
            "tableCell" or "tableHeader" => RenderChildren(node, state, depth, listDepth, "\n"),
            "panel" => RenderPanel(node, state, depth, listDepth),
            "mention" => RenderLabelNode(node, state, "mention-unavailable", "[mention unavailable]", "text"),
            "emoji" => RenderEmoji(node, state),
            "status" => RenderLabelNode(node, state, "status-unavailable", "[status unavailable]", "text"),
            "date" => RenderDate(node, state),
            "inlineCard" or "blockCard" or "embedCard" => RenderCard(node, state),
            "media" or "mediaInline" => RenderMedia(node, state, depth, listDepth, isContainer: false),
            "mediaSingle" or "mediaGroup" => RenderMedia(node, state, depth, listDepth, isContainer: true),
            "doc" => throw new InvalidDataException("Jira returned a nested ADF document root."),
            _ => RenderUnsupported(node, state, depth, listDepth),
        };
        return state.Fit(rendered);
    }

    private static string RenderChildren(
        JsonElement node,
        ProjectionState state,
        int depth,
        int listDepth,
        string separator = "",
        bool requireContent = false)
    {
        if (!node.TryGetProperty("content", out var content))
        {
            if (requireContent)
            {
                throw new InvalidDataException("Jira returned an ADF container without child content.");
            }

            return string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Jira returned malformed ADF child content.");
        }

        var children = new StringBuilder();
        var childBytes = 0;
        var childIndex = 0;
        foreach (var child in content.EnumerateArray())
        {
            if (state.LimitReached)
            {
                break;
            }

            if (childIndex > 0
                && !TryAppendBounded(children, separator, ref childBytes, state))
            {
                break;
            }

            if (childBytes == state.MaximumBodyBytes)
            {
                state.BodyLimitReached = true;
                break;
            }

            var rendered = RenderNode(child, state, depth + 1, listDepth);
            AppendBounded(children, ref childBytes, string.Empty, rendered, state);
            childIndex++;
        }

        return children.ToString();
    }

    private static string RenderText(JsonElement node, ProjectionState state)
    {
        var text = state.Fit(NormalizeAuthoredLineEndings(ReadRequiredString(node, "text")));
        if (!node.TryGetProperty("marks", out var marks))
        {
            return text;
        }

        if (marks.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Jira returned malformed ADF text marks.");
        }

        var prefixes = new List<string>();
        var suffixes = new List<string>();
        var renderedBytes = Encoding.UTF8.GetByteCount(text);
        var isOriginalText = true;
        var markLimitReached = false;
        foreach (var mark in marks.EnumerateArray())
        {
            if (state.BodyLimitReached)
            {
                break;
            }

            state.CountItem();
            if (state.ItemsVisited > MaximumNodesAndMarks)
            {
                state.WorkLimitReached = true;
                break;
            }

            var type = ReadRequiredString(mark, "type");
            string? prefix = null;
            string? suffix = null;
            switch (type)
            {
                case "link":
                    suffix = RenderLinkSuffix(text, isOriginalText, mark, state);
                    break;
                case "strike":
                    prefix = "[struck: ";
                    suffix = "]";
                    break;
                case "subsup":
                    (prefix, suffix) = ReadSubSupWrapper(mark);
                    break;
                case "strong":
                case "em":
                case "underline":
                case "code":
                case "textColor":
                case "backgroundColor":
                case "alignment":
                    break;
                default:
                    state.AddCoverageLimitation("unsupported-adf-mark");
                    break;
            }

            var addedBytes = 0;
            if (prefix is not null)
            {
                prefixes.Add(prefix);
                addedBytes += Encoding.UTF8.GetByteCount(prefix);
            }

            if (suffix is not null)
            {
                suffixes.Add(suffix);
                addedBytes += Encoding.UTF8.GetByteCount(suffix);
            }

            if (addedBytes > 0)
            {
                isOriginalText = false;
            }

            if (addedBytes > state.MaximumBodyBytes - renderedBytes)
            {
                markLimitReached = true;
                break;
            }

            renderedBytes += addedBytes;
        }

        if (prefixes.Count == 0 && suffixes.Count == 0)
        {
            return text;
        }

        var rendered = RenderMarkedTextBounded(text, prefixes, suffixes, state);
        state.BodyLimitReached |= markLimitReached;
        return rendered;
    }

    private static string RenderList(
        JsonElement node,
        ProjectionState state,
        int depth,
        int listDepth,
        bool ordered)
    {
        var content = ReadRequiredContent(node);
        var start = 1;
        if (ordered && node.TryGetProperty("attrs", out var attrs))
        {
            EnsureObject(attrs, "ADF ordered-list attributes");
            if (attrs.TryGetProperty("order", out var order))
            {
                if (order.ValueKind != JsonValueKind.Number || !order.TryGetInt32(out start) || start < 0)
                {
                    throw new InvalidDataException("Jira returned an invalid ADF ordered-list start.");
                }
            }
        }

        var lines = new StringBuilder();
        var lineBytes = 0;
        var index = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (state.LimitReached)
            {
                break;
            }

            if (ReadRequiredString(item, "type") != "listItem")
            {
                throw new InvalidDataException("Jira returned a non-item child in an ADF list.");
            }

            if (index > 0
                && !TryAppendBounded(lines, "\n", ref lineBytes, state))
            {
                break;
            }

            if (lineBytes == state.MaximumBodyBytes)
            {
                state.BodyLimitReached = true;
                break;
            }

            var itemText = RenderNode(item, state, depth + 1, listDepth + 1);
            var indent = new string(' ', listDepth * 2);
            var prefix = ordered
                ? ((long)start + index).ToString(CultureInfo.InvariantCulture) + ". "
                : "- ";
            var itemPrefix = indent + prefix;
            var continuationPrefix = new string(' ', itemPrefix.Length);
            var remaining = state.MaximumBodyBytes - lineBytes;
            if (remaining <= 0)
            {
                state.BodyLimitReached = true;
                break;
            }

            var renderedItem = PrefixLinesBounded(
                itemText,
                itemPrefix,
                continuationPrefix,
                remaining,
                state);
            AppendBounded(
                lines,
                ref lineBytes,
                string.Empty,
                renderedItem,
                state);
            index++;
        }

        return lines.ToString();
    }

    private static string RenderTable(JsonElement node, ProjectionState state, int depth)
    {
        var rows = ReadRequiredContent(node);
        var renderedRows = new StringBuilder();
        var rowBytes = 0;
        var rowIndex = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (state.LimitReached)
            {
                break;
            }

            if (rowIndex > 0
                && !TryAppendBounded(renderedRows, "\n", ref rowBytes, state))
            {
                break;
            }

            if (rowBytes == state.MaximumBodyBytes)
            {
                state.BodyLimitReached = true;
                break;
            }

            state.CountItem();
            if (state.ItemsVisited > MaximumNodesAndMarks)
            {
                state.WorkLimitReached = true;
                break;
            }

            if (ReadRequiredString(row, "type") != "tableRow")
            {
                throw new InvalidDataException("Jira returned a non-row child in an ADF table.");
            }

            var cells = ReadRequiredContent(row);
            var renderedCells = new StringBuilder();
            var cellBytes = 0;
            var cellIndex = 0;
            foreach (var cell in cells.EnumerateArray())
            {
                if (state.LimitReached)
                {
                    break;
                }

                if (cellIndex > 0
                    && !TryAppendBounded(renderedCells, "\t", ref cellBytes, state))
                {
                    break;
                }

                if (cellBytes == state.MaximumBodyBytes)
                {
                    state.BodyLimitReached = true;
                    break;
                }

                var cellType = ReadRequiredString(cell, "type");
                if (cellType is not ("tableCell" or "tableHeader"))
                {
                    throw new InvalidDataException("Jira returned a non-cell child in an ADF table row.");
                }

                var merged = false;
                if (cell.TryGetProperty("attrs", out var attrs))
                {
                    EnsureObject(attrs, "ADF table-cell attributes");
                    if (ReadOptionalPositiveInteger(attrs, "colspan") is > 1
                        || ReadOptionalPositiveInteger(attrs, "rowspan") is > 1)
                    {
                        state.AddCoverageLimitation("merged-table-cell");
                        merged = true;
                    }
                }

                var renderedCell = RenderTableCellBounded(
                    RenderNode(cell, state, depth + 2, 0),
                    merged,
                    state.MaximumBodyBytes - cellBytes,
                    state);

                AppendBounded(
                    renderedCells,
                    ref cellBytes,
                    string.Empty,
                    renderedCell,
                    state);
                cellIndex++;
            }

            AppendBounded(
                renderedRows,
                ref rowBytes,
                string.Empty,
                renderedCells.ToString(),
                state);
            rowIndex++;
        }

        return renderedRows.ToString();
    }

    private static string RenderTableCellBounded(
        string value,
        bool merged,
        int maximumBytes,
        ProjectionState state)
    {
        var builder = new StringBuilder(Math.Min(maximumBytes, 4096));
        var bytesUsed = 0;
        var prefix = merged
            ? value.Length == 0 ? "[merged cell]" : "[merged cell] "
            : string.Empty;
        var boundedPrefix = JiraTextBounds.Utf8Prefix(prefix, maximumBytes, out var prefixTruncated);
        builder.Append(boundedPrefix);
        bytesUsed += Encoding.UTF8.GetByteCount(boundedPrefix);
        if (prefixTruncated)
        {
            state.BodyLimitReached = true;
            return builder.ToString();
        }

        var runesVisited = 0;
        Span<char> encoded = stackalloc char[2];
        foreach (var rune in value.EnumerateRunes())
        {
            if ((runesVisited++ & 255) == 0)
            {
                state.CheckCancellation();
            }

            var replacement = rune.Value switch
            {
                '\n' => " / ",
                '\t' => "\\t",
                _ => null,
            };
            if (replacement is not null)
            {
                var bounded = JiraTextBounds.Utf8Prefix(
                    replacement,
                    maximumBytes - bytesUsed,
                    out var truncated);
                builder.Append(bounded);
                bytesUsed += Encoding.UTF8.GetByteCount(bounded);
                if (truncated)
                {
                    state.BodyLimitReached = true;
                    break;
                }

                continue;
            }

            var runeBytes = rune.Utf8SequenceLength;
            if (runeBytes > maximumBytes - bytesUsed)
            {
                state.BodyLimitReached = true;
                break;
            }

            var characters = rune.EncodeToUtf16(encoded);
            builder.Append(encoded[..characters]);
            bytesUsed += runeBytes;
        }

        return builder.ToString();
    }

    private static string RenderPanel(JsonElement node, ProjectionState state, int depth, int listDepth)
    {
        if (!node.TryGetProperty("attrs", out var attrs))
        {
            throw new InvalidDataException("Jira returned ADF panel content without attributes.");
        }

        EnsureObject(attrs, "ADF panel attributes");
        var panelType = ReadRequiredString(attrs, "panelType");
        if (panelType is not ("info" or "note" or "warning" or "error" or "success" or "custom"))
        {
            state.AddCoverageLimitation("unsupported-panel-type");
            panelType = "panel";
        }

        var content = RenderChildren(node, state, depth, listDepth, "\n\n");
        return content.Length == 0 ? $"[{panelType}]" : $"[{panelType}]\n{content}";
    }

    private static string RenderLabelNode(
        JsonElement node,
        ProjectionState state,
        string limitation,
        string placeholder,
        string property)
    {
        if (node.TryGetProperty("attrs", out var attrs))
        {
            EnsureObject(attrs, "ADF label attributes");
            if (attrs.TryGetProperty(property, out var label))
            {
                if (label.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Jira returned an invalid ADF label attribute.");
                }

                var text = label.GetString() ?? string.Empty;
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }

        state.AddCoverageLimitation(limitation);
        return placeholder;
    }

    private static string RenderEmoji(JsonElement node, ProjectionState state)
    {
        if (node.TryGetProperty("attrs", out var attrs))
        {
            EnsureObject(attrs, "ADF emoji attributes");
            foreach (var property in new[] { "text", "shortName" })
            {
                if (attrs.TryGetProperty(property, out var value))
                {
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException("Jira returned an invalid ADF emoji attribute.");
                    }

                    var text = value.GetString() ?? string.Empty;
                    if (text.Length > 0)
                    {
                        return text;
                    }
                }
            }
        }

        state.AddCoverageLimitation("emoji-unavailable");
        return "[emoji unavailable]";
    }

    private static string RenderDate(JsonElement node, ProjectionState state)
    {
        if (node.TryGetProperty("attrs", out var attrs))
        {
            EnsureObject(attrs, "ADF date attributes");
            if (attrs.TryGetProperty("timestamp", out var timestamp))
            {
                if (timestamp.ValueKind != JsonValueKind.String
                    || !long.TryParse(
                        timestamp.GetString(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var milliseconds))
                {
                    throw new InvalidDataException("Jira returned an invalid ADF date timestamp.");
                }

                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                        .UtcDateTime
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                catch (ArgumentOutOfRangeException)
                {
                    throw new InvalidDataException("Jira returned an out-of-range ADF date.");
                }
            }
        }

        state.AddCoverageLimitation("date-unavailable");
        return "[date unavailable]";
    }

    private static string RenderCard(JsonElement node, ProjectionState state)
    {
        state.AddCoverageLimitation("card-preview-not-retrieved");
        if (node.TryGetProperty("attrs", out var attrs))
        {
            EnsureObject(attrs, "ADF card attributes");
            if (attrs.TryGetProperty("url", out var url))
            {
                if (url.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Jira returned an invalid ADF card URL.");
                }

                if (TrySafeUrl(url.GetString(), out var safeUrl))
                {
                    return $"{safeUrl} [card preview not retrieved]";
                }
            }
        }

        return "[card content unavailable]";
    }

    private static string RenderMedia(
        JsonElement node,
        ProjectionState state,
        int depth,
        int listDepth,
        bool isContainer)
    {
        state.AddCoverageLimitation("media-not-retrieved");
        var pieces = new List<string>();
        if (node.TryGetProperty("attrs", out var attrs))
        {
            EnsureObject(attrs, "ADF media attributes");
            if (attrs.TryGetProperty("alt", out var alt))
            {
                if (alt.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Jira returned an invalid ADF media alt attribute.");
                }

                var boundedAlt = JiraTextBounds.UnicodeScalarPrefix(alt.GetString() ?? string.Empty, 256, out _);
                if (boundedAlt.Length > 0)
                {
                    pieces.Add(boundedAlt);
                }
            }
        }

        var descendants = RenderChildren(node, state, depth, listDepth);
        if (descendants.Length > 0)
        {
            pieces.Add(descendants);
        }

        if (!isContainer || descendants.Length == 0)
        {
            pieces.Add("[media not retrieved]");
        }

        return string.Join(' ', pieces);
    }

    private static string RenderUnsupported(
        JsonElement node,
        ProjectionState state,
        int depth,
        int listDepth)
    {
        state.AddCoverageLimitation("unsupported-adf-content");
        var descendants = RenderChildren(node, state, depth, listDepth);
        return descendants.Length == 0
            ? "[unsupported content]"
            : $"[unsupported content] {descendants}";
    }

    private static string? RenderLinkSuffix(
        string originalText,
        bool isOriginalText,
        JsonElement mark,
        ProjectionState state)
    {
        if (mark.TryGetProperty("attrs", out var attrs))
        {
            EnsureObject(attrs, "ADF link attributes");
            if (attrs.TryGetProperty("href", out var href)
                && href.ValueKind == JsonValueKind.String
                && TrySafeUrl(href.GetString(), out var safeUrl))
            {
                return isOriginalText && originalText.Equals(safeUrl, StringComparison.Ordinal)
                    ? null
                    : $" ({safeUrl})";
            }
        }

        state.AddCoverageLimitation("unsafe-or-missing-link");
        return null;
    }

    private static (string Prefix, string Suffix) ReadSubSupWrapper(JsonElement mark)
    {
        if (!mark.TryGetProperty("attrs", out var attrs))
        {
            throw new InvalidDataException("Jira returned ADF subsup without attributes.");
        }

        EnsureObject(attrs, "ADF subsup attributes");
        return ReadRequiredString(attrs, "type") switch
        {
            "sub" => ("_{", "}"),
            "sup" => ("^{", "}"),
            _ => throw new InvalidDataException("Jira returned an invalid ADF subsup type."),
        };
    }

    private static JsonElement ReadRequiredContent(JsonElement node)
    {
        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Jira returned an ADF container without child content.");
        }

        return content;
    }

    private static string ReadRequiredString(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(property, out var member)
            || member.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(member.GetString()))
        {
            throw new InvalidDataException($"Jira returned ADF content without a valid {property}.");
        }

        return member.GetString() ?? string.Empty;
    }

    private static int? ReadOptionalPositiveInteger(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var member))
        {
            return null;
        }

        if (member.ValueKind != JsonValueKind.Number || !member.TryGetInt32(out var number) || number < 1)
        {
            throw new InvalidDataException($"Jira returned an invalid ADF {property}.");
        }

        return number;
    }

    private static void EnsureObject(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Jira returned malformed {label}.");
        }
    }

    private static bool TrySafeUrl(string? value, out string safeUrl)
    {
        safeUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0
            || Uri.UnescapeDataString(uri.OriginalString).Any(char.IsControl))
        {
            return false;
        }

        safeUrl = uri.AbsoluteUri;
        return true;
    }

    private static string PrefixLinesBounded(string value, string prefix, ProjectionState state)
        => PrefixLinesBounded(value, prefix, prefix, state.MaximumBodyBytes, state);

    private static string PrefixLinesBounded(
        string value,
        string firstPrefix,
        string continuationPrefix,
        int maximumBytes,
        ProjectionState state)
    {
        var builder = new StringBuilder(Math.Min(maximumBytes, 4096));
        var bytesUsed = 0;
        if (!TryAppendBounded(builder, firstPrefix, ref bytesUsed, maximumBytes, state))
        {
            return builder.ToString();
        }

        var runesVisited = 0;
        Span<char> encoded = stackalloc char[2];
        foreach (var rune in value.EnumerateRunes())
        {
            if ((runesVisited++ & 255) == 0)
            {
                state.CheckCancellation();
            }

            if (rune.Value == '\n')
            {
                if (!TryAppendBounded(builder, "\n", ref bytesUsed, maximumBytes, state)
                    || !TryAppendBounded(builder, continuationPrefix, ref bytesUsed, maximumBytes, state))
                {
                    break;
                }

                continue;
            }

            var runeBytes = rune.Utf8SequenceLength;
            if (runeBytes > maximumBytes - bytesUsed)
            {
                state.BodyLimitReached = true;
                break;
            }

            var characters = rune.EncodeToUtf16(encoded);
            builder.Append(encoded[..characters]);
            bytesUsed += runeBytes;
        }

        return builder.ToString();
    }

    private static string RenderMarkedTextBounded(
        string text,
        IReadOnlyList<string> prefixes,
        IReadOnlyList<string> suffixes,
        ProjectionState state)
    {
        var builder = new StringBuilder(Math.Min(state.MaximumBodyBytes, 4096));
        var bytesUsed = 0;
        for (var index = prefixes.Count - 1; index >= 0 && !state.BodyLimitReached; index--)
        {
            AppendBounded(builder, ref bytesUsed, string.Empty, prefixes[index], state);
        }

        if (!state.BodyLimitReached)
        {
            AppendBounded(builder, ref bytesUsed, string.Empty, text, state);
        }

        for (var index = 0; index < suffixes.Count && !state.BodyLimitReached; index++)
        {
            AppendBounded(builder, ref bytesUsed, string.Empty, suffixes[index], state);
        }

        return builder.ToString();
    }

    private static bool TryAppendBounded(
        StringBuilder builder,
        string value,
        ref int bytesUsed,
        ProjectionState state)
        => TryAppendBounded(builder, value, ref bytesUsed, state.MaximumBodyBytes, state);

    private static bool TryAppendBounded(
        StringBuilder builder,
        string value,
        ref int bytesUsed,
        int maximumBytes,
        ProjectionState state)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > maximumBytes - bytesUsed)
        {
            state.BodyLimitReached = true;
            return false;
        }

        builder.Append(value);
        bytesUsed += bytes;
        return true;
    }

    private static string NormalizeAuthoredLineEndings(string value)
        => value.Contains('\r')
            ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : value;

    private static void AppendBounded(
        StringBuilder builder,
        ref int bytesUsed,
        string separator,
        string value,
        ProjectionState state)
    {
        var remaining = state.MaximumBodyBytes - bytesUsed;
        if (remaining <= 0)
        {
            state.BodyLimitReached = true;
            return;
        }

        var separatorBytes = Encoding.UTF8.GetByteCount(separator);
        if (separatorBytes > remaining)
        {
            state.BodyLimitReached = true;
            return;
        }

        var bounded = JiraTextBounds.Utf8Prefix(value, remaining - separatorBytes, out var truncated);
        builder.Append(separator);
        builder.Append(bounded);
        bytesUsed += separatorBytes + Encoding.UTF8.GetByteCount(bounded);
        state.BodyLimitReached |= truncated;
    }

    private sealed class ProjectionState
    {
        private readonly SortedSet<string> _limitations = new(StringComparer.Ordinal);
        private readonly CancellationToken _cancellationToken;

        internal ProjectionState(int maximumBodyBytes, CancellationToken cancellationToken)
        {
            MaximumBodyBytes = maximumBodyBytes;
            _cancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
        }

        internal int MaximumBodyBytes { get; }

        internal int ItemsVisited { get; private set; }

        internal bool WorkLimitReached { get; set; }

        internal bool BodyLimitReached { get; set; }

        internal bool LimitReached => WorkLimitReached || BodyLimitReached;

        internal bool HasCoverageLoss { get; private set; }

        internal void CountItem()
        {
            ItemsVisited++;
            if ((ItemsVisited & 255) == 0)
            {
                CheckCancellation();
            }
        }

        internal void CheckCancellation() => _cancellationToken.ThrowIfCancellationRequested();

        internal void AddCoverageLimitation(string limitation)
        {
            HasCoverageLoss = true;
            AddLimitation(limitation);
        }

        internal void AddLimitation(string limitation) => _limitations.Add(limitation);

        internal string Fit(string value)
        {
            var bounded = JiraTextBounds.Utf8Prefix(value, MaximumBodyBytes, out var truncated);
            BodyLimitReached |= truncated;
            return bounded;
        }

        internal IReadOnlyList<string> GetLimitations()
        {
            const int maximumLimitations = 16;
            if (_limitations.Count <= maximumLimitations)
            {
                return _limitations.ToArray();
            }

            return [.. _limitations.Take(maximumLimitations - 1), "additional-limitations-omitted"];
        }
    }
}

/// <summary>Unicode-safe text bounding shared by Jira projection and result framing.</summary>
internal static class JiraTextBounds
{
    /// <summary>Returns the longest prefix within a UTF-8 byte limit without splitting a Unicode scalar.</summary>
    internal static string Utf8Prefix(string value, int maximumBytes, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            truncated = false;
            return value;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maximumBytes));
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var bytes = rune.Utf8SequenceLength;
            if (used + bytes > maximumBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            used += bytes;
        }

        truncated = true;
        return builder.ToString();
    }

    /// <summary>Returns a Unicode-scalar prefix for bounded attribution fields.</summary>
    internal static string UnicodeScalarPrefix(string value, int maximumScalars, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumScalars);
        var builder = new StringBuilder(Math.Min(value.Length, maximumScalars));
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count == maximumScalars)
            {
                truncated = true;
                return builder.ToString();
            }

            builder.Append(rune.ToString());
            count++;
        }

        truncated = false;
        return value;
    }
}
