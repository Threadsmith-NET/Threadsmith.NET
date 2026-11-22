namespace Threadsmith.Tui.TuiKit;

using System.Globalization;
using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Content;
using TUIKit.Input;
using TUIKit.Modals;

/// <summary>A modal startup projection that discards all ordinary keys and paste.</summary>
internal sealed class StartupModal : Modal
{
    private const int ElapsedWidth = 6;
    private const int ItemWidth = 17 + 25; // "Loading solution " plus a 25-character solution name.
    private const int StatusWidth = 9; // Completed (also accommodates Cancelled).
    private static readonly string[] CompactLogo = ["Threadsmith.NET"];
    private readonly string[] _logoLines;
    private readonly int _logoWidth;
    private readonly string? _label;
    private readonly long _started;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _splinesDuration = TimeSpan.FromMilliseconds(Random.Shared.Next(500, 1001));
    private readonly Action _cancel;
    private readonly Func<PresentationTextRole, CellStyle> _style;
    private readonly IReadOnlyList<SemanticStartupPhaseSnapshot> _completed;

    private readonly List<SemanticStartupPhaseSnapshot> _rows = [];
    private readonly List<RowCache> _rowCache = [];
    private readonly List<(string Text, SemanticStartupPhaseState State)> _visibleLines = [];
    private readonly List<CachedTextRun> _lineRuns = [];
    private string? _fatalDetail;
    private int _fatalWidth;
    private string[] _fatalLines = [];
    private string? _tip;
    private int _tipWidth;
    private string[] _tipLines = [];
    private SemanticStartupPhaseState _state;
    private TimeSpan? _completedElapsed;

    /// <summary>Initializes a new instance of the <see cref="StartupModal"/> class.</summary>
    /// <remarks>A null label omits a separate current-operation row.</remarks>
    internal StartupModal(string logo, string? label, IReadOnlyList<SemanticStartupPhaseSnapshot> completed, Action cancel, Func<PresentationTextRole, CellStyle> style, IReadOnlyList<string>? details = null, TimeProvider? timeProvider = null)
    {
        _logoLines = logo.ReplaceLineEndings("\n").Split('\n');
        _logoWidth = _logoLines.Max(UnicodeWidth.GetWidth);
        _label = label;
        _completed = completed;
        Details = details ?? [];
        _timeProvider = timeProvider ?? TimeProvider.System;
        _started = _timeProvider.GetTimestamp();
        _cancel = cancel;
        _style = style;
    }

    /// <summary>Gets the real monotonic duration of this operation.</summary>
    internal TimeSpan Elapsed => Operation?.Snapshot.Elapsed ?? _completedElapsed ?? _timeProvider.GetElapsedTime(_started);

    /// <summary>Gets the independent decorative timer, capped at its randomly selected duration.</summary>
    internal TimeSpan SplinesElapsed
    {
        get
        {
            var elapsed = _timeProvider.GetElapsedTime(_started);
            return elapsed < _splinesDuration ? elapsed : _splinesDuration;
        }
    }

    /// <summary>Gets whether the decorative delay has elapsed without gating startup readiness.</summary>
    internal bool SplinesCompleted => _timeProvider.GetElapsedTime(_started) >= _splinesDuration;

    /// <summary>Gets or sets the shared rotation across startup phases.</summary>
    internal StartupTips? Tips { get; set; }

    /// <summary>Gets or sets the bounded semantic phase snapshot source.</summary>
    internal Func<IReadOnlyList<SemanticStartupPhaseSnapshot>>? Progress { get; set; }

    /// <summary>Gets or sets the semantic source without copying the process history every frame.</summary>
    internal Func<IReadOnlyList<SemanticStartupPhaseSnapshot>>? SemanticProgress { get; set; }

    /// <summary>Gets or sets informational diagnostics on the terminal owner.</summary>
    internal IReadOnlyList<string> Details { get; set; }

    /// <summary>Gets or sets the common operation timer.</summary>
    internal SemanticStartupProgress.Operation? Operation { get; set; }

    /// <summary>Gets or sets the process-wide startup elapsed time.</summary>
    internal Func<TimeSpan>? TotalElapsed { get; set; }

    /// <summary>Gets or sets the fatal-outcome acknowledgment.</summary>
    internal Action? Acknowledge { get; set; }

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (key.Code == KeyCode.Escape || (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == KeyModifiers.Ctrl))
        {
            _cancel();
        }

