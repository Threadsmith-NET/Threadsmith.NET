namespace Threadsmith.Tui.TuiKit;

using System.Globalization;
using Threadsmith.Execution;
using Threadsmith.Models;
using TUIKit;
using TUIKit.Widgets;

/// <summary>Owns the selected agent's identity and a right-aligned, themed TUIKit context progress task.</summary>
internal sealed class AgentHeader
{
    private readonly MultiProgress _progress = new();
    private readonly ProgressTask _context;
    private readonly CellBuffer _bar = new(1, 1);
    private readonly CachedTextRun _identity = new();
    private readonly CachedTextRun _label = new();
    private readonly CachedTextRun _tail = new();

    /// <summary>Initializes a new instance of the <see cref="AgentHeader"/> class with one reusable progress task.</summary>
    internal AgentHeader()
    {
        _context = _progress.Add(string.Empty);
    }

    /// <summary>Renders context last and reserves its width before abbreviating agent metadata.</summary>
    internal void Render(BufferSurface surface, AgentHeaderState state, CellStyle style)
    {
        if (surface.Size.Width < 1 || surface.Size.Height < 1)
        {
            return;
        }

        var width = surface.Size.Width;
        var compact = width < 60;
        var label = compact ? "Ctx" : "Context";
        var known = state.ContextTokens is >= 0 && state.ContextLimit is > 0;
        var ratio = known ? (double)state.ContextTokens.GetValueOrDefault() / state.ContextLimit.GetValueOrDefault() : 0;
        var percentage = !known ? "?%" : ratio >= 10 ? "999+%" : (ratio * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        var capacity = state.ContextLimit is > 0 ? CompactCount(state.ContextLimit) : "?";
        var tail = " " + percentage + (compact ? "/" : " of ") + capacity;
        var barWidth = Math.Clamp(width / 8, 4, 16);
        var rightWidth = label.Length + 1 + barWidth + tail.Length;
        if (rightWidth >= width)
        {
            var summary = percentage + "/" + capacity;
            _tail.Draw(surface, Math.Max(0, width - summary.Length), 0, summary, style);
            return;
        }

        var leftWidth = width - rightWidth - 1;
        _identity.Draw(surface.CreateView(new Rect(0, 0, leftWidth, 1)), 0, 0, IdentityText(state, leftWidth), style);
        var right = width - rightWidth;
        _label.Draw(surface, right, 0, label, style);
        _context.Report(ratio);

        // MultiProgress owns bar rounding and glyphs. Reapply the semantic header style
        // because the widget otherwise hardcodes default backgrounds and completion green.
        var widgetPercentWidth = ((int)Math.Round(_context.Value * 100)).ToString(CultureInfo.InvariantCulture).Length + 2;
        var widgetWidth = barWidth + 1 + widgetPercentWidth;
        if (_bar.Width != widgetWidth)
        {
            _bar.Resize(widgetWidth, 1);
        }

        _bar.Clear(CellStyle.Default);
        _progress.Render(new BufferSurface(_bar));
        var barStart = right + label.Length + 1;
        for (var x = 0; x < barWidth; x++)
        {
            surface.Set(barStart + x, 0, Cell.Glyph(_bar.Get(x + 1, 0).Grapheme, style, 1));
        }

        _tail.Draw(surface, barStart + barWidth, 0, tail, style);
    }

    /// <summary>Preserves complete metadata in F2 details when the one-line header must abbreviate it.</summary>
    internal static string FormatDetails(AgentHeaderState state)
    {
        var context = state.ContextTokens is >= 0 && state.ContextLimit is > 0
            ? $"~{state.ContextTokens:N0}/{state.ContextLimit:N0} ({100.0 * state.ContextTokens.Value / state.ContextLimit.Value:0}%)"
            : $"?/{(state.ContextLimit is > 0 ? state.ContextLimit.Value.ToString("N0", CultureInfo.InvariantCulture) : "?")}";
        return TranscriptView.Safe($"{state.Label} | ({state.ProviderName ?? "Provider unknown"}) {state.Model} ({state.Reasoning.Value}) | {UsageText(state, false)} | Context {context}"
            + "\n" + RequestUsageText(state.Usage.LatestRequest)
            + "\nChild views are read-only. Ctrl+Left/Right in output selects agents. F7 focuses MAIN's composer.");
    }

    private static string IdentityText(AgentHeaderState state, int width)
    {
        var provider = TranscriptView.Safe(state.ProviderName ?? "Provider unknown");
        var model = TranscriptView.Safe(state.Model);
        var full = $"Using model: ({provider}) {model} ({state.Reasoning.Value}) | {UsageText(state, false)}";
        if (UnicodeWidth.GetWidth(full) <= width)
        {
            return full;
        }

        var prefix = "Using model: ";
        var reasoning = width >= 45 ? " " + Clip(state.Reasoning.Value, 3) : string.Empty;
        var usage = width >= 55 ? " | " + UsageText(state, true) : string.Empty;
        var identityWidth = Math.Max(2, width - UnicodeWidth.GetWidth(prefix + reasoning + usage) - 3);
        provider = Clip(provider, Math.Max(1, identityWidth / 3));
        model = Clip(model, Math.Max(1, identityWidth - UnicodeWidth.GetWidth(provider)));
        return prefix + "(" + provider + ") " + model + reasoning + usage;
    }

    private static string UsageText(AgentHeaderState state, bool compact)
    {
        var usage = state.Usage;
        var prefix = usage.IsEstimate ? "~" : string.Empty;
        var counts = !usage.HasObservation ? compact ? "I? C? O?" : "in ? cache ? out ?"
            : compact
                ? $"I{prefix}{CompactCount(usage.InputTokens)} C{(usage.HasCacheObservation ? CompactCount(usage.CachedInputTokens) : "?")} O{prefix}{CompactCount(usage.OutputTokens)}{(usage.HasUnknownUsage ? "+?" : string.Empty)}"
                : $"in {prefix}{usage.InputTokens:N0} cache {(usage.HasCacheObservation ? usage.CachedInputTokens.ToString("N0", CultureInfo.InvariantCulture) : "?")} out {prefix}{usage.OutputTokens:N0}{(usage.HasUnknownUsage ? " +?" : string.Empty)}";
        if (usage.HasObservation && usage.ReasoningTokens is { } reasoning)
        {
            counts += compact ? $" (R{CompactCount(reasoning)})" : $" (reasoning {reasoning:N0})";
        }

        return counts + (state.IsPostResume ? " since resume" : string.Empty);
    }

    private static string RequestUsageText(ModelRequestUsageSnapshot? request)
    {
        if (request is null)
        {
            return "Latest request: not observed";
        }

        var identity = $"Latest request ({request.RequestId.Stage}, round {request.RequestId.Round + 1})";
        if (request.Usage is not { } usage)
        {
            return identity + ": token and cache usage unavailable";
        }

        var prefix = usage.IsEstimate ? "~" : string.Empty;
        var tokens = $"{identity}: in {prefix}{usage.InputTokens:N0}, out {prefix}{usage.OutputTokens:N0}";
        if (usage.ReasoningTokens is { } reasoning)
        {
            tokens += $" (reasoning {reasoning:N0})";
        }

        if (usage.Cache is not { Availability: CacheUsageAvailability.Reported } cache)
        {
            return tokens + "; cache usage unavailable (provider did not report counters)";
        }

        var reads = cache.CacheReadTokens?.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable";
        var writes = cache.CacheWriteTokens?.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable";
        var percentage = request.CacheHitPercentage?.ToString("0.0", CultureInfo.InvariantCulture);
        return tokens + $"; cache read {reads}, write {writes}; hit {(percentage is null ? "unavailable" : percentage + "%")}";
    }

    private static string CompactCount(long? value) => value switch
    {
        null => "?",
        >= 1_000_000 => (value.Value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value.Value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => value.Value.ToString(CultureInfo.InvariantCulture),
    };

    private static string Clip(string text, int width) => UnicodeWidth.GetWidth(text) <= width
        ? text
        : text[..UnicodeWidth.GetLengthThatFits(text, Math.Max(0, width - 1))] + "…";
}

/// <summary>Detached display metadata for either MAIN or the selected child.</summary>
internal sealed record AgentHeaderState(
    string Label,
    string Model,
    string? ProviderName,
    ReasoningLevel Reasoning,
    long? ContextTokens,
    long? ContextLimit,
    SessionUsageSnapshot Usage,
    bool IsPostResume);
