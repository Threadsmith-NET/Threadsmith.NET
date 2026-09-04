namespace Threadsmith.Tui;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using PrettyPrompt;
using PrettyPrompt.Consoles;
using PrettyPrompt.Highlighting;
using Spectre.Console;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Abstracts interactive terminal input and output for deterministic shell tests.</summary>
internal interface IConsoleSurface : IInteractionSurface
{
    /// <inheritdoc />
    InteractionSurfaceCapabilities IInteractionSurface.Capabilities => new();

    /// <summary>Gets the active frontend-owned session-status separator.</summary>
    string FooterSeparator => TuiThemeUi.Default.FooterSeparator;

    /// <summary>Changes the label shown by the multiline composer.</summary>
    /// <param name="prompt">Prompt text, including any desired separator.</param>
    /// <param name="cancellationToken">Stops the pending update.</param>
    Task SetPromptAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>Applies a validated theme to subsequent terminal output.</summary>
    /// <param name="theme">Theme to activate.</param>
    /// <param name="cancellationToken">Stops the pending update.</param>
    Task SetThemeAsync(ConfiguredTheme theme, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(theme);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Writes a composer-adjacent status row through the serialized console boundary.</summary>
    /// <param name="status">Immutable host-derived status.</param>
    /// <param name="separator">Validated theme-provided separator.</param>
    /// <param name="cancellationToken">Stops the pending write.</param>
    Task ShowSessionStatusAsync(
        SessionStatusSnapshot status,
        string separator,
        CancellationToken cancellationToken = default)
    {
        var text = TuiSessionStatusFormatter.Format(status, 120, separator);
        return string.IsNullOrEmpty(text)
            ? Task.CompletedTask
            : WriteAsync(text + Environment.NewLine, PresentationTextRole.SessionStatus, cancellationToken);
    }

    /// <summary>Reads one multiline composer submission.</summary>
    /// <param name="cancellationToken">Stops the pending read.</param>
    /// <returns>The completed composer interaction.</returns>
    Task<InteractionInput> ReadAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    async Task<InteractionInput> IInteractionSurface.ReadComposerAsync(
        ComposerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SetPromptAsync(request.Prompt, cancellationToken);
        return await ReadAsync(cancellationToken);
    }

    /// <summary>Begins serialized active-run hot-key capture when the surface supports it.</summary>
    /// <param name="timeProvider">Clock used for the bounded double-Escape chord.</param>
    /// <returns>An exclusive input lease, or <see langword="null"/> when unavailable.</returns>
    new IActiveRunInputLease? BeginActiveRunInput(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return null;
    }

    /// <summary>Lets the user choose one item with arrow keys and Enter.</summary>
    /// <param name="title">Prompt displayed above the choices.</param>
    /// <param name="choices">Numbered choice labels.</param>
    /// <param name="cancellationToken">Stops the pending selection.</param>
    /// <returns>The zero-based selected choice index.</returns>
    Task<int> SelectAsync(
        string title,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    async Task<InteractionSelectionResult> IInteractionSurface.SelectAsync(
        InteractionSelectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selected = await SelectAsync(
            request.Title,
            request.Options.Select(option => option.Label).ToArray(),
            cancellationToken);
        return selected < 0 || selected >= request.Options.Count
            ? new InteractionSelectionResult(null, IsCancelled: true)
            : new InteractionSelectionResult(request.Options[selected].Id);
    }

    /// <summary>Shows a transient spinner until an operation completes.</summary>
    /// <param name="text">Status text displayed beside the spinner.</param>
    /// <param name="operation">Operation whose lifetime controls the spinner.</param>
    /// <param name="cancellationToken">Stops the pending status display.</param>
    Task ShowStatusUntilAsync(
        string text,
        Task operation,
        CancellationToken cancellationToken = default);

    /// <summary>Shows bounded dynamic activity until an operation completes.</summary>
    /// <param name="activity">Terminal-neutral activity state.</param>
    /// <param name="operation">Operation whose lifetime controls the activity.</param>
    /// <param name="cancellationToken">Stops the pending activity display.</param>
#pragma warning disable VSTHRD003 // The caller owns and observes the operation that controls this display.
    Task ShowActivityUntilAsync(
        InteractionActivity activity,
        Task operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return ShowStatusUntilAsync(activity.Format(), operation, cancellationToken);
    }
#pragma warning restore VSTHRD003

    /// <summary>Writes copyable output into native terminal scrollback.</summary>
    /// <param name="text">Text to write.</param>
    /// <param name="role">Semantic role of the text.</param>
    /// <param name="cancellationToken">Stops the pending write.</param>
    Task WriteAsync(
        string text,
        PresentationTextRole role = PresentationTextRole.Default,
        CancellationToken cancellationToken = default);

    /// <summary>Writes ordered mixed-role output into native terminal scrollback.</summary>
    /// <param name="segments">Terminal-neutral semantic segments.</param>
    /// <param name="cancellationToken">Stops the pending write.</param>
    async Task WriteSegmentsAsync(
        IReadOnlyList<PresentationTextSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var segment in segments)
        {
            segment.Validate();
            await WriteAsync(segment.Text, segment.Role, cancellationToken);
        }
    }

    /// <summary>Writes ordered segment and semantic-document items without crossing a projection boundary.</summary>
    /// <param name="items">Output items in authoritative event order.</param>
    /// <param name="cancellationToken">Stops the pending write.</param>
    async Task WriteOutputAsync(
        IReadOnlyList<PresentationItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in items)
        {
            await WriteSegmentsAsync(
                PrettyPromptConsoleSurface.ProjectInteractiveOutputItem(item, 120),
                CancellationToken.None);
        }
    }