        if (key.Code == KeyCode.Enter)
        {
            Acknowledge?.Invoke();
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text)
    {
        return true;
    }

    /// <inheritdoc />
    public override void Render(ISurface surface)
    {
        var view = ModalFrame.Create(surface, _style(PresentationTextRole.Default), expandHeight: true, preferredSize: new Size(90, 29));
        if (view is null)
        {
            return;
        }

        var tip = Tips?.Current;
        if (tip != _tip || _tipWidth != view.Size.Width)
        {
            _tip = tip;
            _tipWidth = view.Size.Width;
            _tipLines = tip is null ? [] : ModalFrame.Wrap($"Tip: {tip}", _tipWidth);
        }

        var progress = Progress?.Invoke() ?? [];
        var semantic = SemanticProgress?.Invoke() ?? [];
        _rows.Clear();
        AppendRows(progress, labeled: true);
        AppendRows(semantic, labeled: true);
        for (var index = 0; index < _completed.Count; index++)
        {
            _rows.Add(_completed[index]);
        }

        AppendRows(progress, labeled: false);
        AppendRows(semantic, labeled: false);
        var activeCount = 0;
        var usedSlots = 0;
        foreach (var phase in _rows)
        {
            if (phase.State == SemanticStartupPhaseState.Running)
            {
                activeCount += PhaseLines(phase, view.Size.Width, usedSlots++).Length;
            }
        }

        var operation = Operation?.Snapshot;
        var operationState = operation?.State ?? _state;
        var operationLines = _label is null ? [] : FormatWrappedRows(operation?.Elapsed ?? Elapsed, _label, operationState, string.Empty, view.Size.Width);
        var totalLines = TotalElapsed is { } total ? FormatWrappedRows(total(), "Total startup", null, string.Empty, view.Size.Width) : [];
        var acknowledgment = Acknowledge is null ? [] : ModalFrame.Wrap("press Enter to exit; Esc cancels", view.Size.Width);
        var operationCapacity = Math.Max(1, view.Size.Height - CompactLogo.Length - totalLines.Length - (acknowledgment.Length == 0 ? 0 : acknowledgment.Length + 2));
        if (operationLines.Length > operationCapacity)
        {
            operationLines = [.. operationLines.Take(operationCapacity - 1), $"… {operationState}; label overflow"];
        }

        var currentRows = operationLines.Length + totalLines.Length + (acknowledgment.Length == 0 ? 0 : acknowledgment.Length + 1);
        IReadOnlyList<string> details = Details.SelectMany(detail => ModalFrame.Wrap(detail, view.Size.Width)).ToArray();
        if (Acknowledge is not null)
        {
            var fatalDetail = Details.Count == 0 ? null : Details[^1];
            if (_fatalDetail != fatalDetail || _fatalWidth != view.Size.Width)
            {
                _fatalDetail = fatalDetail;
                _fatalWidth = view.Size.Width;
                _fatalLines = fatalDetail is null ? [] : ModalFrame.Wrap(fatalDetail, _fatalWidth);
            }

            details = _fatalLines;
        }

        var historyMinimum = Acknowledge is null ? Math.Max(activeCount, Math.Min(1, _rows.Count)) : 0;
        var requiredRows = currentRows + historyMinimum + (Acknowledge is null ? 0 : details.Count);
        var lines = _logoLines;
        if (_logoWidth > view.Size.Width
            || lines.Length + 1 + requiredRows + (_tipLines.Length > 0 ? 2 : 0) > view.Size.Height)
        {
            lines = CompactLogo;
        }

        // Keep branding and tips outside the completed-history viewport.
        var logoHeight = lines.Length;
        var logoGap = view.Size.Height > logoHeight + requiredRows ? 1 : 0;

        // A short tip keeps the same footer space as a two-line tip.
        var tipRows = _tipLines.Length > 0 ? Math.Max(2, _tipLines.Length) : 0;
        var tipHeight = Math.Min(tipRows, Math.Max(0, view.Size.Height - logoHeight - logoGap - requiredRows - 1));
        var progressHeight = view.Size.Height - tipHeight - (tipHeight > 0 ? 1 : 0);
        var showSplines = _label?.StartsWith("Loading ", StringComparison.Ordinal) == true
            && progressHeight - logoHeight - logoGap > requiredRows;
        var splinesState = SplinesCompleted ? SemanticStartupPhaseState.Completed : SemanticStartupPhaseState.Running;
        var splinesLines = FormatWrappedRows(SplinesElapsed, "Reticulating Splines", splinesState, string.Empty, view.Size.Width);
        showSplines &= progressHeight - logoHeight - logoGap >= requiredRows + splinesLines.Length;
        if (showSplines)
        {
            currentRows += splinesLines.Length;
        }

        for (var index = 0; index < logoHeight; index++)
        {
            Draw(view, index, lines[index], PresentationTextRole.Brand);
        }

        var y = logoHeight + logoGap;
        var detailCapacity = Math.Max(0, progressHeight - y - currentRows - historyMinimum);
        var fatalDetailRows = Acknowledge is null ? 0 : Math.Min(details.Count, detailCapacity);
        if (Acknowledge is null)
        {
            var detailStart = Math.Max(0, details.Count - detailCapacity);
            for (var index = detailStart; index < details.Count; index++)
            {
                Draw(view, y++, details[index], PresentationTextRole.Status);
            }
        }

        var capacity = Math.Max(0, progressHeight - y - currentRows - fatalDetailRows);
        var historyRemaining = Math.Max(0, capacity - activeCount);
        var activeSlot = usedSlots;
        _visibleLines.Clear();
        for (var index = _rows.Count - 1; index >= 0 && _visibleLines.Count < capacity; index--)
        {
            var phase = _rows[index];
            var running = phase.State == SemanticStartupPhaseState.Running;
            if (!running && historyRemaining == 0)
            {
                if (activeSlot == 0)
                {
                    break;
                }

                continue;
            }

            var wrapped = running ? _rowCache[--activeSlot].Lines : PhaseLines(phase, view.Size.Width, usedSlots++);
            var take = Math.Min(wrapped.Length, running ? capacity - _visibleLines.Count : historyRemaining);
            for (var line = wrapped.Length - 1; line >= wrapped.Length - take; line--)
            {
                _visibleLines.Add((wrapped[line], phase.State));
            }

            if (!running)
            {
                historyRemaining -= take;
            }
        }

        _rowCache.RemoveRange(usedSlots, _rowCache.Count - usedSlots);
        for (var index = _visibleLines.Count - 1; index >= 0; index--)
        {
            var line = _visibleLines[index];
            Draw(view, y++, line.Text, Role(line.State));
        }

        if (showSplines)
        {
            foreach (var line in splinesLines)
            {
                Draw(view, y++, line, Role(splinesState));
            }
        }

        foreach (var line in operationLines)
        {
            Draw(view, y++, line, Role(operationState));
        }

        foreach (var line in totalLines)
        {
            Draw(view, y++, line, PresentationTextRole.Status);
        }

        for (var index = 0; index < fatalDetailRows; index++)
        {
            var text = index == fatalDetailRows - 1 && fatalDetailRows < details.Count
                ? "… Full error shown on exit."
                : details[index];
            Draw(view, y++, text, PresentationTextRole.Error);
        }

        if (acknowledgment.Length > 0)
        {
            y++;
            foreach (var line in acknowledgment)
            {
                Draw(view, y++, line, PresentationTextRole.Warning);
            }
        }

        for (var index = 0; index < Math.Min(tipHeight, _tipLines.Length); index++)
        {
            Draw(view, view.Size.Height - tipHeight + index, _tipLines[index], PresentationTextRole.Default);
        }
    }

