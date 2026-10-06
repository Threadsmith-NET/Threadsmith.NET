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
    private readonly string _logo;
    private readonly string _label;
    private readonly long _started;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _splinesDuration = TimeSpan.FromMilliseconds(Random.Shared.Next(500, 1001));
    private readonly Action _cancel;
    private readonly Func<PresentationTextRole, CellStyle> _style;
    private readonly IReadOnlyList<string> _completed;
    private readonly IReadOnlyList<string> _details;
    private readonly CachedTextRun _text = new();
    private string? _tip;
    private int _tipWidth;
    private string[] _tipLines = [];
    private bool _isComplete;
    private TimeSpan? _completedElapsed;

    /// <summary>Initializes a new instance of the <see cref="StartupModal"/> class.</summary>
    internal StartupModal(string logo, string label, IReadOnlyList<string> completed, Action cancel, Func<PresentationTextRole, CellStyle> style, IReadOnlyList<string>? details = null, TimeProvider? timeProvider = null)
    {
        _logo = logo;
        _label = label;
        _completed = completed;
        _details = details ?? [];
        _timeProvider = timeProvider ?? TimeProvider.System;
        _started = _timeProvider.GetTimestamp();
        _cancel = cancel;
        _style = style;
    }

    /// <summary>Gets the real monotonic duration of this operation.</summary>
    internal TimeSpan Elapsed => _completedElapsed ?? _timeProvider.GetElapsedTime(_started);

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

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (key.Code == KeyCode.Escape || (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == KeyModifiers.Ctrl))
        {
            _cancel();
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
        var view = ModalFrame.Create(surface, _style(PresentationTextRole.Default), expandHeight: true);
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
        var showSplines = _label.StartsWith("Loading ", StringComparison.Ordinal);
        var currentRows = showSplines ? 2 : 1;
        var reservedPhaseRows = Math.Min(1, progress.Count);
        var reservedCompletedRows = Math.Min(1, _completed.Count);
        var reservedRows = currentRows + reservedPhaseRows + reservedCompletedRows;

        // Budget the latest work and timers first; tips also need their separating gap.
        var tipHeight = Math.Min(_tipLines.Length, Math.Max(0, view.Size.Height - reservedRows - 1));
        var tipGap = tipHeight > 0 ? 1 : 0;
        var progressHeight = view.Size.Height - tipHeight - tipGap;
        var lines = _logo.ReplaceLineEndings("\n").Split('\n');
        var logoHeight = Math.Min(lines.Length, Math.Max(0, progressHeight - _completed.Count - _details.Count - progress.Count - currentRows - 1));
        for (var index = 0; index < logoHeight; index++)
        {
            _text.Draw(view, 0, index, lines[index], _style(PresentationTextRole.Brand));
        }

        var y = logoHeight > 0 ? logoHeight + 1 : 0;
        var detailCapacity = Math.Max(0, progressHeight - y - currentRows - reservedPhaseRows - reservedCompletedRows);
        foreach (var detail in _details.Take(detailCapacity))
        {
            _text.Draw(view, 0, y++, detail, _style(PresentationTextRole.Status));
        }

        // Preserve room for the latest real work and the current overall timer on small terminals.
        var availableRows = Math.Max(0, progressHeight - y - currentRows);
        var completedCapacity = progress.Count == 0
            ? availableRows
            : Math.Max(0, availableRows - Math.Min(progress.Count, Math.Max(1, availableRows - 1)));
        foreach (var phase in _completed.TakeLast(completedCapacity))
        {
            _text.Draw(view, 0, y++, phase, _style(PresentationTextRole.Success));
        }

        foreach (var phase in progress.TakeLast(Math.Max(0, progressHeight - y - currentRows)))
        {
            var role = phase.State switch
            {
                SemanticStartupPhaseState.Completed => PresentationTextRole.Success,
                SemanticStartupPhaseState.Failed => PresentationTextRole.Error,
                SemanticStartupPhaseState.Cancelled => PresentationTextRole.Warning,
                _ => PresentationTextRole.Status,
            };
            var label = phase.Phase switch
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
            _text.Draw(view, 0, y++, $"{phase.Elapsed.TotalSeconds:0.0}s {label} {phase.State}", _style(role));
        }

        if (showSplines && y < progressHeight - 1)
        {
            _text.Draw(view, 0, y++, $"{SplinesElapsed.TotalSeconds:0.0}s Reticulating Splines ...{(SplinesCompleted ? " Completed" : string.Empty)}", _style(SplinesCompleted ? PresentationTextRole.Success : PresentationTextRole.Status));
        }

        _text.Draw(
            view,
            0,
            y,
            $"{Elapsed.TotalSeconds:0.0}s {_label}{(_isComplete ? " Completed" : string.Empty)}",
            _style(_isComplete ? PresentationTextRole.Success : PresentationTextRole.Status));
        for (var index = 0; index < tipHeight; index++)
        {
            _text.Draw(view, 0, view.Size.Height - tipHeight + index, _tipLines[index], _style(PresentationTextRole.Default));
        }
    }

    /// <summary>Marks a successful phase for its final rendered frame.</summary>
    internal void Complete()
    {
        _completedElapsed ??= Elapsed;
        _isComplete = true;
    }
}
