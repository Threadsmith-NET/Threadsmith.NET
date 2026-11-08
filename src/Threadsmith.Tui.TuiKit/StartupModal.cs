namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Content;
using TUIKit.Input;
using TUIKit.Modals;

/// <summary>A modal startup projection that discards all ordinary keys and paste.</summary>
internal sealed class StartupModal : Modal
{
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
        var view = ModalFrame.Create(surface, _style(PresentationTextRole.Default), expandHeight: true, preferredSize: new Size(90, 32));
        if (view is null)
        {
            return;
        }

        var tip = Tips?.Current;
        if (tip != _tip || _tipWidth != view.Size.Width)
        {
            _tip = tip;
            _tipWidth = view.Size.Width;
            _tipLines = tip is null ? [] : [.. TextWrapper.Wrap(
                StyledText.From(TranscriptView.Safe($"Tip: {tip}")), _tipWidth)
                .Select(line => line.ToPlainString())];
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
        foreach (var row in _rows)
        {
            if (row.State == SemanticStartupPhaseState.Running)
            {
                activeCount++;
            }
        }

        // Fatal confirmation owns its final row and a blank separator outside the history viewport.
        var currentRows = (_label is null ? 0 : 1) + (TotalElapsed is null ? 0 : 1) + (Acknowledge is null ? 0 : 2);
        var details = Details;
        if (Acknowledge is not null)
        {
            var fatalDetail = Details.Count == 0 ? null : Details[^1];
            if (_fatalDetail != fatalDetail || _fatalWidth != view.Size.Width)
            {
                _fatalDetail = fatalDetail;
                _fatalWidth = view.Size.Width;
                _fatalLines = fatalDetail is null ? [] : [.. TextWrapper.Wrap(StyledText.From(TranscriptView.Safe(fatalDetail)), _fatalWidth)
                    .Select(line => line.ToPlainString())];
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
        if (showSplines)
        {
            currentRows++;
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
        var skipActive = Math.Max(0, activeCount - capacity);
        var skipHistory = Math.Max(0, _rows.Count - activeCount - Math.Max(0, capacity - activeCount));
        foreach (var phase in _rows)
        {
            if (phase.State == SemanticStartupPhaseState.Running ? skipActive-- > 0 : skipHistory-- > 0)
            {
                continue;
            }

            while (_rowCache.Count <= y)
            {
                _rowCache.Add(new RowCache());
            }

            var cache = _rowCache[y];
            if (cache.Snapshot != phase || cache.Width != view.Size.Width)
            {
                cache.Snapshot = phase;
                cache.Width = view.Size.Width;
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
                var detail = phase.Detail is null ? string.Empty : $" — {phase.Detail}";
                cache.Text = FormatRow(phase.Elapsed, label, phase.State, detail, view.Size.Width);
            }

            Draw(view, y++, cache.Text, Role(phase.State));
        }

        if (showSplines && y < progressHeight - 1)
        {
            Draw(view, y++, $"{SplinesElapsed.TotalSeconds:0.0}s Reticulating Splines ...{(SplinesCompleted ? " Completed" : string.Empty)}", SplinesCompleted ? PresentationTextRole.Success : PresentationTextRole.Status);
        }

        if (_label is not null)
        {
            var operation = Operation?.Snapshot;
            var state = operation?.State ?? _state;
            Draw(view, y++, FormatRow(operation?.Elapsed ?? Elapsed, _label, state, string.Empty, view.Size.Width), Role(state));
        }

        if (TotalElapsed is { } total && y < progressHeight)
        {
            Draw(view, y++, $"{total().TotalSeconds:0.0}s Total startup", PresentationTextRole.Status);
        }

        for (var index = 0; index < fatalDetailRows; index++)
        {
            var text = index == fatalDetailRows - 1 && fatalDetailRows < details.Count
                ? "… Full error shown on exit."
                : details[index];
            Draw(view, y++, text, PresentationTextRole.Error);
        }

        if (Acknowledge is not null && y + 1 < progressHeight)
        {
            Draw(view, y + 1, "press Enter to exit; Esc cancels", PresentationTextRole.Warning);
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
        while (_lineRuns.Count <= y)
        {
            _lineRuns.Add(new CachedTextRun());
        }

        _lineRuns[y].Draw(surface, 0, y, text, _style(role));
    }

    private static string FormatRow(TimeSpan elapsed, string label, SemanticStartupPhaseState state, string detail, int width)
    {
        var text = TranscriptView.Safe($"{elapsed.TotalSeconds:0.0}s {label} {state}{detail}");
        return UnicodeWidth.GetWidth(text) <= width ? text : TranscriptView.Safe($"{elapsed.TotalSeconds:0.0}s {state} {label}{detail}");
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

        internal string Text { get; set; } = string.Empty;
    }
}