    /// <summary>Marks a successful phase for its final rendered frame.</summary>
    internal void Complete()
    {
        Finish(SemanticStartupPhaseState.Completed);
    }

    /// <summary>Freezes the operation outcome independently of decoration.</summary>
    internal void Finish(SemanticStartupPhaseState state, TimeSpan? elapsed = null)
    {
        _completedElapsed ??= elapsed ?? Elapsed;
        _state = state;
    }

    /// <summary>Formats every startup status through the same fixed display-cell columns.</summary>
    internal static string FormatRow(TimeSpan elapsed, string label, SemanticStartupPhaseState? state, string detail, int width)
    {
        var itemWidth = Math.Min(ItemWidth, Math.Max(3, width - ElapsedWidth - StatusWidth - 2));
        var item = TranscriptView.Safe(label.Replace("...", string.Empty, StringComparison.Ordinal)).Trim();
        if (UnicodeWidth.GetWidth(item) > itemWidth)
        {
            item = item[..UnicodeWidth.GetLengthThatFits(item, itemWidth - 3)] + "...";
        }

        item += new string(' ', itemWidth - UnicodeWidth.GetWidth(item));
        var seconds = (Math.Clamp(elapsed.TotalSeconds, 0, 999.9).ToString("0.0", CultureInfo.InvariantCulture) + "s").PadLeft(ElapsedWidth);
        var status = (state?.ToString() ?? string.Empty).PadRight(StatusWidth);
        return $"{seconds} {item} {status}{TranscriptView.Safe(detail.Replace("...", string.Empty, StringComparison.Ordinal))}";
    }