    /// <inheritdoc />
    Task IInteractionSurface.PresentAsync(
        PresentationBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return WriteOutputAsync(batch.Items, cancellationToken);
    }

    /// <inheritdoc />
    Task IInteractionSurface.PresentSessionStatusAsync(
        SessionStatusSnapshot status,
        CancellationToken cancellationToken)
    {
        return ShowSessionStatusAsync(status, FooterSeparator, cancellationToken);
    }

    /// <inheritdoc />
    Task IInteractionSurface.PresentActivityUntilAsync(
        InteractionActivity activity,
        Task operation,
        CancellationToken cancellationToken)
    {
#pragma warning disable VSTHRD003 // The compatibility surface deliberately forwards the caller-owned operation.
        return ShowActivityUntilAsync(activity, operation, cancellationToken);
#pragma warning restore VSTHRD003
    }
}

/// <summary>Adapts PrettyPrompt input and Spectre.Console output to the terminal surface.</summary>
internal sealed class PrettyPromptConsoleSurface : IConsoleSurface
{
    private const double _completionPaneHeightFraction = 0.01;
    private const string IdleOutputYieldInput = "threadsmith:idle-output-yield";
    private const string ToggleThinkingInput = "threadsmith:toggle-thinking";
    private const int _completionPaneVerticalBorderRows = 2;