    private static string[] FormatWrappedRows(TimeSpan elapsed, string label, SemanticStartupPhaseState? state, string detail, int width)
    {
        var itemWidth = Math.Min(ItemWidth, Math.Max(3, width - ElapsedWidth - StatusWidth - 2));
        var safeLabel = TranscriptView.Safe(label.Replace("...", string.Empty, StringComparison.Ordinal)).Trim();
        if (UnicodeWidth.GetWidth(safeLabel) <= itemWidth)
        {
            return ModalFrame.Wrap(FormatRow(elapsed, label, state, detail, width), width);
        }

        var seconds = (Math.Clamp(elapsed.TotalSeconds, 0, 999.9).ToString("0.0", CultureInfo.InvariantCulture) + "s").PadLeft(ElapsedWidth);
        return ModalFrame.Wrap($"{seconds} {safeLabel} {state}{detail}", width);
    }

    private string[] PhaseLines(SemanticStartupPhaseSnapshot phase, int width, int slot)
    {
        if (slot == _rowCache.Count)
        {
            _rowCache.Add(new RowCache());
        }

        var cache = _rowCache[slot];
        if (cache.Snapshot != phase || cache.Width != width)
        {
            cache.Snapshot = phase;
            cache.Width = width;
            var label = phase.Label ?? phase.Phase switch
            {
                SemanticStartupPhase.CaptureSnapshots => "Capture file snapshots",
                SemanticStartupPhase.OpenWorkspace => "Open/evaluate solution",
                SemanticStartupPhase.ConfineInputs => "Check input boundaries",
                SemanticStartupPhase.PrepareCompilation => "Prepare compilation",
                SemanticStartupPhase.StartMonitoring => "Start file monitoring",
                SemanticStartupPhase.ReadDocuments => "Read document snapshots",
                SemanticStartupPhase.ReconcileSnapshots => "Reconcile file snapshots",
                _ => phase.Phase.ToString(),
            };
            cache.Lines = FormatWrappedRows(phase.Elapsed, label, phase.State, phase.Detail is null ? string.Empty : " — " + phase.Detail, width);
        }

        return cache.Lines;
    }

    private void AppendRows(IReadOnlyList<SemanticStartupPhaseSnapshot> source, bool labeled)
    {
        for (var index = 0; index < source.Count; index++)
        {
            var row = source[index];
            if ((row.Label is not null) == labeled)
            {
                _rows.Add(row);
            }
        }
    }

    private void Draw(ISurface surface, int y, string text, PresentationTextRole role)
    {
        if (y < 0 || y >= surface.Size.Height)
        {
            return;
        }

        while (_lineRuns.Count <= y)
        {
            _lineRuns.Add(new CachedTextRun());
        }

        _lineRuns[y].Draw(surface, 0, y, text, _style(role));
    }

    private static PresentationTextRole Role(SemanticStartupPhaseState state) => state switch
    {
        SemanticStartupPhaseState.Completed => PresentationTextRole.Success,
        SemanticStartupPhaseState.Failed => PresentationTextRole.Error,
        SemanticStartupPhaseState.Cancelled => PresentationTextRole.Warning,
        SemanticStartupPhaseState.Skipped => PresentationTextRole.Default,
        _ => PresentationTextRole.Status,
    };

    private sealed class RowCache
    {
        internal SemanticStartupPhaseSnapshot? Snapshot { get; set; }

        internal int Width { get; set; }

        internal string[] Lines { get; set; } = [];
    }
}