    private static readonly FieldInfo _minimumCompletionItemsField = typeof(PromptConfiguration).GetField(
        "<MinCompletionItemsCount>k__BackingField",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("PrettyPrompt no longer exposes its completion-pane minimum field.");

    private readonly SemaphoreSlim _consoleGate = new(1, 1);
    private readonly IAnsiConsole _ansiConsole;
    private readonly bool _isOutputRedirected;
    private readonly BufferedPromptConsole _promptConsole;
    private readonly TextWriter _redirectedOutput;
    private readonly bool _suppressStyles;

    private string _footerSeparator;
    private string _promptText;
    private IPrompt? _prompt;
    private TuiThemeResolver _themeResolver;

    /// <summary>Initializes a new instance of the <see cref="PrettyPromptConsoleSurface"/> class.</summary>
    internal PrettyPromptConsoleSurface(
        ConfiguredTheme? initialTheme = null,
        bool? isOutputRedirected = null,
        IAnsiConsole? ansiConsole = null,
        IConsole? promptConsole = null)
    {
        _ansiConsole = ansiConsole ?? AnsiConsole.Console;
        _isOutputRedirected = isOutputRedirected ?? Console.IsOutputRedirected;
        _promptConsole = new BufferedPromptConsole(promptConsole ?? new SystemConsole());
        _redirectedOutput = _ansiConsole.Profile.Out.Writer;
        var suppressionReason = TuiThemeResolver.GetSuppressionReason(
            _isOutputRedirected,
            Environment.GetEnvironmentVariable("NO_COLOR"),
            Environment.GetEnvironmentVariable("TERM"));
        _suppressStyles = suppressionReason is not null;
        var activeTheme = initialTheme ?? BuiltInThemes.Create()[0];
        _themeResolver = new TuiThemeResolver(activeTheme.Theme, _suppressStyles);
        _footerSeparator = activeTheme.Ui.FooterSeparator;
        Debug.WriteLine(
            $"Threadsmith TUI theme '{activeTheme.Theme.Id}' selected; "
            + $"style fallback: {suppressionReason ?? "none"}.");
        _promptText = "threadsmith > ";
    }

    /// <inheritdoc />
    public string FooterSeparator => _footerSeparator;

    /// <inheritdoc />
    InteractionSurfaceCapabilities IInteractionSurface.Capabilities => new(
        SupportsActiveRunInput: !Console.IsInputRedirected,
        SupportsExactRedirectedSource: _isOutputRedirected);

    /// <inheritdoc />
    public async Task SetPromptAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await _consoleGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(_promptText, prompt, StringComparison.Ordinal))
            {
                return;
            }

            _promptText = prompt;
            _prompt = CreatePrompt(_promptText);
        }
        finally
        {
            _consoleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetThemeAsync(ConfiguredTheme theme, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(theme);
        await _consoleGate.WaitAsync(cancellationToken);
        try
        {
            _themeResolver = new TuiThemeResolver(theme.Theme, _suppressStyles);
            _footerSeparator = theme.Ui.FooterSeparator;
            _prompt = CreatePrompt(_promptText);
            Debug.WriteLine($"Threadsmith TUI theme changed to '{theme.Theme.Id}'.");
        }
        finally
        {
            _consoleGate.Release();
        }
    }

    /// <inheritdoc />
    public Task ShowSessionStatusAsync(
        SessionStatusSnapshot status,
        string separator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(separator);
        if (_isOutputRedirected)
        {
            return Task.CompletedTask;
        }

        int width;
        try
        {
            width = Console.WindowWidth;
        }
        catch (IOException)
        {
            return Task.CompletedTask;
        }

        var text = TuiSessionStatusFormatter.Format(status, width, separator);
        return string.IsNullOrEmpty(text)
            ? Task.CompletedTask
            : WriteAsync(text + Environment.NewLine, PresentationTextRole.SessionStatus, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InteractionInput> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _promptConsole.WaitForIdleOutputDrainAsync(cancellationToken);
        await _consoleGate.WaitAsync(cancellationToken);
        PromptReadCompletion promptCompletion = new(false, Task.CompletedTask);
        var promptReadStarted = false;
        PromptResult result;
        try
        {
            _promptConsole.BeginPromptRead();
            promptReadStarted = true;
            var prompt = _prompt ??= CreatePrompt(_promptText);
            result = await prompt.ReadLineAsync().WaitAsync(cancellationToken);
        }
        finally
        {
            if (promptReadStarted)
            {
                promptCompletion = _promptConsole.EndPromptRead();
            }

            _consoleGate.Release();
        }

        if (promptCompletion.WasYieldedForIdleOutput
            && result is KeyPressCallbackResult
            && string.Equals(result.Text, IdleOutputYieldInput, StringComparison.Ordinal))
        {
            await promptCompletion.OutputDrained.WaitAsync(cancellationToken);
            return new InteractionInput(
                IsSubmitted: false,
                string.Empty,
                CancellationToken.None,
                InteractionInputKind.IdleOutputYield);
        }

        var toggledThinking = result is KeyPressCallbackResult
            && string.Equals(result.Text, ToggleThinkingInput, StringComparison.Ordinal);
        return new InteractionInput(
            result.IsSuccess,
            toggledThinking ? string.Empty : result.Text,
            result.CancellationToken,
            toggledThinking ? InteractionInputKind.ToggleThinking : InteractionInputKind.Submission);
    }

    /// <inheritdoc />
    public IActiveRunInputLease? BeginActiveRunInput(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return Console.IsInputRedirected
            ? null
            : _promptConsole.TryBeginActiveRunInput(timeProvider);
    }

    /// <inheritdoc />
    public async Task<int> SelectAsync(
        string title,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
        {
            throw new ArgumentException("At least one choice is required.", nameof(choices));
        }

        await _consoleGate.WaitAsync(cancellationToken);
        try
        {
            var promptStyle = ToMarkupStyle(_themeResolver.Resolve(PresentationTextRole.SelectionPrompt));
            var itemStyle = ToMarkupStyle(_themeResolver.Resolve(PresentationTextRole.SelectionItem));
            string[] numberedChoices = [.. choices.Select((choice, index) =>
                ApplyMarkupStyle($"{index + 1}. {Markup.Escape(choice)}", itemStyle))];
            var prompt = new SelectionPrompt<string>()
                .Title(ApplyMarkupStyle(Markup.Escape(title), promptStyle))
                .EnableSearch()
                .SearchPlaceholderText("Type a number or use Up/Down")
                .AddChoices(numberedChoices);
            prompt.HighlightStyle = ToSpectreStyle(_themeResolver.Resolve(PresentationTextRole.SelectionHighlight));
            var selected = await _ansiConsole.PromptAsync(prompt, cancellationToken);
            return Array.IndexOf(numberedChoices, selected);
        }
        finally
        {
            _consoleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ShowStatusUntilAsync(
        string text,
        Task operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(operation);
        await _consoleGate.WaitAsync(cancellationToken);
        try
        {
            var statusRole = string.Equals(text, "THINKING", StringComparison.Ordinal)
                ? PresentationTextRole.ThinkingIndicator
                : PresentationTextRole.Status;
            var statusStyle = ToMarkupStyle(_themeResolver.Resolve(statusRole));
            await _ansiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(
                    ApplyMarkupStyle(Markup.Escape(text), statusStyle),
                    async _ => await operation.WaitAsync(cancellationToken));
        }
        finally
        {
            _consoleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ShowActivityUntilAsync(
        InteractionActivity activity,
        Task operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(operation);
        await _consoleGate.WaitAsync(cancellationToken);
        try
        {
            var statusRole = activity.Label.StartsWith("THINKING", StringComparison.Ordinal)
                ? PresentationTextRole.ThinkingIndicator
                : PresentationTextRole.Status;
            var statusStyle = ToMarkupStyle(_themeResolver.Resolve(statusRole));
#pragma warning disable VSTHRD003 // The shell created and separately observes the activity operation.
            await _ansiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(
                    ApplyMarkupStyle(Markup.Escape(activity.Format()), statusStyle),
                    async statusContext => await RefreshActivityUntilCompletedAsync(
                            activity,
                            operation,
                            nextText => statusContext.Status(
                                ApplyMarkupStyle(Markup.Escape(nextText), statusStyle)),
                            cancellationToken));
#pragma warning restore VSTHRD003
        }
        finally
        {
            _consoleGate.Release();
        }
    }

    /// <inheritdoc />
    public Task WriteAsync(
        string text,
        PresentationTextRole role = PresentationTextRole.Default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteSegmentsAsync([new PresentationTextSegment(text, role)], cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteSegmentsAsync(
        IReadOnlyList<PresentationTextSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return WriteOutputAsync([new PresentationTextItem(segments)], cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteOutputAsync(
        IReadOnlyList<PresentationItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        int width;
        try
        {
            width = Console.WindowWidth;
        }
        catch (IOException)
        {
            width = 120;
        }

        var writes = new List<ConsoleSurfaceWrite>();
        foreach (var item in items)
        {
            var projected = ProjectOutputItem(item, _isOutputRedirected);
            if (projected is PresentationRawSourceItem rawSourceOutput)
            {
                writes.Add(new RawConsoleSurfaceWrite(rawSourceOutput.RawSource));
                continue;
            }

            writes.AddRange(ProjectInteractiveOutputItem(projected, width).Select(segment =>
                (ConsoleSurfaceWrite)new SegmentConsoleSurfaceWrite(segment)));
        }

        foreach (var write in writes.OfType<SegmentConsoleSurfaceWrite>())
        {
            write.Segment.Validate();
        }

        using var promptOutput = _promptConsole.RegisterPromptOutput();
        await _consoleGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var write in writes)
            {
                switch (write)
                {
                    case RawConsoleSurfaceWrite rawWrite:
                        await WriteRedirectedMarkdownAsync(
                            _redirectedOutput,
                            rawWrite.RawSource,
                            CancellationToken.None);
                        break;
                    case SegmentConsoleSurfaceWrite segmentWrite:
                        _ansiConsole.Write(new Text(
                            segmentWrite.Segment.Text,
                            ToSpectreStyle(_themeResolver.Resolve(segmentWrite.Segment.Role))));
                        break;
                }
            }

            await _ansiConsole.Profile.Out.Writer.FlushAsync();
        }
        finally
        {
            _consoleGate.Release();
        }
    }

    private abstract record ConsoleSurfaceWrite;

    private sealed record SegmentConsoleSurfaceWrite(PresentationTextSegment Segment) : ConsoleSurfaceWrite;

    private sealed record RawConsoleSurfaceWrite(string RawSource) : ConsoleSurfaceWrite;

    /// <summary>Projects a source-bearing item through the immutable redirected-output capability.</summary>
    /// <param name="item">Output item to admit.</param>
    /// <param name="isOutputRedirected">Whether the output sink is proven non-terminal.</param>
    /// <returns>A raw item only for a proven redirected sink; otherwise the original safe item.</returns>
    internal static PresentationItem ProjectOutputItem(PresentationItem item, bool isOutputRedirected)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            PresentationMarkdownItem markdownOutput when isOutputRedirected =>
                new PresentationRawSourceItem(markdownOutput.RawSource),
            PresentationSourceItem sourceOutput when isOutputRedirected =>
                new PresentationRawSourceItem(sourceOutput.RawSource),
            PresentationRawSourceItem when !isOutputRedirected => throw new InvalidOperationException(
                "Raw source output cannot be admitted by an interactive console surface."),
            _ => item,
        };
    }

    /// <summary>Projects one safe output item for interactive terminal presentation.</summary>
    /// <param name="item">Safe output item to project.</param>
    /// <param name="width">Available terminal display-cell width.</param>
    /// <returns>Validated terminal-neutral segments, including answer-block separation.</returns>
    internal static IReadOnlyList<PresentationTextSegment> ProjectInteractiveOutputItem(
        PresentationItem item,
        int width)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            PresentationTextItem segmentOutput => segmentOutput.Segments,
            PresentationSourceItem sourceOutput => PrefixAnswerBlock(
                [new PresentationTextSegment(sourceOutput.SafeSource, PresentationTextRole.Default)],
                sourceOutput.StartsAnswerBlock),
            PresentationMarkdownItem markdownOutput => PrefixAnswerBlock(
                ProjectMarkdownOutput(markdownOutput, width),
                markdownOutput.StartsAnswerBlock),
            PresentationRawSourceItem => throw new InvalidOperationException(
                "Raw source output cannot be admitted by an interactive console surface."),
            _ => throw new InvalidOperationException(
                $"Unsupported TUI output item '{item.GetType().Name}'."),
        };
    }

    /// <summary>Writes exact redirected Markdown source without terminal layout or styling.</summary>
    /// <param name="writer">Captured non-terminal output writer.</param>
    /// <param name="source">Exact raw Markdown source.</param>
    /// <param name="cancellationToken">Stops the pending write before surface admission.</param>
    internal static Task WriteRedirectedMarkdownAsync(
        TextWriter writer,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(source);
        return writer.WriteAsync(source.AsMemory(), cancellationToken);
    }

    /// <summary>Prepends one presentation-owned blank line to a new interactive answer block.</summary>
    /// <param name="segments">Projected answer segments.</param>
    /// <param name="startsAnswerBlock">Whether these segments begin a new model answer.</param>
    /// <returns>The original segments or a blank-line-prefixed presentation copy.</returns>
    internal static IReadOnlyList<PresentationTextSegment> PrefixAnswerBlock(
        IReadOnlyList<PresentationTextSegment> segments,
        bool startsAnswerBlock)
    {
        return startsAnswerBlock
                ? [new PresentationTextSegment("\n", PresentationTextRole.Default), .. segments]
                : segments;
    }

    /// <summary>Projects one Markdown answer for interactive terminal layout.</summary>
    /// <param name="markdownOutput">Validated semantic document and terminal-safe source fallback.</param>
    /// <param name="width">Available terminal display-cell width.</param>
    /// <returns>Terminal-neutral interactive segments.</returns>
    internal static IReadOnlyList<PresentationTextSegment> ProjectMarkdownOutput(
        PresentationMarkdownItem markdownOutput,
        int width)
    {
        ArgumentNullException.ThrowIfNull(markdownOutput);
        try
        {
            return TuiMarkdownLayout.Format(markdownOutput.Document, width);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [new PresentationTextSegment(markdownOutput.SafeSource, PresentationTextRole.Default)];
        }
    }

    /// <summary>Determines whether an ordered event ends the active transient display.</summary>
    /// <param name="domainEvent">Event reaching the visible projection boundary.</param>
    /// <param name="emittedModelOutput">Whether the event emitted visible model output.</param>
    /// <returns><see langword="true"/> when input or visible output must take ownership of the console.</returns>
    internal static bool EndsTransientActivity(
        IDomainEvent domainEvent,
        bool emittedModelOutput)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return domainEvent is ToolInvocationCompleted
            or SemanticCheckCompleted
            or PlanProposed
            or MutationSetProposed
            or RunSteeringPaused
            or RunCompleted
            || (domainEvent is ModelOutputObserved && emittedModelOutput);
    }

    /// <summary>Refreshes transient activity until its operation completes or display cancellation is observed.</summary>
    internal static async Task RefreshActivityUntilCompletedAsync(
        InteractionActivity activity,
        Task operation,
        Action<string> updateStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(updateStatus);
        var lastText = activity.Format();
        while (!operation.IsCompleted)
        {
            var refresh = Task.Delay(
                TimeSpan.FromMilliseconds(250),
                activity.TimeProvider,
                cancellationToken);
#pragma warning disable VSTHRD003 // The shell created and separately observes the activity operation.
            if (await Task.WhenAny(operation, refresh) == operation)
#pragma warning restore VSTHRD003
            {
                break;
            }

            await refresh;
            var nextText = activity.Format();
            if (!string.Equals(nextText, lastText, StringComparison.Ordinal))
            {
                updateStatus(nextText);
                lastText = nextText;
            }
        }

        await operation.WaitAsync(cancellationToken);
    }

    private static string ApplyMarkupStyle(string escapedText, string markupStyle)
    {
        return string.IsNullOrEmpty(markupStyle) ? escapedText : $"[{markupStyle}]{escapedText}[/]";
    }

    private static string ToMarkupStyle(TuiTextStyle style)
    {
        var parts = new List<string>();
        if (style.Foreground is not null)
        {
            parts.Add(ToSpectreColorToken(style.Foreground));
        }

        if (style.Background is not null)
        {
            parts.Add($"on {ToSpectreColorToken(style.Background)}");
        }

        foreach ((var decoration, var name) in new[]
        {
            (TuiTextDecoration.Bold, "bold"),
            (TuiTextDecoration.Dim, "dim"),
            (TuiTextDecoration.Italic, "italic"),
            (TuiTextDecoration.Underline, "underline"),
            (TuiTextDecoration.Strikethrough, "strikethrough"),
            (TuiTextDecoration.Invert, "invert"),
        })
        {
            if (style.Decorations.GetValueOrDefault().HasFlag(decoration))
            {
                parts.Add(name);
            }
        }

        return string.Join(' ', parts);
    }

    private static Style ToSpectreStyle(TuiTextStyle style)
    {
        var markupStyle = ToMarkupStyle(style);
        return string.IsNullOrEmpty(markupStyle) ? Style.Plain : Style.Parse(markupStyle);
    }

    private static string ToSpectreColorToken(TuiColor color)
    {
        return color.Value switch
        {
            "brightblack" => "#808080",
            "brightred" => "#FF0000",
            "brightgreen" => "#00FF00",
            "brightyellow" => "#FFFF00",
            "brightblue" => "#0000FF",
            "brightmagenta" => "#FF00FF",
            "brightcyan" => "#00FFFF",
            "brightwhite" => "#FFFFFF",
            _ => color.Value,
        };
    }

    private IPrompt CreatePrompt(string prompt)
    {
        var promptStyle = _themeResolver.Resolve(PresentationTextRole.ComposerPrompt);
        var configuration = new PromptConfiguration(
            prompt: new FormattedString(prompt, ToPrettyPromptFormat(promptStyle)),
            proportionOfWindowHeightForCompletionPane: _completionPaneHeightFraction);

        // The composer has no completion source. PrettyPrompt's public configuration always reserves
        // two borders plus one completion row, so clear that unused minimum to retain the full window height.
        _minimumCompletionItemsField.SetValue(configuration, -_completionPaneVerticalBorderRows);
        return new Prompt(
            callbacks: new ThinkingPromptCallbacks(_promptConsole),
            console: _promptConsole,
            configuration: configuration);
    }

    private static ConsoleFormat ToPrettyPromptFormat(TuiTextStyle style)
    {
        AnsiColor? foreground = style.Foreground is not null
            && TryParsePrettyPromptColor(style.Foreground, out var foregroundValue)
            ? foregroundValue
            : null;
        AnsiColor? background = style.Background is not null
            && TryParsePrettyPromptColor(style.Background, out var backgroundValue)
            ? backgroundValue
            : null;
        return new ConsoleFormat(
            foreground,
            background,
            style.Decorations.GetValueOrDefault().HasFlag(TuiTextDecoration.Bold),
            style.Decorations.GetValueOrDefault().HasFlag(TuiTextDecoration.Underline),
            style.Decorations.GetValueOrDefault().HasFlag(TuiTextDecoration.Invert));
    }

    private static bool TryParsePrettyPromptColor(TuiColor color, out AnsiColor value)
    {
        if (color.Value == "grey")
        {
            value = AnsiColor.BrightBlack;
            return true;
        }

        return AnsiColor.TryParse(color.Value, out value);
    }

    /// <summary>Maps the Pi-compatible reasoning shortcut without consuming drafted composer text.</summary>
    internal sealed class ThinkingPromptCallbacks(BufferedPromptConsole? promptConsole = null) : PromptCallbacks
    {
        private readonly BufferedPromptConsole? _promptConsole = promptConsole;

        /// <inheritdoc />
        protected override IEnumerable<(KeyPressPattern Pattern, KeyPressCallbackAsync Callback)>
            GetKeyPressCallbacks()
        {
            yield return (
                new KeyPressPattern(
                    ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift,
                    ConsoleKey.F24),
                (text, _, _) => Task.FromResult<KeyPressCallbackResult?>(
                    _promptConsole?.TryAcknowledgeIdlePromptYield(text) is true
                        ? new KeyPressCallbackResult(IdleOutputYieldInput, output: null)
                        : null));
            yield return (
                new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.T),
                (text, _, _) => Task.FromResult<KeyPressCallbackResult?>(
                    text.Length == 0
                        ? new KeyPressCallbackResult(ToggleThinkingInput, output: null)
                        : null));
        }

        /// <inheritdoc />
        protected override Task<(string Text, int Caret)> FormatInput(
            string text,
            int caret,
            KeyPress keyPress,
            CancellationToken cancellationToken)
        {
            _promptConsole?.ObservePromptText(text);
            return Task.FromResult((text, caret));
        }
    }
}
